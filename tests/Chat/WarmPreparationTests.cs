using System.Runtime.InteropServices;
using System.Text;
using Campfire.Contracts;
using Campfire.Features.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Campfire.ChatTests;

public sealed partial class ChatTests
{
    [Fact]
    public async Task WarmRoomUsesTwoReadScopesAndFrameLayoutAvoidsUnusedAccountReads()
    {
        await using var fixture = new ChatApplication();
        var user = fixture.Member;
        var id = fixture.Store.CreateMessage(user, fixture.OpenRoom, "warm preparation", "warm-preparation");
        var reads = new ReadScopeCounter(fixture.Db);
        var store = new ChatStore(reads, fixture.Services.GetRequiredService<RichText>());
        var accessor = new HttpContextAccessor();
        using var renderer = Renderer(fixture, store, accessor);
        accessor.HttpContext = RenderingContext(fixture);
        var page = store.RoomPage(fixture.OpenRoom, user.Id, htmlOnly: true);
        _ = renderer.RoomContentSegments(page.Room, page.Messages, user, renderer.Origin());
        var hydrated = store.PresentationHydrations;
        reads.Count = 0;
        accessor.HttpContext = RenderingContext(fixture);
        page = store.RoomPage(fixture.OpenRoom, user.Id, htmlOnly: true);
        var rendered = renderer.RoomContentSegments(page.Room, page.Messages, user, renderer.Origin());
        Assert.Equal(2, reads.Count); // authorized page, account+logo+bounded invitation
        Assert.Equal(hydrated, store.PresentationHydrations);
        Assert.Contains("warm preparation", DecodeSegments(rendered.Select(x => x.Bytes).ToArray()));
        reads.Count = 0;
        accessor.HttpContext = RenderingContext(fixture);
        accessor.HttpContext.Request.Headers["Turbo-Frame"] = "user_sidebar";
        Assert.Contains("csrf-token", renderer.Layout(user, "frame body"));
        Assert.Equal(0, reads.Count);
        accessor.HttpContext = RenderingContext(fixture);
        fixture.Db.Execute("update accounts set custom_styles='/* newest request */',join_code='new-code'");
        Assert.Contains("newest request", renderer.Layout(user, "ordinary body"));
        Assert.Equal(1, reads.Count);
        Assert.Contains("new-code", renderer.Invitation("http://localhost"));
        Assert.Equal(1, reads.Count); // same render request shares account metadata
        fixture.Store.UpdateRoom(fixture.Admin, fixture.OpenRoom, "Rooms::Closed", "Restricted", [fixture.Admin.Id]);
        Assert.Throws<ChatHttpException>(() => store.RoomPage(fixture.OpenRoom, user.Id, htmlOnly: true));
        Assert.Throws<ChatHttpException>(() => renderer.MessageHtml(id, user.Id));
    }

    [Fact]
    public async Task BatchedSidebarParticipantsPreserveAssociationOrderWithoutPerRoomReads()
    {
        await using var fixture = new ChatApplication();
        var user = fixture.Member;
        var rooms = new[] {
            fixture.Store.CreateRoom(user, "Rooms::Direct", null, [user.Id]),
            fixture.Store.CreateRoom(user, "Rooms::Direct", null, [fixture.Connected.Id]),
            fixture.Store.CreateRoom(user, "Rooms::Direct", null, [fixture.Admin.Id, fixture.Hidden.Id, fixture.Connected.Id])
        };
        var expected = rooms.ToDictionary(room => room, room => fixture.Store.Members(room).Select(member => member.Id).ToArray());
        var reads = new ReadScopeCounter(fixture.Db);
        var store = new ChatStore(reads, fixture.Services.GetRequiredService<RichText>());
        var participants = store.SidebarMembers(rooms);
        Assert.Equal(1, reads.Count);
        foreach (var room in rooms) Assert.Equal(expected[room], participants[room].Select(member => member.Id));
        Assert.Empty(store.SidebarMembers([]));
        Assert.Equal(1, reads.Count);
        var accessor = new HttpContextAccessor { HttpContext = RenderingContext(fixture) };
        using var renderer = Renderer(fixture, store, accessor);
        var expectedHtml = string.Concat(store.Rooms(user.Id, visible: true).Where(room => room.Direct).OrderByDescending(room => room.UpdatedAt).Select(room => renderer.SidebarRoom(room, user.Id)));
        reads.Count = 0;
        var sidebar = renderer.Sidebar(user);
        Assert.Contains(expectedHtml, sidebar);
        Assert.Equal(5, reads.Count); // rooms, one participant batch, two placeholder queries, account
    }

    [Fact]
    public async Task TypedSegmentsCacheOnlyImmutableTokenFreeMessageBytes()
    {
        await using var fixture = new ChatApplication();
        var user = fixture.Member;
        var id = fixture.Store.CreateMessage(user, fixture.OpenRoom, "<p>cache slices Čau ☕</p>", "typed-segments");
        var store = fixture.Store;
        var accessor = new HttpContextAccessor { HttpContext = RenderingContext(fixture) };
        using var renderer = Renderer(fixture, store, accessor);
        var page = store.RoomPage(fixture.OpenRoom, user.Id, htmlOnly: true);
        var first = renderer.MessagesContentSegments(page.Messages, user.Id);
        var stable = first.Where(segment => segment.Cacheable).ToArray();
        Assert.NotEmpty(stable);
        foreach (var segment in stable) Assert.DoesNotContain("authenticity_token", Encoding.UTF8.GetString(segment.Bytes.Span));
        var firstTokens = first.Where(segment => !segment.Cacheable).Select(segment => Encoding.UTF8.GetString(segment.Bytes.Span)).Where(text => text.StartsWith("<input type=\"hidden\" name=\"authenticity_token\"", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(firstTokens);
        accessor.HttpContext = RenderingContext(fixture);
        var second = renderer.MessagesContentSegments(page.Messages, fixture.Admin.Id);
        var stableAgain = second.Where(segment => segment.Cacheable).ToArray();
        Assert.Equal(stable.Length, stableAgain.Length);
        for (var index = 0; index < stable.Length; index++)
        {
            Assert.True(MemoryMarshal.TryGetArray(stable[index].Bytes, out var one));
            Assert.True(MemoryMarshal.TryGetArray(stableAgain[index].Bytes, out var two));
            Assert.Same(one.Array, two.Array);
            Assert.Equal(one.Offset, two.Offset);
            Assert.Equal(one.Count, two.Count);
        }
        foreach (var segment in second.Where(segment => !segment.Cacheable)) Assert.DoesNotContain(firstTokens[0], Encoding.UTF8.GetString(segment.Bytes.Span));
        var typed = renderer.MessagesContentSegments(page.Messages, user.Id);
        Assert.Equal(renderer.MessageHtml(id, user.Id), DecodeSegments(typed.Select(segment => segment.Bytes).ToArray()));
    }

    [Fact]
    public async Task RoomShellReusesOnlyTokenFreeBytesAndRendersCurrentViewerAndAccount()
    {
        await using var fixture = new ChatApplication();
        var user = fixture.Member;
        var room = fixture.Store.CreateRoom(user, "Rooms::Open", "Shell room", []);
        fixture.Store.CreateMessage(user, room, "<p>shell Čau ☕</p>", "shell-cache");
        var accessor = new HttpContextAccessor { HttpContext = RenderingContext(fixture) };
        using var renderer = Renderer(fixture, fixture.Store, accessor);
        var page = fixture.Store.RoomPage(room, user.Id, htmlOnly: true);
        var fields = new System.Text.RegularExpressions.Regex("<input type=\"hidden\" name=\"authenticity_token\" value=\"[^\"]*\">|<meta name=\"csrf-token\" content=\"[^\"]*\">");
        string Html(IReadOnlyList<HtmlSegment> segments) => DecodeSegments(segments.Select(x => x.Bytes).ToArray());
        string Normalize(string html) => fields.Replace(html, "[fresh csrf]");
        var first = renderer.RoomContentSegments(page.Room, page.Messages, user, renderer.Origin());
        Assert.Equal(Normalize(renderer.RoomHtml(page.Room, page.Messages, user, renderer.Origin())), Normalize(Html(first)));
        var firstFields = fields.Matches(Html(first)).Select(x => x.Value).Distinct().ToArray();
        Assert.Equal(2, firstFields.Length); // independent meta and form masks
        Assert.All(first.Where(x => x.Cacheable), x => Assert.DoesNotMatch(fields, Encoding.UTF8.GetString(x.Bytes.Span)));
        accessor.HttpContext = RenderingContext(fixture);
        var second = renderer.RoomContentSegments(page.Room, page.Messages, user, renderer.Origin());
        Assert.Equal(Normalize(Html(first)), Normalize(Html(second)));
        Assert.All(firstFields, token => Assert.DoesNotContain(token, Html(second)));
        var stable = first.Where(x => x.Cacheable).ToArray();
        var repeated = second.Where(x => x.Cacheable).ToArray();
        Assert.Equal(stable.Length, repeated.Length);
        for (var i = 0; i < stable.Length; i++) Assert.True(stable[i].Bytes.Equals(repeated[i].Bytes));

        fixture.Db.Execute("update accounts set custom_styles='/* changed without version touch */'");
        accessor.HttpContext = RenderingContext(fixture);
        var other = Html(renderer.RoomContentSegments(page.Room, page.Messages, fixture.Admin, renderer.Origin()));
        Assert.Contains($"<meta name=\"current-user-id\" content=\"{fixture.Admin.Id}\">", other);
        Assert.DoesNotContain($"<meta name=\"current-user-id\" content=\"{user.Id}\">", other);
        Assert.Contains("changed without version touch", other);
        Assert.All(firstFields, token => Assert.DoesNotContain(token, other));

        // Changes without version touches must not reuse stale shell inputs.
        page.Room.Name = "Renamed <meta name=\"csrf-token\" content=\"unsafe\">";
        user.Name = "New viewer <unsafe>";
        user.Bio = "New bio Čau";
        accessor.HttpContext = RenderingContext(fixture);
        var changed = renderer.RoomContentSegments(page.Room, page.Messages, user, "https://changed.example");
        Assert.Equal(Normalize(renderer.RoomHtml(page.Room, page.Messages, user, "https://changed.example")), Normalize(Html(changed)));
        Assert.Contains("New viewer &lt;unsafe&gt;", Html(changed));
        Assert.DoesNotContain("<meta name=\"csrf-token\" content=\"unsafe\">", Html(changed));

        accessor.HttpContext = RenderingContext(fixture);
        accessor.HttpContext.Request.Headers["Turbo-Frame"] = "user_sidebar";
        var frame = renderer.RoomContentSegments(page.Room, page.Messages, user, renderer.Origin());
        Assert.Equal(Normalize(renderer.RoomHtml(page.Room, page.Messages, user, renderer.Origin())), Normalize(Html(frame)));
        Assert.DoesNotContain("changed without version touch", Html(frame));
    }

    [Fact]
    public async Task CachedRoomShellKeepsLiveInvitationBoundaryAndMembership()
    {
        await using var fixture = new ChatApplication();
        var user = fixture.Member;
        long latest = 0;
        for (var i = 0; i <= ChatStore.PageSize; i++)
            latest = fixture.Store.CreateMessage(user, fixture.OpenRoom, "invitation boundary " + i, "invitation-" + i);
        var accessor = new HttpContextAccessor();
        using var renderer = Renderer(fixture, fixture.Store, accessor);
        string Render()
        {
            accessor.HttpContext = RenderingContext(fixture);
            var page = fixture.Store.RoomPage(fixture.OpenRoom, user.Id, htmlOnly: true);
            return DecodeSegments(renderer.RoomSegments(page.Room, page.Messages, user, renderer.Origin()));
        }
        Assert.DoesNotContain("id=\"system_welcome\"", Render());
        Assert.DoesNotContain("id=\"system_welcome\"", Render());
        fixture.Store.DeleteMessage(user, fixture.OpenRoom, latest);
        Assert.Contains("id=\"system_welcome\"", Render());
        fixture.Store.CreateMessage(user, fixture.OpenRoom, "above boundary again", "invitation-again");
        Assert.DoesNotContain("id=\"system_welcome\"", Render());
        fixture.Store.UpdateRoom(fixture.Admin, fixture.OpenRoom, "Rooms::Closed", "Restricted", [fixture.Admin.Id]);
        Assert.Throws<ChatHttpException>(() => Render());
    }

    private static DefaultHttpContext RenderingContext(ChatApplication fixture)
    {
        var context = new DefaultHttpContext { RequestServices = fixture.Services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        return context;
    }
    private static ChatRenderer Renderer(ChatApplication fixture, ChatStore store, IHttpContextAccessor accessor)
        => new(store, fixture.Services.GetRequiredService<RichText>(), fixture.Services.GetRequiredService<IRailsCrypto>(), fixture.Services.GetRequiredService<IMediaService>(), fixture.Services.GetRequiredService<IWebHostEnvironment>(), accessor, fixture.Services.GetRequiredService<IConfiguration>());
}

internal sealed class ReadScopeCounter(IDataStore inner) : IDataStore
{
    public int Count { get; set; }
    public string DatabasePath => inner.DatabasePath;
    public T Read<T>(Func<SqliteConnection, T> operation) { Count++; return inner.Read(operation); }
    public List<T> Query<T>(string sql, object? parameters = null) => inner.Query<T>(sql, parameters);
    public T? Single<T>(string sql, object? parameters = null) => inner.Single<T>(sql, parameters);
    public T Scalar<T>(string sql, object? parameters = null) => inner.Scalar<T>(sql, parameters);
    public int Execute(string sql, object? parameters = null) => inner.Execute(sql, parameters);
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation) => inner.Write(operation);
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation, Action<SqliteConnection, T> afterCommit) => inner.Write(operation, afterCommit);
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation, CancellationToken cancellationToken = default) => inner.WriteAsync(operation, cancellationToken);
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation, Action<SqliteConnection, T> afterCommit, CancellationToken cancellationToken = default) => inner.WriteAsync(operation, afterCommit, cancellationToken);
}
