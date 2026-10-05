using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.FeatureTests;
using Campfire.Features.Identity;
using Campfire.Features.Storage;

FeatureHost.Check(!JsonSerializer.IsReflectionEnabledByDefault, "Storage suite must disable JSON reflection");
await using var host = await FeatureHost.Start();
var client = host.Client;
var bytes = Encoding.UTF8.GetBytes("0123456789 uploaded bytes");
var checksum = Convert.ToBase64String(MD5.HashData(bytes));
var input = new JsonObject { ["blob"] = new JsonObject { ["filename"] = "<unsafe>.txt", ["byte_size"] = bytes.Length, ["checksum"] = checksum, ["content_type"] = "text/plain" } };
async Task<HttpResponseMessage> Allocate() { using var content = new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json"); return await client.PostAsync("/rails/active_storage/direct_uploads", content); }
using var unauthenticated = await Allocate();
FeatureHost.Check((int)unauthenticated.StatusCode==422, "Engine allocation requires CSRF without Campfire authentication");
client.DefaultRequestHeaders.Add("Cookie", "test_user=1");
using var noCsrf = await Allocate();
FeatureHost.Check((int)noCsrf.StatusCode == 422, "Direct allocation requires CSRF");
client.DefaultRequestHeaders.Add("X-CSRF-Token", "test-csrf");
using var allocation = await Allocate();
FeatureHost.Check(allocation.IsSuccessStatusCode, "Authorized allocation failed");
using var doc = JsonDocument.Parse(await allocation.Content.ReadAsStringAsync());
FeatureHost.Check(doc.RootElement.GetProperty("metadata").ValueKind==JsonValueKind.Object&&!doc.RootElement.GetProperty("metadata").EnumerateObject().Any()&&host.Db.Scalar<string?>("SELECT metadata FROM active_storage_blobs WHERE id=@id",new{id=doc.RootElement.GetProperty("id").GetInt64()}) is null,"Omitted allocation metadata must serialize as an empty hash while preserving SQL NULL");
((JsonObject)input["blob"]!)["metadata"]=new JsonObject{["custom"]="kept"};
using(var explicitMetadata=await Allocate())
{
    using var metadataDocument=JsonDocument.Parse(await explicitMetadata.Content.ReadAsStringAsync());
    FeatureHost.Check(metadataDocument.RootElement.GetProperty("metadata").GetProperty("custom").GetString()=="kept","Explicit allocation metadata was discarded");
}
((JsonObject)input["blob"]!).Remove("metadata");
var upload = doc.RootElement.GetProperty("direct_upload").GetProperty("url").GetString()!;
var signed = doc.RootElement.GetProperty("signed_id").GetString()!;
var token = new Uri(upload).AbsolutePath.Split('/').Last();
using (var tokenData = JsonDocument.Parse(Campfire.Features.Identity.RailsCrypto.Decode64(token.Split("--")[0])))
    FeatureHost.Check(tokenData.RootElement.GetProperty("_rails").TryGetProperty("exp", out _) && !tokenData.RootElement.GetProperty("_rails").GetProperty("data").TryGetProperty("expires_at", out _), "Upload expiration must use Rails metadata");
using var badBody = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', bytes.Length)));
badBody.Headers.ContentType = new("text/plain");
using var rejected = await client.PutAsync(upload, badBody);
FeatureHost.Check((int)rejected.StatusCode == 422 && !File.Exists(host.Media.DiskPath(doc.RootElement.GetProperty("key").GetString()!)), "Corrupt upload must not persist bytes");
using var body = new ByteArrayContent(bytes); body.Headers.ContentType = new("text/plain");
using var accepted = await client.PutAsync(upload, body);
FeatureHost.Check(accepted.StatusCode == HttpStatusCode.NoContent, "Valid bytes failed to persist");
using var blob = await client.GetAsync($"/rails/active_storage/blobs/redirect/{signed}/file.txt");
FeatureHost.Check(blob.StatusCode == HttpStatusCode.Found, "Blob redirect must return a signed disk URL");
var disk = blob.Headers.Location!;
using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, disk); rangeRequest.Headers.Range = new(2, 5);
using var ranged = await client.SendAsync(rangeRequest);
FeatureHost.Check(ranged.StatusCode == HttpStatusCode.PartialContent && await ranged.Content.ReadAsStringAsync() == "2345", "Range bytes mismatch");
using var proxy = await client.GetAsync($"/rails/active_storage/blobs/proxy/{signed}/file.txt");
FeatureHost.Check((await proxy.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "Proxy changed blob bytes");
using var wrongPurpose = await client.GetAsync($"/rails/active_storage/blobs/proxy/{host.Crypto.SignStorage(doc.RootElement.GetProperty("id").ToString(), "variation")}/file.txt");
FeatureHost.Check(wrongPurpose.StatusCode == HttpStatusCode.NotFound, "Blob token must bind purpose");
var expiredToken = host.Crypto.SignStorage(new JsonObject { ["key"] = doc.RootElement.GetProperty("key").GetString(), ["content_type"] = "text/plain", ["disposition"] = "inline", ["service_name"] = "local" }.ToJsonString(), "blob_key", DateTimeOffset.UtcNow.AddSeconds(-1));
using var expired = await client.GetAsync($"/rails/active_storage/disk/{expiredToken}/file.txt");
FeatureHost.Check(expired.StatusCode == HttpStatusCode.NotFound, "Expired disk token accepted");
try { host.Media.DiskPath("../traversal"); throw new InvalidOperationException("Storage path accepted traversal"); } catch (ArgumentException) { }
var id = doc.RootElement.GetProperty("id").GetInt64();
host.Db.Execute("INSERT INTO active_storage_attachments(blob_id,record_type,record_id,name,created_at) VALUES(@id,'Message',1,'attachment',@now)", new { id, now = Campfire.Contracts.RequestUser.Timestamp() });
FeatureHost.Check(host.Media.AttachmentHtml(1).Contains("&lt;unsafe&gt;.txt"), "Filename must be escaped");
foreach(var excluded in new[]{"image/bmp","image/vnd.microsoft.icon","image/vnd.adobe.photoshop"})
{
    host.Db.Execute("UPDATE active_storage_blobs SET content_type=@excluded WHERE id=@id",new{excluded,id=doc.RootElement.GetProperty("id").GetInt64()});
    FeatureHost.Check(!host.Media.AttachmentHtml(1).Contains("/representations/",StringComparison.Ordinal),"Campfire excluded image type produced a variant: "+excluded);
}
host.Db.Execute("UPDATE active_storage_blobs SET content_type='text/plain' WHERE id=@id",new{id=doc.RootElement.GetProperty("id").GetInt64()});
host.App.Configuration["CAMPFIRE_PDFTOPPM_COMMAND"]=Path.Combine(host.Root,"missing-poppler");
host.App.Configuration["CAMPFIRE_MUTOOL_COMMAND"]=Path.Combine(host.Root,"missing-mupdf");
host.Db.Execute("UPDATE active_storage_blobs SET content_type='application/pdf' WHERE id=@id",new{id=doc.RootElement.GetProperty("id").GetInt64()});
FeatureHost.Check(!host.Media.AttachmentHtml(1).Contains("/representations/",StringComparison.Ordinal),"PDF rendered preview without an available original previewer");
await host.Media.ProcessMessageAttachmentAsync(1);
FeatureHost.Check(host.Db.Scalar<long>("SELECT count(*) FROM active_storage_attachments WHERE record_type='ActiveStorage::Blob' AND record_id=@id AND name='preview_image'",new{id=doc.RootElement.GetProperty("id").GetInt64()})==0,"Unavailable PDF tools created a preview record");
host.Db.Execute("UPDATE active_storage_blobs SET content_type='text/plain' WHERE id=@id",new{id=doc.RootElement.GetProperty("id").GetInt64()});
host.Media.PurgeAttachment("Message", 1, "attachment");
FeatureHost.Check(host.Db.Scalar<long>("SELECT count(*) FROM active_storage_blobs WHERE id=@id", new { id }) == 0, "Unreferenced blob purge failed");
using var logo = await client.GetAsync("/account/logo?size=small");
FeatureHost.Check(logo.IsSuccessStatusCode && logo.Content.Headers.ContentType?.MediaType == "image/png", $"Manifest stock logo missing: {(int)logo.StatusCode}, {logo.Content.Headers.ContentType}, root {host.App.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath}");
using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rails-storage.json")));
var rails = new RailsCrypto(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SECRET_KEY_BASE"] = golden.RootElement.GetProperty("secret_key_base").GetString() }).Build());
FeatureHost.Check(rails.SignStorage("42", "blob_id") == golden.RootElement.GetProperty("blob_42").GetString(), "Rails ActiveStorage signature mismatch");
using var variation = JsonDocument.Parse("{\"format\":\"webp\",\"resize_to_limit\":[512,512]}");
FeatureHost.Check(RubyVariation.Digest(variation.RootElement) == "3xm5qtUwCk3YQHQ55FXsVFKT908=", "Decoded Ruby variant digest mismatch");
FeatureHost.Check(RubyVariation.Digest(variation.RootElement, true) == "6gwfjNKv9eUy9jNUtEZvQFLU0hQ=", "Symbol Ruby variant digest mismatch");
using(var mimeGolden=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","marcel-pinned.json"))))
foreach(var mimeCase in mimeGolden.RootElement.EnumerateArray())
{
    var path=Path.Combine(host.Root,"mime-case.bin");File.WriteAllBytes(path,Convert.FromBase64String(mimeCase.GetProperty("bytes").GetString()!));
    FeatureHost.Check(MarcelMime.Identify(path,mimeCase.GetProperty("name").GetString(),mimeCase.GetProperty("declared").GetString())==mimeCase.GetProperty("expected").GetString(),"Pinned Marcel MIME mismatch: "+mimeCase.GetProperty("label").GetString());File.Delete(path);
}
Console.WriteLine("PASS storage: allocation auth/CSRF, checksums, disk/proxy bytes, ranges, signature purpose, escaping, purge, assets, pinned Rails signature/digests");

// Owner and attachment rows participate in one transaction. A metadata INSERT
// fault must preserve the existing owner, attachment and bytes and clean staging.
using var avatarBytes=new MemoryStream(Encoding.UTF8.GetBytes("first avatar"));
var avatarFile=new FormFile(avatarBytes,0,avatarBytes.Length,"avatar","old.txt"){Headers=new HeaderDictionary(),ContentType="text/plain"};
await host.Media.SaveRecordUploadAsync("User",1,"avatar",avatarFile);
var oldAvatar=host.Media.Attached("User",1,"avatar")!;
host.Db.Execute("CREATE TRIGGER fail_avatar_blob BEFORE INSERT ON active_storage_blobs BEGIN SELECT RAISE(ABORT,'injected attachment metadata failure');END");
using var newBytes=new MemoryStream(Encoding.UTF8.GetBytes("replacement"));
var replacement=new FormFile(newBytes,0,newBytes.Length,"avatar","new.txt"){Headers=new HeaderDictionary(),ContentType="text/plain"};
try
{
    await host.Db.WriteAsync(async(connection,transaction)=>{using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="UPDATE users SET name='must rollback' WHERE id=1";command.ExecuteNonQuery();await using var prepared=await host.Media.SaveRecordUploadInTransactionAsync(connection,transaction,"User",1,"avatar",replacement);return 0;});
    throw new Exception("Metadata fault was ignored");
}
catch(Microsoft.Data.Sqlite.SqliteException){ }
FeatureHost.Check(host.Db.Scalar<string>("SELECT name FROM users WHERE id=1")=="Alice"&&host.Media.Attached("User",1,"avatar")!.Id==oldAvatar.Id&&File.Exists(host.Media.DiskPath(oldAvatar.Key)),"Attachment metadata fault must roll back owner and retain old bytes");
FeatureHost.Check(!Directory.EnumerateFiles(host.Root,"*.pending",SearchOption.AllDirectories).Any(),"Rolled-back upload left staging bytes");
host.Db.Execute("DROP TRIGGER fail_avatar_blob");
var preparedSuccess=await host.Db.WriteAsync((connection,transaction)=>host.Media.SaveRecordUploadInTransactionAsync(connection,transaction,"User",2,"avatar",replacement));
await using(preparedSuccess){var preparedBlob=host.Media.Attached("User",2,"avatar")!;FeatureHost.Check(!File.Exists(host.Media.DiskPath(preparedBlob.Key)),"Disk upload must run after owner commit");await preparedSuccess.CompleteAsync();FeatureHost.Check(File.Exists(host.Media.DiskPath(preparedBlob.Key)),"After-commit bytes were not uploaded");}
var signedAttach=await host.Db.WriteAsync((connection,transaction)=>host.Media.SaveRecordUploadInTransactionAsync(connection,transaction,"User",2,"avatar",host.Crypto.SignStorage(oldAvatar.Id.ToString(),"blob_id")));
await using(signedAttach){await signedAttach.CompleteAsync();}
FeatureHost.Check(host.Media.Attached("User",2,"avatar")!.Id==oldAvatar.Id,"Signed attachment ID did not attach existing blob");
var clear=await host.Db.WriteAsync((connection,transaction)=>host.Media.SaveRecordUploadInTransactionAsync(connection,transaction,"User",2,"avatar",""));await using(clear){await clear.CompleteAsync();}
FeatureHost.Check(host.Media.Attached("User",2,"avatar") is null&&File.Exists(host.Media.DiskPath(oldAvatar.Key)),"Clearing shared attachment purged another owner's bytes");
host.Db.Execute("DELETE FROM active_storage_attachments WHERE record_type='User' AND record_id=1;CREATE TRIGGER fail_purge BEFORE DELETE ON active_storage_blobs BEGIN SELECT RAISE(ABORT,'injected purge failure');END");
try{await host.Media.PurgeBlobsAsync([oldAvatar.Id]);throw new Exception("Purge fault ignored");}catch(Microsoft.Data.Sqlite.SqliteException){ }
FeatureHost.Check(File.Exists(host.Media.DiskPath(oldAvatar.Key))&&host.Db.Scalar<long>("SELECT count(*) FROM active_storage_blobs WHERE id=@id",new{id=oldAvatar.Id})==1,"Purge transaction failure did not restore bytes and metadata");
host.Db.Execute("DROP TRIGGER fail_purge");await host.Media.PurgeBlobsAsync([oldAvatar.Id]);
FeatureHost.Check(!File.Exists(host.Media.DiskPath(oldAvatar.Key)),"Retried purge did not delete bytes");
Console.WriteLine("PASS storage transaction faults: owner/metadata rollback, staging cleanup, after-commit bytes, signed/empty attachment, purge rollback/retry");
