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
    [Fact]
    public async Task HtmlPageKeysPreserveTimestampPaginationAndColdRendering()
    {
        await using var fixture = new ChatApplication();
        var user = fixture.Member;
        var room = fixture.Store.CreateRoom(user, "Rooms::Open", "Packed pages", []);
        var ids = Enumerable.Range(0, 5).Select(i => fixture.Store.CreateMessage(user, room, $"<p>page {i} Čau ☕</p>", "packed-" + i)).ToArray();
        for (var i = 0; i < ids.Length; i++)
            fixture.Db.Execute("update messages set created_at=@created where id=@id", new { id = ids[i], created = "2026-03-02 12:00:0" + (i / 2) + ".000000" });
        var cases = new (long? Before, long? After, long? Around)[]
        {
            (null, null, null), (ids[2], null, null), (null, ids[2], null),
            (ids[2], ids[4], null), (null, null, ids[2]), (ids[0], null, null), (null, ids[4], null)
        };
        foreach (var (before, after, around) in cases)
        {
            var full = fixture.Store.Page(room, user.Id, before, after, around);
            var keys = fixture.Store.Page(room, user.Id, before, after, around, htmlOnly: true);
            Assert.Equal(full.Select(x => (x.Id, x.CreatedAt, x.UpdatedAt)), keys.Select(x => (x.Id, x.CreatedAt, x.UpdatedAt)));
            var accessor = new HttpContextAccessor(); // no request tokens for exact cold-output comparison
            using var renderer = Renderer(fixture, fixture.Store, accessor);
            var actual = DecodeSegments(renderer.MessagesSegments(keys, user.Id));
            Assert.Equal(renderer.MessagesHtml(full, user.Id), actual);
        }
        foreach (var around in new long?[] { null, ids[2] })
        {
            var full = fixture.Store.RoomPage(room, user.Id, around: around);
            var keys = fixture.Store.RoomPageForRendering(room, user.Id, around);
            Assert.Equal(full.Messages.Select(x => (x.Id, x.UpdatedAt)), keys.Messages.Select(x => (x.Id, x.UpdatedAt)));
            Assert.All(keys.Messages, x => Assert.False(x.PresentationHydrated));
            using var renderer = Renderer(fixture, fixture.Store, new HttpContextAccessor());
            Assert.Equal(renderer.RoomHtml(full.Room, full.Messages, user, renderer.Origin()),
                DecodeSegments(renderer.RoomSegments(keys.Room, keys.Messages, user, renderer.Origin())));
        }
        fixture.Store.UpdateRoom(fixture.Admin, room, "Rooms::Closed", "Restricted", [fixture.Admin.Id]);
        Assert.Throws<ChatHttpException>(() => fixture.Store.Page(room, user.Id, htmlOnly: true));
        Assert.Throws<ChatHttpException>(() => fixture.Store.RoomPageForRendering(room, user.Id));
    }

    private static string DecodeSegments(IReadOnlyList<ReadOnlyMemory<byte>> segments)
    {
        using var bytes = new MemoryStream();
        foreach (var segment in segments) bytes.Write(segment.Span);
        return Encoding.UTF8.GetString(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }

    [Fact]
    public async Task CachedCreatorAndUnversionedBodyFollowCapturedPinnedRailsVectorsUntilTouch()
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rails-cache-policy.json")));
        await using var fixture = new ChatApplication();
        var renderer = fixture.Services.GetRequiredService<ChatRenderer>();
        const string oldBody = "PARITY-CACHE-OLD", newBody = "PARITY-CACHE-NEW", newName = "PARITY-CREATOR-NEW";
        var oldName = WebUtility.HtmlEncode(fixture.Member.Name);
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "<p>" + oldBody + "</p>", "golden-cache-target");
        var version = fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt;
        void CompareStage(string stage)
        {
            var html = renderer.MessageHtml(id, fixture.Member.Id);
            Assert.Equal(html, DecodeSegments(renderer.MessagesSegments([fixture.Store.MessageMetadata(id, fixture.Member.Id)], fixture.Member.Id)));
            var expected = golden.RootElement.GetProperty("observations").GetProperty(stage);
            Assert.Equal(expected.GetProperty("old_body").GetBoolean(), html.Contains(oldBody, StringComparison.Ordinal));
            Assert.Equal(expected.GetProperty("new_body").GetBoolean(), html.Contains(newBody, StringComparison.Ordinal));
            Assert.Equal(expected.GetProperty("old_creator").GetBoolean(), html.Contains(oldName, StringComparison.Ordinal));
            Assert.Equal(expected.GetProperty("new_creator").GetBoolean(), html.Contains(newName, StringComparison.Ordinal));
        }
        CompareStage("cache-warm-first");
        CompareStage("cache-warm-second");
        fixture.Db.Execute("update users set name=@newName,updated_at='2030-01-01 00:00:00.123456' where id=@id", new { newName, id = fixture.Member.Id });
        CompareStage("cache-after-creator-rename");
        Assert.Equal(oldBody.Length, newBody.Length);
        fixture.Db.Execute("update action_text_rich_texts set body=@body where record_type='Message' and record_id=@id and name='body'", new { body = "<p>" + newBody + "</p>", id });
        CompareStage("cache-after-same-version-body-edit");
        Assert.Equal(version, fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt);
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<p>" + newBody + "</p>");
        Assert.Equal(version, fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt);
        CompareStage("cache-after-same-version-body-edit");
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<p>" + newBody + " EDITED</p>");
        var refreshed = renderer.MessageHtml(id, fixture.Member.Id);
        Assert.Contains(newBody, refreshed);
        Assert.Contains(newName, refreshed);
    }

    [Fact]
    public async Task WarmHtmlPagesNeedNoRichTextBoostOrBlobQueries()
    {
        await using var fixture = new ChatApplication();
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "warm cache coffee", "warm-graph-target");
        fixture.Store.CreateBoost(fixture.Hidden, id, "warm boost");
        using var client = fixture.Client(fixture.Member);
        Assert.Contains("warm cache coffee", await client.GetStringAsync($"/rooms/{fixture.OpenRoom}"));
        var hydrated = fixture.Store.PresentationHydrations;
        // A temporary schema fault proves hits do not silently fetch any of
        // these graphs. Restore every table before leaving the fixture.
        fixture.Db.Execute("ALTER TABLE action_text_rich_texts RENAME TO hidden_rich_texts; ALTER TABLE boosts RENAME TO hidden_boosts; ALTER TABLE active_storage_blobs RENAME TO hidden_blobs");
        try
        {
            foreach (var path in new[] { $"/rooms/{fixture.OpenRoom}", $"/rooms/{fixture.OpenRoom}/messages", $"/rooms/{fixture.OpenRoom}/messages/{id}", "/searches?q=coffee" })
                Assert.Contains("warm cache coffee", await client.GetStringAsync(path));
            Assert.Equal(hydrated, fixture.Store.PresentationHydrations);
        }
        finally
        {
            fixture.Db.Execute("ALTER TABLE hidden_rich_texts RENAME TO action_text_rich_texts; ALTER TABLE hidden_boosts RENAME TO boosts; ALTER TABLE hidden_blobs RENAME TO active_storage_blobs");
        }
        Assert.Contains("warm cache coffee", fixture.Store.Message(id, fixture.Member.Id).Body);
    }

    [Fact]
    public async Task MetadataPagesReuseEncodedFragmentsAndKeepStringJsonEditorAndCsrfContracts()
    {
        await using var fixture = new ChatApplication();
        var renderer = fixture.Services.GetRequiredService<ChatRenderer>();
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "<p>coffee ☕ Čau 日本語</p>", "utf8-fragment");
        fixture.Store.CreateBoost(fixture.Hidden, id, "🔥 café");
        var full = fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id);
        var metadata = fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id, htmlOnly: true);
        Assert.All(metadata, row => { Assert.False(row.PresentationHydrated); Assert.Equal("", row.Body); Assert.Empty(row.Boosts); });
        var room = fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id);
        Assert.Equal(renderer.RoomHtml(room, full, fixture.Member, renderer.Origin()), DecodeSegments(renderer.RoomSegments(room, metadata, fixture.Member, renderer.Origin())));
        Assert.Equal(renderer.MessagesHtml(full, fixture.Member.Id), DecodeSegments(renderer.MessagesSegments(metadata, fixture.Member.Id)));
        Assert.Equal(renderer.SearchHtml(fixture.Member, "coffee", full, fixture.OpenRoom), DecodeSegments(renderer.SearchSegments(fixture.Member, "coffee", metadata, fixture.OpenRoom)));
        var hydrated = fixture.Store.PresentationHydrations;
        _ = renderer.MessagesSegments(fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id, htmlOnly: true), fixture.Member.Id);
        Assert.Equal(hydrated, fixture.Store.PresentationHydrations);

        using var member = fixture.Client(fixture.Member);
        using var admin = fixture.Client(fixture.Admin);
        var path = $"/rooms/{fixture.OpenRoom}";
        var memberResponse = await member.GetAsync(path);
        var memberBytes = await memberResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(memberBytes.LongLength, memberResponse.Content.Headers.ContentLength);
        var memberHtml = Encoding.UTF8.GetString(memberBytes);
        var adminHtml = await admin.GetStringAsync(path);
        Assert.NotEqual(Token(memberHtml), Token(adminHtml));
        Assert.DoesNotContain(Token(memberHtml), adminHtml);
        var memberDocument = new HtmlParser().ParseDocument(memberHtml);
        var memberInputs = memberDocument.QuerySelectorAll("[data-message-id] input[name='authenticity_token']").Select(x => x.GetAttribute("value")).Distinct().ToArray();
        Assert.Single(memberInputs);
        Assert.False(string.IsNullOrEmpty(memberInputs[0]));
        var adminInput = new HtmlParser().ParseDocument(adminHtml).QuerySelector("[data-message-id] input[name='authenticity_token']")!.GetAttribute("value")!;
        Assert.NotEqual(memberInputs[0], adminInput);
        var warmHydrated = fixture.Store.PresentationHydrations;
        Assert.Contains("coffee", await member.GetStringAsync(path));
        Assert.Contains("coffee", await member.GetStringAsync($"/rooms/{fixture.OpenRoom}/messages"));
        Assert.Contains("coffee", await member.GetStringAsync("/searches?q=coffee"));
        Assert.Equal(warmHydrated, fixture.Store.PresentationHydrations);
        var index = await member.GetAsync($"/rooms/{fixture.OpenRoom}/messages");
        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/rooms/{fixture.OpenRoom}/messages");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", index.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await member.SendAsync(conditional)).StatusCode);
        Assert.Equal(warmHydrated, fixture.Store.PresentationHydrations);

        Assert.Contains("coffee ☕ Čau 日本語", await member.GetStringAsync($"/rooms/{fixture.OpenRoom}/messages/{id}/edit"));
        using var bot = fixture.CreateClient(new() { AllowAutoRedirect = false });
        using var json = JsonDocument.Parse(await bot.GetStringAsync($"/rooms/{fixture.OpenRoom}/{fixture.Bot.Id}-{fixture.Bot.BotToken}/messages"));
        Assert.Contains("coffee", json.RootElement[0].GetProperty("body").GetProperty("html").GetString());
        Assert.Equal(fixture.Member.Name, json.RootElement[0].GetProperty("creator").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync($"/messages/{id}/boosts", Form(adminInput, new() { ["boost[content]"] = "accepted" }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync($"/messages/{id}/boosts", Form(memberInputs[0]!, new() { ["boost[content]"] = "rejected" }))).StatusCode);
    }

    [Fact]
    public async Task MetadataCacheFollowsMessageTouchesAndNeverCachesMembership()
    {
        await using var fixture = new ChatApplication();
        var renderer = fixture.Services.GetRequiredService<ChatRenderer>();
        var rich = fixture.Services.GetRequiredService<RichText>();
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom,
            "coffee <action-text-attachment sgid=\"" + rich.MentionSgid(fixture.Hidden.Id) + "\"></action-text-attachment>", "metadata-versions");
        using var client = fixture.Client(fixture.Member);
        var path = $"/rooms/{fixture.OpenRoom}/messages";
        Assert.Contains("coffee", await client.GetStringAsync(path));
        var hydrated = fixture.Store.PresentationHydrations;
        _ = await client.GetStringAsync(path);
        Assert.Equal(hydrated, fixture.Store.PresentationHydrations);
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "edited coffee");
        Assert.Contains("edited coffee", await client.GetStringAsync(path));
        Assert.Equal(hydrated + 1, fixture.Store.PresentationHydrations);
        var boost = fixture.Store.CreateBoost(fixture.Hidden, id, "original boost");
        Assert.Contains("original boost", await client.GetStringAsync(path));
        fixture.Db.Execute("update boosts set content=@content where id=@boost", new { boost, content = "changed boost" });
        Assert.DoesNotContain("changed boost", await client.GetStringAsync(path));
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "edited coffee", clientId: "metadata-boost-touch");
        Assert.Contains("changed boost", await client.GetStringAsync(path));
        fixture.Store.DeleteBoost(fixture.Hidden, id, boost);
        Assert.DoesNotContain("changed boost", await client.GetStringAsync(path));
        fixture.Db.Execute("update users set name=@name,bio=@bio,updated_at=@version where id=@user", new { user = fixture.Member.Id, name = "Renamed creator", bio = "Avatar biography", version = "2028-01-01 00:00:00.123456" });
        var renamed = await client.GetStringAsync(path);
        Assert.DoesNotContain("Renamed creator", renamed);
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "edited coffee", clientId: "metadata-creator-touch");
        renamed = await client.GetStringAsync(path);
        Assert.Contains("Renamed creator", renamed);
        Assert.Contains("v=20280101000000", renamed);
        fixture.Store.UpdateRoom(fixture.Admin, fixture.OpenRoom, "Rooms::Open", "New room name", []);
        Assert.DoesNotContain("New room name", await client.GetStringAsync(path));
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "edited coffee", clientId: "metadata-room-touch");
        Assert.Contains("New room name", await client.GetStringAsync(path));
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<action-text-attachment sgid=\"" + rich.MentionSgid(fixture.Hidden.Id) + "\"></action-text-attachment>");
        Assert.Contains("User 3", await client.GetStringAsync(path));
        fixture.Db.Execute("update users set name=@name where id=@user", new { user = fixture.Hidden.Id, name = "Renamed mention" });
        Assert.DoesNotContain("Renamed mention", await client.GetStringAsync(path));
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<action-text-attachment sgid=\"" + rich.MentionSgid(fixture.Hidden.Id) + "\"></action-text-attachment>", clientId: "metadata-mention-touch");
        Assert.Contains("Renamed mention", await client.GetStringAsync(path));
        // Even independent rich-text versions leave the pinned fragment stale.
        fixture.Db.Execute("update action_text_rich_texts set body=@body,updated_at=@version where record_type='Message' and record_id=@id", new { id, body = "independent rich text", version = "2029-01-01 00:00:00.123456" });
        Assert.DoesNotContain("independent rich text", await client.GetStringAsync(path));
        Assert.Equal("independent rich text".Length, "alternative rich text".Length);
        fixture.Db.Execute("update action_text_rich_texts set body=@body where record_type='Message' and record_id=@id", new { id, body = "alternative rich text" });
        Assert.DoesNotContain("alternative rich text", await client.GetStringAsync(path));
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "alternative rich text", clientId: "metadata-body-touch");
        Assert.Contains("alternative rich text", await client.GetStringAsync(path));
        var pending = fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id, htmlOnly: true);
        fixture.Store.UpdateRoom(fixture.Admin, fixture.OpenRoom, "Rooms::Closed", "Private", [fixture.Admin.Id]);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Empty(fixture.Store.HydrateMessages(pending.Select(x => x.Id), fixture.Member.Id));
        Assert.Throws<ChatHttpException>(() => fixture.Store.Page(fixture.OpenRoom, fixture.Member.Id, htmlOnly: true));
    }

    [Fact]
    public async Task EncodedAttachmentsRefreshOnMessageTouchAndOrphansRemainRenderableOnMiss()
    {
        await using var fixture = new ChatApplication();
        var now = RequestUser.Timestamp();
        fixture.Db.Execute("insert into active_storage_blobs(id,key,filename,content_type,metadata,byte_size,checksum,service_name,created_at) values(900,'metadata-file','original.txt','text/plain','{}',4,'checksum','local',@now)", new { now });
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, null, "metadata-file", 900);
        using var client = fixture.Client(fixture.Admin);
        var path = $"/rooms/{fixture.OpenRoom}/messages";
        Assert.Contains("original.txt", await client.GetStringAsync(path));
        var hydrated = fixture.Store.PresentationHydrations;
        _ = await client.GetStringAsync(path);
        Assert.Equal(hydrated, fixture.Store.PresentationHydrations);
        fixture.Db.Execute("update active_storage_blobs set filename=@filename,metadata=@metadata where id=900", new { filename = "renamed-č.txt", metadata = "{\"analyzed\":true}" });
        Assert.Contains("original.txt", await client.GetStringAsync(path));
        fixture.Store.UpdateMessage(fixture.Admin, fixture.OpenRoom, id, null, clientId: "metadata-file-touch");
        Assert.Contains("renamed-č.txt", await client.GetStringAsync(path));
        Assert.Equal(hydrated + 1, fixture.Store.PresentationHydrations);
        fixture.Db.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF;update messages set creator_id=999999,updated_at=$version where id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", "2031-01-01 00:00:00.123456");
            command.ExecuteNonQuery();
            return 0;
        });
        Assert.Contains("Failed to load message content", await client.GetStringAsync(path));
        var warmOrphan = fixture.Store.PresentationHydrations;
        Assert.Contains("Failed to load message content", await client.GetStringAsync(path));
        Assert.Equal(warmOrphan, fixture.Store.PresentationHydrations);
        Assert.Contains("method=\"post\"", await client.GetStringAsync($"/rooms/{fixture.OpenRoom}/messages/{id}/edit"));
    }

    [Fact]
    public async Task AsyncMessageAdmissionCancelsBeforeCommitAndIndexFailurePreservesCommit()
    {
        await using var fixture = new ChatApplication();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.CreateMessageAsync(fixture.Member, fixture.OpenRoom, "cancelled", "cancelled", cancellationToken: cancelled.Token));
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from messages where client_message_id='cancelled'"));
        var success = await fixture.Store.CreateMessageAsync(fixture.Member, fixture.OpenRoom, "async success", "async-success");
        Assert.Equal("async success", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@success", new { success }));
        fixture.Db.Execute("update memberships set unread_at=null; ALTER TABLE message_search_index RENAME TO original_fts; CREATE TABLE message_search_index(rowid INTEGER PRIMARY KEY,body TEXT); CREATE TRIGGER fail_insert BEFORE INSERT ON message_search_index BEGIN SELECT RAISE(ABORT,'index failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Store.CreateMessageAsync(fixture.Member, fixture.OpenRoom, "committed body", "async-fault"));
        var id = fixture.Db.Scalar<long>("select id from messages where client_message_id='async-fault'");
        Assert.Equal("committed body", fixture.Store.Message(id, fixture.Member.Id).Body);
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from message_search_index where rowid=@id", new { id }));
        Assert.Equal(0, fixture.Db.Scalar<long>("select count(*) from memberships where unread_at is not null"));
        Assert.Equal(fixture.Store.Message(id, fixture.Member.Id).UpdatedAt, fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id).UpdatedAt);
    }
}
