using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.Contracts;
using Campfire.Features.Chat;
using Campfire.Features.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Campfire.ChatTests;

public sealed partial class ChatTests
{
    [Fact]
    public async Task ClosedRoomRequiresMembershipEvenForAdministratorAndSearchCannotLeakItsHistory()
    {
        await using var fixture = new ChatApplication();
        var store = fixture.Store;
        var room = store.CreateRoom(fixture.Member, "Rooms::Closed", "Private", [fixture.Member.Id]);
        var message = store.CreateMessage(fixture.Member, room, "<p>private needle</p>", "private-message");
        Assert.Throws<ChatHttpException>(() => store.Room(room, fixture.Admin.Id));
        Assert.Throws<ChatHttpException>(() => store.Message(message, fixture.Admin.Id));
        Assert.Empty(store.Search(fixture.Admin.Id, "needle"));
        Assert.Single(store.Search(fixture.Member.Id, "needle"));
        using var outsider = fixture.Client(fixture.Admin);
        Assert.Equal(HttpStatusCode.Redirect, (await outsider.GetAsync($"/rooms/{room}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/rooms/{room}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/autocompletable/users?room_id={room}")).StatusCode);
    }

    [Fact]
    public async Task MessageTransactionsMaintainRichTextFtsRoomAndOnlyDisconnectedVisibleUnread()
    {
        await using var fixture = new ChatApplication();
        var room = fixture.OpenRoom;
        fixture.Db.Execute("update memberships set connected_at=@now where room_id=@room and user_id=@connected; update memberships set involvement='invisible' where room_id=@room and user_id=@hidden", new { now = RequestUser.Timestamp(), room, connected = fixture.Connected.Id, hidden = fixture.Hidden.Id });
        var id = fixture.Store.CreateMessage(fixture.Member, room, "<p>coffee <strong>beans</strong></p>", "real-message");
        Assert.Equal("coffee beans", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@id", new { id }));
        Assert.Equal(2L, fixture.Db.Scalar<long>("select count(*) from memberships where room_id=@room and unread_at is not null", new { room }));
        Assert.NotNull(fixture.Db.Single<string>("select unread_at from memberships where room_id=@room and user_id=@id", new { room, id = fixture.Admin.Id }));
        Assert.Null(fixture.Db.Single<string>("select unread_at from memberships where room_id=@room and user_id=@id", new { room, id = fixture.Member.Id }));
        Assert.Null(fixture.Db.Single<string>("select unread_at from memberships where room_id=@room and user_id=@id", new { room, id = fixture.Connected.Id }));
        Assert.Equal(fixture.Store.Message(id, fixture.Member.Id).UpdatedAt, fixture.Store.Room(room, fixture.Member.Id).UpdatedAt);
        fixture.Store.UpdateMessage(fixture.Member, room, id, "<p>tea leaves</p>");
        Assert.Empty(fixture.Store.Search(fixture.Member.Id, "coffee"));
        Assert.Single(fixture.Store.Search(fixture.Member.Id, "tea"));
        fixture.Store.CreateBoost(fixture.Admin, id, "👍");
        fixture.Store.DeleteMessage(fixture.Member, room, id);
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from messages where id=@id", new { id }));
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from boosts where message_id=@id", new { id }));
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from action_text_rich_texts where record_type='Message' and record_id=@id", new { id }));
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from message_search_index where rowid=@id", new { id }));
    }

    [Fact]
    public async Task InvalidAttachmentRollsBackMessageBodyFtsAndUnreadSideEffects()
    {
        await using var fixture = new ChatApplication();
        var original = fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id).UpdatedAt;
        Assert.Throws<ChatHttpException>(() => fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "bad attachment", "rollback", 999999));
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from messages where client_message_id='rollback'"));
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from action_text_rich_texts"));
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from message_search_index"));
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from memberships where unread_at is not null"));
        Assert.Equal(original, fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id).UpdatedAt);
    }

    [Fact]
    public async Task DirectRoomsAreSingletonByExactUserSetAndCannotBeConvertedThroughSharedNamespaces()
    {
        await using var fixture = new ChatApplication();
        var first = fixture.Store.CreateRoom(fixture.Member, "Rooms::Direct", null, [fixture.Admin.Id]);
        Assert.Equal(first, fixture.Store.CreateRoom(fixture.Admin, "Rooms::Direct", null, [fixture.Member.Id, fixture.Admin.Id]));
        Assert.NotEqual(first, fixture.Store.CreateRoom(fixture.Member, "Rooms::Direct", null, [fixture.Admin.Id, fixture.Hidden.Id]));
        var exception = Assert.Throws<ChatHttpException>(() => fixture.Store.UpdateRoom(fixture.Admin, first, "Rooms::Open", "Leaked", []));
        Assert.Equal(404, exception.Status);
        using var client = fixture.Client(fixture.Admin);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/rooms/opens/{first}/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/rooms/closeds/{first}/edit")).StatusCode);
        Assert.Equal("Rooms::Direct", fixture.Store.Room(first, fixture.Admin.Id).Type);
        fixture.Store.DeleteRoom(fixture.Member, first, directNamespace: true);
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from memberships where room_id=@first", new { first }));
    }

    [Fact]
    public async Task RoomConversionPreservesExistingInvolvementAndRevisesAccess()
    {
        await using var fixture = new ChatApplication();
        fixture.Store.Involvement(fixture.Member, fixture.OpenRoom, "nothing");
        var revoked = fixture.Store.UpdateRoom(fixture.Admin, fixture.OpenRoom, "Rooms::Closed", "Limited", [fixture.Admin.Id, fixture.Member.Id]);
        Assert.Contains(fixture.Hidden.Id, revoked);
        Assert.Throws<ChatHttpException>(() => fixture.Store.Room(fixture.OpenRoom, fixture.Hidden.Id));
        Assert.Equal("nothing", fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id).Involvement);
        fixture.Store.UpdateRoom(fixture.Admin, fixture.OpenRoom, "Rooms::Open", "Everyone", []);
        Assert.Equal("mentions", fixture.Store.Room(fixture.OpenRoom, fixture.Hidden.Id).Involvement);
        Assert.Equal("nothing", fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id).Involvement);
    }

    [Fact]
    public async Task PagesUseTimestampsExcludeEqualPeersAndAroundIncludesBothFortyMessageSides()
    {
        await using var fixture = new ChatApplication();
        var ids = new List<long>();
        for (var i = 0; i < 100; i++)
        {
            var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, $"message {i}", "page-" + i);
            fixture.Db.Execute("update messages set created_at=@time where id=@id", new { id, time = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i).ToString("yyyy-MM-dd HH:mm:ss.ffffff") });
            ids.Add(id);
        }
        fixture.Db.Execute("update messages set created_at=(select created_at from messages where id=@anchor) where id=@peer", new { anchor = ids[50], peer = ids[49] });
        Assert.Equal(ids.Skip(60), fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id).Select(x => x.Id));
        var before = fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id, before: ids[50]);
        Assert.Equal(40, before.Count);
        Assert.DoesNotContain(before, x => x.Id == ids[49] || x.Id == ids[50]);
        Assert.Equal(81, fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id, around: ids[50]).Count);
        Assert.Equal(ids.Skip(51).Take(40), fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id, after: ids[50]).Select(x => x.Id));
        Assert.Throws<ChatHttpException>(() => fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id, before: 99999));
    }

    [Fact]
    public async Task MessageAuthorOrAdministratorCanEditAndOnlyBoostOwnerCanDelete()
    {
        await using var fixture = new ChatApplication();
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "owner content", "permission-message");
        Assert.Equal(403, Assert.Throws<ChatHttpException>(() => fixture.Store.UpdateMessage(fixture.Hidden, fixture.OpenRoom, id, "stolen")).Status);
        Assert.Equal(403, Assert.Throws<ChatHttpException>(() => fixture.Store.DeleteMessage(fixture.Hidden, fixture.OpenRoom, id)).Status);
        var boost = fixture.Store.CreateBoost(fixture.Hidden, id, "👏");
        Assert.Equal(404, Assert.Throws<ChatHttpException>(() => fixture.Store.DeleteBoost(fixture.Admin, id, boost)).Status);
        fixture.Store.DeleteBoost(fixture.Hidden, id, boost);
        fixture.Store.UpdateMessage(fixture.Admin, fixture.OpenRoom, id, "admin content");
        Assert.Contains("admin content", fixture.Store.Message(id, fixture.Member.Id).Body);
    }

    [Fact]
    public async Task SanitizerPreservesEditorFormattingAndSignedMentionsWhileRejectingExecutableHtml()
    {
        await using var fixture = new ChatApplication();
        var richText = fixture.Services.GetRequiredService<RichText>();
        var sgid = richText.MentionSgid(fixture.Member.Id);
        var body = $"<p>Hello <s>struck</s><u>under</u><mark>marked</mark><a href=\"javascript:alert(1)\" onclick=\"bad()\">bad link</a><img src=\"evil\"><script>alert(1)</script><action-text-attachment sgid=\"{sgid}\"></action-text-attachment></p><pre data-language=\"ruby\">code</pre>";
        var safe = richText.Sanitize(body);
        Assert.Contains("<s>struck</s>", safe);
        Assert.Contains("data-language=\"ruby\"", safe);
        Assert.DoesNotContain("javascript", safe);
        Assert.DoesNotContain("onclick", safe);
        Assert.DoesNotContain("<script", safe);
        Assert.DoesNotContain("<img", safe);
        Assert.Contains("@Member <unsafe>", richText.PlainText(body));
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, body, "safe-id");
        var html = fixture.Services.GetRequiredService<IChatRenderer>().MessageHtml(id, fixture.Member.Id);
        Assert.Contains("class=\"mention\"", html);
        Assert.Contains("Member &lt;unsafe&gt;", html);
        Assert.DoesNotContain("Member <unsafe>", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("presentation_message_safe-id", html);
    }

    [Fact]
    public async Task HttpMessageCrudReturnsTurboContractsAndRejectsMissingCsrf()
    {
        await using var fixture = new ChatApplication();
        using var client = fixture.Client(fixture.Member);
        var page = await client.GetStringAsync("/rooms/" + fixture.OpenRoom);
        var token = Token(page);
        var denied = await client.PostAsync($"/rooms/{fixture.OpenRoom}/messages", new FormUrlEncodedContent(new Dictionary<string, string> { ["message[body]"] = "csrf rejected" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/vnd.turbo-stream.html");
        var created = await client.PostAsync($"/rooms/{fixture.OpenRoom}/messages", Form(token, new() { ["message[body]"] = "<p>HTTP coffee</p>", ["message[client_message_id]"] = "http-message" }));
        var stream = await created.Content.ReadAsStringAsync();
        Assert.Equal("text/vnd.turbo-stream.html", created.Content.Headers.ContentType?.MediaType);
        Assert.Contains("action=\"append\"", stream);
        Assert.Contains("message_http-message", stream);
        var id = fixture.Db.Scalar<long>("select id from messages where client_message_id='http-message'");
        client.DefaultRequestHeaders.Accept.Clear();
        var edit = await client.GetStringAsync($"/rooms/{fixture.OpenRoom}/messages/{id}/edit");
        Assert.Contains("edit_message_http-message", edit);
        Assert.Contains("name=\"_method\" value=\"patch\"", edit);
        var update = await client.PostAsync($"/rooms/{fixture.OpenRoom}/messages/{id}", Form(token, new() { ["_method"] = "patch", ["message[body]"] = "<p>HTTP tea</p>" }));
        Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);
        Assert.Empty(fixture.Store.Search(fixture.Member.Id, "coffee"));
        client.DefaultRequestHeaders.Accept.ParseAdd("text/vnd.turbo-stream.html");
        var delete = await client.PostAsync($"/rooms/{fixture.OpenRoom}/messages/{id}", Form(token, new() { ["_method"] = "delete" }));
        Assert.Contains("target=\"message_http-message\"", await delete.Content.ReadAsStringAsync());
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from messages where id=@id", new { id }));
    }

    [Fact]
    public async Task MultipartAttachmentsCommitBlobAssociationAndFilenameSearchTogether()
    {
        await using var fixture = new ChatApplication();
        using var client = fixture.Client(fixture.Member);
        var token = Token(await client.GetStringAsync("/rooms/" + fixture.OpenRoom));
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "authenticity_token");
        form.Add(new StringContent("upload-message"), "message[client_message_id]");
        form.Add(new ByteArrayContent("actual uploaded bytes"u8.ToArray()), "message[attachment]", "report.txt");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/vnd.turbo-stream.html");
        var response = await client.PostAsync($"/rooms/{fixture.OpenRoom}/messages", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("report.txt", await response.Content.ReadAsStringAsync());
        var id = fixture.Db.Scalar<long>("select id from messages where client_message_id='upload-message'");
        Assert.Equal(1L, fixture.Db.Scalar<long>("select count(*) from active_storage_attachments where record_type='Message' and record_id=@id", new { id }));
        Assert.Equal(0L, fixture.Db.Scalar<long>("select count(*) from active_storage_attachments where record_type='ChatUpload'"));
        Assert.Equal("report.txt", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@id", new { id }));
        Assert.Single(fixture.Store.Search(fixture.Member.Id, "report"));
    }

    [Fact]
    public async Task BotRawBodyApiRespectsRoomScopeCreatorOwnershipAndPaginationContracts()
    {
        await using var fixture = new ChatApplication();
        var bot = fixture.Bot;
        var room = fixture.Store.CreateRoom(fixture.Member, "Rooms::Closed", "Bot room", [fixture.Member.Id, bot.Id]);
        using var client = fixture.CreateClient(new() { AllowAutoRedirect = false });
        var path = $"/rooms/{room}/{bot.Id}-{bot.BotToken}/messages";
        var create = await client.PostAsync(path, new StringContent("bot coffee"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Contains("/messages/", create.Headers.Location?.OriginalString);
        var list = await client.GetAsync(path);
        Assert.Equal("1", list.Headers.GetValues("X-Total-Count").Single());
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal("bot coffee", json.RootElement[0].GetProperty("body").GetProperty("plain_text").GetString());
        Assert.Equal("bot", json.RootElement[0].GetProperty("creator").GetProperty("role").GetString());
        var memberMessage = fixture.Store.CreateMessage(fixture.Member, room, "member message", "bot-owner-check");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsync(path + "/" + memberMessage, new StringContent("stolen"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/rooms/99999/{bot.Id}-{bot.BotToken}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/rooms/{room}/{bot.Id}-wrong/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync(path, new StringContent("  "))).StatusCode);
    }

    [Fact]
    public async Task RecentSearchesDeduplicateTrimAndSearchSyntaxIsParameterized()
    {
        await using var fixture = new ChatApplication();
        for (var i = 0; i < 12; i++) fixture.Store.RecordSearch(fixture.Member.Id, "query-" + i);
        fixture.Store.RecordSearch(fixture.Member.Id, "query-11");
        Assert.Equal(10, fixture.Store.RecentSearches(fixture.Member.Id).Count);
        Assert.Equal("query-11", fixture.Store.RecentSearches(fixture.Member.Id)[0]);
        fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "coffee beans", "search-message");
        Assert.Single(fixture.Store.Search(fixture.Member.Id, "coffee AND beans"));
        Assert.Throws<SqliteException>(() => fixture.Store.Search(fixture.Member.Id, "' OR 1=1; DROP TABLE messages; --"));
        Assert.Equal(1L, fixture.Db.Scalar<long>("select count(*) from messages"));
        fixture.Store.ClearSearches(fixture.Member.Id);
        Assert.Empty(fixture.Store.RecentSearches(fixture.Member.Id));
    }

    [Fact]
    public async Task GeneratedJsonPreservesAutocompleteBoostAndIdentityErrorContracts()
    {
        await using var fixture = new ChatApplication();
        using var client = fixture.Client(fixture.Member);
        var token = Token(await client.GetStringAsync("/rooms/" + fixture.OpenRoom));
        using var autocomplete = new HttpRequestMessage(HttpMethod.Get, $"/autocompletable/users?room_id={fixture.OpenRoom}&query=Member");
        autocomplete.Headers.Accept.ParseAdd("application/json");
        var response = await client.SendAsync(autocomplete);
        using var users = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var user = users.RootElement[0];
        Assert.Equal("Member &lt;unsafe&gt;", user.GetProperty("name").GetString());
        Assert.Equal(fixture.Member.Id, user.GetProperty("value").GetInt64());
        Assert.Equal(fixture.Member.Id, fixture.Services.GetRequiredService<IRailsCrypto>().VerifySignedGlobalId(user.GetProperty("sgid").GetString()!, "User"));
        Assert.StartsWith("http://localhost/users/", user.GetProperty("avatar_url").GetString());

        var message = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "JSON contract", "generated-json-message");
        using var botClient = fixture.CreateClient(new() { AllowAutoRedirect = false });
        var boost = await botClient.PostAsync($"/rooms/{fixture.OpenRoom}/{fixture.Bot.Id}-{fixture.Bot.BotToken}/messages/{message}/boosts", new StringContent("🔥"));
        Assert.Equal(HttpStatusCode.Created, boost.StatusCode);
        using var json = JsonDocument.Parse(await boost.Content.ReadAsStringAsync());
        Assert.Equal("🔥", json.RootElement.GetProperty("content").GetString());
        Assert.Equal("bot", json.RootElement.GetProperty("booster").GetProperty("role").GetString());
        Assert.Equal(message, json.RootElement.GetProperty("message").GetProperty("id").GetInt64());
        Assert.EndsWith($"/rooms/{fixture.OpenRoom}/messages/{message}", json.RootElement.GetProperty("message").GetProperty("url").GetString());
        Assert.EndsWith("Z", json.RootElement.GetProperty("created_at").GetString());

        var invalidPassword = await client.PostAsync("/users/me/profile", Form(token, new()
        {
            ["_method"] = "patch", ["user[name]"] = fixture.Member.Name, ["user[password]"] = new string('a', 73)
        }));
        Assert.Equal(HttpStatusCode.Redirect, invalidPassword.StatusCode);
        Assert.Contains("/users/me/profile", invalidPassword.Headers.Location?.OriginalString);
    }

    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html, "<meta name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value);
    private static FormUrlEncodedContent Form(string token, Dictionary<string, string> values) { values["authenticity_token"] = token; return new(values); }
}

internal sealed class ChatApplication : WebApplicationFactory<Program>
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "campfire-chat-tests-" + Guid.NewGuid().ToString("N"));
    private bool seeded;
    public IDataStore Db { get { Seed(); return Services.GetRequiredService<IDataStore>(); } }
    public ChatStore Store { get { Seed(); return Services.GetRequiredService<ChatStore>(); } }
    public UserRecord Admin => GetUser(1);
    public UserRecord Member => GetUser(2);
    public UserRecord Hidden => GetUser(3);
    public UserRecord Connected => GetUser(4);
    public UserRecord Bot => GetUser(5);
    public long OpenRoom { get { Seed(); return 1; } }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CAMPFIRE_STORAGE"] = path, ["SECRET_KEY_BASE"] = new string('a', 128), ["CAMPFIRE_DELIVER_INTEGRATIONS"] = "false" }));
    }
    private UserRecord GetUser(long id) => Db.Single<UserRecord>("select * from users where id=@id", new { id })!;
    private void Seed()
    {
        if (seeded) return;
        var db = Services.GetRequiredService<IDataStore>();
        var now = RequestUser.Timestamp();
        db.Execute("insert into accounts(name,join_code,settings,created_at,updated_at) values('Test Campfire','test-code','{}',@now,@now)", new { now });
        for (var i = 1; i <= 5; i++) db.Execute("insert into users(id,name,role,status,bot_token,created_at,updated_at) values(@id,@name,@role,0,@bot,@now,@now)", new { id = i, name = i == 2 ? "Member <unsafe>" : "User " + i, role = i == 1 ? 1 : i == 5 ? 2 : 0, bot = i == 5 ? "secretkey" : null, now });
        Services.GetRequiredService<ChatStore>().CreateRoom(db.Single<UserRecord>("select * from users where id=1")!, "Rooms::Open", "All Talk", []);
        seeded = true;
    }
    public HttpClient Client(UserRecord user)
    {
        var client = CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var context = new DefaultHttpContext { RequestServices = Services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        Services.GetRequiredService<IAuthService>().SignIn(context, user.Id);
        var cookie = context.Response.Headers.SetCookie.Single(x => x!.StartsWith("session_token=", StringComparison.Ordinal))!.Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }
    public override async ValueTask DisposeAsync()
    {
        var database = Services.GetRequiredService<IDataStore>();
        await base.DisposeAsync();
        // Concurrent host disposal can return before DI finishes unwinding.
        // Drain this fixture's store before deleting its SQLite files.
        ((IDisposable)database).Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
