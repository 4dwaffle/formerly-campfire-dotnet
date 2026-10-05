using Campfire.Contracts;
using Campfire.Features.Persistence;
using Dapper;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Campfire.Features.Identity;

internal sealed class IdentityJobs(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        services.GetService<IBackgroundJobs>()?.Register("RemoveBannedContentJob", RemoveContent);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    private async Task RemoveContent(JsonElement[] arguments, CancellationToken cancellationToken)
    {
        var gid = arguments[0].GetProperty("_aj_globalid").GetString();
        if (gid == null || !gid.StartsWith("gid://campfire/User/", StringComparison.Ordinal) || !long.TryParse(gid["gid://campfire/User/".Length..], out var userId)) throw new InvalidDataException("Invalid user GlobalID.");
        if (services.GetRequiredService<IDataStore>().Read(c => c.ExecuteScalar<long>("SELECT count(*) FROM users WHERE id=@userId", new { userId })) == 0) throw new InvalidDataException("The job's user GlobalID no longer exists.");
        await RemoveUserContent(userId, cancellationToken);
    }
    public async Task RemoveUserContent(long userId, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<IDataStore>();
        var media = services.GetRequiredService<IMediaService>();
        var realtime = services.GetRequiredService<IRealtimeEvents>();
        var messages = db.Read(c => c.Query<IdentityFeature.RemovedMessage>("SELECT room_id AS RoomId,id AS MessageId,client_message_id AS ClientMessageId FROM messages WHERE creator_id=@userId ORDER BY id", new { userId }).Materialize());
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blobs = db.Write((connection, transaction) =>
            {
                var id = message.MessageId;
                var blobIds = connection.Query<long>("SELECT DISTINCT a.blob_id FROM active_storage_attachments a WHERE (a.record_type='Message' AND a.record_id=@id) OR (a.record_type='ActionText::RichText' AND a.record_id IN (SELECT rt.id FROM action_text_rich_texts rt WHERE rt.record_type='Message' AND rt.record_id=@id))", new { id }, transaction).Materialize();
                connection.Execute("DELETE FROM boosts WHERE message_id=@id; DELETE FROM message_search_index WHERE rowid=@id; DELETE FROM active_storage_attachments WHERE record_type='ActionText::RichText' AND record_id IN (SELECT id FROM action_text_rich_texts WHERE record_type='Message' AND record_id=@id); DELETE FROM action_text_rich_texts WHERE record_type='Message' AND record_id=@id; DELETE FROM active_storage_attachments WHERE record_type='Message' AND record_id=@id; DELETE FROM messages WHERE id=@id; UPDATE rooms SET updated_at=@now WHERE id=@roomId", new { id, now = RequestUser.Timestamp(), roomId = message.RoomId }, transaction);
                return blobIds;
            });
            await media.PurgeBlobsAsync(blobs);
            await realtime.MessageChangedAsync(message.RoomId, message.MessageId, "remove", message.ClientMessageId);
        }
    }
}
