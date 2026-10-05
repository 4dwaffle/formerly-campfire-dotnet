using System.Security.Cryptography;
using Campfire.Contracts;
using Campfire.Features.Integrations;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Campfire.Features.Storage;

public sealed partial class MediaService
{
    // Called inside the owner's write transaction; no nested datastore read/write.
    // Active Storage persists attachment metadata with the owner, then uploads
    // disk bytes from its after_commit callback.
    public async Task<IPreparedRecordUpload> SaveRecordUploadInTransactionAsync(SqliteConnection connection,SqliteTransaction transaction,string recordType,long recordId,string name,IFormFile file,CancellationToken cancellationToken=default)
    {
        if(recordType is not ("Message" or "User" or "Account" or "ChatUpload")||name is not ("attachment" or "avatar" or "logo"))throw new ArgumentException("Invalid attachment owner");
        var blob=new BlobRecord{Key=Key(),Filename=Path.GetFileName(file.FileName),ContentType=file.ContentType,ByteSize=file.Length,CreatedAt=RequestUser.Timestamp()};
        var destination=DiskPath(blob.Key);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);var staging=destination+".pending";
        try
        {
            await using(var output=new FileStream(staging,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,true))
            {await file.CopyToAsync(output,cancellationToken);if(output.Length!=file.Length)throw new InvalidDataException("Upload length mismatch");output.Position=0;blob.Checksum=Convert.ToBase64String(await MD5.HashDataAsync(output,cancellationToken));}
            blob.ContentType=await Identify(staging,blob.Filename,blob.ContentType,cancellationToken);blob.Metadata="{\"identified\":true}";
            var previous=connection.Query<long>("SELECT blob_id FROM active_storage_attachments WHERE record_type=@recordType AND record_id=@recordId AND name=@name",new{recordType,recordId,name},transaction).ToArray();
            blob.Id=connection.ExecuteScalar<long>("INSERT INTO active_storage_blobs(key,filename,content_type,metadata,service_name,byte_size,checksum,created_at) VALUES(@Key,@Filename,@ContentType,@Metadata,@ServiceName,@ByteSize,@Checksum,@CreatedAt);SELECT last_insert_rowid()",blob,transaction);
            connection.Execute("DELETE FROM active_storage_attachments WHERE record_type=@recordType AND record_id=@recordId AND name=@name;INSERT INTO active_storage_attachments(blob_id,record_type,record_id,name,created_at) VALUES(@blobId,@recordType,@recordId,@name,@now)",new{blobId=blob.Id,recordType,recordId,name,now=blob.CreatedAt},transaction);
            return new PendingRecordUpload(this,blob,staging,destination,recordType,previous);
        }
        catch{File.Delete(staging);throw;}
    }
    public Task<IPreparedRecordUpload> SaveRecordUploadInTransactionAsync(SqliteConnection connection,SqliteTransaction transaction,string recordType,long recordId,string name,string attachable,CancellationToken cancellationToken=default)
    {
        if(recordType is not ("User" or "Account")||name is not ("avatar" or "logo"))throw new ArgumentException("Invalid attachment owner");
        BlobRecord? blob=null;
        if(attachable.Length>0)
        {
            var token=crypto.VerifyStorage(attachable,"blob_id");
            if(!long.TryParse(token,out var id))throw new InvalidDataException("Invalid signed blob ID");
            blob=connection.QuerySingleOrDefault<BlobRecord>($"SELECT {Columns} FROM active_storage_blobs b WHERE b.id=@id",new{id},transaction)??throw new InvalidDataException("Signed blob was not found");
        }
        var previous=connection.Query<long>("SELECT blob_id FROM active_storage_attachments WHERE record_type=@recordType AND record_id=@recordId AND name=@name",new{recordType,recordId,name},transaction).Where(id=>id!=blob?.Id).ToArray();
        connection.Execute("DELETE FROM active_storage_attachments WHERE record_type=@recordType AND record_id=@recordId AND name=@name",new{recordType,recordId,name},transaction);
        if(blob is not null)connection.Execute("INSERT INTO active_storage_attachments(blob_id,record_type,record_id,name,created_at) VALUES(@id,@recordType,@recordId,@name,@now)",new{id=blob.Id,recordType,recordId,name,now=RequestUser.Timestamp()},transaction);
        return Task.FromResult<IPreparedRecordUpload>(new PendingRecordUpload(this,blob,null,null,recordType,previous));
    }
    private async Task CompleteUpload(BlobRecord? blob,string? staging,string? destination,string recordType,long[] previous,CancellationToken cancellationToken)
    {
        if(staging is not null){if(File.Exists(staging))File.Move(staging,destination!,true);else if(!File.Exists(destination))throw new FileNotFoundException("Pending upload bytes are missing",staging);}
        if(blob is not null)
        {
            var jobs=services.GetService<RailsJobQueue>();
            if(jobs?.Enabled==true)await jobs.EnqueueAsync("ActiveStorage::AnalyzeJob",[RailsJobQueue.GlobalId("ActiveStorage::Blob",blob.Id)],cancellationToken);
            else await AnalyzeAsync(blob,cancellationToken);
            if(recordType=="Message")await ProcessAttachmentAsync(blob,cancellationToken);
        }
        await PurgeBlobsAsync(previous);
    }
    public sealed class PendingRecordUpload(MediaService owner,BlobRecord? blob,string? staging,string? destination,string recordType,long[] previous):IPreparedRecordUpload
    {
        private bool committed;
        public long? BlobId=>blob?.Id;
        public async Task CompleteAsync(CancellationToken cancellationToken=default)
        {
            committed=true;
            await owner.CompleteUpload(blob,staging,destination,recordType,previous,cancellationToken);
        }
        public ValueTask DisposeAsync(){if(!committed&&staging is not null)File.Delete(staging);return ValueTask.CompletedTask;}
    }
}
