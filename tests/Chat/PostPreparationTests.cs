using Campfire.Contracts;
using Campfire.Features.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Campfire.ChatTests;

public sealed partial class ChatTests
{
    [Theory]
    [InlineData("plain Čau ☕\nsecond line", "plain Čau ☕\nsecond line")]
    [InlineData(" \tplain\ntext\v ", "plain\ntext")]
    [InlineData("\u00a0plain\u00a0", "\u00a0plain\u00a0")]
    [InlineData("a\r\nb\rc", "a\nb\nc")]
    [InlineData("one &amp; two &#x2615;", "one & two ☕")]
    public async Task TextCreationIndexesOriginalPlaintextAndStillEscapesItsPresentation(string body, string expected)
    {
        await using var fixture = new ChatApplication();
        var user = fixture.Member;
        var id = await fixture.Store.CreateMessageAsync(user, fixture.OpenRoom, body, "post-text");
        Assert.Equal(expected, fixture.Db.Scalar<string>("select body from message_search_index where rowid=@id", new { id }));
        var rich = fixture.Services.GetRequiredService<RichText>();
        Assert.Equal(expected, rich.PlainText(body));
        // The shortcut applies only to plaintext extraction. Presentation
        // still sanitizes and escapes author/content rather than trusting it.
        var renderer = fixture.Services.GetRequiredService<ChatRenderer>();
        var html = renderer.MessageHtml(id, user.Id);
        Assert.Contains("Member &lt;unsafe&gt;", html);
        Assert.DoesNotContain("Member <unsafe>", html);
        Assert.Contains("data-message-id=\"" + id + "\"", html);
        Assert.Equal(fixture.Store.Message(id, user.Id).UpdatedAt, fixture.Store.Room(fixture.OpenRoom, user.Id).UpdatedAt);
    }

    [Fact]
    public async Task WarmCreatedFragmentResponseRequiresOnlyAuthorizedMetadataAndPreservesFreshTokens()
    {
        await using var fixture = new ChatApplication();
        var user = fixture.Member;
        var id = await fixture.Store.CreateMessageAsync(user, fixture.OpenRoom, "<p>fresh post ☕</p>", "warm-post");
        var reads = new ReadScopeCounter(fixture.Db);
        var store = new ChatStore(reads, fixture.Services.GetRequiredService<RichText>());
        var accessor = new HttpContextAccessor { HttpContext = RenderingContext(fixture) };
        using var renderer = Renderer(fixture, store, accessor);
        var broadcast = renderer.BroadcastMessageHtml(id, user.Id);
        Assert.DoesNotContain("authenticity_token", broadcast);
        reads.Count = 0;
        var response = renderer.MessageHtml(store.MessageMetadata(id, user.Id), user.Id);
        Assert.Equal(1, reads.Count);
        Assert.Contains("fresh post ☕", response);
        Assert.Contains("authenticity_token", response);
        accessor.HttpContext = RenderingContext(fixture);
        var other = renderer.MessageHtml(store.MessageMetadata(id, fixture.Admin.Id), fixture.Admin.Id);
        Assert.NotEqual(response, other);
        Assert.Equal(2, reads.Count);
        fixture.Db.Execute("delete from memberships where room_id=@room and user_id=@user", new { room = fixture.OpenRoom, user = user.Id });
        Assert.Throws<ChatHttpException>(() => store.MessageMetadata(id, user.Id));
    }
}
