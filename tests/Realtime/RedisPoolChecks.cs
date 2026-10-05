using System.Net;
using System.Net.Sockets;
using System.Text;
using Campfire.Features.Realtime;
using Campfire.FeatureTests;

internal static class RedisPoolChecks
{
    public static async Task Run(string? redisUrl)
    {
        if(redisUrl is not null)
        {
            await using var redis=new RedisTransport(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["REDIS_URL"]=redisUrl,["CAMPFIRE_REDIS_POOL_SIZE"]="4"}).Build());
            var results=await Task.WhenAll(Enumerable.Range(0,64).Select(async index=>
            {
                var value=index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var echo=await redis.ArrayAsync(["EVAL","return {ARGV[1],ARGV[2]}","0",value,"reply-"+value]);
                FeatureHost.Check(echo.SequenceEqual(new[]{value,"reply-"+value}),"Parallel pooled replies were interleaved");
                return await redis.IntegerAsync(["CLIENT","ID"]);
            }));
            FeatureHost.Check(results.Distinct().Count() is >=1 and <=4,"Command pool exceeded its configured connection bound");
            var one=await redis.IntegerAsync(["CLIENT","ID"]);var two=await redis.IntegerAsync(["CLIENT","ID"]);
            FeatureHost.Check(results.Contains(one)&&results.Contains(two),"Healthy sockets were not reused");
            using var cancellation=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try{await redis.ArrayAsync(["BLPOP","pool-test-"+Guid.NewGuid().ToString("N"),"0"],cancellation.Token);throw new InvalidOperationException("Blocking Redis command ignored cancellation");}catch(OperationCanceledException){}
            FeatureHost.Check(await redis.StringAsync(["PING"])=="PONG","Cancelled connection poisoned the pool");
            var prefix="pipeline-"+Guid.NewGuid().ToString("N");
            await Task.WhenAll(Enumerable.Range(0,64).Select(async index=>
            {
                var key=prefix+index;
                var replies=await redis.IntegersAsync([["INCR",key],["INCR",key],["INCR",key]]);
                FeatureHost.Check(replies.SequenceEqual(new long[]{1,2,3}),"Pipelined replies lost command order or crossed borrowers");
            }));
            using var batchCancellation=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try{await redis.IntegersAsync([["INCR",prefix+"cancelled"],["BLPOP",prefix+"block","0"]],batchCancellation.Token);throw new InvalidOperationException("Blocking pipeline ignored cancellation");}catch(OperationCanceledException){}
            FeatureHost.Check(await redis.IntegerAsync(["GET",prefix+"cancelled"])==1&&await redis.StringAsync(["PING"])=="PONG","Cancelled pipeline replayed its applied prefix or poisoned the pool");
            try{await redis.IntegersAsync([["INCR",prefix+"error"],["NOT_A_REDIS_COMMAND"],["INCR",prefix+"error"]]);throw new InvalidOperationException("Pipeline command error was hidden");}catch(IOException){}
            FeatureHost.Check(await redis.IntegerAsync(["GET",prefix+"error"])==2&&await redis.StringAsync(["PING"])=="PONG","Failed pipeline was replayed or returned a socket with unread replies");
            var username="publish-"+Guid.NewGuid().ToString("N");var password=Guid.NewGuid().ToString("N");
            await redis.StringAsync(["ACL","SETUSER",username,"on",">"+password,"-@all","+select","+publish","+eval","resetkeys","resetchannels","&"+prefix+"first","&"+prefix+"third"]);
            var url=new UriBuilder(redisUrl){UserName=username,Password=password}.Uri.ToString();
            using var subscriberCancellation=new CancellationTokenSource();
            var subscribed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var received=System.Threading.Channels.Channel.CreateUnbounded<string>();
            var subscription=redis.PatternSubscribeAsync(prefix+"*",(channel,payload)=>{received.Writer.TryWrite(payload);return Task.CompletedTask;},subscriberCancellation.Token,()=>subscribed.TrySetResult());
            try
            {
                await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await using var restricted=new RedisTransport(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["REDIS_URL"]=url}).Build());
                try{await restricted.PublishBatchAsync([["PUBLISH",prefix+"first","prefix"],["PUBLISH",prefix+"denied","forbidden"],["PUBLISH",prefix+"third","suffix"]]);throw new InvalidOperationException("Publication ACL error was hidden");}catch(IOException){}
                await redis.PublishBatchAsync([["PUBLISH",prefix+"first","sentinel"]]);
                var payloads=new List<string>();
                while(true){var payload=await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));if(payload=="sentinel")break;payloads.Add(payload);}
                FeatureHost.Check(payloads.SequenceEqual(new[]{"prefix"}),"Publication Lua executed the forbidden suffix after a middle ACL error");
            }
            finally
            {
                subscriberCancellation.Cancel();try{await subscription;}catch(OperationCanceledException){}
                await redis.IntegerAsync(["ACL","DELUSER",username]);
            }
        }
        // Fake peer applies a non-idempotent command and then loses its reply.
        // The caller must receive the uncertainty; the transport must not replay.
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        using var stopping=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var applied=0;var published=0;var auth=0;var select=0;var accepted=0;
        var server=Task.Run(async()=>
        {
            try
            {
                while(!stopping.IsCancellationRequested)
                {
                    using var client=await listener.AcceptTcpClientAsync(stopping.Token);Interlocked.Increment(ref accepted);
                    await using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.UTF8,false,1024,true);
                    while(!stopping.IsCancellationRequested)
                    {
                        var count=await reader.ReadLineAsync(stopping.Token);if(count is null)break;
                        var arguments=new string[int.Parse(count[1..],System.Globalization.CultureInfo.InvariantCulture)];
                        for(var i=0;i<arguments.Length;i++){await reader.ReadLineAsync(stopping.Token);arguments[i]=(await reader.ReadLineAsync(stopping.Token))!;}
                        if(arguments[0]=="INCR")
                        {
                            var countApplied=Interlocked.Increment(ref applied);
                            if(accepted==1)break;
                            // Do not emit any reply until the second command
                            // arrives: this detects accidental sequential sends.
                            if(countApplied==2)continue;
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(":2\r\n"),stopping.Token);
                            break; // both writes applied, second reply lost
                        }
                        if(arguments[0]=="EVAL")
                        {
                            Interlocked.Add(ref published,(arguments.Length-3)/2);
                            break; // publication script applied, reply lost
                        }
                        if(arguments[0]=="AUTH")Interlocked.Increment(ref auth);
                        if(arguments[0]=="SELECT")Interlocked.Increment(ref select);
                        var reply=arguments[0]=="PING"?"+PONG\r\n":"+OK\r\n";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(reply),stopping.Token);
                    }
                }
            }
            catch(OperationCanceledException)when(stopping.IsCancellationRequested){}
            catch(SocketException)when(stopping.IsCancellationRequested){}
        });
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            await using var redis=new RedisTransport(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["REDIS_URL"]=$"redis://worker:secret@127.0.0.1:{port}/3",["CAMPFIRE_REDIS_POOL_SIZE"]="1"}).Build());
            try{await redis.IntegerAsync(["INCR","uncertain"]);throw new InvalidOperationException("Lost reply was reported as successful");}catch(IOException){}
            FeatureHost.Check(applied==1,"Non-idempotent command was replayed after uncertain delivery");
            try{await redis.IntegersAsync([["INCR","batch-first"],["INCR","batch-second"]]);throw new InvalidOperationException("Partially acknowledged pipeline was reported as successful");}catch(IOException){}
            FeatureHost.Check(applied==3,"Pipeline replayed writes after a lost reply or waited before sending the entire batch");
            try{await redis.PublishBatchAsync([["PUBLISH","first","payload1"],["PUBLISH","second","payload2"]]);throw new InvalidOperationException("Lost publication acknowledgement was reported as successful");}catch(IOException){}
            FeatureHost.Check(published==2,"Publication script was replayed after uncertain delivery");
            FeatureHost.Check(await redis.StringAsync(["PING"])=="PONG"&&await redis.StringAsync(["PING"])=="PONG","Pool failed to reconnect after dropped reply");
            FeatureHost.Check(accepted==4&&auth==4&&select==4,"AUTH/SELECT must run once per connection, not per command");
            try{await redis.IntegersAsync(Enumerable.Range(0,65).Select(_=>new[]{"INCR","oversized"}).ToArray());throw new InvalidOperationException("Unbounded pipeline was accepted");}catch(ArgumentOutOfRangeException){}
            FeatureHost.Check(applied==3&&accepted==4,"Oversized pipeline wrote to Redis before rejection");
        }
        finally{stopping.Cancel();listener.Stop();await server;}
        Console.WriteLine("PASS Redis pool/pipelines: bounded parallel ordered replies/reuse, cancellation/error recovery, one-time AUTH/SELECT and no replay after uncertain single/batched writes");
    }
}
