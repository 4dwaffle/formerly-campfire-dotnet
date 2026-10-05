using System.Text;
using System.Text.Json.Nodes;
using Campfire.FeatureTests;
using Campfire.Features.Integrations;

// Remediation regressions. Original baseline captures/report remain unchanged.
// Every database/file/socket is disposable; all webhook targets are local.
var evidence = new JsonObject();
await using (var host = await FeatureHost.Start())
{
    host.Db.Execute("CREATE TRIGGER parity_fail_analysis BEFORE UPDATE OF metadata ON active_storage_blobs BEGIN SELECT RAISE(ABORT, 'parity injected analysis failure'); END");
    using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("safe local content"));
    var file = new FormFile(bytes, 0, bytes.Length, "attachment", "parity.txt") { Headers = new HeaderDictionary(), ContentType = "text/plain" };
    Exception? failure = null;
    try { await host.Media.SaveRecordUploadAsync("Message", 1, "attachment", file); }
    catch (Exception error) { failure = error; }
    var attached = host.Media.Attached("Message", 1, "attachment");
    FeatureHost.Check(failure is not null && attached is not null, "Failure repro must leave committed attachment");
    FeatureHost.Check(File.Exists(host.Media.DiskPath(attached!.Key)), "Committed attachment file must survive analysis failure");
    evidence["upload_postcommit_analysis_failure"] = new JsonObject { ["exception"] = failure!.Message, ["attachment_committed"] = true, ["file_exists"] = true };
}

var documents = new Documents();
var graph = new OpenGraphService(documents);
var canonical = await graph.FromUrl("https://www.example.com", CancellationToken.None);
FeatureHost.Check(canonical?.Url == "https://www.example.com", "Canonical fallback must preserve original URL");
evidence["opengraph_fallback_url"] = canonical!.Url;
documents.Calls = 0;
var uppercase = await graph.FromUrl("https://www.example.com/IMAGE.JPG", CancellationToken.None);
FeatureHost.Check(uppercase is not null && documents.Calls == 1, "Media exclusion must preserve Rails case-sensitive regex");
evidence["opengraph_uppercase_media_fetches"] = documents.Calls;
documents.Calls = 0;
var query = await graph.FromUrl("https://www.example.com/page?download=photo.jpg", CancellationToken.None);
FeatureHost.Check(query is null && documents.Calls == 0, "Media exclusion must inspect whole URL including query");
evidence["opengraph_media_query_fetches"] = documents.Calls;

var botBuilder = WebApplication.CreateBuilder();
botBuilder.Logging.ClearProviders();
botBuilder.WebHost.UseUrls("http://127.0.0.1:0");
await using var botServer = botBuilder.Build();
var captured = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
botServer.MapPost("/parity-local-hook", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    captured.TrySetResult(await reader.ReadToEndAsync());
    return Results.Content("{\"local\":true}", "application/json");
});
await botServer.StartAsync();
await using (var host = await FeatureHost.Start(true))
{
    host.Db.Execute("INSERT INTO rooms(id,type,creator_id,created_at,updated_at) VALUES(3,'Rooms::Direct',1,@now,@now); INSERT INTO memberships(user_id,room_id,involvement,created_at,updated_at) VALUES(1,3,'everything',@now,@now),(3,3,'everything',@now,@now); INSERT INTO webhooks(user_id,url,created_at,updated_at) VALUES(3,@url,@now,@now); UPDATE users SET bot_token='local-token' WHERE id=3; INSERT INTO messages(id,room_id,creator_id,client_message_id,created_at,updated_at) VALUES(2,3,1,'parity',@now,@now); INSERT INTO action_text_rich_texts(record_type,record_id,name,body,created_at,updated_at) VALUES('Message',2,'body','',@now,@now)", new { now = Campfire.Contracts.RequestUser.Timestamp(), url = botServer.Urls.Single() + "/parity-local-hook" });
    using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("local attachment"));
    await host.Media.SaveUploadAsync(2, new FormFile(bytes, 0, bytes.Length, "attachment", "expected-filename.txt") { Headers = new HeaderDictionary(), ContentType = "text/plain" });
    var original = host.Db.Scalar<long>("SELECT count(*) FROM messages WHERE room_id=3");
    await host.App.Services.GetRequiredService<Campfire.Contracts.IIntegrationEvents>().MessageCreatedAsync(2);
    var payload = JsonNode.Parse(await captured.Task.WaitAsync(TimeSpan.FromSeconds(5)))!;
    FeatureHost.Check(payload["message"]!["body"]!["plain"]!.GetValue<string>() == "expected-filename.txt", "Attachment webhook payload must use filename fallback");
    var deadline=DateTime.UtcNow.AddSeconds(5);
    while(host.Db.Scalar<long>("SELECT count(*) FROM messages WHERE room_id=3")==original&&DateTime.UtcNow<deadline)await Task.Delay(20);
    var after = host.Db.Scalar<long>("SELECT count(*) FROM messages WHERE room_id=3");
    FeatureHost.Check(after == original+1, "application/json webhook reply must create an attachment message");
    FeatureHost.Check(host.Db.Scalar<string>("SELECT b.filename FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id=b.id JOIN messages m ON m.id=a.record_id WHERE a.record_type='Message' AND m.creator_id=3")=="attachment.json","JSON response must use original MIME filename");
    evidence["webhook_attachment_payload_plain"] = "expected-filename.txt";
    evidence["webhook_json_reply_messages_created"] = after - original;
    evidence["webhook_payload"] = payload;
}
await botServer.StopAsync();
Console.WriteLine(evidence.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

sealed class Documents : IPublicDocumentClient
{
    public int Calls;
    public Task<PublicDocument?> FetchAsync(Uri uri, bool head, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<PublicDocument?>(new("<meta property='og:title' content='Title'><meta property='og:description' content='Description'>", "text/html"));
    }
    public Task<bool> PublicAsync(Uri uri, CancellationToken cancellationToken) => Task.FromResult(true);
}
