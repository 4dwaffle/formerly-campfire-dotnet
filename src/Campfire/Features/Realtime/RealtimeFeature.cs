using Campfire.Features.Persistence;
using Dapper;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Campfire.Contracts;

namespace Campfire.Features.Realtime;

public static class RealtimeFeature
{
    public static IServiceCollection AddRealtimeFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<RealtimeService>();
        services.AddSingleton<RedisTransport>();
        services.AddSingleton<IRedisTransport>(sp => sp.GetRequiredService<RedisTransport>());
        services.AddHostedService(sp => sp.GetRequiredService<RealtimeService>());
        services.AddSingleton<IRealtimeEvents>(sp => sp.GetRequiredService<RealtimeService>());
        return services;
    }
    public static WebApplication MapRealtimeFeature(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Map("/cable", (HttpContext context, RealtimeService service) => service.ConnectAsync(context));
        return app;
    }
}

public sealed class RealtimeService(IDataStore db, IRailsCrypto crypto, IServiceProvider services, RedisTransport redis, ILogger<RealtimeService> logger) : BackgroundService, IRealtimeEvents
{
    private readonly string instance = Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource subscribedToRedis=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, Connection> connections = new();
    private bool Active(long id) => db.Read(queryConnection1 => queryConnection1.ExecuteScalar<long>("SELECT count(*) FROM users WHERE id=@id AND status=0",new { id })) > 0;
    private bool Member(long userId, long roomId) => Active(userId) && db.Read(queryConnection2 => queryConnection2.ExecuteScalar<long>("SELECT count(*) FROM memberships WHERE user_id=@userId AND room_id=@roomId",new { userId, roomId })) > 0;

    public async Task ConnectAsync(HttpContext context)
    {
        var user = context.User();
        if (user is null) { context.Response.StatusCode = 401; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if(redis.Enabled)try{await subscribedToRedis.Task.WaitAsync(TimeSpan.FromSeconds(10),context.RequestAborted);}catch(TimeoutException){context.Response.StatusCode=503;return;}
        if (!Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) || !string.Equals(origin.GetLeftPart(UriPartial.Authority), context.Request.Scheme + "://" + context.Request.Host, StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 404; return; }
        var protocol = context.WebSockets.WebSocketRequestedProtocols.Contains("actioncable-v1-json") ? "actioncable-v1-json" : null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(protocol);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var connection = new Connection(user, socket, cancel, context.Request.Headers.Cookie.ToString());
        var key = Guid.NewGuid();
        connections[key] = connection;
        connection.Enqueue(new CableControl("welcome"), RealtimeJson.Default.CableControl);
        var sending = SendLoop(connection);
        var heartbeat = Heartbeat(connection);
        try
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !cancel.IsCancellationRequested)
            {
                using var bytes = new MemoryStream();
                WebSocketReceiveResult chunk;
                do
                {
                    chunk = await socket.ReceiveAsync(buffer, cancel.Token);
                    if (chunk.MessageType == WebSocketMessageType.Close) return;
                    if (chunk.MessageType != WebSocketMessageType.Text || bytes.Length + chunk.Count > 65536) { await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Invalid frame", CancellationToken.None); return; }
                    bytes.Write(buffer, 0, chunk.Count);
                } while (!chunk.EndOfMessage);
                try { using var doc = JsonDocument.Parse(bytes.ToArray()); await Receive(connection, doc.RootElement); }
                catch (Exception error) when (error is JsonException or InvalidOperationException) { logger.LogDebug("Ignoring malformed Action Cable frame"); }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
        finally
        {
            connections.TryRemove(key, out _);
            foreach (var subscription in connection.Subscriptions.Values) Absent(connection.User.Id, subscription);
            cancel.Cancel();
            connection.Queue.Writer.TryComplete();
            try { await Task.WhenAll(sending, heartbeat); } catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
        }
    }

    private async Task Receive(Connection connection, JsonElement frame)
    {
        if (!Authenticated(connection)) { await Disconnect(connection, false); return; }
        if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String) return;
        if (!frame.TryGetProperty("identifier", out var identifierValue) || identifierValue.ValueKind != JsonValueKind.String) return;
        var identifier = identifierValue.GetString()!;
        if (command.GetString() == "unsubscribe")
        {
            if (connection.Subscriptions.TryRemove(identifier, out var removed)) Absent(connection.User.Id, removed);
            return;
        }
        if (command.GetString() == "subscribe")
        {
            using var parsed = JsonDocument.Parse(identifier);
            var data = parsed.RootElement;
            if (!data.TryGetProperty("channel", out var channelValue)) return;
            var channel = channelValue.GetString() ?? "";
            long room = 0;
            string? signedStream = null;
            var allowed = channel is "HeartbeatChannel" or "ReadRoomsChannel" or "UnreadRoomsChannel";
            if (channel is "RoomChannel" or "PresenceChannel" or "TypingNotificationsChannel")
            {
                if (data.TryGetProperty("room_id", out var roomValue)) long.TryParse(roomValue.ToString(), out room);
                allowed = Member(connection.User.Id, room);
            }
            if (channel is "RoomMessagesChannel" or "Turbo::StreamsChannel")
            {
                var stream = data.TryGetProperty("signed_stream_name", out var token) ? crypto.VerifyStream(token.GetString() ?? "") : null;
                signedStream = stream;
                room = StreamRoom(stream);
                allowed = channel == "RoomMessagesChannel" && room > 0 && Member(connection.User.Id, room) && RoomTypeMatches(stream!, room);
                // Stock Turbo subscriptions must never bypass the guarded room channel.
                if (channel == "Turbo::StreamsChannel" && stream is not null && !stream.EndsWith(":messages", StringComparison.Ordinal)) allowed = true;
            }
            if (!allowed) { connection.Enqueue(new CableControl("reject_subscription", identifier), RealtimeJson.Default.CableControl); return; }
            var subscription = new Subscription(identifier, channel, room, signedStream);
            if (connection.Subscriptions.TryAdd(identifier, subscription) && channel == "PresenceChannel")
            {
                db.Write((queryConnection3,queryTransaction3) => queryConnection3.Execute("UPDATE memberships SET connections=CASE WHEN connected_at >= @cutoff THEN connections+1 ELSE 1 END,connected_at=@now,unread_at=NULL WHERE user_id=@userId AND room_id=@room",new { cutoff = DateTime.UtcNow.AddSeconds(-60).ToString("yyyy-MM-dd HH:mm:ss.ffffff"), now = RequestUser.Timestamp(), userId = connection.User.Id, room },transaction: queryTransaction3));
                await Broadcast("ReadRoomsChannel", 0, connection.User.Id, new ReadRoom(room), RealtimeJson.Default.CableMessageReadRoom);
            }
            connection.Enqueue(new CableControl("confirm_subscription", identifier), RealtimeJson.Default.CableControl);
            return;
        }
        if (command.GetString() == "message" && connection.Subscriptions.TryGetValue(identifier, out var subscribed) && frame.TryGetProperty("data", out var actionData))
        {
            if (subscribed.RoomId > 0 && !Member(connection.User.Id, subscribed.RoomId)) { await Disconnect(connection, true); return; }
            using var actionDoc = JsonDocument.Parse(actionData.GetString() ?? "{}");
            var action = actionDoc.RootElement.TryGetProperty("action", out var actionValue) ? actionValue.GetString() : null;
            if (subscribed.Channel == "TypingNotificationsChannel" && action is "start" or "stop")
            {
                var name = db.Read(queryConnection4 => queryConnection4.ExecuteScalar<string>("SELECT name FROM users WHERE id=@id",new { id = connection.User.Id })) ?? connection.User.Name;
                await Broadcast(subscribed.Channel, subscribed.RoomId, 0, new TypingNotification(action, new TypingUser(connection.User.Id, name ?? "")), RealtimeJson.Default.CableMessageTypingNotification);
            }
            else if (subscribed.Channel == "PresenceChannel" && action == "absent") Absent(connection.User.Id, subscribed);
            else if (subscribed.Channel == "PresenceChannel" && action == "present")
            {
                db.Write((c,t) => c.Execute("UPDATE memberships SET connections=CASE WHEN connected_at>=@cutoff THEN connections+1 ELSE 1 END,connected_at=@now,unread_at=NULL WHERE user_id=@user AND room_id=@room", new { cutoff = DateTime.UtcNow.AddSeconds(-60).ToString("yyyy-MM-dd HH:mm:ss.ffffff"), now = RequestUser.Timestamp(), user=connection.User.Id, room=subscribed.RoomId }, t));
                await Broadcast("ReadRoomsChannel",0,connection.User.Id,new ReadRoom(subscribed.RoomId),RealtimeJson.Default.CableMessageReadRoom);
            }
            else if (subscribed.Channel == "PresenceChannel" && action == "refresh")
                db.Write((queryConnection5,queryTransaction5) => queryConnection5.Execute("UPDATE memberships SET connections=CASE WHEN connected_at >= @cutoff THEN connections ELSE 1 END,connected_at=@now WHERE user_id=@userId AND room_id=@room",new { cutoff = DateTime.UtcNow.AddSeconds(-60).ToString("yyyy-MM-dd HH:mm:ss.ffffff"), now = RequestUser.Timestamp(), userId = connection.User.Id, room = subscribed.RoomId },transaction: queryTransaction5));
        }
    }

    public static string UserStream(long id, string? suffix) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"gid://campfire/User/{id}")).TrimEnd('=') + (suffix is null ? "" : ":" + suffix);
    public static long StreamRoom(string? stream)
    {
        if (stream is null || !stream.EndsWith(":messages", StringComparison.Ordinal)) return 0;
        try
        {
            var encoded = stream[..^9];
            var gid = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4)));
            var prefix = "gid://campfire/Rooms::";
            if (!gid.StartsWith(prefix, StringComparison.Ordinal)) return 0;
            var slash = gid.LastIndexOf('/');
            if (slash < 0 || gid[prefix.Length..slash] is not ("Open" or "Closed" or "Direct")) return 0;
            return long.TryParse(gid[(slash + 1)..], out var room) && room > 0 ? room : 0;
        }
        catch (FormatException) { return 0; }
    }
    private bool RoomTypeMatches(string stream, long room)
    {
        var encoded = stream[..^9];
        var gid = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4-encoded.Length%4)%4)));
        var type = db.Read(c => c.ExecuteScalar<string?>("SELECT type FROM rooms WHERE id=@room",new { room }));
        return gid == $"gid://campfire/{type}/{room}";
    }
    private void Absent(long user, Subscription subscription)
    {
        if (subscription.Channel != "PresenceChannel") return;
        db.Write((queryConnection6,queryTransaction6) => queryConnection6.Execute("UPDATE memberships SET connections=CASE WHEN connected_at>=@cutoff THEN max(connections-1,0) ELSE 0 END,connected_at=CASE WHEN connected_at<@cutoff OR connected_at IS NULL OR connections<=1 THEN NULL ELSE connected_at END,updated_at=@now WHERE user_id=@user AND room_id=@room",new { cutoff=DateTime.UtcNow.AddSeconds(-60).ToString("yyyy-MM-dd HH:mm:ss.ffffff"), now = RequestUser.Timestamp(), user, room = subscription.RoomId },transaction: queryTransaction6));
    }
    private Task Broadcast<T>(string channel, long room, long user, T payload, JsonTypeInfo<CableMessage<T>> typeInfo)
    {
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(new CableMessage<T>("",payload), typeInfo));
        return Publish(new RealtimeEvent("channel",instance,room,user,Action:channel,Payload:serialized.RootElement.GetProperty("message").Clone()));
    }
    private void BroadcastLocal<T>(string channel, long room, long user, T payload, JsonTypeInfo<CableMessage<T>> typeInfo)
    {
        foreach (var connection in connections.Values)
        {
            if (user != 0 && connection.User.Id != user) continue;
            if (!Authenticated(connection)) continue;
            foreach (var subscription in connection.Subscriptions.Values)
                if (subscription.Channel == channel && subscription.RoomId == room && (room == 0 || Member(connection.User.Id, room)))
                    connection.Enqueue(new CableMessage<T>(subscription.Identifier, payload), typeInfo);
        }
    }
    public Task TurboStreamAsync(string stream, string action, string target, string? html = null, bool maintainScroll = false)
        => Publish(new RealtimeEvent("turbo",instance,Action:action,Target:target,Html:html,Stream:stream,Flag:maintainScroll));
    private Task TurboLocal(string stream, string action, string target, string? html, bool maintainScroll)
    {
        var room = StreamRoom(stream);
        var encodedAction = System.Net.WebUtility.HtmlEncode(action);
        var encodedTarget = System.Net.WebUtility.HtmlEncode(target);
        var payload = $"<turbo-stream action=\"{encodedAction}\" target=\"{encodedTarget}\"{(maintainScroll ? " maintain_scroll=\"true\"" : "")}>{(html is null ? "" : "<template>" + html + "</template>")}</turbo-stream>";
        foreach (var connection in connections.Values)
        {
            if (!Authenticated(connection) || room > 0 && !Member(connection.User.Id, room)) continue;
            foreach (var subscription in connection.Subscriptions.Values)
                if (subscription.Channel == "RoomMessagesChannel" && subscription.RoomId == room && room > 0 || subscription.Channel == "Turbo::StreamsChannel" && subscription.Stream == stream)
                    connection.Enqueue(new CableMessage<string>(subscription.Identifier, payload), RealtimeJson.Default.CableMessageString);
        }
        return Task.CompletedTask;
    }
    public Task MessageChangedAsync(long roomId, long messageId, string action = "append", string? clientMessageId = null)
        => Publish(new RealtimeEvent("message",instance,roomId,Message:messageId,Action:action,ClientId:clientMessageId));
    private Task MessageLocal(long roomId, long messageId, string action, string? clientMessageId)
    {
        var clientId = clientMessageId ?? db.Read(queryConnection7 => queryConnection7.ExecuteScalar<string?>("SELECT client_message_id FROM messages WHERE id=@messageId",new { messageId })) ?? messageId.ToString();
        var roomType = db.Read(queryConnection8 => queryConnection8.ExecuteScalar<string?>("SELECT type FROM rooms WHERE id=@roomId",new { roomId }));
        var prefix = roomType switch { "Rooms::Closed" => "rooms_closed", "Rooms::Direct" => "rooms_direct", _ => "rooms_open" };
        foreach (var connection in connections.Values)
        {
            if (!Authenticated(connection) || !Member(connection.User.Id, roomId)) continue;
            foreach (var subscription in connection.Subscriptions.Values.Where(x => x.Channel == "RoomMessagesChannel" && x.RoomId == roomId))
            {
                var target = action == "append" ? $"messages_{prefix}_{roomId}" : $"message_{clientId}";
                var template = action == "remove" ? "" : "<template>" + services.GetRequiredService<IChatRenderer>().MessageHtml(messageId, connection.User.Id) + "</template>";
                connection.Enqueue(new CableMessage<string>(subscription.Identifier, $"<turbo-stream action=\"{System.Net.WebUtility.HtmlEncode(action)}\" target=\"{System.Net.WebUtility.HtmlEncode(target)}\">{template}</turbo-stream>"), RealtimeJson.Default.CableMessageString);
            }
        }
        if (action == "append")
            foreach (var user in db.Read(queryConnection9 => queryConnection9.Query<long>("SELECT user_id FROM memberships WHERE room_id=@roomId",new { roomId }).Materialize())) BroadcastLocal("UnreadRoomsChannel", 0, user, new UnreadRoom(roomId), RealtimeJson.Default.CableMessageUnreadRoom);
        return Task.CompletedTask;
    }
    public Task DisconnectUserAsync(long userId, bool reconnect = false) => Publish(new RealtimeEvent("disconnect",instance,User:userId,Flag:reconnect));
    private async Task DisconnectLocal(long userId, bool reconnect)
    {
        foreach (var connection in connections.Values.Where(x => x.User.Id == userId)) await Disconnect(connection, reconnect);
    }
    private async Task Publish(RealtimeEvent value)
    {
        if(!redis.Enabled){await Apply(value);return;}
        async Task Send(string stream,string packet)=>await redis.IntegerAsync(["PUBLISH",redis.CablePrefix+":"+stream,packet]);
        switch(value.Kind)
        {
            case "channel":
                var name=value.Action=="ReadRoomsChannel"?$"user_{value.User}_reads":value.Action=="UnreadRoomsChannel"?$"user_{value.User}_unreads":ChannelStream(value.Action!,value.Room);
                await Send(name,value.Payload!.Value.GetRawText());break;
            case "turbo":await Send(value.Stream!,JsonSerializer.Serialize(TurboMarkup(value.Action!,value.Target!,value.Html,value.Flag),RealtimeJson.Default.String));break;
            case "disconnect":await Send("action_cable/"+UserStream(value.User,null),JsonSerializer.Serialize(new CableControl("disconnect",reconnect:value.Flag),RealtimeJson.Default.CableControl));break;
            case "message":
                var roomType=db.Read(c=>c.ExecuteScalar<string?>("SELECT type FROM rooms WHERE id=@room",new{room=value.Room}));
                var prefix=roomType switch{"Rooms::Closed"=>"rooms_closed","Rooms::Direct"=>"rooms_direct",_=>"rooms_open"};
                var client=value.ClientId??db.Read(c=>c.ExecuteScalar<string?>("SELECT client_message_id FROM messages WHERE id=@id",new{id=value.Message}))??value.Message.ToString();
                var target=value.Action=="append"?$"messages_{prefix}_{value.Room}":"message_"+client;
                string? html=null;
                if(value.Action!="remove")
                {
                    var creator=db.Read(c=>c.ExecuteScalar<long>("SELECT creator_id FROM messages WHERE id=@id",new{id=value.Message}));
                    html=services.GetRequiredService<IChatRenderer>().BroadcastMessageHtml(value.Message,creator);
                }
                List<string[]> commands=[["PUBLISH",redis.CablePrefix+":"+RoomStream(roomType,value.Room)+":messages",JsonSerializer.Serialize(TurboMarkup(value.Action!,target,html,false),RealtimeJson.Default.String)]];
                if(value.Action=="append")
                {
                    var unread=JsonSerializer.Serialize(new UnreadRoom(value.Room),RealtimeJson.Default.UnreadRoom);
                    foreach(var user in db.Read(c=>c.Query<long>("SELECT user_id FROM memberships WHERE room_id=@room",new{room=value.Room}).Materialize()))commands.Add(["PUBLISH",$"{redis.CablePrefix}:user_{user}_unreads",unread]);
                }
                // One bounded Lua loop preserves room-before-unread ordering
                // and stops at the first Redis error, like sequential publish.
                foreach(var batch in commands.Chunk(64))await redis.PublishBatchAsync(batch);
                break;
        }
    }
    private string RoomStream(long room)
    {
        var type=db.Read(c=>c.ExecuteScalar<string?>("SELECT type FROM rooms WHERE id=@room",new{room}));return RoomStream(type,room);
    }
    private static string RoomStream(string? type,long room)=>Convert.ToBase64String(Encoding.UTF8.GetBytes($"gid://campfire/{type}/{room}")).TrimEnd('=');
    private string ChannelStream(string channel,long room)=>channel switch{"RoomChannel"=>"room","PresenceChannel"=>"presence","TypingNotificationsChannel"=>"typing_notifications",_=>throw new ArgumentException("Unknown broadcast channel")}+":"+RoomStream(room);
    private static string TurboMarkup(string action,string target,string? html,bool maintainScroll)=>$"<turbo-stream action=\"{System.Net.WebUtility.HtmlEncode(action)}\" target=\"{System.Net.WebUtility.HtmlEncode(target)}\"{(maintainScroll?" maintain_scroll=\"true\"":"")}>{(html is null?"":"<template>"+html+"</template>")}</turbo-stream>";
    private async Task ReceiveRedis(string channel,string packet)
    {
        var stream=channel[(redis.CablePrefix.Length+1)..];
        using var parsed=JsonDocument.Parse(packet);
        foreach(var connection in connections.Values)
        {
            if(stream=="action_cable/"+UserStream(connection.User.Id,null)){if(parsed.RootElement.TryGetProperty("type",out var type)&&type.GetString()=="disconnect")await Disconnect(connection,!parsed.RootElement.TryGetProperty("reconnect",out var reconnect)||reconnect.GetBoolean());continue;}
            if(!Authenticated(connection))continue;
            foreach(var subscription in connection.Subscriptions.Values)
            {
                if(subscription.RoomId>0&&!Member(connection.User.Id,subscription.RoomId))continue;
                var expected=subscription.Channel switch{"ReadRoomsChannel"=>$"user_{connection.User.Id}_reads","UnreadRoomsChannel"=>$"user_{connection.User.Id}_unreads","RoomMessagesChannel" or "Turbo::StreamsChannel"=>subscription.Stream,"RoomChannel" or "PresenceChannel" or "TypingNotificationsChannel"=>ChannelStream(subscription.Channel,subscription.RoomId),_=>null};
                if(expected==stream)connection.Enqueue(new CableMessage<JsonElement>(subscription.Identifier,parsed.RootElement),RealtimeJson.Default.CableMessageJsonElement);
            }
        }
    }
    private Task Apply(RealtimeEvent value)
    {
        switch(value.Kind)
        {
            case "channel": BroadcastLocal(value.Action!,value.Room,value.User,value.Payload!.Value,RealtimeJson.Default.CableMessageJsonElement); return Task.CompletedTask;
            case "message": return MessageLocal(value.Room,value.Message,value.Action!,value.ClientId);
            case "turbo": return TurboLocal(value.Stream!,value.Action!,value.Target!,value.Html,value.Flag);
            case "disconnect": return DisconnectLocal(value.User,value.Flag);
            default: return Task.CompletedTask;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!redis.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await redis.PatternSubscribeAsync(redis.CablePrefix+":*",ReceiveRedis,stoppingToken,()=>subscribedToRedis.TrySetResult()); }
            catch(Exception error) when(!stoppingToken.IsCancellationRequested) { logger.LogError(error,"Redis Cable subscription interrupted"); await Task.Delay(500,stoppingToken); }
        }
    }
    private static async Task Disconnect(Connection connection, bool reconnect)
    {
        connection.Enqueue(new CableControl("disconnect", reason: "remote", reconnect: reconnect), RealtimeJson.Default.CableControl);
        connection.Queue.Writer.TryComplete();
        try { await connection.Drained.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { connection.Cancel.Cancel();return; }
        // Cancelling a pending receive immediately aborts the TCP connection
        // and can discard the disconnect frame just written. Send a close
        // frame first, allowing the peer to read both frames and acknowledge.
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if(connection.Socket.State==WebSocketState.Open)await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"",timeout.Token);
            connection.Cancel.CancelAfter(TimeSpan.FromSeconds(2));
        }
        catch(Exception error)when(error is WebSocketException or OperationCanceledException){connection.Cancel.Cancel();}
    }
    private async Task Heartbeat(Connection connection)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(connection.Cancel.Token))
        {
            if (!Authenticated(connection) || connection.Subscriptions.Values.Any(x => x.RoomId > 0 && !Member(connection.User.Id, x.RoomId))) { await Disconnect(connection, true); return; }
            connection.Enqueue(new CablePing("ping", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), RealtimeJson.Default.CablePing);
        }
    }
    private async Task SendLoop(Connection connection)
    {
        try
        {
            await foreach (var packet in connection.Queue.Reader.ReadAllAsync(connection.Cancel.Token))
            {
                using var doc = JsonDocument.Parse(packet);
                if (doc.RootElement.TryGetProperty("message", out _) && (!Authenticated(connection) || doc.RootElement.TryGetProperty("identifier", out var identifier) && connection.Subscriptions.TryGetValue(identifier.GetString()!, out var subscription) && subscription.RoomId > 0 && !Member(connection.User.Id, subscription.RoomId)))
                {
                    await connection.Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new CableControl("disconnect", reason: "remote", reconnect: true), RealtimeJson.Default.CableControl), WebSocketMessageType.Text, true, connection.Cancel.Token);
                    connection.Cancel.Cancel();
                    return;
                }
                await connection.Socket.SendAsync(packet, WebSocketMessageType.Text, true, connection.Cancel.Token);
            }
        }
        finally { connection.Drained.TrySetResult(); }
    }
    private bool Authenticated(Connection connection)
    {
        if (!Active(connection.User.Id)) return false;
        // Each lookup uses a fresh context, so an already-open socket cannot keep a cached
        // authenticated user after its browser session is deleted.
        var current = new DefaultHttpContext { RequestServices = services };
        current.Request.Headers.Cookie = connection.Cookie;
        return services.GetRequiredService<IAuthService>().Current(current)?.Id == connection.User.Id;
    }
    private sealed record Subscription(string Identifier, string Channel, long RoomId, string? Stream = null);
    private sealed class Connection(UserRecord user, WebSocket socket, CancellationTokenSource cancel, string cookie)
    {
        public UserRecord User { get; } = user;
        public WebSocket Socket { get; } = socket;
        public CancellationTokenSource Cancel { get; } = cancel;
        public string Cookie { get; } = cookie;
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, Subscription> Subscriptions { get; } = new();
        public Channel<byte[]> Queue { get; } = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        public void Enqueue<T>(T value, JsonTypeInfo<T> typeInfo) { if (!Queue.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo))) Cancel.Cancel(); }
    }
}
