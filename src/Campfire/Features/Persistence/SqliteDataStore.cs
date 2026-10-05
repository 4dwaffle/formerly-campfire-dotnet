using Campfire.Contracts;
using Dapper;
using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;

namespace Campfire.Features.Persistence;

public sealed class SqliteDataStore : IDataStore, IDisposable
{
    private sealed class ConnectionConfiguration { public bool Applied; }
    private static readonly ConditionalWeakTable<object, ConnectionConfiguration> configuredConnections = new();
    private readonly string connectionString;
    private readonly SqliteLeasePool connections;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly string environment;
    private const string Versions = "[\"20231215043540\",\"20231220143106\",\"20240110071740\",\"20240115124901\",\"20240130003150\",\"20240130213001\",\"20240131105830\",\"20240209110503\",\"20250825100957\",\"20250825100958\",\"20250825100959\",\"20251126092013\",\"20251126115722\",\"20251126130131\",\"20251212154340\"]";
    public string DatabasePath { get; }
    public SqliteDataStore(IConfiguration configuration)
    {
#if !CAMPFIRE_NATIVE_AOT
        DefaultTypeMap.MatchNamesWithUnderscores = true;
#endif
        var storage = configuration["CAMPFIRE_STORAGE"] ?? "./storage";
        environment = configuration["RAILS_ENV"] ?? "production";
        if (environment is not ("production" or "development" or "test" or "performance")) throw new InvalidOperationException("Unsupported RAILS_ENV for pinned Campfire SQLite configuration.");
        Directory.CreateDirectory(Path.Combine(storage, "db"));
        DatabasePath = Path.GetFullPath(Path.Combine(storage, "db", environment + ".sqlite3"));
        connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, DefaultTimeout = 5, Pooling = false }.ToString();
        connections = new SqliteLeasePool(Open);
        try { InitializeSchema(); } catch { connections.Dispose(); writer.Dispose(); throw; }
    }
    private void InitializeSchema()
    {
        using var lease = connections.Rent();
        var connection = lease.Connection;
        if (connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='users'") == 0)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            if (connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='users'", transaction: transaction) != 0) { transaction.Commit(); return; }
            connection.Execute(SchemaSource(), transaction: transaction);
            // Pinned Rails 6c7f8fa: schema loading records every migration, not only its latest version.
            InstallMetadata(connection, transaction);
            transaction.Commit();
        }
        else
        {
            if (connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('accounts','users','rooms','memberships','sessions','messages','message_search_index','action_text_rich_texts','active_storage_blobs','active_storage_attachments','active_storage_variant_records','boosts','bans','webhooks','push_subscriptions','searches','ar_internal_metadata','schema_migrations')") != 18)
                throw new InvalidOperationException("The existing Campfire database is incomplete. Restore or migrate it with pinned Rails before starting the port.");
            if (connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations WHERE version>'20251212154340'") != 0)
                throw new InvalidOperationException("The database is newer than the pinned Campfire schema.");
            if (connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations WHERE version='20251212154340'") != 1)
                throw new InvalidOperationException("The existing database has not applied the pinned Campfire migrations. Migrate it with pinned Rails before starting the port.");
            if (connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations") == 1 && connection.ExecuteScalar<string>("SELECT version FROM schema_migrations LIMIT 1") == "20251212154340" && connection.ExecuteScalar<long>("SELECT count(*) FROM ar_internal_metadata") == 0)
            {
                // Recognize only the exact schema emitted by the old port. Never invent
                // migration history for a Rails database that is partially migrated.
                using var expected = new SqliteConnection("Data Source=:memory:;Pooling=False");
                expected.Open();
                expected.Execute(SchemaSource());
                var expectedSchema = SchemaFingerprint(expected);
                using var transaction = connection.BeginTransaction(deferred: false);
                if (SchemaFingerprint(connection, transaction) != expectedSchema) throw new InvalidOperationException("Old port migration metadata cannot be repaired: schema, foreign keys or indices differ from the pinned schema. Restore a known Rails-compatible backup or explicitly migrate this database before startup.");
                InstallMetadata(connection, transaction);
                transaction.Commit();
            }
        }
    }
    private static string SchemaSource()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Features", "Persistence", "schema.sql");
        if (!File.Exists(path)) path = Path.Combine(Directory.GetCurrentDirectory(), "Features", "Persistence", "schema.sql");
        if (!File.Exists(path)) path = Path.Combine(Directory.GetCurrentDirectory(), "src", "Campfire", "Features", "Persistence", "schema.sql");
        return File.ReadAllText(path);
    }
    private static string SchemaFingerprint(SqliteConnection connection, SqliteTransaction? transaction = null) => connection.ExecuteScalar<string>("SELECT json_group_array(json_object('type',type,'name',name,'table',tbl_name,'sql',sql)) FROM (SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name)", transaction: transaction)!;
    private void InstallMetadata(SqliteConnection connection, SqliteTransaction transaction)
    {
        connection.Execute("INSERT OR IGNORE INTO schema_migrations(version) SELECT value FROM json_each(@versions)", new { versions = Versions }, transaction);
        connection.Execute("INSERT OR IGNORE INTO ar_internal_metadata(key,value,created_at,updated_at) VALUES('environment',@environment,@now,@now),('schema_sha1',@sha,@now,@now)", new { environment, now = RequestUser.Timestamp(), sha = "f75da8dad38bfb179ffd757bd7a7c2b3f818bc29" }, transaction);
    }
    private PreparedSqliteConnection Open()
    {
        var connection = new PreparedSqliteConnection(connectionString);
        try
        {
            connection.Open();
            // Rails configures each physical connection once. Store leases retain
            // the managed connection and its prepared statements until eviction.
            var settings = configuredConnections.GetValue(connection.Handle!, static _ => new ConnectionConfiguration());
            lock (settings)
            {
                if (!settings.Applied)
                {
                    connection.Execute("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA mmap_size=134217728; PRAGMA journal_size_limit=67108864; PRAGMA cache_size=2000;");
                    settings.Applied = true;
                }
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public T Read<T>(Func<SqliteConnection, T> operation) { using var lease = connections.Rent(); return operation(lease.Connection); }
#if CAMPFIRE_NATIVE_AOT
    // These compatibility helpers erase the parameter/result shape. Every production
    // operation uses Read/Write with concrete, source-generated Dapper calls instead.
    private static NotSupportedException GenericQueryUnavailable() => new("Generic SQL helpers cannot generate Native AOT factories. Use Read/Write with an inline concrete Dapper call.");
    public List<T> Query<T>(string sql, object? parameters = null) => throw GenericQueryUnavailable();
    public T? Single<T>(string sql, object? parameters = null) => throw GenericQueryUnavailable();
    public T Scalar<T>(string sql, object? parameters = null) => throw GenericQueryUnavailable();
    public int Execute(string sql, object? parameters = null) => throw GenericQueryUnavailable();
#else
    [DapperAot(false)]
    public List<T> Query<T>(string sql, object? parameters = null) { using var lease = connections.Rent(); return lease.Connection.Query<T>(sql, parameters).AsList(); }
    [DapperAot(false)]
    public T? Single<T>(string sql, object? parameters = null) { using var lease = connections.Rent(); return lease.Connection.QuerySingleOrDefault<T>(sql, parameters); }
    [DapperAot(false)]
    public T Scalar<T>(string sql, object? parameters = null) { using var lease = connections.Rent(); return lease.Connection.ExecuteScalar<T>(sql, parameters)!; }
    [DapperAot(false)]
    public int Execute(string sql, object? parameters = null) => Write((c, t) => c.Execute(sql, parameters, t));
#endif
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation) => WriteCore(operation, null);
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation, Action<SqliteConnection, T> afterCommit) => WriteCore(operation, afterCommit);
    private T WriteCore<T>(Func<SqliteConnection, SqliteTransaction, T> operation, Action<SqliteConnection, T>? afterCommit)
    {
        writer.Wait();
        try
        {
            using var lease = connections.Rent();
            var c = lease.Connection;
            T result;
            using (var transaction = c.BeginTransaction(deferred: false))
            {
                result = operation(c, transaction);
                transaction.Commit();
            }
            // Keep the writer lease while ordered SQL callbacks run, with the
            // original transaction already committed and disposed. Callback
            // failure preserves that commit and releases the lease in finally.
            afterCommit?.Invoke(c, result);
            return result;
        }
        finally { writer.Release(); }
    }
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation, CancellationToken cancellationToken = default) => WriteAsyncCore(operation, null, cancellationToken);
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation, Action<SqliteConnection, T> afterCommit, CancellationToken cancellationToken = default) => WriteAsyncCore(operation, afterCommit, cancellationToken);
    private async Task<T> WriteAsyncCore<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation, Action<SqliteConnection, T>? afterCommit, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            using var lease = connections.Rent();
            var c = lease.Connection;
            T result;
            using (var transaction = c.BeginTransaction(deferred: false))
            {
                result = await operation(c, transaction);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            // Cancellation cannot undo a completed commit. Finish its ordered
            // database callbacks before releasing the shared writer lease.
            afterCommit?.Invoke(c, result);
            return result;
        }
        finally { writer.Release(); }
    }
    public void Dispose()
    {
        connections.Dispose();
        // Waiters admitted before shutdown still need to unwind writer.Release.
        // This semaphore uses only managed state (no WaitHandle is requested).
    }
}
