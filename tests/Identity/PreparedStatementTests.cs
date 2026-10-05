using Campfire.Features.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SQLitePCL;
using System.Runtime.InteropServices;
using Xunit;

namespace Identity.Tests;

public sealed class PreparedStatementTests
{
    // sqlite3.h statement-status operation IDs (not exposed by SQLitePCLRaw).
    private const int SqliteStatementRun = 6, SqliteStatementReprepare = 5;
    private static SqliteDataStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CAMPFIRE_STORAGE"] = Path.Combine(Path.GetTempPath(), "campfire-prepared-" + Guid.NewGuid()) }).Build());
    private static class NativeStatements
    {
        // Test-only enumeration avoids enabling SQLitePCL's per-statement weak
        // reference registry in production solely to inspect native pointers.
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_next_stmt(IntPtr db, IntPtr statement);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_sql(IntPtr statement);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_stmt_status(IntPtr statement, int operation, int reset);
    }
    private static IntPtr Statement(SqliteConnection connection, string sql)
    {
        // SQLite itself enumerates its live prepared statements. No reflection or
        // counters in the implementation substitute for actual native reuse.
        var handle = connection.Handle!.DangerousGetHandle();
        for (var statement = NativeStatements.sqlite3_next_stmt(handle, IntPtr.Zero); statement != IntPtr.Zero; statement = NativeStatements.sqlite3_next_stmt(handle, statement))
            if (Marshal.PtrToStringUTF8(NativeStatements.sqlite3_sql(statement)) == sql) return statement;
        return IntPtr.Zero;
    }
    private const string ProbeSql = "SELECT @value AS prepared_probe";
    private static (sqlite3 Handle, IntPtr Statement, long Value) Probe(SqliteDataStore db, long value) => db.Read(connection =>
    {
        // This concrete call site is intercepted by Dapper.AOT in this project.
        var result = connection.ExecuteScalar<long>(ProbeSql, new { value });
        var statement = Statement(connection, ProbeSql); Assert.NotEqual(IntPtr.Zero, statement);
        return (connection.Handle!, statement, result);
    });

    [Fact]
    public void GeneratedDapperQueryReusesNativeStatementAcrossLeasesAndClosesOnDispose()
    {
        var db = Store();
        var first = Probe(db, 17); var second = Probe(db, 29);
        Assert.Equal(17, first.Value); Assert.Equal(29, second.Value);
        Assert.Same(first.Handle, second.Handle);
        Assert.Equal(first.Statement, second.Statement);
        Assert.Equal(2, NativeStatements.sqlite3_stmt_status(second.Statement, SqliteStatementRun, 0));
        db.Dispose();
        Assert.True(first.Handle.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => Probe(db, 1));
    }

    [Fact]
    public void CacheEvictsNativeStatementsAndReplacesClosedConnections()
    {
        using var db = Store();
        var first = Probe(db, 1);
        db.Read(connection =>
        {
            for (var i = 0; i < 257; i++)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal((long)i, command.ExecuteScalar());
            }
            Assert.Equal(IntPtr.Zero, Statement(connection, ProbeSql));
            return 0;
        });
        var rebound = Probe(db, 2); Assert.Equal(2, rebound.Value);
        db.Read(connection => { connection.Close(); return 0; });
        Assert.True(rebound.Handle.IsClosed);
        var replacement = Probe(db, 3);
        Assert.NotSame(rebound.Handle, replacement.Handle); Assert.Equal(3, replacement.Value);
    }

    [Fact]
    public void PreparedCommandsResetParametersAndReprepareAfterSchemaChanges()
    {
        using var db = Store();
        const string sql = "SELECT typeof(@value)||':'||coalesce(hex(@value),'')";
        string Typed(object? value, SqliteType type) => db.Read(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            command.Parameters.Add(new SqliteParameter("@value", type) { Value = value ?? DBNull.Value });
            return Assert.IsType<string>(command.ExecuteScalar());
        });
        Assert.Equal("integer:3432", Typed(42L, SqliteType.Integer));
        Assert.Equal("null:", Typed(null, SqliteType.Text));
        Assert.Equal("blob:0001FF", Typed(new byte[] { 0, 1, 255 }, SqliteType.Blob));
        Assert.Equal("text:6869", Typed("hi", SqliteType.Text));
        db.Write((connection, transaction) => connection.Execute("CREATE TABLE prepare_schema(value INTEGER); INSERT INTO prepare_schema VALUES(7)", transaction: transaction));
        const string schemaSql = "SELECT * FROM prepare_schema";
        var first = db.Read(connection => { using var command = connection.CreateCommand(); command.CommandText = schemaSql; Assert.Equal(7L, command.ExecuteScalar()); return Statement(connection, schemaSql)!; });
        db.Write((connection, transaction) => connection.Execute("ALTER TABLE prepare_schema ADD COLUMN extra TEXT", transaction: transaction));
        db.Read(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = schemaSql;
            using var reader = command.ExecuteReader(); Assert.Equal(2, reader.FieldCount); Assert.True(reader.Read()); Assert.Equal(7, reader.GetInt64(0));
            Assert.True(NativeStatements.sqlite3_stmt_status(first, SqliteStatementReprepare, 0) >= 1);
            return 0;
        });
    }

    [Fact]
    public async Task ConcurrentLeasesUseDifferentHandlesAndQueriesObserveCommittedChanges()
    {
        using var db = Store();
        db.Write((connection, transaction) => connection.Execute("CREATE TABLE prepare_visible(value INTEGER); INSERT INTO prepare_visible VALUES(1)", transaction: transaction));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = Task.Run(() => db.Read(connection => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); return connection.Handle!; }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var concurrent = db.Read(connection => connection.Handle!);
        db.Write((connection, transaction) => connection.Execute("UPDATE prepare_visible SET value=2", transaction: transaction));
        Assert.Equal(2, db.Read(connection => connection.ExecuteScalar<long>("SELECT value FROM prepare_visible")));
        release.SetResult(); Assert.NotSame(await held, concurrent);
        var handles = new HashSet<sqlite3>();
        void Nest(int remaining) => db.Read(connection => { handles.Add(connection.Handle!); if (remaining > 0) Nest(remaining - 1); return 0; });
        Nest(39); Assert.Equal(40, handles.Count);
        Assert.Equal(32, handles.Count(handle => !handle.IsClosed));
        db.Dispose(); Assert.All(handles, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public void FailedExecutionAndCallbacksPreserveTransactionBoundariesAndReleaseLease()
    {
        using var db = Store();
        db.Write((connection, transaction) => connection.Execute("CREATE TABLE prepare_write(value INTEGER UNIQUE)", transaction: transaction));
        const string insert = "INSERT INTO prepare_write VALUES(@value)";
        long Insert(long value) => db.Write((connection, transaction) => connection.Execute(insert, new { value }, transaction));
        Assert.Equal(1, Insert(1)); Assert.Throws<SqliteException>(() => Insert(1)); Assert.Equal(1, Insert(2));
        Assert.Throws<InvalidOperationException>(() => db.Write((connection, transaction) => connection.Execute(insert, new { value = 3 }, transaction), (connection, _) =>
        {
            Assert.Equal(1, raw.sqlite3_get_autocommit(connection.Handle!));
            connection.Execute(insert, new { value = 4 });
            throw new InvalidOperationException("callback failure");
        }));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, db.Read(connection => connection.Query<long>("SELECT value FROM prepare_write ORDER BY value").ToArray()));
        Assert.Equal(1, Insert(5));
        SqliteDataReader? escaped = null;
        db.Read(connection => { var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM prepare_write"; escaped = command.ExecuteReader(); Assert.True(escaped.Read()); return 0; });
        Assert.True(escaped!.IsClosed);
    }

    [Fact]
    public async Task DisposalDrainsActiveLeaseAndImmediatelyReleasesDatabaseFiles()
    {
        var db = Store();
        var directory = Path.GetDirectoryName(Path.GetDirectoryName(db.DatabasePath))!;
        var entered = new TaskCompletionSource<sqlite3>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = Task.Run(() => db.Read(connection => { entered.SetResult(connection.Handle!); release.Task.GetAwaiter().GetResult(); return 0; }));
        var handle = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal = Task.Run(db.Dispose);
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { db.Read(_ => 0); return false; }
                catch (ObjectDisposedException) { return true; }
            }, TimeSpan.FromSeconds(10)));
            Assert.False(disposal.IsCompleted); Assert.False(handle.IsClosed);
        }
        finally { release.SetResult(); }
        await Task.WhenAll(reading, disposal).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(handle.IsClosed);
        Directory.Delete(directory, recursive: true);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ShutdownFinishesCommittedCallbackBeforeClosingItsConnection()
    {
        var db = Store();
        db.Write((connection, transaction) => connection.Execute("CREATE TABLE shutdown_callback(value INTEGER)", transaction: transaction));
        var entered = new TaskCompletionSource<sqlite3>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = Task.Run(() => db.Write((connection, transaction) => connection.Execute("INSERT INTO shutdown_callback VALUES(1)", transaction: transaction), (connection, _) =>
        {
            Assert.Equal(1, raw.sqlite3_get_autocommit(connection.Handle!));
            entered.SetResult(connection.Handle!);
            release.Task.GetAwaiter().GetResult();
            connection.Execute("INSERT INTO shutdown_callback VALUES(2)");
        }));
        var handle = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal = Task.Run(db.Dispose);
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { db.Read(_ => 0); return false; }
                catch (ObjectDisposedException) { return true; }
            }, TimeSpan.FromSeconds(10)));
            Assert.False(disposal.IsCompleted); Assert.False(handle.IsClosed);
        }
        finally { release.SetResult(); }
        await Task.WhenAll(writing, disposal).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(handle.IsClosed);
        using var verifier = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db.DatabasePath, Pooling = false }.ToString());
        verifier.Open();
        Assert.Equal(new long[] { 1, 2 }, verifier.Query<long>("SELECT value FROM shutdown_callback ORDER BY value").ToArray());
        Assert.Throws<ObjectDisposedException>(() => db.Write((_, _) => 0));
    }

    [Fact]
    public async Task ExplicitStoreDisposalCompletesWhileHostScopeDisposalIsStillUnwinding()
    {
        var db = Store();
        var directory = Path.GetDirectoryName(Path.GetDirectoryName(db.DatabasePath))!;
        var probe = Probe(db, 53);
        var barrier = new AsyncDisposalBarrier();
        var services = new ServiceCollection();
        services.AddSingleton(_ => db);
        services.AddSingleton(_ => barrier);
        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<SqliteDataStore>();
        _ = provider.GetRequiredService<AsyncDisposalBarrier>();
        var disposing = provider.DisposeAsync().AsTask();
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // A concurrent host disposer can return before the first disposal
            // finishes awaiting an async singleton ahead of the SQLite store.
            await provider.DisposeAsync();
            Assert.False(disposing.IsCompleted);
            Assert.False(probe.Handle.IsClosed);
            var first = Task.Run(db.Dispose);
            var second = Task.Run(db.Dispose);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(probe.Handle.IsClosed);
            Assert.Throws<ObjectDisposedException>(() => Probe(db, 59));
            Directory.Delete(directory, recursive: true);
            Assert.False(Directory.Exists(directory));
        }
        finally { barrier.Release.TrySetResult(); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class AsyncDisposalBarrier : IAsyncDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync() { Entered.TrySetResult(); await Release.Task; }
    }

    [Fact]
    public void MissingParameterCannotReuseOldValueAndClosingReadersUnwindSafely()
    {
        using var db = Store();
        Assert.Equal(37, Probe(db, 37).Value);
        db.Read(connection =>
        {
            using var missing = connection.CreateCommand(); missing.CommandText = ProbeSql;
            Assert.Throws<InvalidOperationException>(() => missing.ExecuteScalar());
            return 0;
        });
        Assert.Equal(41, Probe(db, 41).Value);
        var handle = db.Read(connection =>
        {
            var original = connection.Handle!;
            using var command = connection.CreateCommand(); command.CommandText = "SELECT 1";
            using (var reader = command.ExecuteReader(System.Data.CommandBehavior.CloseConnection)) Assert.True(reader.Read());
            Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
            return original;
        });
        Assert.True(handle.IsClosed);
        // Also let EndLease close a leaked reader whose disposal closes the
        // connection, recursively entering the connection's cleanup path.
        var leakedHandle = db.Read(connection =>
        {
            var command = connection.CreateCommand(); command.CommandText = "SELECT 2";
            var reader = command.ExecuteReader(System.Data.CommandBehavior.CloseConnection); Assert.True(reader.Read());
            return connection.Handle!;
        });
        Assert.True(leakedHandle.IsClosed);
        Assert.Equal(43, Probe(db, 43).Value);
    }
}
