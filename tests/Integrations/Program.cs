using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Campfire.Contracts;
using Campfire.FeatureTests;
using Campfire.Features.Integrations;

FeatureHost.Check(!JsonSerializer.IsReflectionEnabledByDefault, "Integrations suite must disable JSON reflection");
FeatureHost.Check(OutboundPolicy.PushUri("https://fcm.googleapis.com/send/x", out _), "Known push vendor rejected");
FeatureHost.Check(OutboundPolicy.PushUri("https://sub.notify.windows.com/x", out _), "Push subdomain rejected");
foreach (var endpoint in new[] { "http://fcm.googleapis.com/x", "https://fcm.googleapis.com:444/x", "https://fcm.googleapis.com.evil.test/x", "https://127.0.0.1/x", "https://fcm.googleapis.com@evil.test/x" })
    FeatureHost.Check(!OutboundPolicy.PushUri(endpoint, out _), "Unsafe push endpoint accepted: " + endpoint);
foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "172.16.1.2", "192.168.0.1", "169.254.169.254", "100.64.0.1", "::1", "fc00::1", "fe80::1", "::ffff:10.0.0.1", "2001:db8::1", "2002:a00:1::1" })
    FeatureHost.Check(!OutboundPolicy.PublicAddress(IPAddress.Parse(address)), "Private/reserved push address accepted: " + address);
FeatureHost.Check(OutboundPolicy.PublicAddress(IPAddress.Parse("8.8.8.8")) && OutboundPolicy.PublicAddress(IPAddress.Parse("2606:4700:4700::1111")), "Public IP rejected");

using var receiver = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
var q = receiver.ExportParameters(false).Q;
var clientPoint = new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray();
var auth = RandomNumberGenerator.GetBytes(16);
var payload = "{\"title\":\"Actual push ☃\",\"options\":{\"body\":\"message\"}}";
var encrypted = WebPushEncoding.Encrypt(payload, WebPushEncoding.Encode(clientPoint), WebPushEncoding.Encode(auth));
FeatureHost.Check(BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(16, 4)) == encrypted.Length - 86 && encrypted[20] == 65, "aes128gcm header mismatch");
var serverPoint = encrypted[21..86];
using var sender = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = serverPoint[1..33], Y = serverPoint[33..] } });
var shared = receiver.DeriveRawSecretAgreement(sender.PublicKey);
var secret = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, auth, Encoding.ASCII.GetBytes("WebPush: info\0").Concat(clientPoint).Concat(serverPoint).ToArray());
var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 16, encrypted[..16], Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 12, encrypted[..16], Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
var plain = new byte[encrypted.Length - 102];
using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, encrypted.AsSpan(86, plain.Length), encrypted.AsSpan(86 + plain.Length), plain);
FeatureHost.Check(plain[^2] == 2 && plain[^1] == 0 && Encoding.UTF8.GetString(plain[..^2]) == payload, "Push payload cannot be decrypted by receiver");
FeatureHost.Check(WebPushEncoding.Encrypt(new string('x',4078),WebPushEncoding.Encode(clientPoint),WebPushEncoding.Encode(auth)).Length==4182,"Original maximum push record rejected");
try { WebPushEncoding.Encrypt(new string('x',4079),WebPushEncoding.Encode(clientPoint),WebPushEncoding.Encode(auth)); throw new Exception("Oversized push record accepted"); } catch(ArgumentException) { }
using var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var vp = vapid.ExportParameters(true);
var pub = new byte[] { 4 }.Concat(vp.Q.X!).Concat(vp.Q.Y!).ToArray();
var authorization = WebPushEncoding.Authorization(new Uri("https://fcm.googleapis.com/x"), WebPushEncoding.Encode(vp.D!), WebPushEncoding.Encode(pub), "mailto:test@example.invalid");
var jwt = authorization[8..].Split(",k=")[0].Split('.');
FeatureHost.Check(vapid.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), WebPushEncoding.Decode(jwt[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "VAPID signature invalid");
using (var claims = JsonDocument.Parse(WebPushEncoding.Decode(jwt[1]))) FeatureHost.Check(claims.RootElement.GetProperty("aud").GetString() == "https://fcm.googleapis.com", "VAPID audience mismatch");

var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var botServer = builder.Build();
var calls = 0; JsonElement? captured = null;
botServer.MapPost("/hook", async (HttpContext context) =>
{
    using var doc = await JsonDocument.ParseAsync(context.Request.Body); captured = doc.RootElement.Clone(); Interlocked.Increment(ref calls);
    return Results.Text("<strong>local bot answer</strong><script>alert('unsafe')</script>", "text/html");
});
await botServer.StartAsync();
var localUrl = botServer.Urls.Single() + "/hook";
await using (var disabled = await FeatureHost.Start())
{
    disabled.Db.Execute("INSERT INTO memberships(user_id,room_id,involvement,created_at,updated_at) VALUES(3,1,'everything',@now,@now);INSERT INTO webhooks(user_id,url,created_at,updated_at) VALUES(3,@localUrl,@now,@now);UPDATE rooms SET type='Rooms::Direct' WHERE id=1", new { localUrl, now = RequestUser.Timestamp() });
    await disabled.App.Services.GetRequiredService<IIntegrationEvents>().MessageCreatedAsync(1);
    await Task.Delay(100);
    FeatureHost.Check(calls == 0, "Fixture delivery must require explicit opt-in");
}
await using (var enabled = await FeatureHost.Start(true))
{
    enabled.Db.Execute("INSERT INTO memberships(user_id,room_id,involvement,created_at,updated_at) VALUES(3,1,'everything',@now,@now);INSERT INTO webhooks(user_id,url,created_at,updated_at) VALUES(3,@localUrl,@now,@now);UPDATE rooms SET type='Rooms::Direct' WHERE id=1;UPDATE users SET bot_token='local-token' WHERE id=3", new { localUrl, now = RequestUser.Timestamp() });
    await enabled.App.Services.GetRequiredService<IIntegrationEvents>().MessageCreatedAsync(1);
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (enabled.Db.Scalar<long>("SELECT count(*) FROM messages WHERE creator_id=3") == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
    FeatureHost.Check(calls == 1, "Direct room webhook did not reach local server");
    FeatureHost.Check(captured?.GetProperty("room").GetProperty("path").GetString() == "/rooms/1/3-local-token/messages", "Bot payload API path mismatch");
    var reply = enabled.Db.Scalar<long>("SELECT id FROM messages WHERE creator_id=3 LIMIT 1");
    FeatureHost.Check(reply > 0 && enabled.Db.Scalar<string>("SELECT body FROM action_text_rich_texts WHERE record_id=@reply", new { reply }).Contains("<strong>local bot answer</strong>") && !enabled.Db.Scalar<string>("SELECT body FROM action_text_rich_texts WHERE record_id=@reply", new { reply }).Contains("<script>"), "Bot HTML must sanitize and persist");
    FeatureHost.Check(enabled.Db.Scalar<long>("SELECT count(*) FROM message_search_index WHERE rowid=@reply AND body MATCH 'answer'", new { reply }) == 1, "Bot reply missing search side effect");
    FeatureHost.Check(enabled.Db.Scalar<string?>("SELECT unread_at FROM memberships WHERE user_id=1 AND room_id=1") is not null, "Bot reply missing unread side effect");
    enabled.Client.DefaultRequestHeaders.Add("Cookie", "test_user=1"); enabled.Client.DefaultRequestHeaders.Add("X-CSRF-Token", "test-csrf");
    using var own = await enabled.Client.DeleteAsync("/users/me/push_subscriptions/99999"); FeatureHost.Check(own.StatusCode == HttpStatusCode.Found, "Push deletion should redirect");
}
await botServer.StopAsync();
var fakeDocuments = new TestDocuments();
var graph = new OpenGraphService(fakeDocuments);
var metadata = await graph.FromUrl("https://example.test/page", CancellationToken.None);
FeatureHost.Check(metadata is { Title: "Safe title", Description: "Description", Url: "https://canonical.test/page", Image: "https://image.test/image.png" }, "OpenGraph metadata contract/sanitization mismatch");
fakeDocuments.SvgImage = true;
FeatureHost.Check((await graph.FromUrl("https://example.test/page", CancellationToken.None))?.Image is null, "OpenGraph must reject SVG preview image");
await graph.FromUrl("https://twitter.com/alice/status/1", CancellationToken.None);
FeatureHost.Check(fakeDocuments.LastDocument?.Host == "fxtwitter.com", "Twitter unfurl rewrite missing");
FeatureHost.Check(await graph.FromUrl("https://example.test/file.mp4", CancellationToken.None) is null, "Media URL must not fetch HTML");
var publicClient = new PublicDocumentClient();
FeatureHost.Check(await publicClient.FetchAsync(new Uri(localUrl), false, CancellationToken.None) is null, "Real unfurl transport must deny loopback bot endpoints");
FeatureHost.Check(OpenGraphService.Parse("<meta property='og:title' content='Snow ☃'>")["title"]=="Snow ","Pinned Nokogiri binary/no-meta content rule changed");
FeatureHost.Check(OpenGraphService.Parse("<meta charset='UTF-8'><meta property='og:title' content='Snow ☃'>")["title"]=="Snow ☃","Declared HTML meta encoding discarded Unicode");
FeatureHost.Check(UserAgentLabel.Format("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36")=="Chrome 132.0.0.0 on Windows","Subscription browser metadata missing Chrome/Windows");
FeatureHost.Check(UserAgentLabel.Format("Mozilla/5.0 (iPhone; CPU iPhone OS 18_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.2 Mobile/15E148 Safari/604.1")=="Safari 18.2 on iPhone","Subscription browser metadata missing Safari/iPhone");
Console.WriteLine("PASS integrations: push vendor/address guards, aes128gcm receiver decrypt, VAPID signature/audience, opt-in delivery, local bot payload and sanitized message/FTS/unread side effects");

sealed class TestDocuments : IPublicDocumentClient
{
    public bool SvgImage { get; set; }
    public Uri? LastDocument { get; private set; }
    public Task<bool> PublicAsync(Uri uri, CancellationToken cancellationToken) => Task.FromResult(uri.Host is "canonical.test" or "image.test" or "example.test" or "fxtwitter.com");
    public Task<PublicDocument?> FetchAsync(Uri uri, bool head, CancellationToken cancellationToken)
    {
        if (head) return Task.FromResult<PublicDocument?>(new(null, SvgImage ? "image/svg+xml" : "image/png"));
        LastDocument = uri;
        return Task.FromResult<PublicDocument?>(new("<html><meta property='og:title' content='&lt;img src=x onerror=evil&gt;Safe title'><meta name='og:description' content='&lt;b&gt;Description&lt;/b&gt;'><meta property='og:url' content='https://canonical.test/page'><meta property='og:image' content='https://image.test/image.png'></html>", "text/html"));
    }
}
