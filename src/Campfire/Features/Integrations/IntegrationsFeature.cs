using Campfire.Features.Persistence;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Campfire.Contracts;
using Campfire.Features.Chat;
using Dapper;
using Ganss.Xss;
using System.Text.Json.Nodes;

namespace Campfire.Features.Integrations;

public static class IntegrationsFeature
{
    public static IServiceCollection AddIntegrationsFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IntegrationService>();
        services.AddSingleton<RailsJobQueue>();
        services.AddSingleton<IBackgroundJobs>(sp=>sp.GetRequiredService<RailsJobQueue>());
        services.AddSingleton<IIntegrationEvents>(sp => sp.GetRequiredService<IntegrationService>());
        services.AddHostedService(sp => sp.GetRequiredService<IntegrationService>());
        services.AddHostedService(sp=>sp.GetRequiredService<RailsJobQueue>());
        services.AddSingleton<IPublicDocumentClient, PublicDocumentClient>();
        services.AddSingleton<OpenGraphService>();
        return services;
    }
    public static WebApplication MapIntegrationsFeature(this WebApplication app)
    {
        app.MapGet("/users/{userId}/push_subscriptions", (HttpContext context, IntegrationService service) => service.Index(context));
        app.MapPost("/users/{userId}/push_subscriptions", (HttpContext context, IntegrationService service) => service.Subscribe(context));
        app.MapDelete("/users/{userId}/push_subscriptions/{id:long}", (HttpContext context, long id, IntegrationService service) => service.Unsubscribe(context, id));
        app.MapPost("/users/{userId}/push_subscriptions/{id:long}/test_notifications", (HttpContext context, long id, IntegrationService service) => service.TestNotification(context, id));
        app.MapPost("/unfurl_link", (HttpContext context, OpenGraphService service) => service.Create(context));
        return app;
    }
}

public sealed class PushRecord
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dhKey { get; set; } = "";
    public string AuthKey { get; set; } = "";
    public string UserAgent { get; set; } = "";
}

public sealed class IntegrationService(IDataStore db, IRailsCrypto crypto, IServiceProvider services, IConfiguration configuration, RailsJobQueue jobs, ILogger<IntegrationService> logger) : BackgroundService, IIntegrationEvents
{
    private readonly SemaphoreSlim pushPool=new(50,50);
    // Delivery requires a deliberate operator choice. Seeded fixtures and benchmark runs never
    // send to their stored external endpoints by merely rendering or creating messages.
    private bool Deliver => !string.Equals(configuration["CAMPFIRE_DELIVER_INTEGRATIONS"], "false", StringComparison.OrdinalIgnoreCase);
    public async Task MessageCreatedAsync(long messageId)
    {
        var message=db.Read(c=>c.QuerySingleOrDefault<IntegrationMessage>("SELECT m.id,m.room_id RoomId,m.creator_id CreatorId,r.name RoomName,r.type RoomType,u.name UserName,coalesce(t.body,'') Body FROM messages m JOIN rooms r ON r.id=m.room_id JOIN users u ON u.id=m.creator_id LEFT JOIN action_text_rich_texts t ON t.record_type='Message' AND t.record_id=m.id AND t.name='body' WHERE m.id=@messageId",new{messageId}));
        if(message is null)return;
        await jobs.EnqueueAsync("Room::PushMessageJob",[RailsJobQueue.GlobalId(message.RoomType,message.RoomId),RailsJobQueue.GlobalId("Message",messageId)]);
        var mentioned=Mentions(message.Body);
        var bots=db.Read(c=>c.Query<long>("SELECT w.user_id FROM webhooks w JOIN users u ON u.id=w.user_id JOIN memberships m ON m.user_id=u.id WHERE m.room_id=@room AND u.role=2 AND u.status=0 AND u.id!=@creator",new{room=message.RoomId,creator=message.CreatorId}).Materialize());
        foreach(var bot in bots.Where(x=>message.RoomType=="Rooms::Direct"||mentioned.Contains(x))) await jobs.EnqueueAsync("Bot::WebhookJob",[RailsJobQueue.GlobalId("User",bot),RailsJobQueue.GlobalId("Message",messageId)]);
    }
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.Register("Room::PushMessageJob",(args,ct)=>Dispatch(RailsJobQueue.RecordId(args[1]),ct,deliverHooks:false));
        jobs.Register("Bot::WebhookJob",async(args,ct)=>
        {
            var id=RailsJobQueue.RecordId(args[1]);var botId=RailsJobQueue.RecordId(args[0]);
            var message=db.Read(c=>c.QuerySingleOrDefault<IntegrationMessage>("SELECT m.id,m.room_id RoomId,m.creator_id CreatorId,r.name RoomName,r.type RoomType,u.name UserName,coalesce(t.body,'') Body FROM messages m JOIN rooms r ON r.id=m.room_id JOIN users u ON u.id=m.creator_id LEFT JOIN action_text_rich_texts t ON t.record_type='Message' AND t.record_id=m.id AND t.name='body' WHERE m.id=@id",new{id}));
            var bot=db.Read(c=>c.QuerySingleOrDefault<WebhookRecord>("SELECT w.id,w.url,u.id UserId,u.name,u.bot_token BotToken FROM webhooks w JOIN users u ON u.id=w.user_id WHERE u.id=@botId AND u.status=0",new{botId}));
            if(message is not null&&bot is not null)await Webhook(message,bot,ct);
        });
        foreach(var name in new[]{"Analyze","Purge","Transform","PreviewImage","Mirror"}){var jobClass="ActiveStorage::"+name+"Job";jobs.Register(jobClass,(args,ct)=>services.GetRequiredService<Campfire.Features.Storage.MediaService>().RunJobAsync(jobClass,args,ct));}
        return base.StartAsync(cancellationToken);
    }
    protected override Task ExecuteAsync(CancellationToken stoppingToken)=>Task.CompletedTask;
    private string Plain(string html)
    {
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        foreach (var mention in document.QuerySelectorAll("action-text-attachment,span.mention"))
        {
            var id = crypto.VerifySignedGlobalId(mention.GetAttribute("sgid") ?? "", "User");
            var name = id is null ? null : db.Read(queryConnection1 => queryConnection1.ExecuteScalar<string?>("SELECT name FROM users WHERE id=@id",new { id }));
            mention.TextContent = name is null ? mention.GetAttribute("caption") ?? "" : "@" + name;
        }
        foreach (var br in document.QuerySelectorAll("br")) br.Replace(document.CreateTextNode("\n"));
        foreach (var block in document.QuerySelectorAll("p,div,li,blockquote,pre,h1,h2,h3,h4,h5,h6,tr")) block.AppendChild(document.CreateTextNode("\n"));
        return document.Body?.TextContent.Trim() ?? "";
    }
    private List<long> Mentions(string html)
    {
        var ids = new List<long>();
        foreach (Match match in Regex.Matches(html, "sgid=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase))
        {
            var id = crypto.VerifySignedGlobalId(WebUtility.HtmlDecode(match.Groups[1].Value), "User");
            if (id.HasValue) ids.Add(id.Value);
        }
        return ids.Distinct().ToList();
    }
    private async Task Dispatch(long id, CancellationToken cancellationToken, bool deliverHooks = true)
    {
        var message = db.Read(queryConnection2 => queryConnection2.QuerySingleOrDefault<IntegrationMessage>("SELECT m.id,m.room_id RoomId,m.creator_id CreatorId,r.name RoomName,r.type RoomType,u.name UserName,coalesce(t.body,'') Body FROM messages m JOIN rooms r ON r.id=m.room_id JOIN users u ON u.id=m.creator_id LEFT JOIN action_text_rich_texts t ON t.record_type='Message' AND t.record_id=m.id AND t.name='body' WHERE m.id=@id",new { id }));
        if (message is null) return;
        var mentioned = Mentions(message.Body);
        var bots = db.Read(queryConnection3 => queryConnection3.Query<WebhookRecord>("SELECT w.id,w.url,u.id UserId,u.name,u.bot_token BotToken FROM webhooks w JOIN users u ON u.id=w.user_id JOIN memberships mem ON mem.user_id=u.id WHERE u.role=2 AND u.status=0 AND mem.room_id=@room AND u.id!=@creator",new { room = message.RoomId, creator = message.CreatorId }).Materialize());
        if (deliverHooks)
            foreach (var bot in bots.Where(x => message.RoomType == "Rooms::Direct" || mentioned.Contains(x.UserId))) await Webhook(message, bot, cancellationToken);
        var subscriptions = db.Read(queryConnection4 => queryConnection4.Query<PushRecord>("SELECT p.id,p.user_id UserId,p.endpoint,p.p256dh_key P256dhKey,p.auth_key AuthKey FROM push_subscriptions p JOIN users u ON u.id=p.user_id JOIN memberships m ON m.user_id=u.id WHERE u.status=0 AND m.room_id=@room AND u.id!=@creator AND (m.connected_at IS NULL OR m.connected_at<@cutoff) AND m.involvement IN ('everything','mentions')",new { room = message.RoomId, creator = message.CreatorId, cutoff = DateTime.UtcNow.AddSeconds(-60).ToString("yyyy-MM-dd HH:mm:ss.ffffff") }).Materialize());
        var plain = Plain(message.Body);
        if (plain.Length == 0) plain = db.Read(queryConnection5 => queryConnection5.ExecuteScalar<string?>("SELECT b.filename FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id=b.id WHERE a.record_type='Message' AND a.record_id=@id AND a.name='attachment' LIMIT 1",new { id })) ?? "";
        var deliveries=new List<Task>();
        foreach (var subscription in subscriptions)
        {
            var involvement = db.Read(queryConnection6 => queryConnection6.ExecuteScalar<string>("SELECT involvement FROM memberships WHERE user_id=@user AND room_id=@room",new { user = subscription.UserId, room = message.RoomId }));
            if (involvement == "mentions" && !mentioned.Contains(subscription.UserId)) continue;
            var title = message.RoomType == "Rooms::Direct" ? message.UserName : message.RoomName ?? "";
            var body = message.RoomType == "Rooms::Direct" ? plain : message.UserName + ": " + plain;
            var badge=db.Read(c=>c.ExecuteScalar<long>("SELECT count(*) FROM memberships WHERE user_id=@id AND unread_at IS NOT NULL",new{id=subscription.UserId}));
            async Task DeliverOne(){await pushPool.WaitAsync(cancellationToken);try{await Push(subscription,title,body,$"/rooms/{message.RoomId}",cancellationToken,snapshotBadge:badge);}finally{pushPool.Release();}}
            deliveries.Add(DeliverOne());
        }
        await Task.WhenAll(deliveries);
    }
    private async Task Webhook(IntegrationMessage message, WebhookRecord bot, CancellationToken cancellationToken)
    {
        // Admin-defined bot hooks intentionally permit private services, unlike push endpoints.
        if (!Uri.TryCreate(bot.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        if (db.Read(queryConnection7 => queryConnection7.ExecuteScalar<long>("SELECT count(*) FROM memberships m JOIN users u ON u.id=m.user_id WHERE m.room_id=@room AND m.user_id=@user AND u.status=0",new { room = message.RoomId, user = bot.UserId })) == 0) return;
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout=TimeSpan.FromSeconds(7) };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var plain = Plain(message.Body);
        if(plain.Length==0)plain=db.Read(c=>c.ExecuteScalar<string?>("SELECT b.filename FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id=b.id WHERE a.record_type='Message' AND a.record_id=@id AND a.name='attachment' LIMIT 1",new{id=message.Id}))??"";
        plain=plain.Replace("@"+bot.Name,"",StringComparison.Ordinal).Trim();
        var payload = new WebhookPayload(new WebhookUser(message.CreatorId, message.UserName), new WebhookRoom(message.RoomId, message.RoomName, $"/rooms/{message.RoomId}/{bot.UserId}-{bot.BotToken}/messages"), new WebhookMessage(message.Id, new WebhookBody(message.Body, plain), $"/rooms/{message.RoomId}/@{message.Id}"));
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,uri){Content=new StringContent(JsonSerializer.Serialize(payload, IntegrationsJson.Default.WebhookPayload), Encoding.UTF8, "application/json")};
            using var headersTimeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);headersTimeout.CancelAfter(TimeSpan.FromSeconds(7));
            using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,headersTimeout.Token);
            var type = response.Content.Headers.ContentType?.MediaType;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bytes = new MemoryStream(); var buffer = new byte[8192]; int read;
            while(true) { using var readTimeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);readTimeout.CancelAfter(TimeSpan.FromSeconds(7));read=await stream.ReadAsync(buffer,readTimeout.Token);if(read==0)break;bytes.Write(buffer,0,read); }
            if (response.StatusCode == HttpStatusCode.OK && type is "text/plain" or "text/html")
                await Reply(message.RoomId, bot.UserId, Encoding.UTF8.GetString(bytes.ToArray()), null, cancellationToken);
            else if (type is not null)
            {
                bytes.Position = 0;
                var mime=RailsMime.Lookup(type);
                var file = new FormFile(bytes, 0, bytes.Length, "attachment", "attachment." + mime.Symbol) { Headers = new HeaderDictionary(), ContentType = mime.Type };
                await Reply(message.RoomId, bot.UserId, "", file, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { await Reply(message.RoomId, bot.UserId, "Failed to respond within 7 seconds", null, cancellationToken); }
        catch (HttpRequestException error) { logger.LogWarning(error, "Bot webhook failed for {BotId}", bot.UserId); }
    }
    private async Task Reply(long room, long user, string body, IFormFile? file, CancellationToken cancellationToken)
    {
        var sanitized = new HtmlSanitizer().Sanitize(body);
        var media = services.GetRequiredService<Campfire.Features.Storage.MediaService>();
        var staging = Random.Shared.NextInt64(1, long.MaxValue);
        if (file is not null) await media.SaveRecordUploadAsync("ChatUpload", staging, "attachment", file, cancellationToken);
        long id;
        try { id = db.Write((connection, transaction) =>
        {
            if (connection.ExecuteScalar<long>("SELECT count(*) FROM memberships m JOIN users u ON u.id=m.user_id WHERE m.room_id=@room AND m.user_id=@user AND u.status=0", new { room, user }, transaction) == 0) return 0L;
            var now = RequestUser.Timestamp();
            var message = connection.ExecuteScalar<long>("INSERT INTO messages(room_id,creator_id,client_message_id,created_at,updated_at) VALUES(@room,@user,@client,@now,@now); SELECT last_insert_rowid()", new { room, user, client = Guid.NewGuid().ToString(), now }, transaction);
            connection.Execute("INSERT INTO action_text_rich_texts(record_type,record_id,name,body,created_at,updated_at) VALUES('Message',@message,'body',@sanitized,@now,@now); INSERT INTO message_search_index(rowid,body) VALUES(@message,@plain); UPDATE rooms SET updated_at=@now WHERE id=@room; UPDATE memberships SET unread_at=@now,updated_at=@now WHERE room_id=@room AND user_id!=@user AND involvement!='invisible' AND (connected_at IS NULL OR connected_at<@cutoff)", new { message, sanitized, plain = file?.FileName ?? Plain(sanitized), now, room, user, cutoff = DateTime.UtcNow.AddSeconds(-60).ToString("yyyy-MM-dd HH:mm:ss.ffffff") }, transaction);
            if (file is not null)
                connection.Execute("UPDATE active_storage_attachments SET record_type='Message',record_id=@message WHERE record_type='ChatUpload' AND record_id=@staging AND name='attachment'", new { message, staging }, transaction);
            return message;
        }); }
        finally { if (file is not null) media.PurgeAttachment("ChatUpload", staging, "attachment"); }
        if (id == 0) return;
        if(file is not null)await media.ProcessMessageAttachmentAsync(id,cancellationToken);
        await services.GetRequiredService<IRealtimeEvents>().MessageChangedAsync(room, id);
        // Rails webhook replies broadcast but do not run controller bot webhook fanout.
        // Push room callbacks still apply; enqueue only the push path to avoid recursive bot loops.
        await jobs.EnqueueAsync("Room::PushMessageJob",[RailsJobQueue.GlobalId(db.Read(c=>c.ExecuteScalar<string>("SELECT type FROM rooms WHERE id=@room",new{room}))!,room),RailsJobQueue.GlobalId("Message",id)],cancellationToken);
    }
    private async Task<bool> Push(PushRecord record, string title, string body, string path, CancellationToken cancellationToken, bool fromPool=true, long? snapshotBadge=null)
    {
        if (!Deliver || !OutboundPolicy.PushUri(record.Endpoint, out var uri)) return false;
        var privateKey = configuration["VAPID_PRIVATE_KEY"]; var publicKey = configuration["VAPID_PUBLIC_KEY"];
        if (string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(publicKey)) return false;
        var address = await OutboundPolicy.ResolvePush(uri!, cancellationToken);
        if (address is null) return false;
        var badge = snapshotBadge??db.Read(queryConnection8 => queryConnection8.ExecuteScalar<long>("SELECT count(*) FROM memberships WHERE user_id=@id AND unread_at IS NOT NULL",new { id = record.UserId }));
        var payload = JsonSerializer.Serialize(new PushPayload(title, new NotificationOptions(body, "/account/logo", new NotificationData(path, badge))), IntegrationsJson.Default.PushPayload);
        try
        {
            using var handler = OutboundPolicy.PinnedHandler(address);
            handler.ConnectTimeout=TimeSpan.FromSeconds(60);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.TryAddWithoutValidation("Authorization", WebPushEncoding.Authorization(uri!, privateKey, publicKey, configuration["VAPID_SUBJECT"] ?? "mailto:support@37signals.com"));
            request.Headers.TryAddWithoutValidation("TTL", "2419200"); request.Headers.TryAddWithoutValidation("Urgency", "high");
            request.Content = new ByteArrayContent(WebPushEncoding.Encrypt(payload, record.P256dhKey, record.AuthKey));
            request.Content.Headers.ContentType = new("application/octet-stream"); request.Content.Headers.ContentEncoding.Add("aes128gcm");
            using var response = await http.SendAsync(request, cancellationToken);
            if (fromPool&&response.StatusCode==HttpStatusCode.Gone) db.Write((queryConnection9,queryTransaction9) => queryConnection9.Execute("DELETE FROM push_subscriptions WHERE id=@id",new { id = record.Id },transaction: queryTransaction9));
            if(!fromPool&&!response.IsSuccessStatusCode)throw new HttpRequestException("Push service rejected notification",null,response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or System.Security.Cryptography.CryptographicException or FormatException or ArgumentException or OperationCanceledException) { if(!fromPool)throw;if(error is System.Security.Cryptography.CryptographicException || error is HttpRequestException requestError&&requestError.HttpRequestError==HttpRequestError.SecureConnectionError)db.Write((c,t)=>c.Execute("DELETE FROM push_subscriptions WHERE id=@id",new{id=record.Id},t));logger.LogWarning(error, "Push delivery failed for {SubscriptionId}", record.Id); return false; }
    }
    private static async Task<bool> ValidMutation(HttpContext context)
    {
        var token = context.Request.HasFormContentType ? (await context.Request.ReadFormAsync())["authenticity_token"].ToString() : null;
        return context.RequestServices.GetRequiredService<IAuthService>().ValidateCsrf(context, token);
    }
    public IResult Index(HttpContext context)
    {
        var user = context.User(); if (user is null) return Results.Redirect("/session/new");
        var token = context.RequestServices.GetRequiredService<IAuthService>().CsrfToken(context);
        var records = db.Read(queryConnection10 => queryConnection10.Query<PushRecord>("SELECT id,user_id UserId,endpoint,user_agent UserAgent FROM push_subscriptions WHERE user_id=@id",new { id = user.Id }).Materialize());
        var renderer = services.GetService<ChatRenderer>();
        string Icon(string name)=>renderer?.Icon(name)??$"<img aria-hidden=\"true\" src=\"/assets/{name}\" width=\"20\" height=\"20\">";
        var html = new StringBuilder("<section class=\"panel panel--wide flex flex-column gap\"><h1 class=\"txt-align-center txt-large margin-none\">Push Notification Subscriptions</h1><div class=\"pad-inline fill-shade border-radius\" id=\"push_subscriptions\"><menu class=\"pad flex flex-column gap\">");
        foreach (var record in records) html.Append($"<li class=\"flex flex-column margin-none membership-item\"><span class=\"overflow-ellipsis txt-primary txt-undecorated\"><strong>{WebUtility.HtmlEncode(UserAgentLabel.Format(record.UserAgent))}</strong><br></span><span class=\"flex align-start gap txt-small\"><span>{WebUtility.HtmlEncode(record.Endpoint)}</span><span class=\"flex align-center gap\"><form class=\"button_to\" method=\"post\" action=\"/users/me/push_subscriptions/{record.Id}/test_notifications\"><button class=\"btn btn--reversed\" type=\"submit\">{Icon("notification-bell-everything.svg")}<span class=\"for-screen-reader\">Send test notification</span></button><input type=\"hidden\" name=\"authenticity_token\" value=\"{WebUtility.HtmlEncode(token)}\"></form><form class=\"button_to\" method=\"post\" action=\"/users/me/push_subscriptions/{record.Id}\"><input type=\"hidden\" name=\"_method\" value=\"delete\"><button class=\"btn btn--negative\" type=\"submit\">{Icon("minus.svg")}<span class=\"for-screen-reader\">Delete subscription</span></button><input type=\"hidden\" name=\"authenticity_token\" value=\"{WebUtility.HtmlEncode(token)}\"></form></span></span></li>");
        html.Append("</menu></div></section>");
        var result = renderer is null ? html.ToString() : renderer.Layout(user, html.ToString(), "Push notification subscriptions", nav: "<a class=\"btn\" href=\"/\">Back to chat</a>", sidebar: renderer.SidebarFrame());
        return Results.Content(result, "text/html");
    }
    public async Task<IResult> Subscribe(HttpContext context)
    {
        var user = context.User(); if (user is null) return Results.Unauthorized();
        if (!await ValidMutation(context)) return Results.StatusCode(422);
        try
        {
            using var doc = context.Request.HasFormContentType ? FormSubscription(await context.Request.ReadFormAsync()) : await JsonDocument.ParseAsync(context.Request.Body);
            var value = doc.RootElement.GetProperty("push_subscription");
            var endpoint = value.GetProperty("endpoint").GetString(); var p256dh = value.GetProperty("p256dh_key").GetString(); var auth = value.GetProperty("auth_key").GetString();
            if (!OutboundPolicy.PushUri(endpoint, out var uri) || await OutboundPolicy.ResolvePush(uri!, context.RequestAborted) is null) return Results.UnprocessableEntity();
            db.Write((connection, transaction) =>
            {
                var id = connection.ExecuteScalar<long?>("SELECT id FROM push_subscriptions WHERE user_id=@userId AND endpoint=@endpoint AND p256dh_key=@p256dh AND auth_key=@auth LIMIT 1", new { userId = user.Id, endpoint, p256dh, auth }, transaction);
                if (id is null) connection.Execute("INSERT INTO push_subscriptions(user_id,endpoint,p256dh_key,auth_key,user_agent,created_at,updated_at) VALUES(@userId,@endpoint,@p256dh,@auth,@agent,@now,@now)", new { userId = user.Id, endpoint, p256dh, auth, agent = context.Request.Headers.UserAgent.ToString(), now = RequestUser.Timestamp() }, transaction);
                else connection.Execute("UPDATE push_subscriptions SET updated_at=@now WHERE id=@id", new { id, now = RequestUser.Timestamp() }, transaction);
                return 0;
            });
            return Results.Ok();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return Results.UnprocessableEntity(); }
    }
    private static JsonDocument FormSubscription(IFormCollection form)
    {
        var value=new JsonObject();foreach(var field in new[]{"endpoint","p256dh_key","auth_key"})value[field]=form["push_subscription["+field+"]"].ToString();return JsonDocument.Parse(new JsonObject{["push_subscription"]=value}.ToJsonString());
    }
    public async Task<IResult> Unsubscribe(HttpContext context, long id)
    {
        var user = context.User(); if (user is null) return Results.Redirect("/session/new");
        if (!await ValidMutation(context)) return Results.StatusCode(422);
        db.Write((queryConnection11,queryTransaction11) => queryConnection11.Execute("DELETE FROM push_subscriptions WHERE id=@id AND user_id=@userId",new { id, userId = user.Id },transaction: queryTransaction11));
        return Results.Redirect("/users/me/push_subscriptions");
    }
    public async Task<IResult> TestNotification(HttpContext context, long id)
    {
        var user = context.User(); if (user is null) return Results.Redirect("/session/new");
        if (!await ValidMutation(context)) return Results.StatusCode(422);
        var record = db.Read(queryConnection12 => queryConnection12.QuerySingleOrDefault<PushRecord>("SELECT id,user_id UserId,endpoint,p256dh_key P256dhKey,auth_key AuthKey FROM push_subscriptions WHERE id=@id AND user_id=@userId",new { id, userId = user.Id }));
        if (record is null) return Results.NotFound();
        await Push(record, "Campfire Test", Guid.NewGuid().ToString(), $"{context.Request.Scheme}://{context.Request.Host}/users/me/push_subscriptions", context.RequestAborted,false);
        return Results.Redirect("/users/me/push_subscriptions");
    }
    internal sealed class IntegrationMessage
    {
        public long Id { get; set; } public long RoomId { get; set; } public long CreatorId { get; set; }
        public string? RoomName { get; set; } public string RoomType { get; set; } = ""; public string UserName { get; set; } = ""; public string Body { get; set; } = "";
    }
    internal sealed class WebhookRecord
    {
        public long Id { get; set; } public long UserId { get; set; } public string Url { get; set; } = ""; public string Name { get; set; } = ""; public string BotToken { get; set; } = "";
    }
}
