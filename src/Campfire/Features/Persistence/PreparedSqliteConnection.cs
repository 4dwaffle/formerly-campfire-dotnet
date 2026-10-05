using System.Data;
using Microsoft.Data.Sqlite;

namespace Campfire.Features.Persistence;

// SqliteCommand's preparation belongs to its managed connection. Keeping the
// connection open between exclusive store leases preserves its native statements.
internal sealed class PreparedSqliteConnection(string connectionString, int capacity = 256) : SqliteConnection(connectionString)
{
    private readonly Dictionary<string, LinkedListNode<SqliteCommand>> idle = new(StringComparer.Ordinal);
    private readonly LinkedList<SqliteCommand> recent = new();
    private readonly HashSet<ForwardingCommand> issued = new();
    private bool closing;

    public override SqliteCommand CreateCommand()
    {
        var command = new ForwardingCommand(this) { Connection = this, Transaction = Transaction, CommandTimeout = DefaultTimeout };
        issued.Add(command);
        return command;
    }

    private SqliteCommand Take(string sql)
    {
        if (idle.Remove(sql, out var node)) { recent.Remove(node); return node.Value; }
        return new SqliteCommand(sql, this);
    }

    private void Return(SqliteCommand command)
    {
        command.Transaction = null;
        // Release managed request objects; native binding cleanup is separate
        // from sqlite3_reset performed by the provider's reader disposal.
        foreach (SqliteParameter parameter in command.Parameters) parameter.Value = DBNull.Value;
        if (closing || State != ConnectionState.Open || capacity <= 0 || idle.ContainsKey(command.CommandText)) { command.Dispose(); return; }
        if (idle.Count == capacity)
        {
            var oldest = recent.Last!;
            recent.RemoveLast(); idle.Remove(oldest.Value.CommandText); oldest.Value.Dispose();
        }
        idle.Add(command.CommandText, recent.AddFirst(command));
    }

    internal void EndLease()
    {
        // Like SqliteConnection.Close, a lease invalidates outstanding readers
        // and rolls back any transaction a caller left open.
        foreach (var command in issued.ToArray()) command.Dispose();
        Transaction?.Dispose();
    }

    public override void Close()
    {
        closing = true;
        try
        {
            EndLease();
            foreach (var command in recent) command.Dispose();
            recent.Clear(); idle.Clear();
            base.Close();
        }
        finally { closing = false; }
    }

    private sealed class ForwardingCommand(PreparedSqliteConnection owner) : SqliteCommand
    {
        private SqliteCommand? inner;
        private SqliteDataReader? reader;
        private bool faulted;

        private SqliteCommand Bind()
        {
            if (Connection != owner || owner.State != ConnectionState.Open) throw new InvalidOperationException("The prepared command requires its open owning connection.");
            if (reader is { IsClosed: false }) throw new InvalidOperationException("An open reader already uses this command.");
            if (inner != null && inner.CommandText != CommandText) Release();
            inner ??= owner.Take(CommandText);
            inner.CommandTimeout = CommandTimeout;
            inner.Transaction = Transaction;
            // Parameter shape is independent of SQL identity. Dapper's generated
            // binders still populate this provider-compatible outer command.
            var target = inner.Parameters;
            var sameShape = target.Count == Parameters.Count;
            for (var i = 0; sameShape && i < target.Count; i++) sameShape = target[i].ParameterName == Parameters[i].ParameterName;
            if (!sameShape)
            {
                target.Clear();
                foreach (SqliteParameter parameter in Parameters) target.Add(new SqliteParameter { ParameterName = parameter.ParameterName });
            }
            for (var i = 0; i < Parameters.Count; i++)
            {
                var source = Parameters[i]; var destination = target[i];
                destination.SqliteType = source.SqliteType;
                destination.Size = source.Size;
                destination.IsNullable = source.IsNullable;
                destination.Value = source.Value;
            }
            return inner;
        }

        public override void Prepare() { try { Bind().Prepare(); } catch { faulted = true; throw; } }
        public override SqliteDataReader ExecuteReader(CommandBehavior behavior)
        {
            try { return reader = Bind().ExecuteReader(behavior); }
            catch { faulted = true; throw; }
        }
        public override int ExecuteNonQuery() { try { return Bind().ExecuteNonQuery(); } catch { faulted = true; throw; } }
        public override object? ExecuteScalar() { try { return Bind().ExecuteScalar(); } catch { faulted = true; throw; } }

        private void Release()
        {
            var command = inner; inner = null;
            try { reader?.Dispose(); }
            catch { faulted = true; throw; }
            finally { reader = null; if (faulted) { command?.Dispose(); command = null; } }
            if (command == null) return;
            if (faulted) command.Dispose(); else owner.Return(command);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { Release(); } finally { owner.issued.Remove(this); } }
            base.Dispose(disposing);
        }
    }
}

// A bound on retained idle resources, not a blocking admission limit. Bursts rent
// additional isolated connections; excess connections close when their lease ends.
internal sealed class SqliteLeasePool(Func<PreparedSqliteConnection> create, int maximumIdle = 32) : IDisposable
{
    private readonly object gate = new();
    private readonly Stack<PreparedSqliteConnection> idle = new();
    private bool disposed;
    private int active;

    internal Lease Rent()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            while (idle.TryPop(out var connection))
            {
                if (connection.State == ConnectionState.Open) { active++; return new(this, connection); }
                connection.Dispose();
            }
            active++;
        }
        try { return new(this, create()); }
        catch { EndActive(); throw; }
    }

    private void Return(PreparedSqliteConnection connection)
    {
        try
        {
            try { connection.EndLease(); }
            catch { connection.Dispose(); throw; }
            lock (gate)
            {
                if (!disposed && idle.Count < maximumIdle && connection.State == ConnectionState.Open) { idle.Push(connection); return; }
            }
            connection.Dispose();
        }
        finally { EndActive(); }
    }

    private void EndActive()
    {
        lock (gate) { active--; if (active == 0) Monitor.PulseAll(gate); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            // Shutdown alone waits for existing operations. In particular, a
            // committed callback keeps owning its connection until it finishes.
            while (active != 0) Monitor.Wait(gate);
            while (idle.TryPop(out var connection)) connection.Dispose();
        }
    }

    internal sealed class Lease(SqliteLeasePool pool, PreparedSqliteConnection connection) : IDisposable
    {
        private bool returned;
        internal PreparedSqliteConnection Connection => connection;
        public void Dispose() { if (!returned) { returned = true; pool.Return(connection); } }
    }
}
