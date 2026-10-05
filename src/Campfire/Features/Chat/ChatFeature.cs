using Dapper;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Campfire.Contracts;

namespace Campfire.Features.Chat;

public static class ChatFeature
{
    public static IServiceCollection AddChatFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<RichText>();
        services.AddSingleton<ChatStore>();
        services.AddSingleton<ChatRenderer>();
        services.AddSingleton<IChatRenderer>(provider => provider.GetRequiredService<ChatRenderer>());
        services.AddSingleton<IPageRenderer>(provider => provider.GetRequiredService<ChatRenderer>());
        return services;
    }
    public static WebApplication MapChatFeature(this WebApplication app)
    {
        var routes = app.MapGroup("");
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var auth = context.RequestServices.GetRequiredService<IAuthService>();
            var user = auth.Current(context);
            if (user is null) { auth.CaptureReturnTo(context); return Results.Redirect("/session/new"); }
            if (user.Status != 0) return Results.StatusCode(403);
            if (user.Role == 2 && !context.Request.RouteValues.ContainsKey("bot_key")) return Results.StatusCode(403);
            try
            {
                if (BotRequest(context) && RouteRoom(context) is long roomId)
                {
                    try { _ = context.RequestServices.GetRequiredService<ChatStore>().Room(roomId, user.Id); }
                    catch (ChatHttpException) { return Results.StatusCode(404); }
                }
                var result = await next(invocation);
                if (!HtmlRequest(context) && (result is IContentTypeHttpResult { ContentType: "text/html; charset=utf-8" } || result is HtmlSegmentsResult)) return Error(context, 406);
                return result;
            }
            catch (ChatHttpException exception)
            {
                if (exception.Status == 404 && System.Text.RegularExpressions.Regex.IsMatch(context.Request.Path.Value ?? "", @"\A/rooms/(?:opens/|closeds/|directs/)?\d+(?:/edit)?\z")) return RoomNotFound(context);
                return Error(context, exception.Status);
            }
            catch (JsonException) { return Error(context, 400); }
        });
        routes.MapGet("/", (HttpContext context, ChatStore store, ChatRenderer renderer) =>
        {
            var user = context.User()!;
            var room = LastRoom(context, store, user.Id);
            return room is null ? Html(renderer.Layout(user, $"<div id=\"message-area\" class=\"message-area\"><div class=\"message-area--empty min-width center\"><figure class=\"center pad\"><img src=\"{renderer.Asset("messages-empty.svg")}\" aria-hidden=\"true\" class=\"colorize--black translucent\"><span class=\"for-screen-reader\">{ChatRenderer.E(user.Name)}</span></figure></div></div>", title: "No rooms yet", sidebar: renderer.SidebarFrame(), bodyClass: "sidebar")) : Results.Redirect("/rooms/" + room.Id);
        });
        routes.MapGet("/rooms", (HttpContext context, ChatStore store) =>
        {
            var room = store.Rooms(context.User()!.Id).OrderByDescending(x => x.Id).FirstOrDefault();
            return room is null ? Error(context, 500) : Results.Redirect("/rooms/" + room.Id);
        });
        routes.MapGet("/rooms/{id:long}", ShowRoom);
        // Active Record casts room identifiers before find_by. Unmatched
        // identifiers still reach RoomsController's redirect/alert branch.
        routes.MapGet("/rooms/{id}", (HttpContext context, string id, ChatStore store, ChatRenderer renderer) => ShowRoom(context, RailsRoomId(id), store, renderer));
        routes.MapGet("/rooms/{id:long}/@{messageId:long}", ShowRoom);
        routes.MapDelete("/rooms/{id:long}", (HttpContext context, long id, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime, IMediaService media) => DeleteRoom(context, id, store, renderer, realtime, media, false));
        foreach (var route in new[] { ("opens", "Rooms::Open"), ("closeds", "Rooms::Closed"), ("directs", "Rooms::Direct") })
        {
            var (prefix, type) = route;
            routes.MapGet("/rooms/" + prefix, (HttpContext context, ChatStore store) =>
            {
                var room = store.Rooms(context.User()!.Id).OrderByDescending(x => x.Id).FirstOrDefault();
                return room is null ? Error(context, 500) : Results.Redirect("/rooms/" + room.Id);
            });
            routes.MapGet("/rooms/" + prefix + "/new", (HttpContext context, ChatStore store, ChatRenderer renderer) =>
            {
                var user = context.User()!;
                if (type != "Rooms::Direct" && !store.CanCreate(user)) return Results.StatusCode(403);
                return Html(type == "Rooms::Direct" ? renderer.Layout(user, renderer.DirectNew(user)) : renderer.RoomForm(new RoomRecord { Name = "New room", Type = type, CreatorId = user.Id }, user));
            });
            routes.MapGet("/rooms/" + prefix + "/{id:long}/edit", (HttpContext context, long id, ChatStore store, ChatRenderer renderer) =>
            {
                var room = store.Room(id, context.User()!.Id);
                if (room.Direct != (type == "Rooms::Direct")) throw new ChatHttpException(404, "Room not found");
                room.Type = type;
                return Html(renderer.RoomForm(room, context.User()!));
            });
            if (type != "Rooms::Direct") routes.MapGet("/rooms/" + prefix + "/{id:long}", (HttpContext context, long id, ChatStore store) =>
            {
                var room = store.Room(id, context.User()!.Id);
                if (room.Direct) throw new ChatHttpException(404, "Room not found");
                return Results.Redirect("/rooms/" + id);
            });
            else routes.MapGet("/rooms/directs/{id:long}", (HttpContext context, long id, ChatStore store) =>
            {
                if (!store.Room(id, context.User()!.Id).Direct) return RoomNotFound(context);
                // The pinned inherited show action has no directs/show template.
                return Error(context, 500);
            });
            routes.MapPost("/rooms/" + prefix, async (HttpContext context, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime) =>
            {
                var data = await Input.Read(context);
                if (type != "Rooms::Direct") data.RequireObject("room");
                var id = store.CreateRoom(context.User()!, type, type == "Rooms::Direct" ? null : data.Value("room[name]"), data.Ids());
                await BroadcastRoom(store, renderer, realtime, id, "prepend");
                return Results.Redirect("/rooms/" + id);
            });
            if (type != "Rooms::Direct") routes.MapMethods("/rooms/" + prefix + "/{id:long}", ["PATCH", "PUT"], async (HttpContext context, long id, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime) =>
            {
                var data = await Input.Read(context);
                data.RequireObject("room");
                var oldRoom = store.Room(id, context.User()!.Id);
                var oldUsers = store.Members(id).Select(x => x.Id).ToArray();
                var revoked = store.UpdateRoom(context.User()!, id, type, data.Has("room[name]") ? data.Value("room[name]") : oldRoom.Name, data.Ids());
                foreach (var userId in revoked)
                {
                    await realtime.TurboStreamAsync(ChatRenderer.UserStream(userId), "remove", oldRoom.Dom("list"));
                    await realtime.DisconnectUserAsync(userId, true);
                }
                // Conversion changes Rails STI-derived DOM ids.
                if (oldRoom.Type != type) foreach (var userId in oldUsers) await realtime.TurboStreamAsync(ChatRenderer.UserStream(userId), "remove", oldRoom.Dom("list"));
                await BroadcastRoom(store, renderer, realtime, id, oldRoom.Type == type ? "replace" : "prepend");
                return Results.Redirect("/rooms/" + id);
            });
            if (type == "Rooms::Direct") routes.MapDelete("/rooms/directs/{id:long}", (HttpContext context, long id, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime, IMediaService media) => DeleteRoom(context, id, store, renderer, realtime, media, true));
            else routes.MapDelete("/rooms/" + prefix + "/{id:long}", (HttpContext context, long id, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime, IMediaService media) =>
            {
                if (store.Room(id, context.User()!.Id).Direct) throw new ChatHttpException(404, "Room not found");
                return DeleteRoom(context, id, store, renderer, realtime, media, false);
            });
        }
        routes.MapGet("/rooms/{roomId:long}/settings", (HttpContext context) => Error(context, 500));
        routes.MapGet("/users/{userId}/sidebar", (HttpContext context, string userId, ChatRenderer renderer) =>
        {
            var user = context.User()!;
            return Html(renderer.Layout(user, renderer.Sidebar(user)));
        });
        routes.MapGet("/rooms/{roomId:long}/involvement", (HttpContext context, long roomId, ChatStore store, ChatRenderer renderer) => Html(renderer.InvolvementHtml(store.Room(roomId, context.User()!.Id))));
        routes.MapMethods("/rooms/{roomId:long}/involvement", ["PUT", "PATCH"], async (HttpContext context, long roomId, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime) =>
        {
            var data = await Input.Read(context);
            var old = store.Room(roomId, context.User()!.Id);
            var value = data.Value("involvement") ?? context.Request.Query["involvement"].ToString();
            store.Involvement(context.User()!, roomId, value);
            if (!old.Direct)
            {
                if (value == "invisible") await realtime.TurboStreamAsync(ChatRenderer.UserStream(context.User()!.Id), "remove", old.Dom("list"));
                else if (old.Involvement == "invisible") await realtime.TurboStreamAsync(ChatRenderer.UserStream(context.User()!.Id), "prepend", "shared_rooms", renderer.SidebarRoom(store.Room(roomId, context.User()!.Id), context.User()!.Id));
            }
            return Results.Redirect($"/rooms/{roomId}/involvement");
        });
        routes.MapGet("/rooms/{roomId:long}/messages", MessageIndex);
        routes.MapGet("/rooms/{roomId:long}/messages/{id:long}", ShowMessage);
        routes.MapGet("/messages/{id:long}", ShowMessage);
        routes.MapGet("/rooms/{roomId:long}/messages/{id:long}/edit", EditMessage);
        routes.MapGet("/messages/{id:long}/edit", EditMessage);
        routes.MapPost("/rooms/{roomId:long}/messages", CreateMessage);
        routes.MapMethods("/rooms/{roomId:long}/messages/{id:long}", ["PATCH", "PUT"], UpdateMessage);
        routes.MapMethods("/messages/{id:long}", ["PATCH", "PUT"], UpdateMessage);
        routes.MapDelete("/rooms/{roomId:long}/messages/{id:long}", DestroyMessage);
        routes.MapDelete("/messages/{id:long}", DestroyMessage);
        routes.MapGet("/rooms/{roomId:long}/refresh", (HttpContext context, long roomId, ChatStore store, ChatRenderer renderer) =>
        {
            var (created, updated) = store.Refresh(roomId, context.User()!.Id, Long(context.Request.Query["since"]), htmlOnly: true);
            if (!TurboRequest(context)) return Error(context, 406);
            var room = store.Room(roomId, context.User()!.Id);
            var streams = created.Count == 0 ? "" : ChatRenderer.Turbo("append", room.Dom("messages"), renderer.MessagesHtml(created, context.User()!.Id));
            streams += string.Concat(updated.Select(x => ChatRenderer.Turbo("replace", "message_" + x.ClientMessageId, renderer.MessageHtml(x, context.User()!.Id))));
            return Turbo(streams);
        });
        routes.MapGet("/searches", (HttpContext context, ChatStore store, ChatRenderer renderer) =>
        {
            var query = ChatStore.CleanQuery(context.Request.Query["q"].ToString());
            return new HtmlSegmentsResult(renderer.SearchContentSegments(context.User()!, query, store.Search(context.User()!.Id, query, htmlOnly: true), LastRoom(context, store, context.User()!.Id)?.Id));
        });
        routes.MapPost("/searches", async (HttpContext context, ChatStore store) =>
        {
            var input = await Input.Read(context);
            var query = ChatStore.CleanQuery(input.Value("q") ?? context.Request.Query["q"].ToString());
            _ = store.Search(context.User()!.Id, query);
            if (!input.Has("q")) return Error(context, 500); // searches.query is NOT NULL in the pinned schema.
            store.RecordSearch(context.User()!.Id, query);
            return Results.Redirect("/searches?q=" + Uri.EscapeDataString(query));
        });
        routes.MapDelete("/searches/clear", (HttpContext context, ChatStore store) => { _ = store.Search(context.User()!.Id, ChatStore.CleanQuery(context.Request.Query["q"].ToString())); store.ClearSearches(context.User()!.Id); return Results.Redirect("/searches"); });
        routes.MapGet("/autocompletable/users", (HttpContext context, ChatStore store, ChatRenderer renderer, RichText richText) =>
        {
            var filter = context.Request.Query["filter"].FirstOrDefault();
            var query = string.IsNullOrWhiteSpace(filter) ? context.Request.Query["query"].FirstOrDefault() : filter;
            if (string.IsNullOrWhiteSpace(query)) query = null;
            var users = store.Autocomplete(context.User()!.Id, ScopedParameter(context.Request.Query["room_id"]), query, (int)Math.Max(Long(context.Request.Query["page"]), 1));
            return JsonRequest(context) ? Results.Json(users.Select(x => new AutocompleteUserResponse(ChatRenderer.E(x.Name), x.Id, renderer.Origin() + renderer.AvatarUrl(x.Id, x.UpdatedAt), richText.MentionSgid(x.Id))).ToArray(), ChatJsonContext.Default.AutocompleteUserResponseArray) : Html(string.Concat(users.Select(renderer.Prompt)));
        });
        routes.MapGet("/messages/{messageId:long}/boosts", (HttpContext context, long messageId, ChatStore store, ChatRenderer renderer) => Html(renderer.Layout(context.User()!, renderer.BoostsHtml(store.Message(messageId, context.User()!.Id)))));
        routes.MapGet("/messages/{messageId:long}/boosts/new", (HttpContext context, long messageId, ChatStore store, ChatRenderer renderer) => Html(renderer.Layout(context.User()!, renderer.NewBoostHtml(store.Message(messageId, context.User()!.Id), context.User()!))));
        routes.MapPost("/messages/{messageId:long}/boosts", CreateBoost);
        routes.MapDelete("/messages/{messageId:long}/boosts/{id:long}", DestroyBoost);
        routes.MapGet("/rooms/{roomId:long}/{bot_key}/messages", MessageIndex);
        routes.MapPost("/rooms/{roomId:long}/{bot_key}/messages", CreateMessage);
        routes.MapMethods("/rooms/{roomId:long}/{bot_key}/messages/{id:long}", ["PATCH", "PUT"], UpdateMessage);
        routes.MapDelete("/rooms/{roomId:long}/{bot_key}/messages/{id:long}", DestroyMessage);
        routes.MapPost("/rooms/{roomId:long}/{bot_key}/messages/{messageId:long}/boosts", CreateBoost);
        routes.MapDelete("/rooms/{roomId:long}/{bot_key}/messages/{messageId:long}/boosts/{id:long}", DestroyBoost);
        return app;
    }
    private static IResult ShowRoom(HttpContext context, long id, ChatStore store, ChatRenderer renderer)
    {
        var around = NullableLong(context.Request.RouteValues["messageId"]) ?? NullableLong(context.Request.Query["message_id"]);
        (RoomRecord Room, List<ChatMessage> Messages) page;
        try { page = store.RoomPageForRendering(id, context.User()!.Id, around); }
        catch (ChatHttpException) { return RoomNotFound(context); }
        context.Response.Cookies.Append("last_room", id.ToString(CultureInfo.InvariantCulture), new CookieOptions { Path = "/", SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddYears(20) });
        return new HtmlSegmentsResult(renderer.RoomContentSegments(page.Room, page.Messages, context.User()!, renderer.Origin()));
    }
    private static IResult MessageIndex(HttpContext context, long roomId, ChatStore store, ChatRenderer renderer, IDataStore db)
    {
        var before = ScopedParameter(context.Request.Query["before"]);
        var afterAnchor = before.HasValue ? null : ScopedParameter(context.Request.Query["after"]);
        var messages = store.Page(roomId, context.User()!.Id, before, afterAnchor, htmlOnly: !BotRequest(context) || !JsonRequest(context));
        if (BotRequest(context))
        {
            context.Response.Headers["X-Total-Count"] = db.Read(queryConnection1 => queryConnection1.ExecuteScalar<long>("select count(*) from messages where room_id=@roomId",new { roomId })).ToString(CultureInfo.InvariantCulture);
            if (messages.Count != 0)
            {
                var after = afterAnchor.HasValue;
                var anchor = after ? messages[^1] : messages[0];
                var exists = db.Read(queryConnection2 => queryConnection2.ExecuteScalar<long>("select count(*) from messages where room_id=@roomId and created_at" + (after ? ">" : "<") + "@timestamp",new { roomId, timestamp = anchor.CreatedAt })) > 0;
                if (exists) context.Response.Headers.Link = $"<{renderer.Origin()}{context.Request.Path}?{(after ? "after" : "before")}={anchor.Id}>; rel=\"next\"";
            }
            if (!JsonRequest(context)) return HtmlRequest(context) ? new HtmlSegmentsResult(renderer.MessagesContentSegments(messages, context.User()!.Id)) : Error(context, 406);
            return Results.Json(messages.Select(x => MessageJson(x, renderer, context.RequestServices.GetRequiredService<RichText>())).ToArray(), ChatJsonContext.Default.MessageResponseArray);
        }
        if (messages.Count == 0) return Results.NoContent();
        if (!HtmlRequest(context)) return Error(context, 406);
        var etag = MessageEtag(context, messages);
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "private, must-revalidate";
        var modified = messages.Select(x => DateTimeOffset.Parse(x.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)).Max();
        modified = DateTimeOffset.FromUnixTimeSeconds(modified.ToUnixTimeSeconds());
        context.Response.Headers.LastModified = modified.ToString("R", CultureInfo.InvariantCulture);
        var etagMatches = context.Request.Headers.IfNoneMatch.Any(value => value?.Split(',').Any(item => item.Trim() == "*" || item.Trim().Replace("W/", "", StringComparison.Ordinal) == etag[2..]) == true);
        var dateMatches = DateTimeOffset.TryParse(context.Request.Headers.IfModifiedSince.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since) && since >= modified;
        if (context.Request.Headers.ContainsKey("If-None-Match") ? etagMatches : dateMatches) return Results.StatusCode(304);
        return new HtmlSegmentsResult(renderer.MessagesContentSegments(messages, context.User()!.Id));
    }
    private static string MessageEtag(HttpContext context, IEnumerable<ChatMessage> messages)
    {
        // Pinned Rails fresh_when expands the ordered Array's model cache keys,
        // then its messages/index template dependency digest. The digest below
        // is generated from the pinned compiled ERB, not from fixture content;
        // reproduce it with bench/parity/chat/etag_vectors.py when updating views.
        const string templateDigest = "8686c9089c0ca2724d567e0bf0e90539";
        var keys = messages.Select(message => "messages/" + message.Id.ToString(CultureInfo.InvariantCulture) + "-" + DateTimeOffset.Parse(message.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime().ToString("yyyyMMddHHmmssffffff", CultureInfo.InvariantCulture));
        var expanded = string.Join('/', keys) + "/" + templateDigest;
        var flash = context.RequestServices.GetRequiredService<IRailsCrypto>().Verify(context.Request.Cookies["campfire_chat_alert"] ?? "", "chat_flash");
        if (!string.IsNullOrEmpty(flash))
        {
            expanded += "/alert/" + flash;
            // Rails discards the loaded flash at the end of this request even
            // when index renders without the application layout (or returns304).
            context.Response.Cookies.Delete("campfire_chat_alert");
        }
        if (!string.IsNullOrWhiteSpace(context.Request.Headers["Turbo-Frame"])) expanded += "/frame";
        var prefix = Environment.GetEnvironmentVariable("RAILS_CACHE_ID") ?? Environment.GetEnvironmentVariable("RAILS_APP_VERSION");
        if (prefix is not null) expanded = prefix + "/" + expanded;
        return "W/\"" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expanded)).AsSpan(0, 16)).ToLowerInvariant() + "\"";
    }
    private static IResult ShowMessage(HttpContext context, long id, ChatStore store, ChatRenderer renderer, RichText richText)
    {
        var message = store.MessageMetadata(id, context.User()!.Id, RequiredMessageRoom(context));
        return JsonRequest(context) ? Error(context, 406) : Html(renderer.Layout(context.User()!, renderer.MessageHtml(message, context.User()!.Id), forceApplicationLayout: true));
    }
    private static IResult EditMessage(HttpContext context, long id, ChatStore store, ChatRenderer renderer)
    {
        var message = store.Message(id, context.User()!.Id, RequiredMessageRoom(context));
        if (!context.User()!.IsAdmin && message.CreatorId != context.User()!.Id) return Results.StatusCode(403);
        return Html(renderer.Layout(context.User()!, renderer.EditMessageHtml(message), forceApplicationLayout: true));
    }
    private static async Task<IResult> CreateMessage(HttpContext context, long roomId, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime, IIntegrationEvents integrations, IMediaService media, IRailsCrypto crypto, IDataStore db)
    {
        RoomRecord room;
        try { room = store.Room(roomId, context.User()!.Id); }
        catch (ChatHttpException) when (!BotRequest(context))
        {
            return Html(renderer.Layout(context.User()!, "<turbo-frame id=\"composer-frame\"><span class=\"composer__input input input--actor shake margin-block-end txt-negative txt-align-center\" style=\"--input-border-color: var(--color-negative)\"><span>This room was deleted.</span></span></turbo-frame>", forceApplicationLayout: true));
        }
        var input = await Input.Read(context, raw: BotRequest(context));
        if (!BotRequest(context)) input.RequireObject("message");
        var body = BotRequest(context) && (input.Value("attachment") is not null || input.File("attachment") is not null) ? null : input.Value(BotRequest(context) ? "raw" : "message[body]");
        var attachment = await Attachment(context, input, media, crypto, db);
        if (BotRequest(context) && string.IsNullOrWhiteSpace(input.Value("raw")) && attachment is null) return Results.StatusCode(422);
        var id = await store.CreateMessageAsync(context.User()!, roomId, body, input.Value("message[client_message_id]"), attachment, !BotRequest(context) && input.Has("message[body]"), context.RequestAborted);
        if (attachment.HasValue) await media.ProcessMessageAttachmentAsync(id, context.RequestAborted);
        await realtime.MessageChangedAsync(roomId, id);
        await integrations.MessageCreatedAsync(id);
        if (BotRequest(context)) { context.Response.Headers.Location = renderer.Origin() + "/messages/" + id; context.Response.ContentType = JsonRequest(context) ? "application/json" : "text/html"; return Results.StatusCode(201); }
        if (!TurboRequest(context)) return Error(context, 406);
        // Rails retains @room after set_room; attachment processing only
        // touches it. Reauthorize the message while using the fragment the
        // preceding broadcast already warmed, rather than loading its graph.
        var message = store.MessageMetadata(id, context.User()!.Id);
        return Turbo(ChatRenderer.Turbo("append", room.Dom("messages"), renderer.MessageHtml(message, context.User()!.Id)));
    }
    private static async Task<IResult> UpdateMessage(HttpContext context, long id, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime, IMediaService media, IRailsCrypto crypto, IDataStore db, RichText richText)
    {
        var message = store.Message(id, context.User()!.Id, RequiredMessageRoom(context));
        if (!context.User()!.IsAdmin && message.CreatorId != context.User()!.Id) return Results.StatusCode(403);
        var oldBlobs = store.MessageBlobs(id, context.User()!.Id);
        var input = await Input.Read(context, raw: BotRequest(context));
        if (!BotRequest(context)) input.RequireObject("message");
        var attachment = await Attachment(context, input, media, crypto, db);
        var attachmentKey = BotRequest(context) ? "attachment" : "message[attachment]";
        var clearAttachment = input.Has(attachmentKey) && string.IsNullOrEmpty(input.Value(attachmentKey)) && input.File(attachmentKey) is null;
        var body = BotRequest(context) ? input.Value("attachment") is not null || input.File("attachment") is not null ? null : input.Value("raw") : input.Value("message[body]");
        store.UpdateMessage(context.User()!, message.RoomId, id, body, input.Value("message[client_message_id]"), attachment, clearAttachment, !BotRequest(context) && input.Has("message[body]"));
        var updated = store.Message(id, context.User()!.Id);
        if (attachment.HasValue) await media.ProcessMessageAttachmentAsync(id, context.RequestAborted);
        if (attachment.HasValue || clearAttachment) await media.PurgeBlobsAsync(oldBlobs.Except(store.MessageBlobs(id, context.User()!.Id)));
        await realtime.TurboStreamAsync(ChatRenderer.RoomStream(store.Room(updated.RoomId, context.User()!.Id)), "replace", "presentation_message_" + updated.ClientMessageId, renderer.PresentationHtml(updated), true);
        if (BotRequest(context) && JsonRequest(context)) return Results.Json(MessageJson(updated, renderer, richText), ChatJsonContext.Default.MessageResponse);
        return JsonRequest(context) ? Error(context, 500) : HtmlRequest(context) ? Results.Redirect($"/rooms/{message.RoomId}/messages/{id}") : Error(context, 406);
    }
    private static async Task<IResult> DestroyMessage(HttpContext context, long id, ChatStore store, IRealtimeEvents realtime, IMediaService media)
    {
        var message = store.Message(id, context.User()!.Id, RequiredMessageRoom(context));
        var blobs = store.MessageBlobs(id, context.User()!.Id);
        store.DeleteMessage(context.User()!, message.RoomId, id);
        await realtime.MessageChangedAsync(message.RoomId, id, "remove", message.ClientMessageId);
        await media.PurgeBlobsAsync(blobs);
        return BotRequest(context) ? Results.NoContent() : TurboRequest(context) ? Turbo(ChatRenderer.Turbo("remove", "message_" + message.ClientMessageId)) : Error(context, 406);
    }
    private static async Task<IResult> CreateBoost(HttpContext context, long messageId, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime)
    {
        var message = store.Message(messageId, context.User()!.Id, RouteRoom(context));
        var input = await Input.Read(context, raw: BotRequest(context));
        if (!BotRequest(context)) input.RequireObject("boost");
        var content = input.Value(BotRequest(context) ? "raw" : "boost[content]") ?? "";
        if (BotRequest(context) && string.IsNullOrWhiteSpace(content)) return Results.StatusCode(422);
        var id = store.CreateBoost(context.User()!, messageId, content);
        var boost = store.Message(messageId, context.User()!.Id).Boosts.Single(x => x.Id == id);
        await realtime.TurboStreamAsync(ChatRenderer.RoomStream(store.Room(message.RoomId, context.User()!.Id)), "append", "boosts_message_" + message.ClientMessageId, renderer.BoostHtml(boost), true);
        return BotRequest(context) ? JsonRequest(context) ? Results.Json(new BoostResponse(boost.Id, boost.Content, RichText.Iso(boost.CreatedAt), new ChatUserResponse(boost.BoosterId, boost.BoosterName, Role(context.User()!.Role), renderer.Origin() + renderer.AvatarUrl(boost.BoosterId, boost.BoosterUpdatedAt)), new BoostMessageResponse(message.Id, renderer.Origin() + $"/rooms/{message.RoomId}/messages/{message.Id}")), ChatJsonContext.Default.BoostResponse, statusCode: 201) : Error(context, 500) : Results.Redirect($"/messages/{messageId}/boosts");
    }
    private static async Task<IResult> DestroyBoost(HttpContext context, long messageId, long id, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime)
    {
        var message = store.Message(messageId, context.User()!.Id, RouteRoom(context));
        store.DeleteBoost(context.User()!, messageId, id);
        await realtime.TurboStreamAsync(ChatRenderer.RoomStream(store.Room(message.RoomId, context.User()!.Id)), "remove", "boost_" + id);
        return Results.NoContent();
    }
    private static async Task<IResult> DeleteRoom(HttpContext context, long id, ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime, IMediaService media, bool directNamespace)
    {
        var room = store.Room(id, context.User()!.Id);
        if (directNamespace && !room.Direct) return RoomNotFound(context);
        var blobs = store.RoomBlobs(id, context.User()!.Id);
        var users = store.DeleteRoom(context.User()!, id, directNamespace);
        // Never leak private room activity on the global stream.
        foreach (var userId in users)
        {
            await realtime.TurboStreamAsync(ChatRenderer.UserStream(userId), "remove", room.Dom("list"));
            await realtime.DisconnectUserAsync(userId, true);
        }
        await media.PurgeBlobsAsync(blobs);
        return Results.Redirect("/");
    }
    private static async Task BroadcastRoom(ChatStore store, ChatRenderer renderer, IRealtimeEvents realtime, long roomId, string action)
    {
        var members = store.Members(roomId);
        foreach (var user in members)
        {
            var room = store.Room(roomId, user.Id);
            if (!room.Direct && room.Involvement == "invisible") continue;
            await realtime.TurboStreamAsync(ChatRenderer.UserStream(user.Id), action, action == "prepend" ? room.Direct ? "direct_rooms" : "shared_rooms" : room.Dom("list"), renderer.SidebarRoom(room, user.Id));
        }
    }
    private static async Task<long?> Attachment(HttpContext context, Input input, IMediaService media, IRailsCrypto crypto, IDataStore db)
    {
        var file = input.File("message[attachment]") ?? input.File("attachment");
        if (file is not null)
        {
            var stagingId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            await media.SaveRecordUploadAsync("ChatUpload", stagingId, "attachment", file, context.RequestAborted);
            return db.Read(queryConnection3 => queryConnection3.QuerySingleOrDefault<long?>("select blob_id from active_storage_attachments where record_type='ChatUpload' and record_id=@stagingId and name='attachment'",new { stagingId }));
        }
        var token = input.Value("message[attachment]") ?? input.Value("attachment");
        if (string.IsNullOrEmpty(token)) return null;
        var raw = crypto.VerifyStorage(token, "blob_id");
        if (raw is not null && long.TryParse(raw, out var id)) return id;
        throw new ChatHttpException(500, "Invalid attachment signature");
    }
    private static MessageResponse MessageJson(ChatMessage message, ChatRenderer renderer, RichText richText) => new(message.Id, RichText.Iso(message.CreatedAt),
        new MessageBodyResponse(string.IsNullOrWhiteSpace(richText.PlainText(message.Body)) ? message.AttachmentFilename ?? "" : richText.PlainText(message.Body), richText.BodyHtml(message.Body, user => renderer.Avatar(user))),
        new ChatUserResponse(message.CreatorId, message.CreatorName, Role(message.CreatorRole), renderer.Origin() + renderer.AvatarUrl(message.CreatorId, message.CreatorUpdatedAt)),
        new MessageRoomResponse(message.RoomId), renderer.Origin() + $"/rooms/{message.RoomId}/messages/{message.Id}");
    private static string Role(int role) => role switch { 1 => "administrator", 2 => "bot", _ => "member" };
    private static RoomRecord? LastRoom(HttpContext context, ChatStore store, long userId)
    {
        var rooms = store.Rooms(userId);
        return rooms.FirstOrDefault(x => x.Id.ToString(CultureInfo.InvariantCulture) == context.Request.Cookies["last_room"]) ?? rooms.OrderBy(x => x.CreatedAt).FirstOrDefault();
    }
    private static long? RouteRoom(HttpContext context) => NullableLong(context.Request.RouteValues["roomId"]);
    private static long RequiredMessageRoom(HttpContext context) => RouteRoom(context) ?? ScopedParameter(context.Request.Query["room_id"]) ?? throw new ChatHttpException(404, "Room not found");
    private static long? ScopedParameter(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : throw new ChatHttpException(404, "Record not found");
    }
    private static long Long(object? value) => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var number) ? number : 0;
    private static long RailsRoomId(string value)
    {
        var prefix = System.Text.RegularExpressions.Regex.Match(value, @"^\s*[+-]?[0-9]+").Value;
        return long.TryParse(prefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }
    private static long? NullableLong(object? value) => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var number) ? number : null;
    private static bool BotRequest(HttpContext context) => context.Request.RouteValues.ContainsKey("bot_key");
    private static bool JsonRequest(HttpContext context) => context.Items["RailsFormat"] is string format ? format == "json" : BotRequest(context) || context.Request.Path.Value?.EndsWith(".json", StringComparison.Ordinal) == true || context.Request.Headers.Accept.Any(x => x?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    private static bool TurboRequest(HttpContext context) => context.Items["RailsFormat"] is string format ? format == "turbo_stream" : context.Request.Headers.Accept.Any(x => x?.Contains("text/vnd.turbo-stream.html", StringComparison.OrdinalIgnoreCase) == true);
    private static bool HtmlRequest(HttpContext context) => context.Items["RailsFormat"] is string format ? format == "html" : !JsonRequest(context) && (!TurboRequest(context) || context.Request.Headers.Accept.Any(x => x?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true));
    private static IResult RoomNotFound(HttpContext context)
    {
        var crypto = context.RequestServices.GetRequiredService<IRailsCrypto>();
        context.Response.Cookies.Append("campfire_chat_alert", crypto.Sign("Room not found or inaccessible", "chat_flash"), new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps, MaxAge = TimeSpan.FromMinutes(5) });
        return Results.Redirect("/");
    }
    private static IResult Html(string html) => Results.Text(html, "text/html; charset=utf-8");
    private static IResult Turbo(string html) => Results.Text(html, "text/vnd.turbo-stream.html; charset=utf-8");
    private static IResult Error(HttpContext context, int status)
    {
        if (JsonRequest(context)) return Results.Json(new ChatErrorResponse(status, Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status)), ChatJsonContext.Default.ChatErrorResponse, statusCode: status);
        return status is 404 or 500 ? new ChatErrorResult(status) : Results.StatusCode(status);
    }
    private sealed class ChatErrorResult(int status) : IResult
    {
        public Task ExecuteAsync(HttpContext context) => Campfire.Features.WebSupport.RailsHttpCompatibility.ErrorPage(context, status);
    }

    private sealed class Input
    {
        private readonly Dictionary<string, string?> values = [];
        private IFormFileCollection? files;
        public string? Value(string key) => values.GetValueOrDefault(key);
        public bool Has(string key) => values.ContainsKey(key);
        public void RequireObject(string name)
        {
            if (!values.Keys.Any(key => key.StartsWith(name + "[", StringComparison.Ordinal)) && files?.Any(file => file.Name.StartsWith(name + "[", StringComparison.Ordinal)) != true)
                throw new ChatHttpException(string.IsNullOrWhiteSpace(Value(name)) ? 400 : 500, "param is missing or the value is empty: " + name);
        }
        public IFormFile? File(string key) => files?.GetFile(key);
        public IEnumerable<long> Ids() => (Value("user_ids[]") ?? Value("user_ids") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Long).Where(x => x > 0);
        public static async Task<Input> Read(HttpContext context, bool raw = false)
        {
            var input = new Input();
            foreach (var pair in context.Request.Query) input.values[pair.Key] = pair.Value.ToString();
            if (raw)
            {
                context.Request.EnableBuffering();
                input.values["raw"] = await new StreamReader(context.Request.Body, leaveOpen: true).ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;
            }
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                foreach (var pair in form) input.values[pair.Key] = pair.Value.ToString();
                input.files = form.Files;
                // Button-to direct placeholders use query parameters.
                if (!input.values.ContainsKey("user_ids[]")) input.values["user_ids[]"] = context.Request.Query["user_ids[]"].ToString();
            }
            else if (context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                foreach (var property in json.RootElement.ValueKind == JsonValueKind.Object ? json.RootElement.EnumerateObject().ToArray() : [])
                {
                    if (property.Value.ValueKind == JsonValueKind.Object) foreach (var child in property.Value.EnumerateObject()) input.values[property.Name + "[" + child.Name + "]"] = child.Value.ValueKind == JsonValueKind.Null ? null : child.Value.ToString();
                    else if (property.Value.ValueKind == JsonValueKind.Array) input.values[property.Name] = string.Join(',', property.Value.EnumerateArray().Select(x => x.ToString()));
                    else input.values[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
                }
            }
            else if (!raw) input.values["user_ids[]"] = context.Request.Query["user_ids[]"].ToString();
            return input;
        }
    }
}
