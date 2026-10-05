using System.Globalization;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Campfire.Contracts;

namespace Campfire.Features.Realtime;

// RESP2 bulk strings and flat arrays cover the pub/sub, reliable-list and Lua
// operations we use. No serializer, reflection or runtime code generation.
public sealed class RedisTransport(IConfiguration configuration) : IRedisTransport, IAsyncDisposable
{
    private readonly string? address = Resolve(configuration);
    private readonly ConcurrentQueue<Connection> idle = new();
    private readonly object poolSync=new();
    private readonly SemaphoreSlim borrowers = new(PoolSize(configuration),PoolSize(configuration));
    private int disposed;
    private static int PoolSize(IConfiguration configuration)
    {
        var value=configuration["CAMPFIRE_REDIS_POOL_SIZE"];
        if(value is null)return 16;
        if(!int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var size)||size is <1 or >256)throw new InvalidOperationException("CAMPFIRE_REDIS_POOL_SIZE must be between 1 and 256");
        return size;
    }
    private static string? Resolve(IConfiguration configuration)
    {
        var address=configuration["CAMPFIRE_REDIS_URL"]??configuration["REDIS_URL"];
        if(string.IsNullOrWhiteSpace(address)&&string.Equals(configuration["CAMPFIRE_REQUIRE_REDIS"],"true",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("CAMPFIRE_REQUIRE_REDIS requires REDIS_URL");
        return address;
    }
    public bool Enabled => !string.IsNullOrWhiteSpace(address);
    public string Prefix => configuration["CAMPFIRE_REDIS_PREFIX"] ?? "campfire_production:dotnet";
    public string CablePrefix => configuration["CAMPFIRE_CABLE_PREFIX"] ?? "campfire_production";
    public async Task<string?> StringAsync(string[] command, CancellationToken cancellationToken = default) => (await Commands([command], cancellationToken))[0].Value;
    public async Task<long> IntegerAsync(string[] command, CancellationToken cancellationToken = default) => long.Parse((await Commands([command], cancellationToken))[0].Value ?? "0", CultureInfo.InvariantCulture);
    public async Task<string?[]> ArrayAsync(string[] command, CancellationToken cancellationToken = default) => (await Commands([command], cancellationToken))[0].Array ?? [];
    public async Task<long[]> IntegersAsync(IReadOnlyList<string[]> commands, CancellationToken cancellationToken = default)
        => (await Commands(commands,cancellationToken)).Select(reply=>long.Parse(reply.Value??"0",CultureInfo.InvariantCulture)).ToArray();
    public Task<long> PublishBatchAsync(IReadOnlyList<string[]> commands,CancellationToken cancellationToken=default)
    {
        if(commands.Count is <1 or >64)throw new ArgumentOutOfRangeException(nameof(commands),"Publication batches require between 1 and 64 commands");
        var script=new List<string>{"EVAL","local n=0;for i=1,#ARGV,2 do redis.call('PUBLISH',ARGV[i],ARGV[i+1]);n=n+1;end;return n","0"};
        foreach(var command in commands)
        {
            if(command.Length!=3||command[0]!="PUBLISH")throw new ArgumentException("Publication batches accept only PUBLISH commands",nameof(commands));
            script.Add(command[1]);script.Add(command[2]);
        }
        // Unlike pipelining, redis.call stops at the first server-side error.
        // Already published prefix events remain applied; nothing is replayed.
        return IntegerAsync(script.ToArray(),cancellationToken);
    }
    private async Task<Reply[]> Commands(IReadOnlyList<string[]> commands, CancellationToken cancellationToken)
    {
        if(commands.Count is <1 or >64)throw new ArgumentOutOfRangeException(nameof(commands),"Redis pipelines require between 1 and 64 commands");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        await borrowers.WaitAsync(timeout.Token);
        Connection? connection=null;
        var reusable=false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            if(!idle.TryDequeue(out connection))connection=await Open(timeout.Token);
            await connection.Write(commands,timeout.Token);
            var replies=new Reply[commands.Count];
            for(var index=0;index<replies.Length;index++)replies[index]=await connection.Read(timeout.Token);
            reusable=true;
            return replies;
        }
        finally
        {
            // A failed pipeline may have applied any prefix (or all commands).
            // Never replay uncertain writes or return a socket with unread replies.
            try
            {
                if(connection is not null)
                {
                    lock(poolSync){if(reusable&&disposed==0){idle.Enqueue(connection);connection=null;}}
                    if(connection is not null)await connection.DisposeAsync();
                }
            }
            finally{borrowers.Release();}
        }
    }
    public async Task SubscribeAsync(string channel, Func<string, Task> consume, CancellationToken cancellationToken, Action? subscribed=null)
    {
        await using var connection = await Open(cancellationToken);
        await connection.Write(["SUBSCRIBE", channel], cancellationToken);
        await connection.Read(cancellationToken);
        subscribed?.Invoke();
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = (await connection.Read(cancellationToken)).Array;
            if (packet is { Length: 3 } && packet[0] == "message" && packet[2] is string value) await consume(value);
        }
    }
    public async Task PatternSubscribeAsync(string pattern,Func<string,string,Task> consume,CancellationToken cancellationToken,Action? subscribed=null)
    {
        await using var connection=await Open(cancellationToken);await connection.Write(["PSUBSCRIBE",pattern],cancellationToken);await connection.Read(cancellationToken);subscribed?.Invoke();
        while(!cancellationToken.IsCancellationRequested){var packet=(await connection.Read(cancellationToken)).Array;if(packet is {Length:4}&&packet[0]=="pmessage"&&packet[2] is string channel&&packet[3] is string value)await consume(channel,value);}
    }
    private async Task<Connection> Open(CancellationToken cancellationToken)
    {
        if (!Enabled) throw new InvalidOperationException("Redis is not configured");
        var uri = new Uri(address!);
        if (uri.Scheme is not ("redis" or "rediss")) throw new InvalidOperationException("Invalid Redis URI scheme");
        var socket = new TcpClient { NoDelay=true };
        try
        {
            await socket.ConnectAsync(uri.Host, uri.IsDefaultPort ? 6379 : uri.Port, cancellationToken);
            Stream stream = socket.GetStream();
            if (uri.Scheme == "rediss")
            {
                var tls = new SslStream(stream, false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, cancellationToken);
                stream = tls;
            }
            var connection = new Connection(socket, new BufferedStream(stream,16384));
            if (uri.UserInfo.Length > 0)
            {
                var credentials = uri.UserInfo.Split(':', 2).Select(Uri.UnescapeDataString).ToArray();
                await connection.Write(credentials.Length == 2 && credentials[0].Length > 0 ? ["AUTH", credentials[0], credentials[1]] : ["AUTH", credentials[^1]], cancellationToken);
                await connection.Read(cancellationToken);
            }
            if (int.TryParse(uri.AbsolutePath.Trim('/'), out var database) && database != 0)
            {
                await connection.Write(["SELECT", database.ToString(CultureInfo.InvariantCulture)], cancellationToken);
                await connection.Read(cancellationToken);
            }
            return connection;
        }
        catch { socket.Dispose(); throw; }
    }
    private sealed record Reply(string? Value, string?[]? Array = null);
    private sealed class Connection(TcpClient socket, Stream stream) : IAsyncDisposable
    {
        private readonly byte[] single = new byte[1];
        public Task Write(string[] command,CancellationToken cancellationToken)=>Write([command],cancellationToken);
        public async Task Write(IReadOnlyList<string[]> commands, CancellationToken cancellationToken)
        {
            using var bytes = new MemoryStream();
            void Text(string value) => bytes.Write(Encoding.UTF8.GetBytes(value));
            foreach(var command in commands)
            {
                Text("*" + command.Length.ToString(CultureInfo.InvariantCulture) + "\r\n");
                foreach (var argument in command)
                {
                    var raw = Encoding.UTF8.GetBytes(argument);
                    Text("$" + raw.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"); bytes.Write(raw); Text("\r\n");
                }
            }
            await stream.WriteAsync(bytes.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        private async Task<int> Byte(CancellationToken cancellationToken)
        {
            if (await stream.ReadAsync(single, cancellationToken) == 0) throw new IOException("Redis disconnected");
            return single[0];
        }
        private async Task<string> Line(CancellationToken cancellationToken)
        {
            using var bytes = new MemoryStream();
            int next;
            while ((next = await Byte(cancellationToken)) != '\r')
            {
                if (bytes.Length > 1048576) throw new IOException("Redis reply line too long");
                bytes.WriteByte((byte)next);
            }
            if (await Byte(cancellationToken) != '\n') throw new IOException("Invalid Redis reply");
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        public async Task<Reply> Read(CancellationToken cancellationToken)
        {
            var type = await Byte(cancellationToken);
            var line = await Line(cancellationToken);
            if (type == '-') throw new IOException("Redis command failed: " + line);
            if (type is '+' or ':') return new(line);
            var length = int.Parse(line, CultureInfo.InvariantCulture);
            if (length == -1) return new(null);
            if (length is < 0 or > 67108864) throw new IOException("Invalid Redis reply length");
            if (type == '$')
            {
                var buffer = new byte[length]; await stream.ReadExactlyAsync(buffer, cancellationToken);
                if (await Byte(cancellationToken) != '\r' || await Byte(cancellationToken) != '\n') throw new IOException("Invalid Redis bulk reply");
                return new(Encoding.UTF8.GetString(buffer));
            }
            if (type == '*')
            {
                var values = new string?[length];
                for (var index = 0; index < length; index++) values[index] = (await Read(cancellationToken)).Value;
                return new(null, values);
            }
            throw new IOException("Unsupported Redis reply type");
        }
        public async ValueTask DisposeAsync() { await stream.DisposeAsync(); socket.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        var connections=new List<Connection>();
        lock(poolSync)
        {
            if(disposed!=0)return;
            Volatile.Write(ref disposed,1);
            while(idle.TryDequeue(out var connection))connections.Add(connection);
        }
        foreach(var connection in connections)await connection.DisposeAsync();
        // In-flight borrowers dispose their connection when returning. Keep
        // the gate alive until those calls release it.
    }
}
