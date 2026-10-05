using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.Contracts;
using Campfire.FeatureTests;
using Campfire.Features.Realtime;

FeatureHost.Check(!JsonSerializer.IsReflectionEnabledByDefault, "Realtime suite must disable JSON reflection");
await using var host = await FeatureHost.Start();
var realtime = host.App.Services.GetRequiredService<IRealtimeEvents>();
using var socket = new ClientWebSocket(); socket.Options.AddSubProtocol("actioncable-v1-json"); socket.Options.SetRequestHeader("Cookie", "test_user=1");
socket.Options.SetRequestHeader("Origin",host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
await socket.ConnectAsync(host.WebSocketUri, CancellationToken.None);
async Task<JsonElement> Packet(ClientWebSocket target)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var buffer = new byte[65536]; using var bytes = new MemoryStream(); WebSocketReceiveResult result;
    do { result = await target.ReceiveAsync(buffer, timeout.Token); if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Socket closed unexpectedly"); bytes.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
    using var parsed = JsonDocument.Parse(bytes.ToArray()); return parsed.RootElement.Clone();
}
async Task<JsonElement> Next(string expected)
{
    while (true) { var packet = await Packet(socket); if (packet.TryGetProperty("type", out var type) && type.GetString() == "ping") continue; if (expected == "message" ? packet.TryGetProperty("identifier", out _) && packet.TryGetProperty("message", out _) : packet.TryGetProperty("type", out var t) && t.GetString() == expected) return packet; throw new InvalidOperationException("Unexpected frame: " + packet); }
}
async Task Send(string command, string identifier, string? data = null) => await socket.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["command"] = command, ["identifier"] = identifier, ["data"] = data }.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
FeatureHost.Check((await Next("welcome")).GetProperty("type").GetString() == "welcome", "Missing Action Cable welcome");
var stream = Convert.ToBase64String(Encoding.UTF8.GetBytes("gid://campfire/Rooms::Closed/1")).TrimEnd('=') + ":messages";
var token = host.Crypto.SignStream(stream);
var guarded = new JsonObject { ["channel"] = "RoomMessagesChannel", ["signed_stream_name"] = token }.ToJsonString();
var bypass = new JsonObject { ["channel"] = "Turbo::StreamsChannel", ["signed_stream_name"] = token }.ToJsonString();
await Send("subscribe", bypass); await Next("reject_subscription");
await Send("subscribe", new JsonObject { ["channel"] = "RoomChannel", ["room_id"] = 2 }.ToJsonString()); await Next("reject_subscription");
await Send("subscribe", guarded); await Next("confirm_subscription");
await realtime.MessageChangedAsync(1, 1);
var append = (await Next("message")).GetProperty("message").GetString()!;
FeatureHost.Check(append.Contains("target=\"messages_rooms_closed_1\"") && append.Contains("Actual rendered message 1"), "Broadcast target/render mismatch");
var typing = new JsonObject { ["channel"] = "TypingNotificationsChannel", ["room_id"] = 1 }.ToJsonString();
await Send("subscribe", typing); await Next("confirm_subscription");
await Send("message", typing, "{\"action\":\"start\"}");
var typingPacket = (await Next("message")).GetProperty("message");
FeatureHost.Check(typingPacket.GetProperty("action").GetString() == "start" && typingPacket.GetProperty("user").GetProperty("id").GetInt64() == 1 && typingPacket.GetProperty("user").GetProperty("name").GetString() == "Alice", "Typing packet JSON shape mismatch");
var unread = new JsonObject { ["channel"] = "UnreadRoomsChannel" }.ToJsonString();
await Send("subscribe", unread); await Next("confirm_subscription");
await realtime.MessageChangedAsync(1, 1);
var firstUnread = (await Next("message")).GetProperty("message");
var secondUnread = (await Next("message")).GetProperty("message");
var unreadPayload = firstUnread.ValueKind == JsonValueKind.Object ? firstUnread : secondUnread;
FeatureHost.Check(unreadPayload.ValueKind == JsonValueKind.Object && unreadPayload.GetProperty("roomId").GetInt64() == 1, "Unread packet must retain camelCase roomId");
await Send("unsubscribe", unread);
await realtime.TurboStreamAsync(stream, "append", "messages_rooms_closed_1", "<p>Scroll</p>", true);
FeatureHost.Check((await Next("message")).GetProperty("message").GetString()!.Contains("maintain_scroll=\"true\""), "Frontend scroll attribute mismatch");
host.Db.Execute("DELETE FROM action_text_rich_texts WHERE record_id=1; DELETE FROM messages WHERE id=1");
await realtime.MessageChangedAsync(1, 1, "remove", "client-uuid");
FeatureHost.Check((await Next("message")).GetProperty("message").GetString()!.Contains("target=\"message_client-uuid\""), "Deleted message target must retain client UUID");
var read = new JsonObject { ["channel"] = "ReadRoomsChannel" }.ToJsonString();
await Send("subscribe", read); await Next("confirm_subscription");
var presence = new JsonObject { ["channel"] = "PresenceChannel", ["room_id"] = 1 }.ToJsonString();
await Send("subscribe", presence);
await Next("message"); await Next("confirm_subscription");
FeatureHost.Check(host.Db.Scalar<long>("SELECT connections FROM memberships WHERE user_id=1 AND room_id=1") == 1 && host.Db.Scalar<string?>("SELECT unread_at FROM memberships WHERE user_id=1 AND room_id=1") is null, "Presence must clear unread atomically");
await Send("message",presence,"{\"action\":\"present\"}");await Next("message");
FeatureHost.Check(host.Db.Scalar<long>("SELECT connections FROM memberships WHERE user_id=1 AND room_id=1")==2,"Explicit present must increment presence");
await Send("message",presence,"{\"action\":\"absent\"}");
await Send("subscribe",new JsonObject{["channel"]="HeartbeatChannel"}.ToJsonString());await Next("confirm_subscription");
FeatureHost.Check(host.Db.Scalar<long>("SELECT connections FROM memberships WHERE user_id=1 AND room_id=1")==1,"Explicit absent must decrement presence");
host.Db.Execute("UPDATE memberships SET connections=2,connected_at=@stale WHERE user_id=1 AND room_id=1",new{stale=DateTime.UtcNow.AddSeconds(-70).ToString("yyyy-MM-dd HH:mm:ss.ffffff")});
await Send("unsubscribe", presence);
await Send("subscribe", new JsonObject { ["channel"] = "HeartbeatChannel" }.ToJsonString()); await Next("confirm_subscription");
FeatureHost.Check(host.Db.Scalar<long>("SELECT connections FROM memberships WHERE user_id=1 AND room_id=1") == 0, "Unsubscribe leaked presence connection");
host.Db.Execute("DELETE FROM memberships WHERE user_id=1 AND room_id=1");
await Send("message", guarded, "{\"action\":\"refresh\"}");
FeatureHost.Check((await Next("disconnect")).GetProperty("reconnect").GetBoolean(), "Revocation must disconnect with reconnect");
using var replay = new ClientWebSocket(); replay.Options.AddSubProtocol("actioncable-v1-json"); replay.Options.SetRequestHeader("Cookie", "test_user=1");
replay.Options.SetRequestHeader("Origin",host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
await replay.ConnectAsync(host.WebSocketUri, CancellationToken.None); await Packet(replay);
await replay.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["command"] = "subscribe", ["identifier"] = guarded }.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
FeatureHost.Check((await Packet(replay)).GetProperty("type").GetString() == "reject_subscription", "Harvested room signature must fail after revocation");
await replay.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
Console.WriteLine("PASS realtime: Action Cable frames, guarded streams, membership isolation, actual Turbo DOM/scroll, deleted UUID, presence/read, revocation and replay");
await RedisPoolChecks.Run(Environment.GetEnvironmentVariable("CAMPFIRE_TEST_REDIS_URL"));

if(Environment.GetEnvironmentVariable("CAMPFIRE_TEST_REDIS_URL") is string redisUrl)
{
    var options=new Dictionary<string,string?>{["REDIS_URL"]=redisUrl,["CAMPFIRE_REDIS_PREFIX"]="parity-test-"+Guid.NewGuid().ToString("N"),["CAMPFIRE_CABLE_PREFIX"]="campfire_test"};
    await using var first=await FeatureHost.Start(configuration:options);await using var second=await FeatureHost.Start(configuration:options);
    var redis=first.App.Services.GetRequiredService<RedisTransport>();
    using var firstSocket=new ClientWebSocket();using var secondSocket=new ClientWebSocket();
    async Task Setup(ClientWebSocket target,FeatureHost application)
    {
        target.Options.SetRequestHeader("Cookie","test_user=1");target.Options.SetRequestHeader("Origin",application.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));target.Options.AddSubProtocol("actioncable-v1-json");await target.ConnectAsync(application.WebSocketUri,CancellationToken.None);await Packet(target);
        await target.SendAsync(Encoding.UTF8.GetBytes(new JsonObject{["command"]="subscribe",["identifier"]=typing}.ToJsonString()),WebSocketMessageType.Text,true,CancellationToken.None);FeatureHost.Check((await Packet(target)).GetProperty("type").GetString()=="confirm_subscription","Redis channel did not confirm");
    }
    async Task<JsonElement> NextMessage(ClientWebSocket target){while(true){var value=await Packet(target);if(value.TryGetProperty("message",out var message)&&value.TryGetProperty("identifier",out _))return message;}}
    await Setup(firstSocket,first);await Setup(secondSocket,second);
    await firstSocket.SendAsync(Encoding.UTF8.GetBytes(new JsonObject{["command"]="message",["identifier"]=typing,["data"]="{\"action\":\"start\"}"}.ToJsonString()),WebSocketMessageType.Text,true,CancellationToken.None);
    FeatureHost.Check((await NextMessage(firstSocket)).GetProperty("user").GetProperty("id").GetInt64()==1&&(await NextMessage(secondSocket)).GetProperty("user").GetProperty("id").GetInt64()==1,"Redis did not fan out typing across hosts");
    var roomGid=Convert.ToBase64String(Encoding.UTF8.GetBytes("gid://campfire/Rooms::Closed/1")).TrimEnd('=');
    await redis.IntegerAsync(["PUBLISH","campfire_test:typing_notifications:"+roomGid,"{\"action\":\"stop\",\"user\":{\"id\":1,\"name\":\"Rails wire\"}}"]);
    FeatureHost.Check((await NextMessage(firstSocket)).GetProperty("user").GetProperty("name").GetString()=="Rails wire"&&(await NextMessage(secondSocket)).GetProperty("user").GetProperty("name").GetString()=="Rails wire","Pinned Rails Redis channel/payload did not reach both hosts");
    foreach(var target in new[]{firstSocket,secondSocket})
    {
        await target.SendAsync(Encoding.UTF8.GetBytes(new JsonObject{["command"]="subscribe",["identifier"]=guarded}.ToJsonString()),WebSocketMessageType.Text,true,CancellationToken.None);
        FeatureHost.Check((await Packet(target)).GetProperty("type").GetString()=="confirm_subscription","Redis guarded channel did not confirm");
        await target.SendAsync(Encoding.UTF8.GetBytes(new JsonObject{["command"]="subscribe",["identifier"]=new JsonObject{["channel"]="UnreadRoomsChannel"}.ToJsonString()}.ToJsonString()),WebSocketMessageType.Text,true,CancellationToken.None);
        FeatureHost.Check((await Packet(target)).GetProperty("type").GetString()=="confirm_subscription","Redis unread channel did not confirm");
    }
    await first.App.Services.GetRequiredService<IRealtimeEvents>().MessageChangedAsync(1,1);
    foreach(var target in new[]{firstSocket,secondSocket})
    {
        var roomMessage=await NextMessage(target);var unreadMessage=await NextMessage(target);
        FeatureHost.Check(roomMessage.ValueKind==JsonValueKind.String&&roomMessage.GetString()!.Contains("target=\"messages_rooms_closed_1\"")&&unreadMessage.GetProperty("roomId").GetInt64()==1,"Pipelined broadcast lost original room-before-member ordering or scoped unread payload");
    }
    first.Db.Execute("DELETE FROM action_text_rich_texts WHERE record_id=1;DELETE FROM messages WHERE id=1");
    await first.App.Services.GetRequiredService<IRealtimeEvents>().MessageChangedAsync(1,1,"remove","redis-deleted-uuid");
    FeatureHost.Check((await NextMessage(firstSocket)).GetString()!.Contains("target=\"message_redis-deleted-uuid\"")&&(await NextMessage(secondSocket)).GetString()!.Contains("target=\"message_redis-deleted-uuid\""),"Redis remove queried an already-deleted creator or lost captured UUID");
    await second.App.Services.GetRequiredService<IRealtimeEvents>().DisconnectUserAsync(1,true);
    async Task<bool> Disconnected(ClientWebSocket target){while(true){var value=await Packet(target);if(value.TryGetProperty("type",out var type)&&type.GetString()=="disconnect")return value.GetProperty("reconnect").GetBoolean();}}
    FeatureHost.Check(await Disconnected(firstSocket)&&await Disconnected(secondSocket),"Remote disconnect did not fan out across hosts");

    var jobs=first.App.Services.GetRequiredService<Campfire.Features.Integrations.RailsJobQueue>();
    var internalDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var outboundDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    jobs.Register("Test::Internal",(args,ct)=>{internalDone.TrySetResult();return Task.CompletedTask;});jobs.Register("Bot::WebhookJob",(args,ct)=>{outboundDone.TrySetResult();return Task.CompletedTask;});
    // Stop the second worker so the controlled handlers own this queue.
    await second.App.Services.GetRequiredService<Campfire.Features.Integrations.RailsJobQueue>().StopAsync(CancellationToken.None);
    await redis.IntegerAsync(["DEL","resque:queues"]);await redis.StringAsync(["SET","resque:queues","injected wrong type"]);
    try{await jobs.EnqueueAsync("Test::Internal",[]);throw new InvalidOperationException("Queue registration failure was ignored");}catch(IOException){}
    FeatureHost.Check(await redis.IntegerAsync(["LLEN","resque:queue:default"])==0,"Failed queue registration still enqueued a job");
    await redis.IntegerAsync(["DEL","resque:queues"]);
    await jobs.EnqueueAsync("Bot::WebhookJob",[]);await jobs.EnqueueAsync("Test::Internal",[]);
    await internalDone.Task.WaitAsync(TimeSpan.FromSeconds(5));FeatureHost.Check(!outboundDone.Task.IsCompleted&&await redis.IntegerAsync(["LLEN",redis.Prefix+":outbound-held"])==1,"Disabled delivery blocked internal work or discarded outbound work");
    first.App.Configuration["CAMPFIRE_DELIVER_INTEGRATIONS"]="true";await outboundDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await redis.IntegerAsync(["RPUSH","resque:queue:default","not-json"]);
    async Task Until(Func<Task<bool>> predicate){var deadline=DateTime.UtcNow.AddSeconds(5);while(!await predicate()){if(DateTime.UtcNow>=deadline)throw new Exception("Redis condition timed out");await Task.Delay(25);}}
    await Until(async()=>await redis.IntegerAsync(["LLEN","resque:failed"])>=1);
    var failed=await redis.ArrayAsync(["LRANGE","resque:failed","0","-1"]);FeatureHost.Check(failed.Any(value=>value?.Contains("MalformedJob",StringComparison.Ordinal)==true||value?.Contains("JsonReaderException",StringComparison.Ordinal)==true),"Malformed imported job did not leave failure evidence");
    jobs.Register("ActiveStorage::AnalyzeJob",(args,ct)=>throw new InvalidDataException("injected integrity failure"));await jobs.EnqueueAsync("ActiveStorage::AnalyzeJob",[]);
    await Until(async()=>await redis.IntegerAsync(["ZCARD",redis.Prefix+":scheduled"])==1);
    var failureCount=await redis.IntegerAsync(["LLEN","resque:failed"]);
    jobs.Register("ActiveStorage::PurgeJob",(args,ct)=>throw new InvalidDataException("purge must not retry integrity"));await jobs.EnqueueAsync("ActiveStorage::PurgeJob",[]);
    await Until(async()=>await redis.IntegerAsync(["LLEN","resque:failed"])>failureCount);
    FeatureHost.Check(await redis.IntegerAsync(["ZCARD",redis.Prefix+":scheduled"])==1,"Purge incorrectly reused Analyze integrity retry policy");
    var recovered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);jobs.Register("Test::Recovered",(args,ct)=>{recovered.TrySetResult();return Task.CompletedTask;});
    var originalWire=new JsonObject{["class"]="ActiveJob::QueueAdapters::ResqueAdapter::JobWrapper",["args"]=new JsonArray(new JsonObject{["job_class"]="Test::Recovered",["arguments"]=new JsonArray(),["executions"]=0})}.ToJsonString();
    await redis.IntegerAsync(["RPUSH",redis.Prefix+":processing:crashed",originalWire]);await redis.IntegerAsync(["SADD",redis.Prefix+":workers","crashed"]);await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var futureDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    jobs.Register("Test::Future",(args,ct)=>{futureDone.TrySetResult();return Task.CompletedTask;});
    var futureWire="{\"class\":\"ActiveJob::QueueAdapters::ResqueAdapter::JobWrapper\",\"args\":[{\"job_class\":\"Test::Future\",\"arguments\":[]}] }";
    await redis.IntegerAsync(["ZADD",redis.Prefix+":scheduled",(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600).ToString(System.Globalization.CultureInfo.InvariantCulture),futureWire]);
    await Task.Delay(150);
    FeatureHost.Check(!futureDone.Task.IsCompleted,"Reservation executed a job before its scheduled time");
    await redis.IntegerAsync(["ZADD",redis.Prefix+":scheduled","0",futureWire]);
    await futureDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    FeatureHost.Check(await redis.StringAsync(["ZSCORE",redis.Prefix+":scheduled",futureWire]) is null,"Executed scheduled job remained scheduled");

    var liveDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    jobs.Register("Test::LiveReservation",(args,ct)=>{liveDone.TrySetResult();return Task.CompletedTask;});
    var liveWire="{\"class\":\"ActiveJob::QueueAdapters::ResqueAdapter::JobWrapper\",\"args\":[{\"job_class\":\"Test::LiveReservation\",\"arguments\":[]}] }";
    await redis.StringAsync(["SET",redis.Prefix+":lease:live-foreign","1","PX","30000"]);
    await redis.IntegerAsync(["RPUSH",redis.Prefix+":processing:live-foreign",liveWire]);
    await redis.IntegerAsync(["SADD",redis.Prefix+":workers","live-foreign"]);
    await Task.Delay(150);
    FeatureHost.Check(!liveDone.Task.IsCompleted&&await redis.IntegerAsync(["LLEN",redis.Prefix+":processing:live-foreign"])==1,"Reservation stole work from a live foreign worker");
    await redis.IntegerAsync(["DEL",redis.Prefix+":lease:live-foreign"]);
    await liveDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var faultCalls=0;var faultDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    jobs.Register("Test::ReservationFault",(args,ct)=>{Interlocked.Increment(ref faultCalls);faultDone.TrySetResult();return Task.CompletedTask;});
    await redis.IntegerAsync(["DEL",redis.Prefix+":scheduled"]);
    await redis.StringAsync(["SET",redis.Prefix+":scheduled","injected wrong type"]);
    await jobs.EnqueueAsync("Test::ReservationFault",[]);
    await Task.Delay(250);
    FeatureHost.Check(faultCalls==0&&await redis.IntegerAsync(["LLEN","resque:queue:default"])==1,"Maintenance failure still reserved or executed a queued job");
    await redis.IntegerAsync(["DEL",redis.Prefix+":scheduled"]);
    await faultDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(150);
    FeatureHost.Check(faultCalls==1,"Recovered reservation replayed a successfully handled job");
    var counter=redis.Prefix+":limit";var lua="local n=redis.call('INCR',KEYS[1]);if n==1 then redis.call('EXPIRE',KEYS[1],180);end;return n";
    for(var count=1;count<=3;count++)FeatureHost.Check(await redis.IntegerAsync(["EVAL",lua,"1",counter])==count,"Atomic shared rate limiter counter changed");
    FeatureHost.Check(await redis.IntegerAsync(["TTL",counter]) is >170 and <=180,"Fixed rate limiter TTL was not initialized");
    Console.WriteLine("PASS Redis: two-host Cable/Rails wire, shared revocation, disabled outbound/internal job execution, held replay, malformed failures, storage retry schedule, expired/live-worker recovery, due/future promotion, maintenance fault/recovery and atomic INCR/TTL");
}
