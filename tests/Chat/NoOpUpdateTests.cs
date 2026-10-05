using Campfire.Contracts;
using Campfire.Features.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Campfire.ChatTests;

public sealed partial class ChatTests
{
    [Fact]
    public async Task UnchangedBodyPreservesVersionsCacheAndUnreadButStillIndexesAfterCommit()
    {
        await using var fixture = new ChatApplication();
        var renderer = fixture.Services.GetRequiredService<ChatRenderer>();
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, "<p>PARITY-CACHE-OLD</p>", "noop-target");
        var version = fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt;
        var bodyVersion = fixture.Db.Scalar<string>("select updated_at from action_text_rich_texts where record_type='Message' and record_id=@id", new { id });
        var roomVersion = fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id).UpdatedAt;
        var unread = fixture.Db.Scalar<string>("select group_concat(user_id||':'||coalesce(unread_at,'')||':'||updated_at,'|') from (select * from memberships order by id)");
        Assert.Contains("PARITY-CACHE-OLD", renderer.MessageHtml(id, fixture.Member.Id));
        // Match the captured pinned Rails vector: raw body changes do not
        // alter timestamps; assigning that same persisted body is a no-op.
        fixture.Db.Execute("update action_text_rich_texts set body='<p>PARITY-CACHE-NEW</p>' where record_type='Message' and record_id=@id; ALTER TABLE message_search_index RENAME TO original_fts; CREATE TABLE message_search_index(rowid INTEGER PRIMARY KEY,body TEXT); INSERT INTO message_search_index VALUES(@id,'PARITY-CACHE-OLD'); CREATE TRIGGER fail_update BEFORE UPDATE ON message_search_index BEGIN SELECT RAISE(ABORT,'index callback invoked'); END", new { id });
        Assert.Throws<SqliteException>(() => fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<p>PARITY-CACHE-NEW</p>"));
        Assert.Throws<SqliteException>(() => fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, null, clearAttachment: true));
        Assert.Equal(version, fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt);
        Assert.Equal(bodyVersion, fixture.Db.Scalar<string>("select updated_at from action_text_rich_texts where record_type='Message' and record_id=@id", new { id }));
        Assert.Equal(roomVersion, fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id).UpdatedAt);
        Assert.Equal("PARITY-CACHE-OLD", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@id", new { id }));
        Assert.Contains("PARITY-CACHE-OLD", renderer.MessageHtml(id, fixture.Member.Id));
        fixture.Db.Execute("DROP TRIGGER fail_update");
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<p>PARITY-CACHE-NEW</p>");
        Assert.Equal("PARITY-CACHE-NEW", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@id", new { id }));
        Assert.Equal(version, fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt);
        Assert.Contains("PARITY-CACHE-OLD", renderer.MessageHtml(id, fixture.Member.Id));
        fixture.Db.Execute("CREATE TRIGGER fail_update BEFORE UPDATE ON message_search_index BEGIN SELECT RAISE(ABORT,'index callback invoked'); END");
        Assert.Throws<SqliteException>(() => fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<p>GENUINE EDIT</p>"));
        Assert.NotEqual(version, fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt);
        Assert.NotEqual(bodyVersion, fixture.Db.Scalar<string>("select updated_at from action_text_rich_texts where record_type='Message' and record_id=@id", new { id }));
        Assert.NotEqual(roomVersion, fixture.Store.Room(fixture.OpenRoom, fixture.Member.Id).UpdatedAt);
        Assert.Contains("GENUINE EDIT", renderer.MessageHtml(id, fixture.Member.Id));
        Assert.Equal("PARITY-CACHE-NEW", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@id", new { id }));
        fixture.Db.Execute("DROP TRIGGER fail_update");
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, "<p>INDEXED EDIT</p>");
        Assert.Equal("INDEXED EDIT", fixture.Db.Scalar<string>("select body from message_search_index where rowid=@id", new { id }));
        Assert.Equal(unread, fixture.Db.Scalar<string>("select group_concat(user_id||':'||coalesce(unread_at,'')||':'||updated_at,'|') from (select * from memberships order by id)"));
        Assert.Equal(403, Assert.Throws<ChatHttpException>(() => fixture.Store.UpdateMessage(fixture.Hidden, fixture.OpenRoom, id, "<p>INDEXED EDIT</p>")).Status);
    }

    [Fact]
    public async Task SameBlobAndClientIdAreNoOpsButClientOrAttachmentMutationsTouch()
    {
        await using var fixture = new ChatApplication();
        fixture.Db.Execute("insert into active_storage_blobs(id,key,filename,content_type,metadata,byte_size,checksum,service_name,created_at) values(901,'noop-blob','noop.txt','text/plain','{}',4,'checksum','local',@now)", new { now = RequestUser.Timestamp() });
        var id = fixture.Store.CreateMessage(fixture.Member, fixture.OpenRoom, null, "noop-attachment", 901);
        var version = fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt;
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, null, clientId: "noop-attachment", blobId: 901);
        Assert.Equal(version, fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt);
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, null, clientId: "changed-client");
        Assert.NotEqual(version, fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt);
        Assert.Equal(901, fixture.Store.Message(id, fixture.Member.Id).AttachmentBlobId);
        var clientVersion = fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt;
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, null, clearAttachment: true);
        Assert.NotEqual(clientVersion, fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt);
        Assert.Null(fixture.Store.Message(id, fixture.Member.Id).AttachmentBlobId);
        var detachedVersion = fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt;
        fixture.Store.UpdateMessage(fixture.Member, fixture.OpenRoom, id, null, clearAttachment: true);
        Assert.Equal(detachedVersion, fixture.Store.MessageMetadata(id, fixture.Member.Id).UpdatedAt);
    }
}
