using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Campfire.Features.Realtime;
using Campfire.Contracts;

namespace Campfire.Features.Integrations;

// Preserve Resque's JobWrapper wire format so pending Rails jobs can be read
// after migration and newly queued jobs can still be consumed by Rails workers.
public sealed class RailsJobQueue(RedisTransport redis, IConfiguration configuration, ILogger<RailsJobQueue> logger) : BackgroundService, IBackgroundJobs
{
    private readonly string worker=Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string,Func<JsonElement[],CancellationToken,Task>> handlers=new();
    private readonly Channel<string> development=Channel.CreateUnbounded<string>();
    private readonly ConcurrentQueue<string> heldDevelopment=new();
    private const string Ready="resque:queue:default";
    public bool Enabled=>redis.Enabled;
    private string Processing=>redis.Prefix+":processing:"+worker;
    private string Workers=>redis.Prefix+":workers";
    private string Lease(string owner)=>redis.Prefix+":lease:"+owner;
    private const string ReserveScript = """
        redis.call('SADD',KEYS[1],ARGV[1]);
        redis.call('SET',KEYS[2],'1','PX',30000);
        while redis.call('LLEN',KEYS[3])>0 do redis.call('RPOPLPUSH',KEYS[3],KEYS[4]);end;
        if ARGV[3]=='1' then
          while redis.call('LLEN',KEYS[5])>0 do redis.call('LMOVE',KEYS[5],KEYS[4],'LEFT','RIGHT');end;
        end;
        for _,w in ipairs(redis.call('SMEMBERS',KEYS[1])) do
          if redis.call('EXISTS',ARGV[2]..':lease:'..w)==0 then
            local p=ARGV[2]..':processing:'..w;
            while redis.call('LLEN',p)>0 do redis.call('RPOPLPUSH',p,KEYS[4]);end;
            redis.call('SREM',KEYS[1],w);
          end;
        end;
        local jobs=redis.call('ZRANGEBYSCORE',KEYS[6],'-inf',ARGV[4]);
        for _,j in ipairs(jobs) do redis.call('ZREM',KEYS[6],j);redis.call('RPUSH',KEYS[4],j);end;
        return redis.call('LMOVE',KEYS[4],KEYS[3],'LEFT','RIGHT');
        """;
    private bool Deliver=>!string.Equals(configuration["CAMPFIRE_DELIVER_INTEGRATIONS"],"false",StringComparison.OrdinalIgnoreCase);
    private static bool Outbound(string payload)
    {
        try { using var parsed=JsonDocument.Parse(payload);var name=parsed.RootElement.GetProperty("args")[0].GetProperty("job_class").GetString();return name is "Bot::WebhookJob" or "Room::PushMessageJob"; }
        catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException){return false;}
    }
    public void Register(string jobClass,Func<JsonElement[],CancellationToken,Task> handle)=>handlers[jobClass]=handle;
    public static JsonObject GlobalId(string model,long id)=>new(){["_aj_globalid"]=$"gid://campfire/{model}/{id}"};
    public static long RecordId(JsonElement argument)
    {
        if(argument.ValueKind==JsonValueKind.Number)return argument.GetInt64();
        var gid=argument.GetProperty("_aj_globalid").GetString()!;
        return long.Parse(gid[(gid.LastIndexOf('/')+1)..],CultureInfo.InvariantCulture);
    }
    public async Task EnqueueAsync(string jobClass,JsonNode?[] arguments,CancellationToken cancellationToken=default)
    {
        var job=new JsonObject{["job_class"]=jobClass,["job_id"]=Guid.NewGuid().ToString(),["queue_name"]="default",["arguments"]=new JsonArray(arguments),["executions"]=0,["exception_executions"]=new JsonObject(),["locale"]="en",["timezone"]="UTC",["enqueued_at"]=DateTimeOffset.UtcNow.ToString("O")};
        var payload=new JsonObject{["class"]="ActiveJob::QueueAdapters::ResqueAdapter::JobWrapper",["args"]=new JsonArray(job)}.ToJsonString();
        // Preserve registration-before-enqueue failure semantics in one round
        // trip. Independent pipelined commands could enqueue after SADD fails.
        if(redis.Enabled)await redis.IntegerAsync(["EVAL","redis.call('SADD',KEYS[1],ARGV[1]);return redis.call('RPUSH',KEYS[2],ARGV[2])","2","resque:queues",Ready,"default",payload],cancellationToken);
        else await development.Writer.WriteAsync(payload,cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!redis.Enabled)
        {
            await foreach(var payload in development.Reader.ReadAllAsync(stoppingToken))
            {
                if(!Deliver&&Outbound(payload)){heldDevelopment.Enqueue(payload);continue;}
                if(Deliver)while(heldDevelopment.TryDequeue(out var held))await Run(held,stoppingToken);
                await Run(payload,stoppingToken);
            }
            return;
        }
        await redis.IntegerAsync(["SADD",Workers,worker],stoppingToken);
        try
        {
            while(!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // A lost acknowledgement can leave this worker's last item
                    // reserved even though its lease is healthy. No handler is
                    // running at this boundary; reclaim it with at-least-once
                    // semantics before reserving more work.
                    // One ordered Lua operation performs the same maintenance
                    // before reservation. A server error stops its suffix; an
                    // uncertain acknowledgement is never replayed by transport.
                    // The next loop reclaims our reservation before taking more.
                    var payload=await redis.StringAsync(["EVAL",ReserveScript,"6",Workers,Lease(worker),Processing,Ready,
                        redis.Prefix+":outbound-held",redis.Prefix+":scheduled",worker,redis.Prefix,Deliver?"1":"0",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)],stoppingToken);
                    if(payload is null){await Task.Delay(100,stoppingToken);continue;}
                    if(!Deliver&&Outbound(payload))
                    {
                        await redis.IntegerAsync(["EVAL","redis.call('RPUSH',KEYS[1],ARGV[1]);return redis.call('LREM',KEYS[2],1,ARGV[1])","2",redis.Prefix+":outbound-held",Processing,payload],stoppingToken);continue;
                    }
                    // Keep the lease live during long native media processing.
                    using var leaseCancellation=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var heartbeat=LeaseHeartbeat(leaseCancellation.Token);
                    try{await Run(payload,stoppingToken);await redis.IntegerAsync(["LREM",Processing,"1",payload],stoppingToken);}
                    finally{leaseCancellation.Cancel();try{await heartbeat;}catch(OperationCanceledException){}}
                }
                catch(Exception error) when(!stoppingToken.IsCancellationRequested){logger.LogError(error,"Durable job transport failed");await Task.Delay(500,stoppingToken);}
            }
        }
        finally
        {
            // Do not acknowledge unfinished work. Another worker recovers it after lease expiry.
            try{await redis.IntegerAsync(["DEL",Lease(worker)],CancellationToken.None);}catch(IOException){}
        }
    }
    private async Task LeaseHeartbeat(CancellationToken cancellationToken)
    {
        while(!cancellationToken.IsCancellationRequested){await Task.Delay(5000,cancellationToken);await RenewLease(cancellationToken);}
    }
    private async Task RenewLease(CancellationToken cancellationToken)
    {
        await redis.IntegerAsync(["EVAL","redis.call('SADD',KEYS[1],ARGV[1]);redis.call('SET',KEYS[2],'1','PX',30000);return 1","2",Workers,Lease(worker),worker],cancellationToken);
    }
    private async Task Run(string payload,CancellationToken cancellationToken)
    {
        var job=default(JsonElement);var name="MalformedJob";
        try
        {
            using var parsed=JsonDocument.Parse(payload);
            job=parsed.RootElement.GetProperty("args")[0].Clone();
            name=job.GetProperty("job_class").GetString()!;
            if(!handlers.TryGetValue(name,out var handler)) throw new InvalidOperationException("Unregistered Rails job "+name);
            await handler(job.GetProperty("arguments").EnumerateArray().Select(x=>x.Clone()).ToArray(),cancellationToken);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
        catch(Exception error)
        {
            // Original app jobs have no retry_on. Storage jobs retry integrity/
            // deadlock failures ten times; absent records are safely discarded.
            var attempt=job.ValueKind==JsonValueKind.Object&&job.TryGetProperty("executions",out var executed)?executed.GetInt32():0;
            var retriable=name switch
            {
                "ActiveStorage::AnalyzeJob" or "ActiveStorage::MirrorJob" or "ActiveStorage::TransformJob" or "ActiveStorage::PreviewImageJob" => error is InvalidDataException,
                "ActiveStorage::PurgeJob" => error is Microsoft.Data.Sqlite.SqliteException sqlite && sqlite.SqliteErrorCode==5,
                _ => false
            };
            if(redis.Enabled&&retriable&&attempt<9)
            {
                var wrapper=JsonNode.Parse(payload)!;wrapper["args"]![0]!["executions"]=attempt+1;
                var polynomial=Math.Pow(attempt+1,4);var delay=polynomial+2+Random.Shared.NextDouble()*polynomial*0.15;
                await redis.IntegerAsync(["ZADD",redis.Prefix+":scheduled",(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d+delay).ToString(CultureInfo.InvariantCulture),wrapper.ToJsonString()],cancellationToken);
            }
            else if(redis.Enabled)
            {
                JsonNode? original;try{original=JsonNode.Parse(payload);}catch(JsonException){original=JsonValue.Create(payload);}
                var failed=new JsonObject{["failed_at"]=DateTimeOffset.UtcNow.ToString("O"),["payload"]=original,["exception"]=error.GetType().Name,["error"]=error.Message,["backtrace"]=new JsonArray(error.StackTrace),["worker"]=worker,["queue"]="default"};
                await redis.IntegerAsync(["RPUSH","resque:failed",failed.ToJsonString()],cancellationToken);
            }
            logger.LogError(error,"Rails job {JobClass} failed at execution {Attempt}",name,attempt+1);
        }
    }
}
