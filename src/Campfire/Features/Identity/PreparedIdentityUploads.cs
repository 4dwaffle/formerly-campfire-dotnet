using Campfire.Contracts;
using Microsoft.Data.Sqlite;

namespace Campfire.Features.Identity;

// ActiveStorage metadata belongs to the record transaction; bytes/job completion
// runs after commit. Dispose also cleans prepared bytes if the transaction fails.
internal sealed class PreparedIdentityUploads(IMediaService media, IdentityParameters parameters, CancellationToken cancellationToken) : IAsyncDisposable
{
    private readonly List<IPreparedRecordUpload> pending = [];
    public async Task Prepare(SqliteConnection connection, SqliteTransaction transaction, string recordType, long recordId, string name, bool compactNull = false)
    {
        var field = (recordType == "Account" ? "account" : "user") + "[" + name + "]";
        if (parameters.Form.Files.GetFile(field) is IFormFile file)
            pending.Add(await media.SaveRecordUploadInTransactionAsync(connection, transaction, recordType, recordId, name, file, cancellationToken));
        else if (parameters.Has(field) && (!compactNull || parameters.Value(field) != null))
            pending.Add(await media.SaveRecordUploadInTransactionAsync(connection, transaction, recordType, recordId, name, parameters.Value(field) ?? "", cancellationToken));
    }
    public async Task Complete()
    {
        foreach (var item in pending) await item.CompleteAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var item in pending) await item.DisposeAsync();
    }
}
