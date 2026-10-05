using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Campfire.SystemTests;

public sealed class PortFlowTests
{
    [Theory]
    [InlineData("exact", 304)]
    [InlineData("wildcard", 200)]
    [InlineData("list", 200)]
    [InlineData("strong", 200)]
    [InlineData("space", 200)]
    public async Task ConditionalGetUsesPinnedRackLiteralValidator(string variant, int expected)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("<p>validator body</p>");
        var etag = "W/\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..32] + "\"";
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/rooms/1";
        context.Request.Headers.IfNoneMatch = variant switch
        {
            "wildcard" => "*", "list" => etag + ", \"other\"",
            "strong" => etag[2..], "space" => " " + etag, _ => etag
        };
        using var output = new MemoryStream();
        context.Response.Body = output;
        await Campfire.Features.WebSupport.RailsHttpCompatibility.NormalizeRequest(context,
            new HtmlSegmentsResult(new ReadOnlyMemory<byte>[] { bytes }).ExecuteAsync);
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Equal(expected == 304 ? [] : bytes, output.ToArray());
    }

    [Theory]
    [InlineData("gzip", true)]
    [InlineData("GZIP", true)]
    [InlineData("gzip;q=0", false)]
    [InlineData("identity;q=1,gzip;q=0.5", false)]
    [InlineData("*;q=1", true)]
    [InlineData("gzip;q=invalid", false)]
    public async Task FragmentCompressionPreservesNegotiationDecodedBytesAndWholeBodyValidator(string acceptEncoding, bool compressed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResponseCompression(options => options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>());
        services.AddSingleton<Campfire.Features.WebSupport.FragmentGzipEncoder>();
        using var provider = services.BuildServiceProvider();
        var fragment = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<p>ž🔥 preserved HTML</p>", 2000)));
        var token = System.Text.Encoding.UTF8.GetBytes("<input value='fresh-request-token'>");
        HtmlSegment[] segments = [new(fragment.AsMemory(0, 18001), true), new(token), new(fragment.AsMemory(18001), true), new(token)];
        var bytes = segments.SelectMany(segment => segment.Bytes.ToArray()).ToArray();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "GET";
        context.Request.Path = "/rooms/1";
        context.Request.Headers.AcceptEncoding = acceptEncoding;
        using var output = new MemoryStream();
        context.Response.Body = output;
        await Campfire.Features.WebSupport.RailsHttpCompatibility.NormalizeRequest(context, new HtmlSegmentsResult(segments).ExecuteAsync);
        Assert.Equal(compressed ? "gzip" : "", context.Response.Headers.ContentEncoding.ToString());
        var actual = output.ToArray();
        if (compressed)
        {
            Assert.Equal(actual.Length, context.Response.ContentLength);
            using var input = new MemoryStream(actual);
            using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            await gzip.CopyToAsync(decoded);
            actual = decoded.ToArray();
            Assert.Contains("Accept-Encoding", context.Response.Headers.Vary.ToString());
        }
        Assert.Equal(bytes, actual);
        var digest = "W/\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..32] + "\"";
        Assert.Equal(digest, context.Response.Headers.ETag.ToString());

        var conditional = new DefaultHttpContext { RequestServices = provider };
        conditional.Request.Method = "GET";
        conditional.Request.Path = "/rooms/1";
        conditional.Request.Headers.AcceptEncoding = acceptEncoding;
        conditional.Request.Headers.IfNoneMatch = digest;
        using var empty = new MemoryStream();
        conditional.Response.Body = empty;
        await Campfire.Features.WebSupport.RailsHttpCompatibility.NormalizeRequest(conditional, new HtmlSegmentsResult(segments).ExecuteAsync);
        Assert.Equal(304, conditional.Response.StatusCode);
        Assert.Empty(empty.ToArray());
        Assert.Empty(conditional.Response.Headers.ContentEncoding.ToString());
        var head = new DefaultHttpContext { RequestServices = provider };
        head.Request.Method = "HEAD";
        head.Request.Path = "/rooms/1";
        head.Request.Headers.AcceptEncoding = acceptEncoding;
        using var headBody = new MemoryStream();
        head.Response.Body = headBody;
        await Campfire.Features.WebSupport.RailsHttpCompatibility.NormalizeRequest(head, new HtmlSegmentsResult(segments).ExecuteAsync);
        Assert.Empty(headBody.ToArray());
        Assert.Empty(head.Response.Headers.ContentEncoding.ToString());
        Assert.Equal(digest, head.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task FragmentDigestMatchesWholeBodyAcrossManySmallSlices()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<form>ž🔥</form>", 10000)));
        var segments = new List<HtmlSegment>();
        for (var offset = 0; offset < bytes.Length; offset += 83)
            segments.Add(new(bytes.AsMemory(offset, Math.Min(83, bytes.Length - offset)), true));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/rooms/1";
        using var body = new MemoryStream();
        context.Response.Body = body;
        await Campfire.Features.WebSupport.RailsHttpCompatibility.NormalizeRequest(context, new HtmlSegmentsResult(segments).ExecuteAsync);
        Assert.Equal(bytes, body.ToArray());
        Assert.Equal("W/\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..32] + "\"", context.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task SegmentedHtmlPreservesWireDigestConditionalRequestsAndHead()
    {
        var text = "<p>" + new string('x', 40000) + "ž🔥</p>";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        // Split inside a multibyte character: segments are bytes, not independently decoded strings.
        ReadOnlyMemory<byte>[] segments = [bytes.AsMemory(0, 40004), bytes.AsMemory(40004)];
        var digest = "W/\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..32] + "\"";
        foreach (var method in new[] { "GET", "HEAD" })
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = "/rooms/1";
            using var body = new MemoryStream();
            context.Response.Body = body;
            await Campfire.Features.WebSupport.RailsHttpCompatibility.NormalizeRequest(context, new HtmlSegmentsResult(segments).ExecuteAsync);
            Assert.Equal(digest, context.Response.Headers.ETag.ToString());
            Assert.Equal(method == "HEAD" ? Array.Empty<byte>() : bytes, body.ToArray());
            Assert.Equal(method, context.Request.Method);
        }
        var conditional = new DefaultHttpContext();
        conditional.Request.Method = "GET";
        conditional.Request.Path = "/rooms/1";
        conditional.Request.Headers.IfNoneMatch = digest;
        using var empty = new MemoryStream();
        conditional.Response.Body = empty;
        await Campfire.Features.WebSupport.RailsHttpCompatibility.NormalizeRequest(conditional, new HtmlSegmentsResult(segments).ExecuteAsync);
        Assert.Equal(304, conditional.Response.StatusCode);
        Assert.Empty(empty.ToArray());
        Assert.Null(conditional.Response.ContentLength);
    }

    [Fact]
    public async Task RailsRemoteAddressFiltersProxyChainsAndRejectsConflictingHeaders()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.1");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.4, 198.51.100.9:5000, 10.0.0.8";
        await RailsRemoteIp.Capture(context, captured =>
        {
            captured.Request.Headers.Remove("X-Forwarded-For");
            Assert.Equal("198.51.100.9", RailsRemoteIp.Address(captured));
            return Task.CompletedTask;
        });
        var conflicting = new DefaultHttpContext();
        conflicting.Request.Headers["X-Forwarded-For"] = "203.0.113.4";
        conflicting.Request.Headers["Client-Ip"] = "198.51.100.9";
        Assert.Throws<InvalidOperationException>(() => RailsRemoteIp.Address(conflicting));
        var standard = new DefaultHttpContext();
        standard.Request.Headers["Forwarded"] = "for=\"[2001:db8:0:0:0:0:0:1]:443\";proto=https, for=10.0.0.8";
        standard.Request.Headers["X-Forwarded-For"] = "198.51.100.9";
        Assert.Equal("2001:db8:0:0:0:0:0:1", RailsRemoteIp.Address(standard));
    }
    [Fact]
    public async Task FirstRunCreatesSessionAndChatUsesRealDatabaseAndCsrf()
    {
        await using var application = new PortApplication();
        using var client = application.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var signUpPage = await client.GetStringAsync("/first_run");
        var forbidden = await client.PostAsync("/first_run", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user[name]"] = "Port Administrator", ["user[email_address]"] = "port@example.test", ["user[password]"] = "test-password"
        }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forbidden.StatusCode);
        var creation = await client.PostAsync("/first_run", Form(Token(signUpPage), new()
        {
            ["user[name]"] = "Port Administrator", ["user[email_address]"] = "port@example.test", ["user[password]"] = "test-password"
        }));
        Assert.Equal(HttpStatusCode.Redirect, creation.StatusCode);
        var profileJson = await client.GetAsync("/users/me/profile.json");
        Assert.Equal(HttpStatusCode.NotAcceptable, profileJson.StatusCode);
        Assert.Equal("application/json", profileJson.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"status\":406,\"error\":\"Not Acceptable\"}", await profileJson.Content.ReadAsStringAsync());
        var profileHead = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/users/me/profile.json"));
        Assert.Equal(HttpStatusCode.NotAcceptable, profileHead.StatusCode);
        Assert.Equal("application/json", profileHead.Content.Headers.ContentType?.MediaType);
        Assert.Equal("", await profileHead.Content.ReadAsStringAsync());
        var database = application.Services.GetRequiredService<IDataStore>();
        var room = database.Scalar<long>("SELECT id FROM rooms LIMIT 1");
        var html = await client.GetStringAsync($"/rooms/{room}");
        Assert.Contains("message-area", html);
        Assert.Contains("lexxy-editor", html);
        var token = Token(html);
        using var messageRequest = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{room}/messages") { Content = Form(token, new()
        {
            ["message[body]"] = "<p>Port integration needle " + new string('x', 40000) + "</p>", ["message[client_message_id]"] = "port-flow-message"
        }) };
        messageRequest.Headers.Accept.ParseAdd("text/vnd.turbo-stream.html");
        var response = await client.SendAsync(messageRequest);
        Assert.True(response.IsSuccessStatusCode, $"Message creation failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var message = database.Scalar<long>("SELECT id FROM messages WHERE client_message_id=@client", new { client = "port-flow-message" });
        Assert.True(message > 0);
        Assert.Equal(1, database.Scalar<long>("SELECT count(*) FROM message_search_index WHERE message_search_index MATCH 'needle' AND rowid=@message", new { message }));
        Assert.Contains("Port integration needle", await client.GetStringAsync($"/rooms/{room}/messages"));
        Assert.Contains("Port integration needle", await client.GetStringAsync("/searches?q=needle"));
        var largeRoom = await client.GetAsync($"/rooms/{room}");
        var roomBytes = await largeRoom.Content.ReadAsByteArrayAsync();
        Assert.True(roomBytes.Length > 32768);
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(roomBytes))[..32];
        Assert.Equal("W/\"" + digest + "\"", largeRoom.Headers.ETag?.ToString());

        // Revoking a persisted session/user must affect the next request, even with a valid signed cookie.
        database.Execute("DELETE FROM sessions WHERE user_id=(SELECT id FROM users WHERE email_address=@email)", new { email = "port@example.test" });
        var revoked = await client.GetAsync($"/rooms/{room}");
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        Assert.Equal("/session/new", revoked.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task PwaManifestEscapesAccountNameAndQrRejectsMalformedInput()
    {
        await using var application = new PortApplication();
        using var client = application.CreateClient(new() { AllowAutoRedirect = false });
        var database = application.Services.GetRequiredService<IDataStore>();
        var name = "Campfire \"quoted\" <tag>";
        database.Execute("INSERT INTO accounts(name,join_code,settings,created_at,updated_at) VALUES(@name,'test-join-code','{}',@now,@now)", new { name, now = RequestUser.Timestamp() });
        var manifest = await client.GetAsync("/webmanifest.json");
        Assert.Equal("application/json", manifest.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        Assert.Equal(WebUtility.HtmlEncode(name), json.RootElement.GetProperty("name").GetString());
        Assert.StartsWith("http://localhost/assets/", json.RootElement.GetProperty("shortcuts")[0].GetProperty("icons")[0].GetProperty("src").GetString());
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/qr_code/invalid!")).StatusCode);
        var qr = await client.GetAsync("/qr_code/aHR0cHM6Ly9leGFtcGxlLnRlc3Qv");
        Assert.Equal("image/svg+xml", qr.Content.Headers.ContentType?.MediaType);
        Assert.Contains("viewBox", await qr.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RailsHeadFormatsBrowserGateAndHealthConditionalRequest()
    {
        await using var application = new PortApplication();
        using var client = application.CreateClient(new() { AllowAutoRedirect = false });
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/session/new.html"));
        Assert.Equal(HttpStatusCode.Found, head.StatusCode); // Rails redirects a fresh installation to first_run.
        Assert.Equal("", await head.Content.ReadAsStringAsync());
        Assert.Equal("SAMEORIGIN", head.Headers.GetValues("X-Frame-Options").Single());
        var oldBrowser = new HttpRequestMessage(HttpMethod.Get, "/session/new");
        oldBrowser.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 Firefox/114.0");
        Assert.Contains("Upgrade to a supported web browser", await (await client.SendAsync(oldBrowser)).Content.ReadAsStringAsync());
        var manifest = new HttpRequestMessage(HttpMethod.Get, "/webmanifest");
        manifest.Headers.Accept.ParseAdd("text/html");
        Assert.Equal(HttpStatusCode.NotAcceptable, (await client.SendAsync(manifest)).StatusCode);
        var serviceWorker = new HttpRequestMessage(HttpMethod.Get, "/service-worker.js");
        Assert.Equal("text/javascript", (await client.SendAsync(serviceWorker)).Content.Headers.ContentType?.MediaType);
        var health = await client.GetAsync("/up");
        var conditional = new HttpRequestMessage(HttpMethod.Get, "/up");
        conditional.Headers.IfNoneMatch.Add(health.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/not-a-route")).StatusCode);
        var missingJsonPath = await client.GetAsync("/not-a-route.json");
        Assert.Equal(HttpStatusCode.NotFound, missingJsonPath.StatusCode);
        Assert.Equal("text/html", missingJsonPath.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/not-a-route", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/session/new"))).StatusCode);
    }

    private static string Token(string html)
    {
        var match = Regex.Match(html, "<meta name=\"csrf-token\" content=\"([^\"]+)\"");
        Assert.True(match.Success, "Page has no CSRF meta token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    private static FormUrlEncodedContent Form(string token, Dictionary<string, string> values)
    {
        values["authenticity_token"] = token;
        return new(values);
    }

    private sealed class PortApplication : WebApplicationFactory<Program>
    {
        private readonly string storage = Path.Combine(Path.GetTempPath(), "campfire-port-flow-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CAMPFIRE_STORAGE"] = storage,
                ["SECRET_KEY_BASE"] = new string('a', 128),
                ["CAMPFIRE_DELIVER_INTEGRATIONS"] = "false"
            }));
        }
        public override async ValueTask DisposeAsync()
        {
            var database = Services.GetRequiredService<IDataStore>();
            await base.DisposeAsync();
            // app.Run and WebApplicationFactory can dispose the host concurrently.
            // DI's second disposal returns while the first is still unwinding;
            // finish disposal of the fixture-owned store before deleting its files.
            ((IDisposable)database).Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
        }
    }
}
