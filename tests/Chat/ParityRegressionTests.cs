using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Campfire.Contracts;
using Campfire.Features.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Campfire.ChatTests;

public sealed partial class ChatTests
{
    // Expectations captured from the pinned ActionText::PlainTextConversion,
    // independently of the .NET implementation (bench/parity/chat vectors).
    [Theory]
    [InlineData("<p>First</p><p>Second</p>", "First\n\nSecond")]
    [InlineData("<ul><li>one</li><li>two<ul><li>nested</li></ul></li></ul>", "• one\n• two\n  • nested")]
    [InlineData("<ol start=\"5\"><li>one</li><li>two</li></ol>", "1. one\n2. two")]
    [InlineData("<blockquote> quoted </blockquote>", " “quoted” ")]
    [InlineData("<figure><figcaption>caption</figcaption></figure>", "[caption]")]
    [InlineData("<h2>head</h2><pre>a\nb</pre>", "heada\nb")]
    [InlineData(" spaces ", "spaces")]
    [InlineData("<custom>preserved<script>removed</script></custom>", "preserved")]
    [InlineData("<div>a<br>b</div><div>c</div>", "a\nb\nc")]
    [InlineData("<table><tr><td>one</td><td>two</td></tr><tr><td>three</td></tr></table>", "onetwothree")]
    [InlineData("<blockquote></blockquote>", "“”")]
    [InlineData("<p>one<br><br></p><p>two</p>", "one\n\ntwo")]
    [InlineData("<ul><li>a<ol><li>b<ul><li>c</li></ul></li></ol></li></ul>", "• a\n  1. b\n    • c")]
    public async Task PlainTextMatchesPinnedActionTextConversion(string html, string expected)
    {
        await using var fixture = new ChatApplication();
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, html, "plaintext-vector");
        var rich = fixture.Services.GetRequiredService<RichText>();
        Assert.Equal(expected, rich.PlainText(html));
        Assert.Equal(expected, fixture.Db.Scalar<string>("select body from message_search_index where rowid=@id", new { id }));
    }

    [Fact]
    public async Task EditorRebuildsEmbeddedContentAndRotatedLegacyMentionsWithoutTrustingTheirMarkup()
    {
        await using var fixture = new ChatApplication();
        var rich = fixture.Services.GetRequiredService<RichText>();
        var token = fixture.Services.GetRequiredService<IRailsCrypto>().SignedGlobalId("User", fixture.Member.Id);
        token = token[..token.IndexOf("--", StringComparison.Ordinal)] + "--bad-signature";
        var malicious = "<div class=\"og-embed__title\"><a href=\"https://example.com\">Title</a></div><div class=\"og-embed__image\"><img src=\"https://example.com/image.png\" onerror=\"editorPwn()\" data-controller=\"editor-pwn\"></div>";
        var body = $"<p><action-text-attachment sgid=\"{token}\" content-type=\"application/octet-stream\"></action-text-attachment><action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" content=\"{WebUtility.HtmlEncode(malicious)}\"></action-text-attachment></p>";
        var editable = rich.EditableBody(body, user => "<span>" + WebUtility.HtmlEncode(user.Name) + "</span>");
        var document = new HtmlParser().ParseDocument(editable);
        var nodes = document.QuerySelectorAll("action-text-attachment");
        Assert.Equal("application/vnd.campfire.mention", nodes[0].GetAttribute("content-type"));
        Assert.Contains("Member &lt;unsafe&gt;", nodes[0].GetAttribute("content"));
        Assert.DoesNotContain("editorPwn", nodes[1].GetAttribute("content"));
        Assert.DoesNotContain("data-controller", nodes[1].GetAttribute("content"));
        Assert.Contains("@Member <unsafe>", rich.PlainText(body));
        Assert.Null(rich.MentionUser("not-base64--invalid"));
        // Marshal payload recovery extracts only the User GID without deserialization.
        var marshal = Convert.ToBase64String(Encoding.UTF8.GetBytes("\x04\bgarbagegid://campfire/User/2\x06"));
        var legacy = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"_rails\":{\"message\":\"" + marshal + "\"}}")) + "--bad";
        Assert.Equal(2, rich.MentionUser(legacy)?.Id);
    }

    [Fact]
    public async Task PreviewPolicyMatchesRailsForOwnHostLiteralIpEscapesAndSafeNamedHosts()
    {
        await using var fixture = new ChatApplication();
        var accessor = fixture.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        accessor.HttpContext.Request.Host = new HostString("chat.example.com");
        var rich = fixture.Services.GetRequiredService<RichText>();
        foreach (var url in new[] { "http://127.0.0.1/p", "http://2130706433/p", "http://0x7f.0.0.1/p", "http://[::1]/p", "https:/rooms/1", "https://chat.example.com/p", "https://CHAT.EXAMPLE.COM./p", "https://%63hat.example.com/p", "javascript:alert(1)" }) Assert.False(rich.SafeWebUrl(url), url);
        Assert.True(rich.SafeWebUrl("https://other.example.com/p"));
        var rendered = rich.Presentation("<action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" href=\"http://127.0.0.1/p\" url=\"http://127.0.0.1/img\" filename=\"Title\" caption=\"Description\"></action-text-attachment>", _ => "");
        Assert.Contains("Title", rendered);
        Assert.Contains("Description", rendered);
        Assert.DoesNotContain("<img", rendered);
        Assert.DoesNotContain("<a ", rendered);
        accessor.HttpContext = null;
    }

    [Fact]
    public async Task DefaultCurlBodyRemainsRawUtf8AndBotParagraphJsonMatchesRails()
    {
        await using var fixture = new ChatApplication();
        using var client = fixture.CreateClient(new() { AllowAutoRedirect = false });
        var path = $"/rooms/{fixture.OpenRoom}/{fixture.Bot.Id}-{fixture.Bot.BotToken}/messages";
        var created = await client.PostAsync(path, new StringContent("coffee=☕&one+two", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = long.Parse(created.Headers.Location!.OriginalString.Split('/')[^1]);
        Assert.Equal("coffee=☕&one+two", fixture.Store.Message(id, fixture.Bot.Id).Body);
        var updated = await client.PatchAsync(path + "/" + id, new StringContent("<p>First</p><p>Second</p>"));
        using var json = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal("First\n\nSecond", json.RootElement.GetProperty("body").GetProperty("plain_text").GetString());
        Assert.Matches(@"\.\d{3}Z$", json.RootElement.GetProperty("created_at").GetString());
        var boost = await client.PostAsync(path + "/" + id + "/boosts", new StringContent("great=☕", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.Created, boost.StatusCode);
        using var boostJson = JsonDocument.Parse(await boost.Content.ReadAsStringAsync());
        Assert.Equal("great=☕", boostJson.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task RequiredParametersAndNegotiationPreserveCommitTiming()
    {
        await using var fixture = new ChatApplication();
        using var client = fixture.Client(fixture.Member);
        var token = Token(await client.GetStringAsync("/rooms/" + fixture.OpenRoom));
        var missing = await client.PostAsync($"/rooms/{fixture.OpenRoom}/messages", Form(token, new()));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from messages"));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{fixture.OpenRoom}/messages") { Content = Form(token, new() { ["message[body]"] = "committed despite format", ["message[client_message_id]"] = "format-commit" }) };
        request.Headers.Accept.ParseAdd("application/json");
        Assert.Equal(HttpStatusCode.NotAcceptable, (await client.SendAsync(request)).StatusCode);
        var id = fixture.Db.Scalar<long>("select id from messages where client_message_id='format-commit'");
        Assert.Single(fixture.Store.Search(fixture.Member.Id, "committed"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/messages/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/messages/{id}?room_id={fixture.OpenRoom}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotAcceptable, (await client.GetAsync($"/rooms/{fixture.OpenRoom}/messages/{id}.json")).StatusCode);
        var update = new HttpRequestMessage(HttpMethod.Patch, $"/rooms/{fixture.OpenRoom}/messages/{id}") { Content = Form(token, new() { ["message[body]"] = "updated despite missing JSON template" }) };
        update.Headers.Accept.ParseAdd("application/json");
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.SendAsync(update)).StatusCode);
        Assert.Contains("updated despite", fixture.Store.Message(id, fixture.Member.Id).Body);
    }

    [Fact]
    public async Task MalformedRoomsOrphanedCreatorsAndEmptySearchScopesRetainPinnedBehavior()
    {
        await using var fixture = new ChatApplication();
        using var admin = fixture.Client(fixture.Admin);
        var invalid = await admin.GetAsync("/rooms/not-a-number");
        Assert.Equal(HttpStatusCode.Redirect, invalid.StatusCode);
        Assert.Equal("/", invalid.Headers.Location!.OriginalString);
        Assert.Contains("Room not found or inaccessible", await admin.GetStringAsync("/rooms/" + fixture.OpenRoom));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/rooms/{fixture.OpenRoom}suffix")).StatusCode);
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "Lost author", "orphan-fixture");
        // Reproduce the pinned database's deliberately orphaned creator fixture.
        fixture.Db.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF; update messages set creator_id=999999 where id=$id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery();
        });
        var message = fixture.Store.Message(id, fixture.Admin.Id);
        Assert.True(message.CreatorMissing);
        var shown = await admin.GetAsync($"/rooms/{fixture.OpenRoom}/messages/{id}");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Contains("Failed to load message content", await shown.Content.ReadAsStringAsync());
        using var edit = new HttpRequestMessage(HttpMethod.Get, $"/rooms/{fixture.OpenRoom}/messages/{id}/edit");
        edit.Headers.Add("Turbo-Frame", "edit_message_test");
        var edited = await admin.SendAsync(edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var editor = await edited.Content.ReadAsStringAsync();
        Assert.StartsWith("<!DOCTYPE html>", editor);
        Assert.Contains("<lexxy-editor", editor);
        Assert.Contains("Lost author", editor);
        fixture.Db.Execute("delete from memberships where user_id=@id", new { id = fixture.Connected.Id });
        using var loner = fixture.Client(fixture.Connected);
        Assert.Equal(HttpStatusCode.InternalServerError, (await loner.GetAsync("/searches?q=coffee")).StatusCode);
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from searches where user_id=@id", new { id = fixture.Connected.Id }));
    }

    [Fact]
    public async Task SidebarFrameRequestRetainsApplicationHeadAndCurrentCsrf()
    {
        await using var fixture = new ChatApplication();
        using var client = fixture.Client(fixture.Member);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/users/me/sidebar");
        request.Headers.Add("Turbo-Frame", "user_sidebar");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var source = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<html>", source);
        var document = new HtmlParser().ParseDocument(source);
        Assert.Equal("", document.Title);
        Assert.NotNull(document.QuerySelector("head meta[name=csrf-token]"));
        Assert.Null(document.QuerySelector("head meta[name=current-user-id]"));
        Assert.NotNull(document.QuerySelector("body turbo-frame#user_sidebar"));
        Assert.Null(document.QuerySelector("head script[type=importmap]"));
        Assert.NotEmpty(Token(source));
        var full = await client.GetStringAsync("/users/me/sidebar");
        Assert.StartsWith("<!DOCTYPE html>", full);
        Assert.Contains("type=\"importmap\"", full);
    }

    [Fact]
    public async Task MessageIndexValidatorsMatchPinnedRailsModelAndTemplateGoldenVectors()
    {
        await using var fixture = new ChatApplication();
        using var client = fixture.Client(fixture.Member);
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "one", "etag-one");
        fixture.Db.Execute("update messages set id=7,created_at='2026-10-05 07:08:09.123456',updated_at='2026-10-05 07:08:09.123456' where id=@id", new { id });
        var path = $"/rooms/{fixture.OpenRoom}/messages";
        var first = await client.GetAsync(path);
        Assert.Equal("W/\"e6669fc6fa2159fba71ea44c1703687a\"", first.Headers.ETag!.ToString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-05T07:08:09Z"), first.Content.Headers.LastModified);
        using var strong = new HttpRequestMessage(HttpMethod.Get, path);
        strong.Headers.IfNoneMatch.ParseAdd("\"e6669fc6fa2159fba71ea44c1703687a\"");
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(strong)).StatusCode);
        using var frame = new HttpRequestMessage(HttpMethod.Get, path);
        frame.Headers.Add("Turbo-Frame", "user_sidebar");
        Assert.Equal("W/\"78075de8caf3e95213c1037563979f96\"", (await client.SendAsync(frame)).Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/rooms/9876543210")).StatusCode);
        var flash = await client.GetAsync(path);
        Assert.Equal("W/\"95c1672147da8c250840b78835606eaf\"", flash.Headers.ETag!.ToString());
        Assert.Equal(first.Headers.ETag, (await client.GetAsync(path)).Headers.ETag);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/rooms/9876543210")).StatusCode);
        using var flashFrame = new HttpRequestMessage(HttpMethod.Get, path);
        flashFrame.Headers.Add("Turbo-Frame", "user_sidebar");
        Assert.Equal("W/\"897927e6abcd2fa2fe20489bd43213f3\"", (await client.SendAsync(flashFrame)).Headers.ETag!.ToString());
        Assert.Equal(first.Headers.ETag, (await client.GetAsync(path)).Headers.ETag);
        id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "two", "etag-two");
        fixture.Db.Execute("update messages set id=8,created_at='2026-10-05 07:08:10.000000',updated_at='2026-10-05 07:08:10.000000' where id=@id", new { id });
        var second = await client.GetAsync(path);
        Assert.Equal("W/\"4ec78dd8c512caec5151f628e554fbfd\"", second.Headers.ETag!.ToString());
        using var stale = new HttpRequestMessage(HttpMethod.Get, path);
        stale.Headers.IfNoneMatch.ParseAdd(first.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task ConditionalCacheAndMalformedSelectorsUseRailsContracts()
    {
        await using var fixture = new ChatApplication();
        fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "message", "cache");
        using var client = fixture.Client(fixture.Member);
        var path = $"/rooms/{fixture.OpenRoom}/messages";
        var page = await client.GetAsync(path);
        Assert.NotNull(page.Content.Headers.LastModified);
        var modified = new HttpRequestMessage(HttpMethod.Get, path);
        modified.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddYears(1);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(modified)).StatusCode);
        var precedence = new HttpRequestMessage(HttpMethod.Get, path);
        precedence.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddYears(1);
        precedence.Headers.IfNoneMatch.ParseAdd("\"wrong\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(precedence)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "?before=garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/autocompletable/users?room_id=garbage")).StatusCode);
        using var users = JsonDocument.Parse(await client.GetStringAsync("/autocompletable/users.json?filter=&query=Member"));
        Assert.Single(users.RootElement.EnumerateArray());
        Assert.Equal(fixture.Member.Id, users.RootElement[0].GetProperty("value").GetInt64());
    }

    [Fact]
    public async Task BlankAttachmentClearsRowsAndReindexesWithoutClearingAbsentAttachment()
    {
        await using var fixture = new ChatApplication();
        var now = RequestUser.Timestamp();
        fixture.Db.Execute("insert into active_storage_blobs(id,key,filename,content_type,metadata,service_name,byte_size,checksum,created_at) values(101,'paritydetach','proof.txt','text/plain','{}','local',5,'checksum',@now)", new { now });
        var diskPath = fixture.Services.GetRequiredService<Campfire.Features.Storage.MediaService>().DiskPath("paritydetach");
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        await File.WriteAllTextAsync(diskPath, "proof");
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "", "attached", 101);
        using var client = fixture.Client(fixture.Member);
        var token = Token(await client.GetStringAsync("/rooms/" + fixture.OpenRoom));
        Assert.Equal(HttpStatusCode.Redirect, (await client.PatchAsync($"/rooms/{fixture.OpenRoom}/messages/{id}", Form(token, new() { ["message[body]"] = "" }))).StatusCode);
        Assert.Equal(101, fixture.Store.Message(id, fixture.Member.Id).AttachmentBlobId);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PatchAsync($"/rooms/{fixture.OpenRoom}/messages/{id}", Form(token, new() { ["message[attachment]"] = "" }))).StatusCode);
        Assert.Null(fixture.Store.Message(id, fixture.Member.Id).AttachmentBlobId);
        Assert.Equal("", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@id", new { id }));
    }

    [Fact]
    public async Task ClosedRoomRenameCommitsBeforeFailedMembershipRevisionLikeRails()
    {
        await using var fixture = new ChatApplication();
        var id = fixture.Store.CreateRoom(fixture.Admin, "Rooms::Closed", "before", [fixture.Admin.Id, fixture.Member.Id]);
        fixture.Db.Execute("CREATE TRIGGER parity_membership_abort BEFORE DELETE ON memberships BEGIN SELECT RAISE(ABORT,'parity failure'); END");
        Assert.Throws<SqliteException>(() => fixture.Store.UpdateRoom(fixture.Admin, id, "Rooms::Closed", "after", [fixture.Admin.Id]));
        Assert.Equal("after", fixture.Store.Room(id, fixture.Admin.Id).Name);
        Assert.Equal(2, fixture.Store.Members(id).Count);
    }

    [Fact]
    public async Task SearchCallbackFailuresKeepCommittedMessagesAndStopUnreadReceiveLikeRails()
    {
        await using var fixture = new ChatApplication();
        var update = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "original-update", "fts-update");
        var delete = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "original-delete", "fts-delete");
        fixture.Db.Execute("update memberships set unread_at=null; ALTER TABLE message_search_index RENAME TO original_fts; CREATE TABLE message_search_index(rowid INTEGER PRIMARY KEY,body TEXT); INSERT INTO message_search_index SELECT rowid,body FROM original_fts; CREATE TRIGGER fail_insert BEFORE INSERT ON message_search_index WHEN NEW.body LIKE 'fault-%' BEGIN SELECT RAISE(ABORT,'index failure'); END; CREATE TRIGGER fail_update BEFORE UPDATE ON message_search_index WHEN NEW.body LIKE 'fault-%' BEGIN SELECT RAISE(ABORT,'index failure'); END; CREATE TRIGGER fail_delete BEFORE DELETE ON message_search_index BEGIN SELECT RAISE(ABORT,'index failure'); END");
        Assert.Throws<SqliteException>(() => fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "fault-create", "fts-create"));
        var created = fixture.Db.Scalar<long>("select id from messages where client_message_id='fts-create'");
        Assert.Equal("fault-create", fixture.Store.Message(created, fixture.Member.Id).Body);
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from message_search_index where rowid=@created", new { created }));
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from memberships where unread_at is not null"));
        Assert.Equal(fixture.Store.Message(created, fixture.Member.Id).UpdatedAt, fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id).UpdatedAt);
        Assert.Throws<SqliteException>(() => fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, update, "fault-update"));
        Assert.Equal("fault-update", fixture.Store.Message(update, fixture.Member.Id).Body);
        Assert.Equal("original-update", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@update", new { update }));
        Assert.Throws<SqliteException>(() => fixture.Store.CreateBoost(fixture.Member, update, "committed boost"));
        var boost = fixture.Db.Scalar<long>("select id from boosts where message_id=@update", new { update });
        Assert.Throws<SqliteException>(() => fixture.Store.DeleteBoost(fixture.Member, update, boost));
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from boosts where id=@boost", new { boost }));
        Assert.Throws<SqliteException>(() => fixture.Store.DeleteMessage(fixture.Member, fixture.OpenRoom, delete));
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from messages where id=@delete", new { delete }));
        Assert.Equal("original-delete", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@delete", new { delete }));
        fixture.Db.Execute("DROP TRIGGER fail_delete; delete from message_search_index where rowid=@update", new { update });
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, update, "updated without index row");
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from message_search_index where rowid=@update", new { update }));
    }

    [Fact]
    public async Task DirectSidebarPreservesMemberOrderAndBroadcastFormsOmitPublisherCsrf()
    {
        await using var fixture = new ChatApplication();
        var direct = fixture.Store.CreateRoom(fixture.Member, "Rooms::Direct", null, [fixture.Admin.Id, fixture.Hidden.Id, fixture.Connected.Id]);
        var room = fixture.Store.Room(direct, fixture.Member.Id);
        var renderer = fixture.Services.GetRequiredService<ChatRenderer>();
        var members = fixture.Store.Members(direct).Where(x => x.Id != fixture.Member.Id).ToArray();
        Assert.Equal(new[] { fixture.Admin.Id, fixture.Connected.Id, fixture.Hidden.Id }.Order(), members.Select(x => x.Id));
        var initials = members.Select(x => string.Concat(x.Name.Split(' ').Take(3).Select(n => n[..1].ToUpperInvariant()))).ToArray();
        Assert.Contains(string.Join(", ", initials[..^1]) + ", and " + initials[^1], renderer.SidebarRoom(room, fixture.Member.Id));
        var id = fixture.Store.CreateMessage(fixture.Member, direct, "broadcast", "csrf-broadcast");
        using var client = fixture.Client(fixture.Member);
        Assert.Contains("authenticity_token", await client.GetStringAsync($"/rooms/{direct}/messages/{id}"));
        Assert.DoesNotContain("authenticity_token", renderer.BroadcastMessageHtml(id, fixture.Member.Id));
    }

    [Fact]
    public async Task SourceBranchFormatsAndNullInputsPreserveOriginalSideEffects()
    {
        await using var fixture = new ChatApplication();
        using var human = fixture.Client(fixture.Member);
        var token = Token(await human.GetStringAsync("/rooms/" + fixture.OpenRoom));
        var path = $"/rooms/{fixture.OpenRoom}/messages";
        Assert.Equal(HttpStatusCode.NotAcceptable, (await human.PostAsync(path, Form(token, new() { ["message[body]"] = "html commits", ["message[client_message_id]"] = "html-format" }))).StatusCode);
        var id = fixture.Db.Scalar<long>("select id from messages where client_message_id='html-format'");
        var nullBody = new HttpRequestMessage(HttpMethod.Patch, path + "/" + id) { Content = new StringContent("{\"message\":{\"body\":null}}", Encoding.UTF8, "application/json") };
        nullBody.Headers.Add("X-CSRF-Token", token);
        Assert.Equal(HttpStatusCode.Redirect, (await human.SendAsync(nullBody)).StatusCode);
        Assert.Equal("", fixture.Store.Message(id, fixture.Member.Id).Body);
        Assert.Equal(1, fixture.Db.Scalar<long>("select count(*) from action_text_rich_texts where record_type='Message' and record_id=@id and body is null", new { id }));
        Assert.Equal(HttpStatusCode.NotAcceptable, (await human.GetAsync(path + ".turbo_stream")).StatusCode);
        Assert.Equal(HttpStatusCode.NotAcceptable, (await human.GetAsync($"/rooms/{fixture.OpenRoom}/refresh?since=0")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await human.PostAsync(path, Form(token, new() { ["message[attachment]"] = "invalid-token" }))).StatusCode);
        fixture.Store.RecordSearch(fixture.Member.Id, "saved");
        Assert.Equal(HttpStatusCode.InternalServerError, (await human.PostAsync("/searches", Form(token, new() { ["q"] = "OR" }))).StatusCode);
        Assert.Equal(new[] { "saved" }, fixture.Store.RecentSearches(fixture.Member.Id));
        Assert.Equal(HttpStatusCode.InternalServerError, (await human.PostAsync("/searches", Form(token, new()))).StatusCode);
        using var bot = fixture.CreateClient(new() { AllowAutoRedirect = false });
        var botPath = $"/rooms/{fixture.OpenRoom}/{fixture.Bot.Id}-{fixture.Bot.BotToken}/messages";
        var created = await bot.PostAsync(botPath + "?attachment=", new StringContent("ignored raw text"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var botId = long.Parse(created.Headers.Location!.OriginalString.Split('/')[^1]);
        Assert.Equal("", fixture.Store.Message(botId, fixture.Bot.Id).Body);
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from action_text_rich_texts where record_type='Message' and record_id=@botId", new { botId }));
        Assert.Equal("\u00a0spaces\u00a0", fixture.Services.GetRequiredService<RichText>().PlainText("\u00a0spaces\u00a0"));
        Assert.Equal("text/html", (await bot.GetAsync(botPath + ".html")).Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Redirect, (await bot.PatchAsync(botPath + "/" + botId + ".html", new StringContent("updated"))).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await bot.PostAsync(botPath + "/" + botId + "/boosts.html", new StringContent("persisted"))).StatusCode);
        Assert.Single(fixture.Store.Message(botId, fixture.Bot.Id).Boosts);
        var removed = new HttpRequestMessage(HttpMethod.Delete, path + "/" + id);
        removed.Headers.Add("X-CSRF-Token", token);
        Assert.Equal(HttpStatusCode.NotAcceptable, (await human.SendAsync(removed)).StatusCode);
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from messages where id=@id", new { id }));
    }

    [Fact]
    public async Task OpenRoomGrantCallbackFailureKeepsCreatorAndBeforeAnchorWins()
    {
        await using var fixture = new ChatApplication();
        fixture.Db.Execute("CREATE TRIGGER open_grant_failure BEFORE INSERT ON memberships WHEN NEW.room_id>1 AND NEW.user_id=3 BEGIN SELECT RAISE(ABORT,'grant failure'); END");
        Assert.Throws<SqliteException>(() => fixture.Store.CreateRoom(fixture.Admin, "Rooms::Open", "failed callback", []));
        var room = fixture.Db.Scalar<long>("select id from rooms where name='failed callback'");
        Assert.Equal(new[] { fixture.Admin.Id }, fixture.Store.Members(room).Select(x => x.Id));
        var first = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "first", "first");
        var middle = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "middle", "middle");
        var last = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "last", "last");
        fixture.Db.Execute("update messages set created_at='2026-01-01 00:00:00.123456' where id=@first; update messages set created_at='2026-01-02 00:00:00.123456' where id=@middle; update messages set created_at='2026-01-03 00:00:00.123456' where id=@last", new { first, middle, last });
        Assert.Equal(new[] { first }, fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id, before: middle, after: last).Select(x => x.Id));
        using var human = fixture.Client(fixture.Member);
        Assert.Equal(HttpStatusCode.OK, (await human.GetAsync($"/rooms/{fixture.OpenRoom}/messages?before={middle}&after=garbage")).StatusCode);
        Assert.Contains($"data-message-id=\"{middle}\"", await human.GetStringAsync($"/rooms/{fixture.OpenRoom}?message_id={middle}"));
    }

    [Fact]
    public async Task FragmentCacheUsesMessageTouchesWithoutCachingPermissions()
    {
        await using var fixture = new ChatApplication();
        var renderer = fixture.Services.GetRequiredService<ChatRenderer>();
        var mention = fixture.Services.GetRequiredService<RichText>().MentionSgid(fixture.Hidden.Id);
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "<p>original<action-text-attachment sgid=\"" + mention + "\"></action-text-attachment></p>", "fragment-original");
        using var user = fixture.Client(fixture.Member);
        var path = $"/rooms/{fixture.OpenRoom}/messages/{id}";
        var first = await user.GetStringAsync(path);
        var misses = renderer.FragmentCacheMisses;
        var second = await user.GetStringAsync(path);
        string Fragment(string page)
        {
            var element = new HtmlParser().ParseDocument(page).QuerySelector("[data-message-id]")!;
            foreach (var input in element.QuerySelectorAll("input[name='authenticity_token']")) input.Remove();
            return element.OuterHtml;
        }
        Assert.Equal(Fragment(first), Fragment(second));
        Assert.Equal(misses, renderer.FragmentCacheMisses);
        Assert.True(renderer.FragmentCacheHits > 0);
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "edited");
        Assert.Contains("edited", await user.GetStringAsync(path));
        var boost = fixture.Store.CreateBoost(fixture.Hidden, id, "cache-boost");
        Assert.Contains("cache-boost", await user.GetStringAsync(path));
        fixture.Store.DeleteBoost(fixture.Hidden, id, boost);
        Assert.DoesNotContain("cache-boost", await user.GetStringAsync(path));
        fixture.Db.Execute("update users set name='Renamed creator',bio='Changed bio',updated_at='2027-01-01 00:00:00.123456' where id=@id", new { id = fixture.Member.Id });
        var renamed = await user.GetStringAsync(path);
        Assert.DoesNotContain("Renamed creator", Fragment(renamed));
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "edited", clientId: "fragment-creator-touch");
        renamed = await user.GetStringAsync(path);
        Assert.Contains("Renamed creator", Fragment(renamed));
        Assert.Contains("Changed bio", renamed);
        Assert.Contains("v=20270101000000", renamed);
        fixture.Store.UpdateRoom(fixture.Admin, fixture.OpenRoom, "Rooms::Open", "Renamed room", []);
        Assert.DoesNotContain("Renamed room", await user.GetStringAsync(path));
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "edited", clientId: "fragment-room-touch");
        Assert.Contains("Renamed room", await user.GetStringAsync(path));
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<action-text-attachment sgid=\"" + mention + "\"></action-text-attachment>");
        Assert.Contains("User 3", await user.GetStringAsync(path));
        fixture.Db.Execute("update users set name='Renamed mention',updated_at='2027-01-02 00:00:00.123456' where id=@id", new { id = fixture.Hidden.Id });
        Assert.DoesNotContain("Renamed mention", await user.GetStringAsync(path));
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<action-text-attachment sgid=\"" + mention + "\"></action-text-attachment>", clientId: "fragment-mention-touch");
        Assert.Contains("Renamed mention", await user.GetStringAsync(path));
        fixture.Store.UpdateRoom(fixture.Admin, fixture.OpenRoom, "Rooms::Closed", "Private", [fixture.Admin.Id]);
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync(path)).StatusCode);
        Assert.Throws<ChatHttpException>(() => renderer.MessageHtml(id, fixture.Member.Id));
    }

    [Fact]
    public async Task CachedFragmentsInjectOnlyCurrentRequestCsrfAndSanitizersRemainExclusive()
    {
        await using var fixture = new ChatApplication();
        var renderer = fixture.Services.GetRequiredService<ChatRenderer>();
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "<p>cached body</p>", "csrf-cache");
        using var member = fixture.Client(fixture.Member);
        using var admin = fixture.Client(fixture.Admin);
        var path = $"/rooms/{fixture.OpenRoom}/messages/{id}";
        var memberPage = await member.GetStringAsync(path);
        var adminPage = await admin.GetStringAsync(path);
        Assert.NotEqual(Token(memberPage), Token(adminPage));
        Assert.DoesNotContain(Token(memberPage), adminPage);
        var accepted = await admin.PostAsync($"/messages/{id}/boosts", Form(Token(adminPage), new() { ["boost[content]"] = "accepted" }));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var rejected = await admin.PostAsync($"/messages/{id}/boosts", Form(Token(memberPage), new() { ["boost[content]"] = "rejected" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.DoesNotContain("authenticity_token", renderer.BroadcastMessageHtml(id, fixture.Member.Id));
        var rich = fixture.Services.GetRequiredService<RichText>();
        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            var html = rich.Sanitize("<p><mark>safe-" + index + "</mark><a href=\"javascript:bad()\">link</a><img src=\"evil\" onerror=\"bad()\"><script>unsafe</script></p>");
            Assert.Contains("safe-" + index, html);
            Assert.DoesNotContain("javascript:", html);
            Assert.DoesNotContain("onerror", html);
            Assert.DoesNotContain("<script", html);
        })));
    }

    [Fact]
    public async Task FragmentModelKeysUnifyStringAndEncodedCacheAndBudgetOversizedKeys()
    {
        await using var fixture = new ChatApplication();
        var renderer = fixture.Services.GetRequiredService<ChatRenderer>();
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "key test", "key-framing");
        var message = fixture.Store.Message(id, fixture.Member.Id);
        var first = new ChatBoost { Id = 101, MessageId = id, BoosterId = fixture.Admin.Id, Content = "first", BoosterName = fixture.Admin.Name, BoosterUpdatedAt = fixture.Admin.UpdatedAt, CreatedAt = RequestUser.Timestamp() };
        var second = new ChatBoost { Id = 202, MessageId = id, BoosterId = fixture.Hidden.Id, Content = "second", BoosterName = fixture.Hidden.Name, BoosterUpdatedAt = fixture.Hidden.UpdatedAt, CreatedAt = RequestUser.Timestamp(), BoosterBio = "bio" };
        // The original policy does not concatenate mutable boost dependencies:
        // they become visible only after a message version changes.
        first.BoosterBio = $"{second.Id}:{second.Content.Length}:{second.Content}:{second.BoosterUpdatedAt}:{second.BoosterName.Length}:{second.BoosterName}:{second.BoosterBio}";
        message.Boosts = [first];
        Assert.DoesNotContain("id=\"boost_202\"", renderer.BroadcastMessageHtml(id, fixture.Member.Id));
        _ = renderer.MessageHtml(message, fixture.Member.Id);
        first.BoosterBio = "";
        message.Boosts = [first, second];
        Assert.DoesNotContain("id=\"boost_202\"", renderer.MessageHtml(message, fixture.Member.Id));
        message.UpdatedAt = "2031-01-01 00:00:00.123456";
        Assert.Contains("id=\"boost_202\"", renderer.MessageHtml(message, fixture.Member.Id));
        message.Boosts = [];
        message.AttachmentMetadata = "{\"diagnostic\":\"" + new string('x', 17 * 1024 * 1024) + "\"}";
        var hits = renderer.FragmentCacheHits;
        _ = renderer.MessageHtml(message, fixture.Member.Id);
        Assert.Equal(hits + 1, renderer.FragmentCacheHits);
        // Unused metadata is no longer retained in a key or value.
        Assert.Equal(renderer.MessageHtml(message, fixture.Member.Id), DecodeSegments(renderer.MessagesSegments([message], fixture.Member.Id)));
        message.AttachmentMetadata = null;
        message.RoomId = 700;
        message.CreatorId = fixture.Admin.Id;
        message.UpdatedAt = "2031-01-02 00:00:00.123456";
        var moved = renderer.MessageHtml(message, fixture.Member.Id);
        Assert.Contains($"/rooms/700/@{id}", moved);
        Assert.Contains($"data-user-id=\"{fixture.Admin.Id}\"", moved);
        message.RoomId = 701;
        message.CreatorId = fixture.Member.Id;
        message.UpdatedAt = "2031-01-03 00:00:00.123456";
        var movedAgain = renderer.MessageHtml(message, fixture.Member.Id);
        Assert.Contains($"/rooms/701/@{id}", movedAgain);
        Assert.Contains($"data-user-id=\"{fixture.Member.Id}\"", movedAgain);
        var accessor = fixture.Services.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        try
        {
            accessor.HttpContext = new DefaultHttpContext { RequestServices = fixture.Services };
            accessor.HttpContext.Request.Scheme = "http";
            accessor.HttpContext.Request.Host = new HostString(new string('x', 17 * 1024 * 1024));
            hits = renderer.FragmentCacheHits;
            var misses = renderer.FragmentCacheMisses;
            _ = renderer.MessageHtml(message, fixture.Member.Id);
            _ = renderer.MessageHtml(message, fixture.Member.Id);
            Assert.Equal(hits, renderer.FragmentCacheHits);
            Assert.Equal(misses + 2, renderer.FragmentCacheMisses);
        }
        finally { accessor.HttpContext = previous; }
    }

    [Fact]
    public async Task OriginalFrontendControlsAndSoloPreviewRenderingArePresent()
    {
        await using var fixture = new ChatApplication();
        var rich = fixture.Services.GetRequiredService<RichText>();
        var body = "<div>https://basecamp.com/<action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" href=\"https://basecamp.com/\" url=\"https://basecamp.com/image.png\" filename=\"Basecamp\" caption=\"Description\"></action-text-attachment></div>";
        Assert.DoesNotContain(">https://basecamp.com/</a>", rich.Presentation(body, _ => ""));
        Assert.Contains("mailto:person@example.com", rich.Presentation("person@example.com", _ => ""));
        Assert.Contains("href=\"http://www.example.com\"", rich.Presentation("www.example.com.", _ => ""));
        Assert.Contains("href=\"https://example.com/wiki_(proof)\"", rich.Presentation("https://example.com/wiki_(proof).", _ => ""));
        Assert.Contains("<a target=\"_blank\" href=\"https://example.com\"", rich.Presentation("<pre>https://example.com</pre>", _ => ""));
        Assert.True(EmojiCharacters.IsEmoji(0xA9));
        Assert.False(EmojiCharacters.IsEmoji(0x200D));
        Assert.False(EmojiCharacters.IsEmoji('1'));
        using var client = fixture.Client(fixture.Admin);
        var page = await client.GetStringAsync("/rooms/" + fixture.OpenRoom);
        Assert.Contains("Show join link QR code", page);
        Assert.Contains("data-web-share-url-value", page);
        Assert.Contains("notification-bell-alert", page);
        Assert.Contains("data-notifications-target=\"details\"", page);
        var editId = fixture.Store.CreateMessage(fixture.Admin, fixture.OpenRoom, body, "edit-controls");
        var edit = await client.GetStringAsync($"/rooms/{fixture.OpenRoom}/messages/{editId}/edit");
        Assert.Contains("keydown->composer#submitByKeyboard:capture", edit);
        Assert.Contains("data-direct-upload-url", edit);
        Assert.Contains("data-blob-url-template", edit);
        Assert.Contains("language-list", await client.GetStringAsync("/rooms/opens/new"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/rooms/opens")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync($"/rooms/{fixture.OpenRoom}/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/users/999999/sidebar")).StatusCode);
    }
}
