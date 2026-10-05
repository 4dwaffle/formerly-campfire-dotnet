using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Campfire.Contracts;

namespace Campfire.Features.Chat;

public sealed partial class ChatRenderer : IChatRenderer, IPageRenderer, IDisposable
{
    private readonly ChatStore store;
    private readonly RichText richText;
    private readonly IRailsCrypto crypto;
    private readonly IMediaService media;
    private readonly IHttpContextAccessor accessor;
    private readonly Dictionary<string, string> assets;
    private readonly string stylesheetTags;
    private readonly string importmapTags;
    private readonly string vapidKey;
    private readonly string preparedComposer, preparedOptimistic, preparedLightbox, preparedDirectNew;
    private static readonly AsyncLocal<bool> suppressCsrf = new();
    private readonly MemoryCache messageFragments = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    private static readonly object csrfInputKey = new();
    private static readonly object directNamesKey = new();
    private static readonly object layoutAccountKey = new();
    private long fragmentHits, fragmentMisses;
    public long FragmentCacheHits => Interlocked.Read(ref fragmentHits);
    public long FragmentCacheMisses => Interlocked.Read(ref fragmentMisses);
    public void Dispose() => messageFragments.Dispose();
    public ChatRenderer(ChatStore store, RichText richText, IRailsCrypto crypto, IMediaService media, IWebHostEnvironment environment, IHttpContextAccessor accessor, IConfiguration configuration)
    {
        this.store = store; this.richText = richText; this.crypto = crypto; this.media = media; this.accessor = accessor;
        var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        assets = manifest.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => "/assets/" + x.Value.GetProperty("digested_path").GetString());
        preparedComposer = PrepareFragment(HtmlFragments.Composer);
        preparedOptimistic = PrepareFragment(HtmlFragments.Optimistic);
        preparedLightbox = PrepareFragment(HtmlFragments.Lightbox);
        preparedDirectNew = PrepareFragment(HtmlFragments.DirectNew);
        stylesheetTags = File.ReadAllText(Path.Combine(root, "stylesheets.html"));
        importmapTags = File.ReadAllText(Path.Combine(root, "importmap.html"));
        vapidKey = configuration["VAPID_PUBLIC_KEY"] ?? "";
    }
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    string IPageRenderer.Layout(UserRecord user, string body, string title, string nav, string footer, string sidebar, string bodyClass) => Layout(user, body, title, nav, footer, sidebar, bodyClass);
    string IPageRenderer.Avatar(UserRecord user) => Avatar(user);
    public string InstallInstructions(HttpContext context) => PwaInstructions.Install(context, this);
    public string Asset(string logical) => assets.GetValueOrDefault(logical, "/assets/" + logical);
    public string Icon(string name, int size = 20) => $"<img aria-hidden=\"true\" src=\"{E(Asset(name))}\" width=\"{size}\" height=\"{size}\">";
    public string AvatarUrl(long id, string updatedAt) => "/users/" + Uri.EscapeDataString(crypto.SignedId("User", id, "avatar")) + "/avatar?v=" + RichText.Version(updatedAt);
    public string Avatar(UserRecord user, int size = 48) => $"<a title=\"{E(Title(user.Name, user.Bio))}\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\"/users/{user.Id}\"><img aria-hidden=\"true\" src=\"{E(AvatarUrl(user.Id, user.UpdatedAt))}\" width=\"{size}\" height=\"{size}\"></a>";
    public static string Title(string name, string? bio) => string.IsNullOrWhiteSpace(bio) ? name : name + " – " + bio;
    public static string RoomStream(RoomRecord room) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"gid://campfire/{room.Type}/{room.Id}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ":messages";
    public static string UserStream(long userId) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"gid://campfire/User/{userId}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ":rooms";
    public string StreamSource(string stream, string channel = "Turbo::StreamsChannel") => $"<turbo-cable-stream-source channel=\"{E(channel)}\" signed-stream-name=\"{E(crypto.SignStream(stream))}\"></turbo-cable-stream-source>";
    public string CsrfInput()
    {
        var context = accessor.HttpContext;
        if (context is null || suppressCsrf.Value) return "";
        if (context.Items.TryGetValue(csrfInputKey, out var existing)) return (string)existing!;
        var token = context.RequestServices.GetRequiredService<IAuthService>().CsrfToken(context);
        var input = $"<input type=\"hidden\" name=\"authenticity_token\" value=\"{E(token)}\">";
        context.Items[csrfInputKey] = input;
        return input;
    }
    public string Forms(string html) => FormPattern().Replace(html, m => m.Value + CsrfInput());
    private string PrepareFragment(string source) => AssetPattern().Replace(source, match => E(Asset(match.Groups[1].Value)));
    private string Fragment(string html, UserRecord user, RoomRecord? room = null)
    {
        if (html.Contains("%%ROOM_ID%%", StringComparison.Ordinal)) html = html.Replace("%%ROOM_ID%%", room?.Id.ToString() ?? "");
        if (html.Contains("%%USER_ID%%", StringComparison.Ordinal)) html = html.Replace("%%USER_ID%%", user.Id.ToString());
        if (html.Contains("%%USER_NAME%%", StringComparison.Ordinal)) html = html.Replace("%%USER_NAME%%", E(user.Name));
        if (html.Contains("%%USER_TITLE%%", StringComparison.Ordinal)) html = html.Replace("%%USER_TITLE%%", E(Title(user.Name, user.Bio)));
        if (html.Contains("%%USER_AVATAR%%", StringComparison.Ordinal)) html = html.Replace("%%USER_AVATAR%%", E(AvatarUrl(user.Id, user.UpdatedAt)));
        return Forms(html);
    }
    public string Layout(UserRecord user, string body, string title = "Campfire", string nav = "", string footer = "", string sidebar = "", string bodyClass = "", RoomRecord? room = null, bool forceApplicationLayout = false)
    {
        var context = accessor.HttpContext;
        var alert = context is null ? null : crypto.Verify(context.Request.Cookies["campfire_chat_alert"] ?? "", "chat_flash");
        if (context is not null && !string.IsNullOrEmpty(alert)) context.Response.Cookies.Delete("campfire_chat_alert");
        if (!forceApplicationLayout && context is not null && !string.IsNullOrWhiteSpace(context.Request.Headers["Turbo-Frame"]))
        {
            // Original turbo_rails/frame layout yields only csrf_meta_tags,
            // view :head content and the requested body. The full application
            // layout is used when there is no Turbo-Frame request header.
            var frameCsrf = E(context.RequestServices.GetRequiredService<IAuthService>().CsrfToken(context));
            var frameHead = room is null ? "" : $"<meta name=\"turbo-cache-control\" content=\"no-preview\"><meta name=\"current-room-id\" content=\"{room.Id}\">";
            return $"<html><head><meta name=\"csrf-param\" content=\"authenticity_token\"><meta name=\"csrf-token\" content=\"{frameCsrf}\">{frameHead}</head><body>{body}</body></html>";
        }
        var flash = string.IsNullOrEmpty(alert) ? "" : $"<div class=\"flash\" data-controller=\"element-removal\" data-action=\"animationend->element-removal#remove\"><div class=\"flash__inner shadow\" style=\"--flash-background: var(--color-negative)\">{Icon("alert.svg", 24)}</div><span class=\"for-screen-reader\" role=\"alert\" aria-atomic=\"true\">{E(alert)}</span></div>";
        var account = LayoutAccount();
        var csrf = context is null ? "" : E(context.RequestServices.GetRequiredService<IAuthService>().CsrfToken(context));
        var currentUserMeta = user.Id == 0 ? "" : $"<meta name=\"current-user-id\" content=\"{user.Id}\"><meta name=\"current-user-name\" content=\"{E(user.Name)}\">";
        // Account custom styles are administrator-authored CSS. Escape a closing style
        // delimiter so the setting never creates executable HTML in this layout.
        var custom = string.IsNullOrWhiteSpace(account.CustomStyles) ? "" : "<style data-turbo-track=\"reload\">" + account.CustomStyles.Replace("<", "\\3c ", StringComparison.Ordinal) + "</style>";
        return $$"""
            <!DOCTYPE html><html><head><title>{{E(title)}}</title>
            <meta name="viewport" content="width=device-width, initial-scale=1, user-scalable=no, interactive-widget=resizes-content"><meta name="view-transition" content="same-origin"><meta name="color-scheme" content="light dark"><meta name="theme-color" content="#ffffff" media="(prefers-color-scheme: light)"><meta name="theme-color" content="#000000" media="(prefers-color-scheme: dark)"><meta name="apple-mobile-web-app-capable" content="yes">
            <meta name="csrf-param" content="authenticity_token"><meta name="csrf-token" content="{{csrf}}">{{currentUserMeta}}<meta name="action-cable-url" content="/cable"><meta name="vapid-public-key" content="{{E(vapidKey)}}"><meta name="turbo-prefetch" content="true">
            <link rel="manifest" href="/webmanifest.json"><link rel="icon" href="/account/logo?v={{RichText.Version(account.UpdatedAt)}}" type="image/png"><link rel="apple-touch-icon" href="/account/logo?v={{RichText.Version(account.UpdatedAt)}}">{{stylesheetTags}}{{custom}}{{importmapTags}}
            {{(room is null ? "" : $"<meta name=\"turbo-cache-control\" content=\"no-preview\"><meta name=\"current-room-id\" content=\"{room.Id}\">")}}
            </head><body class="{{E(bodyClass)}}{{(user.IsAdmin ? " admin" : "")}}{{(account.HasLogo ? " account-has-logo" : "")}}" data-controller="local-time lightbox"><a href="#main-content" class="skip-navigation btn">Skip to main content</a><nav id="nav">{{nav}}</nav>{{flash}}<main id="main-content">{{body}}<footer id="footer">{{footer}}</footer></main><aside id="sidebar" data-controller="toggle-class" data-toggle-class-toggle-class="open">{{sidebar}}</aside>{{Fragment(preparedLightbox, user)}}<a href="https://once.com" id="app-logo" target="_blank" aria-label="Once software from 37signals home page"><img alt="Campfire logo" src="{{E(Asset("campfire-icon.png"))}}" width="256" height="216"></a></body></html>
            """;
    }
    public string SidebarFrame() => "<turbo-frame id=\"user_sidebar\" src=\"/users/me/sidebar\" data-turbo-permanent=\"true\" target=\"_top\" data-controller=\"rooms-list read-rooms turbo-frame\" data-rooms-list-unread-class=\"unread\" data-action=\"presence:present@window->rooms-list#read read-rooms:read->rooms-list#read turbo:frame-load->rooms-list#loaded refresh-room:visible@window->turbo-frame#reload\"></turbo-frame>";
    private AccountPresentation LayoutAccount()
    {
        var context = accessor.HttpContext;
        if (context is null) return store.LayoutAccount();
        if (context.Items.TryGetValue(layoutAccountKey, out var value)) return (AccountPresentation)value!;
        var account = store.LayoutAccount();
        context.Items[layoutAccountKey] = account;
        return account;
    }
    public string RoomName(RoomRecord room, long viewerId)
    {
        if (!room.Direct) return room.Name ?? "";
        var members = store.Members(room.Id);
        var names = members.Where(x => x.Id != viewerId).Select(x => x.Name).ToArray();
        if (names.Length == 0) names = members.Select(x => x.Name).ToArray();
        return Sentence(names);
    }
    private static string Sentence(string[] values) => values.Length switch { 0 => "", 1 => values[0], 2 => values[0] + " and " + values[1], _ => string.Join(", ", values[..^1]) + ", and " + values[^1] };
    public string RoomHtml(RoomRecord room, List<ChatMessage> messages, UserRecord user, string origin)
        => RoomShell(room, MessagesHtml(messages, user.Id), user, origin);
    private string RoomShell(RoomRecord room, string messagesHtml, UserRecord user, string origin)
    {
        var name = RoomName(room, user.Id);
        var nav = $"<span class=\"btn btn--reversed btn--faux room--current\"><h1 class=\"room__contents txt-medium overflow-ellipsis\">{(room.Direct ? "<span class=\"for-screen-reader\">Ping with </span>" : "")}{E(name)}</h1></span><a class=\"btn\" style=\"view-transition-name: edit-room-{room.Id}\" data-room-id=\"{room.Id}\" href=\"{room.EditPath}\">{Icon("menu-dots-horizontal.svg")}<span class=\"for-screen-reader\">Settings for this {(room.Direct ? "Ping" : "room")}</span></a>{NotificationBell(room)}";
        var invitation = RequestShowInvitation(room.Id) ? Invitation(origin) : "";
        var body = $$"""
            <div id="message-area" class="message-area" contents="true" data-controller="messages presence drop-target" data-action="turbo:before-stream-render@document-&gt;messages#beforeStreamRender keydown.up@document-&gt;messages#editMyLastMessage dragenter-&gt;drop-target#dragenter dragover-&gt;drop-target#dragover drop-&gt;drop-target#drop visibilitychange@document-&gt;presence#visibilityChanged" data-messages-first-of-day-class="message--first-of-day" data-messages-formatted-class="message--formatted" data-messages-me-class="message--me" data-messages-mentioned-class="message--mentioned" data-messages-threaded-class="message--threaded" data-messages-page-url-value="{{E(origin)}}/rooms/{{room.Id}}/messages">
            {{Fragment(preparedOptimistic, user)}}<div id="{{room.Dom("messages")}}" class="messages" data-controller="maintain-scroll refresh-room" data-action="turbo:before-stream-render@document-&gt;maintain-scroll#beforeStreamRender visibilitychange@document-&gt;refresh-room#visibilityChanged online@window-&gt;refresh-room#online" data-messages-target="messages" data-refresh-room-loaded-at-value="{{RichText.Epoch(room.UpdatedAt)}}" data-refresh-room-url-value="/rooms/{{room.Id}}/refresh">{{invitation}}{{messagesHtml}}</div>
            {{StreamSource(RoomStream(room), "RoomMessagesChannel")}}<button class="message-area__return-to-latest btn" data-action="messages#returnToLatest" data-messages-target="latest" hidden="hidden">{{Icon("arrow-down.svg")}}<span class="for-screen-reader">Jump to newest message</span></button></div>
            """;
        return Layout(user, body, name, nav, Fragment(preparedComposer, user, room), SidebarFrame(), "sidebar", room);
    }
    public string MessageHtml(long messageId, long viewerId) => MessageHtml(store.MessageMetadata(messageId, viewerId), viewerId);
    public string BroadcastMessageHtml(long messageId, long viewerId)
    {
        var previous = suppressCsrf.Value;
        suppressCsrf.Value = true;
        try { return MessageHtml(messageId, viewerId); }
        finally { suppressCsrf.Value = previous; }
    }
    public string MessagesHtml(IEnumerable<ChatMessage> messages, long viewerId)
    {
        var rows = messages.ToList();
        var segments = MessagesSegments(rows, viewerId);
        using var bytes = new MemoryStream();
        foreach (var segment in segments) bytes.Write(segment.Span);
        return Encoding.UTF8.GetString(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }
    public string MessageHtml(ChatMessage message, long viewerId)
    {
        return MessagesHtml([message], viewerId);
    }
    private string MessageRoomName(ChatMessage message)
    {
        if (message.RoomType != "Rooms::Direct") return message.RoomName ?? "";
        var context = accessor.HttpContext;
        Dictionary<long, string>? names = null;
        if (context is not null)
        {
            if (!context.Items.TryGetValue(directNamesKey, out var value)) context.Items[directNamesKey] = value = new Dictionary<long, string>();
            names = (Dictionary<long, string>)value!;
            if (names.TryGetValue(message.RoomId, out var existing)) return existing;
        }
        var name = RoomName(new RoomRecord { Id = message.RoomId, Type = message.RoomType }, 0);
        if (names is not null) names[message.RoomId] = name;
        return name;
    }
    private string RenderMessageHtml(ChatMessage message, string roomName)
    {
        // MessagesHelper#message_tag catches a missing creator while yielding
        // the message partial and renders messages/unrenderable. Keep the
        // authorized row visible instead of dropping it through an inner join.
        if (message.CreatorMissing) return "<div class=\"message message--formatted message--failed center\"><div class=\"message__body\"><div class=\"message__body-content txt-align-center\">Failed to load message content</div></div></div>";
        var client = E(message.ClientMessageId);
        var plaintext = richText.PlainText(message.Body);
        var creatorTitle = E(Title(message.CreatorName, message.CreatorBio));
        var permalink = $"/rooms/{message.RoomId}/@{message.Id}";
        return $$"""
            <div id="message_{{client}}" class="message {{(OnlyEmoji(plaintext) ? "message--emoji" : "")}}" data-controller="reply" data-user-id="{{message.CreatorId}}" data-message-id="{{message.Id}}" data-message-timestamp="{{RichText.Epoch(message.CreatedAt)}}" data-message-updated-at="{{RichText.Epoch(message.UpdatedAt)}}" data-sort-value="{{RichText.Epoch(message.CreatedAt)}}" data-messages-target="message" data-search-results-target="message" data-refresh-room-target="message" data-reply-composer-outlet="#composer">
            <h2 class="message__day-separator"><time datetime="{{RichText.Iso(message.CreatedAt)}}" data-local-time-target="date"></time></h2>
            <figure class="avatar message__avatar"><a title="{{creatorTitle}}" class="btn avatar" data-turbo-frame="_top" href="/users/{{message.CreatorId}}"><img aria-hidden="true" src="{{E(AvatarUrl(message.CreatorId, message.CreatorUpdatedAt))}}" width="48" height="48"></a></figure>
            <turbo-frame id="edit_message_{{client}}"><div class="message__body"><div class="message__body-content"><div class="message__meta"><h3 class="message__heading"><span class="message__author" title="{{creatorTitle}}"><strong data-reply-target="author">{{E(message.CreatorName)}}</strong></span><a target="_top" class="message__permalink" href="{{permalink}}"><time class="message__timestamp" datetime="{{RichText.Iso(message.CreatedAt)}}" data-local-time-target="time"></time></a><span class="message__room"> <a target="_top" data-reply-target="link" href="{{permalink}}">{{E(roomName)}}</a></span></h3>{{MessageActions(message)}}</div>
            {{PresentationHtml(message, plaintext)}}{{BoostsHtml(message)}}</div></div></turbo-frame></div>
            """;
    }
    public string PresentationHtml(ChatMessage message)
        => PresentationHtml(message, message.AttachmentFilename is null ? richText.PlainText(message.Body) : "");
    private string PresentationHtml(ChatMessage message, string plaintext)
    {
        var html = message.AttachmentFilename is not null ? media.AttachmentHtml(message.Id) : SoundPresentation(plaintext) ?? richText.Presentation(message.Body, user => Avatar(user));
        return $"<div id=\"presentation_message_{E(message.ClientMessageId)}\" dir=\"auto\" data-reply-target=\"body\" data-messages-target=\"body\">{html}</div>";
    }
    public string MessageActions(ChatMessage message)
    {
        var quick = string.Concat(new[] { ("👍", "Thumbs up"), ("👏", "Clapping"), ("👋", "Waving hand"), ("💪", "Muscle"), ("❤️", "Red heart"), ("😂", "Face with tears of joy"), ("🎉", "Party popper"), ("🔥", "Fire") }.Select(x => $"<form data-turbo-frame=\"boosting_message_{E(message.ClientMessageId)}\" data-action=\"popup#close\" action=\"/messages/{message.Id}/boosts\" accept-charset=\"UTF-8\" method=\"post\"><input type=\"hidden\" name=\"boost[content]\" value=\"{x.Item1}\"><button type=\"submit\" title=\"{x.Item2}\" class=\"btn message__action-btn\" data-emoji=\"{x.Item1}\"><figure class=\"margin-none boost-character\">{x.Item1}</figure><span class=\"for-screen-reader\">{x.Item2}</span></button></form>"));
        var replyOrAttachment = message.AttachmentBlobId.HasValue
            ? $"<a class=\"btn message__action-btn center full-width hide-in-ios-pwa\" title=\"Download\" aria-label=\"Download\" href=\"{E(AttachmentUrl(message))}?disposition=attachment\">{Icon("download.svg")}</a><button class=\"btn message__action-btn center full-width\" data-controller=\"web-share\" data-action=\"web-share#share\" data-web-share-files-value=\"{E(AttachmentUrl(message))}\" data-web-share-title-value=\"{E(message.AttachmentFilename)}\" title=\"Share\" aria-label=\"Share\">{Icon("share.svg")}</button>"
            : $"<button class=\"btn message__action-btn center full-width\" data-action=\"reply#reply\" title=\"Reply\" aria-label=\"Reply\">{Icon("reply.svg")}</button>";
        return Forms($$"""
            <div class="message__actions" data-controller="soft-keyboard"><details class="position-relative" data-controller="popup" data-action="keydown.esc-&gt;popup#close toggle-&gt;popup#toggle click@document-&gt;popup#closeOnClickOutside" data-popup-orientation-top-class="popup-orientation-top"><summary class="btn message__action-btn message__options-btn">{{Icon("menu-dots-horizontal.svg")}}<span class="for-screen-reader">Message options</span></summary><div class="message__actions-menu border shadow" data-popup-target="menu"><div class="quick-boosts">{{quick}}<a class="btn message__action-btn message__boost-btn" data-turbo-frame="new_boost_message_{{E(message.ClientMessageId)}}" data-action="soft-keyboard#open popup#close" href="/messages/{{message.Id}}/boosts/new">{{Icon("boost.svg")}}<span class="for-screen-reader">New boost</span></a></div><div class="flex flex-wrap border-top margin-block-start-half pad-block-start-half message__actions-grid">{{replyOrAttachment}}<button class="btn message__action-btn center full-width" title="Copy link" aria-label="Copy link" data-controller="copy-to-clipboard" data-action="copy-to-clipboard#copy" data-copy-to-clipboard-success-class="btn--success" data-copy-to-clipboard-content-value="{{E(Origin())}}/rooms/{{message.RoomId}}/@{{message.Id}}">{{Icon("link.svg")}}</button><a class="btn message__action-btn center full-width message__edit-btn" data-turbo-frame="edit_message_{{E(message.ClientMessageId)}}" title="Edit" aria-label="Edit" href="/rooms/{{message.RoomId}}/messages/{{message.Id}}/edit">{{Icon("pencil.svg")}}</a></div></div></details></div>
            """);
    }
    private string AttachmentUrl(ChatMessage message) => $"/rails/active_storage/blobs/redirect/{crypto.SignStorage(message.AttachmentBlobId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "blob_id")}/{Uri.EscapeDataString(message.AttachmentFilename ?? "file")}";
    public string BoostHtml(ChatBoost boost) => Forms($$"""
        <div id="boost_{{boost.Id}}" class="boost boost-item flex-inline postion--relative max-width align-center fill-white gap" data-controller="boost-delete" data-boost-delete-perform-class="boost--deleting" data-boost-delete-reveal-class="expanded" data-boost-delete-booster-id-value="{{boost.BoosterId}}"><figure class="avatar boost__avatar flex-item-no-shrink"><a title="{{E(Title(boost.BoosterName, boost.BoosterBio))}}" class="btn avatar" data-turbo-frame="_top" href="/users/{{boost.BoosterId}}"><img aria-label="{{E(boost.BoosterName + " boosted " + boost.Content)}}" src="{{E(AvatarUrl(boost.BoosterId, boost.BoosterUpdatedAt))}}" width="48" height="48"></a></figure><span role="button" class="txt-small{{(OnlyEmoji(boost.Content) ? " txt-medium" : "")}}" data-action="click-&gt;boost-delete#reveal keydown.enter-&gt;boost-delete#reveal:prevent" data-boost-delete-target="content">{{E(boost.Content)}}</span><form class="button_to" method="post" action="/messages/{{boost.MessageId}}/boosts/{{boost.Id}}"><input type="hidden" name="_method" value="delete"><button data-action="boost-delete#perform" data-boost-delete-target="button" class="btn btn--negative flex-item-justify-end boost__delete" type="submit">{{Icon("minus.svg")}}<span class="for-screen-reader">Delete this boost</span></button></form></div><span id="delete_boost_accessible_label" class="for-screen-reader">Press enter to delete this boost</span>
        """);
    public string BoostsHtml(ChatMessage message) => $$"""
        <turbo-frame id="boosting_message_{{E(message.ClientMessageId)}}"><div class="boosts flex flex-wrap align-center gap full-width" style="--column-gap: 0.4ch; --row-gap: 0" data-controller="turbo-streaming" data-action="turbo:submit-start->turbo-streaming#unsubscribe"><div class="flex-inline flex-wrap gap" id="boosts_message_{{E(message.ClientMessageId)}}" data-turbo-streaming-target="container">{{string.Concat(message.Boosts.Select(BoostHtml))}}</div><turbo-frame id="new_boost_message_{{E(message.ClientMessageId)}}"><div class="flex-inline message__boost-inline" data-controller="soft-keyboard"><a class="boost__action txt-small btn" action="soft-keyboard#open" href="/messages/{{message.Id}}/boosts/new">{{Icon("boost.svg")}}<span class="for-screen-reader">Add a boost</span></a></div></turbo-frame></div></turbo-frame>
        """;
    public string NewBoostHtml(ChatMessage message, UserRecord user) => Forms($$"""
        <turbo-frame id="new_boost_message_{{E(message.ClientMessageId)}}"><div class="boost flex-inline postion--relative max-width fill-white" style="--column-gap: var(--inline-space-half)"><form class="boost__form flex align-center gap expanded" data-controller="form scroll-into-view" data-turbo-frame="boosting_message_{{E(message.ClientMessageId)}}" data-action="keydown.esc-&gt;form#cancel" action="/messages/{{message.Id}}/boosts" accept-charset="UTF-8" method="post"><label class="boost__form-label flex gap" role="button" tabindex="0" aria-label="Add a boost"><figure class="avatar boost__avatar flex-item-no-shrink">{{Avatar(user)}}<span class="for-screen-reader">{{E(user.Name)}}</span></figure><input autofocus="autofocus" autocomplete="off" autocorrect="off" maxlength="16" required="required" pattern="\S+.*" class="input input--boost txt-small" size="16" type="text" name="boost[content]"></label><button type="submit" class="btn btn--reversed">{{Icon("check.svg")}}<span class="for-screen-reader">Submit</span></button><a data-turbo-frame="boosts_message_{{E(message.ClientMessageId)}}" data-form-target="cancel" class="btn btn--negative" href="/messages/{{message.Id}}/boosts">{{Icon("minus.svg")}}<span class="for-screen-reader">Cancel</span></a></form></div></turbo-frame>
        """);
    public string EditMessageHtml(ChatMessage message) => message.AttachmentBlobId.HasValue ? AttachmentEditHtml(message) : Forms($$"""
        <turbo-frame id="edit_message_{{E(message.ClientMessageId)}}"><div class="message__body position-relative" data-controller="scroll-into-view"><div class="message__body-content message__body-content--editing gap"><div class="composer--edit composer--rich-text"><form id="form_message_{{E(message.ClientMessageId)}}" data-controller="form" data-action="lexxy:file-accept-&gt;form#preventAttachment keydown.esc-&gt;form#cancel keydown.ctrl+enter-&gt;form#submit:prevent keydown.meta+enter-&gt;form#submit:prevent" action="/rooms/{{message.RoomId}}/messages/{{message.Id}}" method="post"><input type="hidden" name="_method" value="patch"><div class="full-width input input--actor min-width fill-white"><lexxy-editor rows="1" class="input lexxy-content" aria-multiline="true" aria-label="Edit message" autofocus="autofocus" permitted-attachment-types="application/vnd.campfire.mention application/vnd.actiontext.opengraph-embed" id="message_body" input="message_body_trix_input_{{E(message.ClientMessageId)}}" name="message[body]" value="{{E(richText.EditableBody(message.Body, user => Avatar(user)))}}" data-action="lexxy:change->typing-notifications#start keydown->composer#submitByKeyboard:capture" data-direct-upload-url="{{E(Origin())}}/rails/active_storage/direct_uploads" data-blob-url-template="{{E(Origin())}}/rails/active_storage/blobs/redirect/:signed_id/:filename"><lexxy-prompt trigger="@" name="mention" src="/autocompletable/users?room_id={{message.RoomId}}" remote-filtering="true" empty-results="No matches"></lexxy-prompt></lexxy-editor></div><a data-form-target="cancel" hidden="hidden" href="/rooms/{{message.RoomId}}/messages/{{message.Id}}">Close editor and discard changes</a><div class="message__edit-btns flex align-center justify-space-between gap full-width pad-block-start-half"><button type="submit" class="btn btn--reversed">{{Icon("check.svg")}}<span class="for-screen-reader">Save changes</span></button><button type="submit" class="btn btn--negative" form="delete_form_message_{{E(message.ClientMessageId)}}" data-turbo-confirm="Are you sure you want to delete this message?">{{Icon("trash.svg")}}<span class="for-screen-reader">Delete message</span></button></div></form></div></div><div class="message__actions flex flex-wrap"><a class="message__action-btn message__edit-close-btn txt-small btn btn--borderless" href="/rooms/{{message.RoomId}}/messages/{{message.Id}}">{{Icon("remove.svg")}}<span class="for-screen-reader">Close editor and discard changes</span></a></div><form id="delete_form_message_{{E(message.ClientMessageId)}}" data-turbo-frame="edit_message_{{E(message.ClientMessageId)}}" action="/rooms/{{message.RoomId}}/messages/{{message.Id}}" method="post"><input type="hidden" name="_method" value="delete"></form></div></turbo-frame>
        """);
    private string AttachmentEditHtml(ChatMessage message) => Forms($$"""
        <turbo-frame id="edit_message_{{E(message.ClientMessageId)}}"><div class="message__body position-relative" data-controller="scroll-into-view"><div class="message__body-content message__body-content--editing gap">{{media.AttachmentHtml(message.Id)}}<div class="message__edit-btns flex align-center justify-space-between gap full-width pad-block-start-half"><button type="submit" class="btn btn--negative center margin-block-end" form="delete_form_message_{{E(message.ClientMessageId)}}" data-turbo-confirm="Are you sure you want to delete this message?">{{Icon("trash.svg")}}<span class="for-screen-reader">Delete message</span></button></div></div><div class="message__actions flex flex-wrap"><a class="message__action-btn message__edit-close-btn txt-small btn btn--borderless" href="/rooms/{{message.RoomId}}/messages/{{message.Id}}">{{Icon("remove.svg")}}<span class="for-screen-reader">Close editor and discard changes</span></a></div><form id="delete_form_message_{{E(message.ClientMessageId)}}" data-turbo-frame="edit_message_{{E(message.ClientMessageId)}}" action="/rooms/{{message.RoomId}}/messages/{{message.Id}}" method="post"><input type="hidden" name="_method" value="delete"></form></div></turbo-frame>
        """);
    public string Sidebar(UserRecord user)
    {
        var rooms = store.Rooms(user.Id, visible: true);
        var participants = store.SidebarMembers(rooms.Where(x => x.Direct).Select(x => x.Id));
        var directs = string.Concat(rooms.Where(x => x.Direct).OrderByDescending(x => x.UpdatedAt).Select(x => SidebarRoom(x, user.Id, participants.GetValueOrDefault(x.Id, []))));
        var shared = string.Concat(rooms.Where(x => !x.Direct).Select(x => SidebarRoom(x, user.Id)));
        var placeholders = string.Concat(store.DirectPlaceholders(user.Id).Select(x => $"<form class=\"button_to\" method=\"post\" action=\"/rooms/directs?user_ids%5B%5D={x.Id}\"><button class=\"direct borderless fill-transparent unpad\"><span class=\"avatar\"><img src=\"{E(AvatarUrl(x.Id, x.UpdatedAt))}\" aria-hidden=\"true\"></span><span class=\"direct__author flex align-center gap max-width min-width border-radius txt-small\"><span class=\"txt-nowrap overflow-ellipsis\"><span class=\"for-screen-reader\">Start a ping with</span>{E(x.Name.Split(' ')[0])}</span></span></button></form>"));
        return Forms($$"""
            <turbo-frame id="user_sidebar" data-turbo-permanent="true" target="_top" data-controller="rooms-list read-rooms turbo-frame" data-rooms-list-unread-class="unread" data-action="presence:present@window->rooms-list#read read-rooms:read->rooms-list#read turbo:frame-load->rooms-list#loaded refresh-room:visible@window->turbo-frame#reload">{{StreamSource("rooms")}}{{StreamSource(UserStream(user.Id))}}<div class="sidebar__container overflow-y overflow-hide-scrollbar" data-controller="badge-dot" data-badge-dot-unread-class="unread" data-action="rooms-list:unread@window->badge-dot#update rooms-list:read@window->badge-dot#update turbo:submit-start->turbo-frame#unpermanize"><turbo-frame id="direct_rooms_control" target="_top"><div class="directs gap overflow-x overflow-hide-scrollbar"><a class="direct direct__new" data-turbo-frame="_self" href="/rooms/directs/new"><span class="avatar avatar--icon">{{Icon("messages-add.svg")}}</span><span class="direct__author flex max-width min-width border-radius pad-inline-half"><span class="for-screen-reader">New</span><span class="txt-small overflow-clip">Ping</span></span></a><div id="direct_rooms" contents data-controller="sorted-list" data-action="rooms-list:unread@window->sorted-list#updateItem">{{directs}}</div><div contents>{{placeholders}}</div></div></turbo-frame><div class="rooms position-relative flex flex-column gap"><div id="shared_rooms" contents data-controller="sorted-list">{{shared}}</div>{{(user.IsAdmin || ChatStore.CanCreate(user, LayoutAccount()) ? $"<a class=\"rooms__new-btn btn room align-center gap txt-reversed\" aria-label=\"New Chat Room\" href=\"/rooms/opens/new\">{Icon("add.svg")}</a>" : "")}}</div><button class="btn sidebar__toggle" data-action="toggle-class#toggle">{{Icon("menu.svg")}}<span class="for-screen-reader">Open menu</span></button></div><div class="flex align-end sidebar__tools gap justify-end"><a class="btn avatar flex-item-no-shrink sidebar__tool" href="/users/me/profile"><img src="{{E(AvatarUrl(user.Id, user.UpdatedAt))}}" width="48" height="48" aria-hidden="true"><span class="for-screen-reader">My Settings</span></a><a class="btn align-center gap txt-reversed sidebar__tool" href="/account/edit">{{Icon("settings.svg")}}<span class="for-screen-reader">Account Settings</span></a></div></turbo-frame>
            """);
    }
    public string SidebarRoom(RoomRecord room, long viewerId) => SidebarRoom(room, viewerId, room.Direct ? store.Members(room.Id) : []);
    private string SidebarRoom(RoomRecord room, long viewerId, List<UserRecord> participants)
    {
        var attrs = $"id=\"{room.Dom("list")}\" data-rooms-list-target=\"room\" data-badge-dot-target=\"unread\" data-sorted-list-target=\"item\" data-room-id=\"{room.Id}\" href=\"/rooms/{room.Id}\"";
        var unread = room.UnreadAt is null ? "" : " unread";
        if (!room.Direct) return $"<a class=\"align-center gap room btn txt-nowrap{unread}\" {attrs} data-sorted-list-name=\"{E(room.Name)}\" style=\"--column-gap: 0.5em\"><span class=\"overflow-ellipsis\">{E(room.Name)}</span></a>";
        var members = participants.Where(x => x.Id != viewerId).ToList();
        if (members.Count == 0) members = participants;
        var group = members.Count > 1;
        var avatars = string.Concat(members.Take(4).Select(x => $"<span class=\"avatar\"><img src=\"{E(AvatarUrl(x.Id, x.UpdatedAt))}\" width=\"{(group ? 20 : 48)}\" height=\"{(group ? 20 : 48)}\" aria-hidden=\"true\"></span>"));
        var initials = members.Select(x => string.Concat(x.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3).Select(n => n[..1].ToUpperInvariant()))).ToArray();
        var label = group ? initials.Length == 2 ? string.Join('+', initials) : Sentence(initials) : members.FirstOrDefault()?.Name.Split(' ')[0] ?? "";
        return $"<a class=\"direct{unread}\" {attrs} data-sorted-list-number=\"{RichText.Epoch(room.UpdatedAt)}\">{(group ? "<div class=\"avatar__group\">" + avatars + "</div>" : avatars)}<span class=\"direct__author flex align-center gap max-width min-width border-radius txt-small\"><span class=\"txt-nowrap overflow-ellipsis\"><span class=\"for-screen-reader\">Ping with</span>{E(label)}</span></span></a>";
    }
    public string NotificationBell(RoomRecord room) => $$"""
        <span><span class="button_to_change_notifying" data-controller="notifications" data-notifications-subscriptions-url-value="/users/me/push_subscriptions" data-notifications-attention-class="btn--pulsing"><turbo-frame id="{{room.Dom("involvement")}}" data-controller="turbo-frame" data-action="notifications:ready@window->turbo-frame#load" data-turbo-frame-url-param="/rooms/{{room.Id}}/involvement"><button class="btn" data-action="click->notifications#attemptToSubscribe" data-notifications-target="bell">{{Icon("notification-bell-loading.svg")}}<img aria-hidden="true" src="{{E(Asset("notification-bell-alert.svg"))}}" width="20" height="20" hidden><span class="for-screen-reader">Notification settings for this {{(room.Direct ? "Ping" : "room")}}</span></button></turbo-frame><dialog data-notifications-target="notAllowedNotice" class="dialog pad center center-block border-radius border shadow" style="--inline-space: var(--block-space)"><div class="flex flex-column txt-align-center"><span class="btn btn--faux center txt-x-large">{{Icon("notification-bell-alert.svg", 48)}}<span class="for-screen-reader">Notifications alert</span></span><section><h1 class="txt-large margin-none">Notifications aren’t allowed</h1><div class="txt-align-start margin-block-start">{{NotificationInstructions()}}</div></section><form method="dialog" class="flex align-center gap center"><button class="btn dialog__close" autofocus="true"><span class="for-screen-reader">Close</span>{{Icon("remove.svg")}}</button></form></div></dialog></span></span>
        """;
    private string NotificationInstructions() => accessor.HttpContext is { } context ? PwaInstructions.Browser(context, this) + PwaInstructions.System(context, this) + InstallInstructions(context) : "";
    public string InvolvementHtml(RoomRecord room)
    {
        var sequence = room.Direct ? new[] { "everything", "nothing" } : new[] { "mentions", "everything", "nothing", "invisible" };
        var next = sequence[(Math.Max(Array.IndexOf(sequence, room.Involvement), 0) + 1) % sequence.Length];
        var label = room.Involvement switch { "mentions" => "Notifying about @ mentions", "everything" => "Notifying about all messages", "invisible" => "Notifications are off and room invisible in sidebar", _ => "Notifications are off" };
        return Forms($"<turbo-frame id=\"{room.Dom("involvement")}\" data-controller=\"turbo-frame\" data-action=\"notifications:ready@window->turbo-frame#load\" data-turbo-frame-url-param=\"/rooms/{room.Id}/involvement\"><form class=\"button_to\" method=\"post\" action=\"/rooms/{room.Id}/involvement?involvement={next}\"><input type=\"hidden\" name=\"_method\" value=\"put\"><button role=\"checkbox\" aria-checked=\"true\" aria-labelledby=\"{room.Dom("involvement_label")}\" tabindex=\"0\" class=\"btn {E(room.Involvement)}\">{Icon("notification-bell-" + room.Involvement + ".svg")}<span class=\"for-screen-reader\" id=\"{room.Dom("involvement_label")}\">{label}</span></button></form></turbo-frame>");
    }
    public string DirectNew(UserRecord user) => Fragment(preparedDirectNew, user);
    public string Invitation(string origin)
    {
        var url = origin + "/join/" + LayoutAccount().JoinCode;
        var qrPath = "/qr_code/" + Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).Replace('+', '-').Replace('/', '_'));
        var regenerate = accessor.HttpContext?.User()?.IsAdmin == true ? $"<form class=\"button_to\" method=\"post\" action=\"/account/join_code\"><button class=\"btn btn--regenerate\" type=\"submit\">{Icon("refresh.svg")}<span class=\"for-screen-reader\">Regenerate join link</span></button></form>" : "";
        return Forms($$"""
            <div id="system_welcome" class="message message--formatted txt-align-center center"><div class="message__body center"><div class="message__body-content position-relative"><figure class="account-logo avatar center margin-block-end txt-large"><img src="/account/logo" alt="Account logo" width="300" height="300"></figure><div class="flex align-center gap"><div class="system-welcome--translation">{{Translation("invite_message")}}</div><p><strong>Welcome to Campfire</strong><br>To invite people to chat, share the join link below.</p></div><div class="flex flex-column align-center gap"><label class="flex flex-column gap full-width" style="--row-gap: 0.5em"><strong id="invite_label" class="invite-label">Share to invite more people</strong><span class="flex align-center gap input input--actor fill-white">{{Icon("person-add.svg")}}<input type="text" class="input" id="invite_url" value="{{E(url)}}" aria-labelledby="invite_label" readonly></span></label><div class="flex align-center gap"><a class="btn" href="{{E(qrPath)}}" data-lightbox-target="image" data-action="lightbox#open" data-lightbox-url-value="{{E(qrPath)}}"><span class="for-screen-reader">Show join link QR code</span>{{Icon("qr-code.svg")}}</a><button class="btn" data-controller="copy-to-clipboard" data-action="copy-to-clipboard#copy" data-copy-to-clipboard-success-class="btn--success" data-copy-to-clipboard-content-value="{{E(url)}}"><span class="for-screen-reader">Copy join link</span>{{Icon("copy-paste.svg")}}</button><button class="btn" hidden data-controller="web-share" data-action="web-share#share" data-web-share-url-value="{{E(url)}}" data-web-share-title-value="Link to join Campfire" data-web-share-text-value="Hit this link to join me in Campfire and start chatting."><span class="for-screen-reader">Share join link</span>{{Icon("share.svg")}}</button>{{regenerate}}</div></div></div></div></div>
            """);
    }
    public string Translation(string key)
    {
        var languages = Campfire.Features.Identity.Translations.All.GetValueOrDefault(key, []);
        return $"<details class=\"position-relative\" data-controller=\"popup\" data-action=\"keydown.esc->popup#close toggle->popup#toggle click@document->popup#closeOnClickOutside\" data-popup-orientation-top-class=\"popup-orientation-top\"><summary class=\"btn\" tabindex=\"-1\">{Icon("globe.svg")}<span class=\"for-screen-reader\">Translate</span></summary><div class=\"language-list-menu shadow\" data-popup-target=\"menu\"><dl class=\"language-list\">{string.Concat(languages.Select(pair => "<dt>" + E(pair.Flag) + "</dt><dd class=\"margin-none\">" + E(pair.Text) + "</dd>"))}</dl></div></details>";
    }
    public string Origin() => accessor.HttpContext is { } context ? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}" : "";
    public string Prompt(UserRecord user) => $"<lexxy-prompt-item search=\"{E(user.Name)}\" sgid=\"{E(richText.MentionSgid(user.Id))}\"><template type=\"menu\"><span class=\"autocomplete__item flex align-center gap unpad\">{Avatar(user)}<span class=\"autocompletable__name\">{E(user.Name)}</span></span></template><template type=\"editor\"><span class=\"mention\" sgid=\"{E(richText.MentionSgid(user.Id))}\">{Avatar(user)} {E(user.Name)}</span></template></lexxy-prompt-item>";
    public static string Turbo(string action, string target, string html = "", bool maintainScroll = false) => $"<turbo-stream action=\"{E(action)}\" target=\"{E(target)}\"{(maintainScroll ? " maintain_scroll=\"true\"" : "")}><template>{html}</template></turbo-stream>";
    private static bool OnlyEmoji(string text) => text.Length > 0 && text.EnumerateRunes().All(x => EmojiCharacters.IsEmoji(x.Value));
    private string? SoundPresentation(string text)
    {
        var match = SoundPattern().Match(text);
        if (!match.Success || !SoundCatalog.Values.TryGetValue(match.Groups[1].Value, out var sound)) return null;
        var content = sound.Image is null ? E(sound.Text) : $"<img src=\"{E(Asset("sounds/" + sound.Image))}\" width=\"{sound.Width}\" height=\"{sound.Height}\" class=\"align--middle\">";
        return $"<div class=\"sound\" data-controller=\"sound\" data-action=\"messages:play->sound#play\" data-sound-url-value=\"{E(Asset(match.Groups[1].Value + ".mp3"))}\"><button class=\"btn btn--plain\" data-action=\"sound#play\">🔊</button>{content}</div>";
    }
    [GeneratedRegex("%%ASSET:([^%]+)%%")] private static partial Regex AssetPattern();
    [GeneratedRegex("<form\\b[^>]*method=\"post\"[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex FormPattern();
    [GeneratedRegex(@"\A/play (\w+)\z")] private static partial Regex SoundPattern();
}
