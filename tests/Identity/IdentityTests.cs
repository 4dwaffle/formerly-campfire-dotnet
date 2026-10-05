using Campfire.Features.Identity;
using Campfire.Features.Persistence;
using Campfire.Contracts;
using Dapper;
using Microsoft.AspNetCore.TestHost;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Identity.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void RequestCsrfDerivationKeepsFreshMasksAndInvalidatesOnSessionChanges()
    {
        using var db = new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(), "campfire-csrf-cache-" + Guid.NewGuid())));
        var auth = new AuthService(db, Crypto());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("campfire.test");
        context.Request.Path = "/rooms/1";
        context.Request.Method = "POST";
        static byte[] Unmask(string value)
        {
            var bytes = RailsCrypto.Decode64(value);
            Assert.Equal(64, bytes.Length);
            return Enumerable.Range(0, 32).Select(i => (byte)(bytes[i] ^ bytes[i + 32])).ToArray();
        }
        var first = auth.CsrfToken(context);
        var second = auth.CsrfToken(context);
        Assert.NotEqual(first, second);
        Assert.Equal(Unmask(first), Unmask(second));
        Assert.True(auth.ValidateCsrf(context, first));
        Assert.True(auth.ValidateCsrf(context, second));
        Assert.Single(context.Response.Headers.SetCookie);

        var session = Assert.IsType<JsonObject>(context.Items["campfire.browser_session"]);
        session["_csrf_token"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var changed = auth.CsrfToken(context);
        Assert.NotEqual(Unmask(first), Unmask(changed));
        Assert.False(auth.ValidateCsrf(context, first));
        Assert.True(auth.ValidateCsrf(context, changed));
        auth.SignOut(context);
        var reset = auth.CsrfToken(context);
        Assert.NotEqual(Unmask(changed), Unmask(reset));
        Assert.False(auth.ValidateCsrf(context, changed));
        Assert.True(auth.ValidateCsrf(context, reset));
        Assert.False(auth.ValidateCsrf(new DefaultHttpContext(), reset));
    }

    [Fact]
    public void GeneratedSqlFactoriesPreserveRailsColumnNamesAndListParameters()
    {
        var db = new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(), "campfire-generated-sql-" + Guid.NewGuid())));
        var now = RequestUser.Timestamp();
        var id = db.Write((connection, transaction) => connection.ExecuteScalar<long>(
            "INSERT INTO users(name,email_address,password_digest,bot_token,role,status,created_at,updated_at) VALUES(@name,@email,@digest,@token,1,0,@now,@now); SELECT last_insert_rowid()",
            new { name = "Generated owner", email = "generated@example.test", digest = "digest", token = "token", now }, transaction));
        const string sql = "SELECT * FROM users WHERE id IN (SELECT value FROM json_each(@ids)) /* generated-factory regression */";
        var row = Assert.Single(db.Read(connection => connection.Query<UserRecord>(sql, new { ids = SqlParameters.Ids([id]) }).Materialize()));
        Assert.Equal("generated@example.test", row.EmailAddress);
        Assert.Equal("digest", row.PasswordDigest);
        Assert.Equal("token", row.BotToken);
        Assert.Equal(now, row.CreatedAt);
        Assert.Equal(now, row.UpdatedAt);
        Assert.True(row.IsAdmin);
        Assert.Empty(db.Read(connection => connection.Query<UserRecord>(sql, new { ids = SqlParameters.Ids([]) }).Materialize()));
        Assert.Equal("[9223372036854775807,-1]", SqlParameters.Ids([long.MaxValue, -1]));
        Assert.DoesNotContain(SqlMapper.GetCachedSQL(), cached => cached.Item2.Contains("generated-factory regression", StringComparison.Ordinal));
        Assert.DoesNotContain(SqlMapper.GetCachedSQL(), cached => cached.Item2 == "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='users'");
    }

    private static JsonNode Vectors => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rails_compat.json")))!;
    private static IConfiguration Config(string storage, string? secret = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CAMPFIRE_STORAGE"] = storage, ["SECRET_KEY_BASE"] = secret ?? Vectors["secret_key_base"]!.GetValue<string>() }).Build();
    private static RailsCrypto Crypto() => new(Config(Path.GetTempPath()));
    [Fact]
    public void SignedCookieJsonPreservesSeparatorsLiteralEscapesAndExistingUnicodeEncoding()
    {
        var crypto = Crypto();
        var cases = new Dictionary<string,string>
        {
            ["plain ASCII"] = "\"plain ASCII\"",
            ["雪🌍"] = "\"雪\\uD83C\\uDF0D\"",
            ["<>&\u2028\u2029"] = "\"\\u003c\\u003e\\u0026\u2028\u2029\"",
            ["\\u2028\\u2029"] = "\"\\\\u2028\\\\u2029\"",
            ["quote\" slash\\ newline\n"] = "\"quote\\\" slash\\\\ newline\\n\""
        };
        foreach (var (value,expectedJson) in cases)
        {
            var signed = crypto.SignCookie("session_token", value);
            Assert.Equal(value, crypto.VerifyCookie("session_token", signed));
            using var envelope = JsonDocument.Parse(RailsCrypto.Decode64(signed.Split("--")[0]));
            var message = envelope.RootElement.GetProperty("_rails").GetProperty("message").GetString()!;
            Assert.Equal(expectedJson, System.Text.Encoding.UTF8.GetString(RailsCrypto.Decode64(message)));
        }
    }
    [Fact]
    public void RailsSignedCookieVectorsAreReadAndGenerated()
    {
        var crypto = Crypto();
        foreach (var vector in Vectors["signed_cookies"]!["generate"]!.AsArray())
        {
            var v = vector!; var expiry = v["expires_at"] == null ? (DateTimeOffset?)null : DateTimeOffset.Parse(v["expires_at"]!.GetValue<string>());
            Assert.Equal(v["raw"]!.GetValue<string>(), crypto.SignCookie(v["name"]!.GetValue<string>(), v["value"]!.GetValue<string>(), expiry));
        }
        foreach (var vector in Vectors["signed_cookies"]!["verify"]!.AsArray())
        {
            var v = vector!;
            Assert.Equal(v["expected"] is JsonValue scalar && scalar.TryGetValue<string>(out var expected) ? expected : null, crypto.VerifyCookie(v["name"]!.GetValue<string>(), v["raw"]!.GetValue<string>(), DateTimeOffset.Parse(v["now"]!.GetValue<string>())));
        }
    }
    [Fact]
    public void RailsEncryptedCookieVectorsPreserveAndValidateSessionData()
    {
        var crypto = Crypto();
        foreach (var vector in Vectors["encrypted_cookies"]!["verify"]!.AsArray())
        {
            var v = vector!;
            var actual = crypto.DecryptCookieValue(v["name"]!.GetValue<string>(), v["raw"]!.GetValue<string>(), DateTimeOffset.Parse(v["now"]!.GetValue<string>()));
            Assert.True(JsonNode.DeepEquals(v["expected"], actual), v["case"]?.ToString());
        }
    }
    [Fact]
    public void RailsStreamsSignedIdsAndStorageUseTheirOwnVerifiers()
    {
        var crypto = Crypto();
        foreach (var vector in Vectors["turbo_stream_names"]!["generate"]!.AsArray()) { var v = vector!; Assert.Equal(v["signed"]!.ToString(), crypto.SignStream(v["stream_name"]!.ToString())); }
        foreach (var vector in Vectors["turbo_stream_names"]!["verify"]!.AsArray()) { var v = vector!; Assert.Equal(v["expected"] is JsonValue scalar && scalar.TryGetValue<string>(out var expected) ? expected : null, crypto.VerifyStream(v["signed"]!.ToString())); }
        foreach (var vector in Vectors["signed_ids"]!["generate"]!.AsArray().Where(v => v!["expires_at"] == null)) { var v = vector!; Assert.Equal(v["signed_id"]!.ToString(), crypto.SignedId(v["model"]!.ToString(), v["id"]!.GetValue<long>(), v["purpose"]?.ToString() ?? "")); }
        foreach (var vector in Vectors["app_verifiers"]!["generate"]!.AsArray().Where(v => v!["name"]!.ToString() == "ActiveStorage")) { var v = vector!; Assert.Equal(v["message"]!.ToString(), crypto.SignStorage(v["data_json"]!.ToString(), v["purpose"]?.ToString() ?? "", v["expires_at"] == null ? null : DateTimeOffset.Parse(v["expires_at"]!.ToString()))); }
        Assert.Null(crypto.VerifyStorage(crypto.SignStorage("42", "blob_key", DateTimeOffset.UtcNow.AddMinutes(-1)), "blob_key"));
        Assert.Equal("42", crypto.VerifyStorage(crypto.SignStorage("42", "blob_key", DateTimeOffset.UtcNow.AddMinutes(1)), "blob_key"));
        var id = crypto.SignedId("User", 9, "avatar"); Assert.Equal(9, crypto.VerifySignedId(id, "User", "avatar")); Assert.Null(crypto.VerifySignedId(id, "User", "transfer")); Assert.Null(crypto.VerifySignedId(id, "Room", "avatar"));
        var sgid = Vectors["sgids"]!["generate"]!.AsArray().First(v => v!["gid"]!.ToString() == "gid://campfire/User/1" && v["purpose"]!.ToString() == "attachable")!;
        Assert.Equal(sgid["sgid"]!.ToString(), crypto.SignedGlobalId("User", 1)); Assert.Equal(1, crypto.VerifySignedGlobalId(sgid["sgid"]!.ToString(), "User")); Assert.Null(crypto.VerifySignedGlobalId(sgid["sgid"]!.ToString(), "Room"));
    }
    [Fact]
    public void RailsMaskedGlobalAndPerFormCsrfTokensRemainValid()
    {
        var crypto = Crypto(); var session = new JsonObject { ["_csrf_token"] = Vectors["csrf"]!["session_token"]!.ToString() };
        var directory = Path.Combine(Path.GetTempPath(), "campfire-identity-" + Guid.NewGuid()); var db = new SqliteDataStore(Config(directory)); var auth = new AuthService(db, crypto);
        foreach (var vector in Vectors["csrf"]!["validity"]!.AsArray())
        {
            var v = vector!; var c = new DefaultHttpContext(); c.Request.Scheme = "http"; c.Request.Host = new HostString("campfire.test"); c.Request.Path = v["path"]!.ToString(); c.Request.Method = v["method"]!.ToString(); c.Request.Headers.Cookie = "_campfire_session=" + Uri.EscapeDataString(crypto.EncryptCookie("_campfire_session", session));
            Assert.True(v["expected"]!.GetValue<bool>() == auth.ValidateCsrf(c, v["token"]?.ToString()), v["case"] + " " + v["path"] + " " + v["method"]);
        }
    }
    [Fact]
    public void ImmediateWriteRollsBackAndForeignKeysAreEnforced()
    {
        var db = new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(), "campfire-db-" + Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => db.Write<int>((c, t) => { c.Execute("INSERT INTO users(name,created_at,updated_at) VALUES(@name,@now,@now)", new { name = "rollback", now = RequestUser.Timestamp() }, t); throw new InvalidOperationException("rollback"); }));
        Assert.Equal(0, db.Scalar<long>("SELECT count(*) FROM users"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Execute("INSERT INTO sessions(user_id,token,created_at,updated_at,last_active_at) VALUES(999,@token,@now,@now,@now)", new { token = "invalid", now = RequestUser.Timestamp() }));
        Assert.Equal("wal", db.Scalar<string>("PRAGMA journal_mode"));
    }
    [Fact]
    public async Task FirstRunLoginPermissionsAndDeactivationHaveRealSideEffects()
    {
        await using var fixture = await Site.Create();
        var admin = fixture.Client;
        var first = await admin.GetAsync("/first_run"); var csrf = await fixture.Token(admin, first);
        var create = await fixture.Send(admin, "POST", "/first_run", csrf, ("user[name]", "<script>Owner</script>"), ("user[email_address]", "owner@example.test"), ("user[password]", "secret123456"));
        Assert.Equal(HttpStatusCode.Found, create.StatusCode); fixture.Cookies(admin, create);
        Assert.Equal(1, fixture.Db.Scalar<long>("SELECT count(*) FROM accounts")); Assert.Equal(1, fixture.Db.Scalar<long>("SELECT count(*) FROM rooms WHERE type='Rooms::Open' AND name='All Talk'")); Assert.Equal(1, fixture.Db.Scalar<long>("SELECT count(*) FROM memberships"));
        var account = await admin.GetAsync("/account/edit"); csrf = await fixture.Token(admin, account); var html = await account.Content.ReadAsStringAsync(); Assert.Contains("&lt;script&gt;Owner&lt;/script&gt;", html); Assert.DoesNotContain("<script>Owner</script>", html);
        var code = fixture.Db.Scalar<string>("SELECT join_code FROM accounts"); var member = fixture.NewClient(); var join = await member.GetAsync("/join/" + code); var memberCsrf = await fixture.Token(member, join);
        var joined = await fixture.Send(member, "POST", "/join/" + code, memberCsrf, ("user[name]", "Member"), ("user[email_address]", "member@example.test"), ("user[password]", "secret123456")); Assert.Equal(HttpStatusCode.Found, joined.StatusCode); fixture.Cookies(member, joined);
        var memberId = fixture.Db.Scalar<long>("SELECT id FROM users WHERE email_address=@email", new { email = "member@example.test" });
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Send(member, "PATCH", "/account", memberCsrf, ("account[name]", "forged"))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await fixture.Send(member, "PATCH", "/account", "bogus", ("account[name]", "forged"))).StatusCode);
        var removed = await fixture.Send(admin, "DELETE", "/account/users/" + memberId, csrf); Assert.Equal(HttpStatusCode.Found, removed.StatusCode);
        Assert.Equal(1, fixture.Db.Scalar<long>("SELECT status FROM users WHERE id=@id", new { id = memberId })); Assert.Equal(0, fixture.Db.Scalar<long>("SELECT count(*) FROM sessions WHERE user_id=@id", new { id = memberId })); Assert.Equal(0, fixture.Db.Scalar<long>("SELECT count(*) FROM memberships WHERE user_id=@id", new { id = memberId }));
        Assert.Equal(HttpStatusCode.Found, (await member.GetAsync("/account/edit")).StatusCode);
        var signin = fixture.NewClient(); var signinPage = await signin.GetAsync("/session/new"); var signinCsrf = await fixture.Token(signin, signinPage);
        Assert.DoesNotContain(SqlMapper.GetCachedSQL(), cached => cached.Item2 == "SELECT name FROM accounts LIMIT 1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Send(signin, "POST", "/session", signinCsrf, ("email_address", "member@example.test"), ("password", "secret123456"))).StatusCode);
        var signed = await fixture.Send(signin, "POST", "/session", signinCsrf, ("email_address", "owner@example.test"), ("password", "secret123456")); Assert.Equal(HttpStatusCode.Found, signed.StatusCode); fixture.Cookies(signin, signed); Assert.Equal(HttpStatusCode.OK, (await signin.GetAsync("/account/edit")).StatusCode);
    }
    [Fact]
    public void RailsSignedIdAndGlobalIdLegacyVerificationVectorsAreCompatible()
    {
        var crypto = Crypto();
        foreach (var vector in Vectors["signed_ids"]!["verify"]!.AsArray())
        {
            var v = vector!; var actual = crypto.VerifySignedIdAt(v["signed_id"]!.ToString(), v["model"]!.ToString(), v["purpose"]?.ToString() ?? "", DateTimeOffset.Parse(v["now"]!.ToString()));
            Assert.Equal(v["expected"] == null ? (long?)null : long.Parse(v["expected"]!.ToString()), actual);
        }
        foreach (var vector in Vectors["sgids"]!["verify"]!.AsArray())
        {
            var v = vector!; var expected = v["expected"]?.ToString();
            var model = expected?.StartsWith("gid://campfire/Rooms::Open/", StringComparison.Ordinal) == true ? "Rooms::Open" : "User";
            long? id = expected == null ? null : long.Parse(expected.Split('/').Last().Split('?')[0]);
            var actual = crypto.VerifySignedGlobalIdAt(v["sgid"]!.ToString(), model, v["purpose"]!.ToString(), DateTimeOffset.Parse(v["now"]!.ToString()));
            Assert.True(id == actual, v["case"] + ": expected " + id + " actual " + actual);
        }
    }
    [Fact]
    public async Task BotAdministrationUpdatesWebhookMembershipKeyAndPermissions()
    {
        await using var site = await Site.Create(); var csrf = await Bootstrap(site);
        var created = await site.Send(site.Client, "POST", "/account/bots", csrf, ("user[name]", "<b>Reporter</b>"), ("user[webhook_url]", "https://example.invalid/hook")); Assert.Equal(HttpStatusCode.Found, created.StatusCode);
        var bot = site.Db.Single<UserRecord>("SELECT * FROM users WHERE role=2")!;
        Assert.Equal(12, bot.BotToken!.Length); Assert.Equal(1, site.Db.Scalar<long>("SELECT count(*) FROM memberships WHERE user_id=@id", new { id = bot.Id })); Assert.Equal("https://example.invalid/hook", site.Db.Scalar<string>("SELECT url FROM webhooks WHERE user_id=@id", new { id = bot.Id }));
        var listing = await site.Client.GetAsync("/account/bots"); var markup = await listing.Content.ReadAsStringAsync(); Assert.Contains("&lt;b&gt;Reporter&lt;/b&gt;", markup); Assert.Contains("data-controller=\"copy-to-clipboard\"", markup); Assert.Contains(bot.Id + "-" + bot.BotToken + "/messages", markup);
        var crypto = site.App.Services.GetRequiredService<RailsCrypto>(); var auth = site.App.Services.GetRequiredService<IAuthService>();
        HttpContext BotContext(string key) { var c = new DefaultHttpContext(); c.Request.RouteValues["bot_key"] = key; return c; }
        Assert.Equal(bot.Id, auth.Current(BotContext(bot.Id + "-" + bot.BotToken))!.Id);
        Assert.Equal(HttpStatusCode.Found, (await site.Send(site.Client,"PATCH", "/account/bots/" + bot.Id, csrf, ("user[name]", "Reporter"), ("user[webhook_url]", ""))).StatusCode);
        Assert.Equal(0, site.Db.Scalar<long>("SELECT count(*) FROM webhooks WHERE user_id=@id", new { id = bot.Id }));
        Assert.Equal(HttpStatusCode.Found, (await site.Send(site.Client, "PUT", "/account/bots/" + bot.Id + "/key", csrf)).StatusCode);
        var newToken = site.Db.Scalar<string>("SELECT bot_token FROM users WHERE id=@id",new { id=bot.Id }); Assert.NotEqual(bot.BotToken,newToken); Assert.Null(auth.Current(BotContext(bot.Id + "-" + bot.BotToken))); Assert.Equal(bot.Id, auth.Current(BotContext(bot.Id + "-" + newToken))!.Id);
        Assert.Equal(HttpStatusCode.Found, (await site.Send(site.Client,"DELETE", "/account/bots/" + bot.Id,csrf)).StatusCode); Assert.Null(auth.Current(BotContext(bot.Id + "-" + newToken))); Assert.Equal(1,site.Db.Scalar<int>("SELECT status FROM users WHERE id=@id",new { id=bot.Id }));
        // A regular authenticated member cannot inspect bot secrets.
        var member = site.NewClient(); var code = site.Db.Scalar<string>("SELECT join_code FROM accounts"); var join = await member.GetAsync("/join/"+code); var memberCsrf = await site.Token(member,join); var joined = await site.Send(member,"POST","/join/"+code,memberCsrf,("user[name]","Member"),("user[email_address]","botmember@example.test"),("user[password]","secret")); site.Cookies(member,joined);
        Assert.Equal(HttpStatusCode.Forbidden,(await member.GetAsync("/account/bots")).StatusCode);
    }
    [Fact]
    public async Task BanRemovesContentAndSessionsThenUnbanRestoresStatusAndDeactivationPreservesDirectMembership()
    {
        await using var site = await Site.Create(); var csrf = await Bootstrap(site); var now = RequestUser.Timestamp();
        var member = site.NewClient(); var code = site.Db.Scalar<string>("SELECT join_code FROM accounts"); var join = await member.GetAsync("/join/"+code); var memberCsrf = await site.Token(member,join); var joined = await site.Send(member,"POST","/join/"+code,memberCsrf,("user[name]","Writer"),("user[email_address]","writer@example.test"),("user[password]","secret")); site.Cookies(member,joined);
        var memberId = site.Db.Scalar<long>("SELECT id FROM users WHERE email_address='writer@example.test'"); var roomId = site.Db.Scalar<long>("SELECT id FROM rooms LIMIT 1");
        site.Db.Execute("UPDATE sessions SET ip_address=@ip WHERE user_id=@memberId",new { ip="192.0.2.10",memberId });
        var messageId = site.Db.Scalar<long>("INSERT INTO messages(room_id,creator_id,client_message_id,created_at,updated_at) VALUES(@roomId,@memberId,'client-ban',@now,@now) RETURNING id",new { roomId,memberId,now });
        site.Db.Execute("INSERT INTO action_text_rich_texts(record_type,record_id,name,body,created_at,updated_at) VALUES('Message',@messageId,'body','remove me',@now,@now); INSERT INTO message_search_index(rowid,body) VALUES(@messageId,'remove me'); INSERT INTO boosts(message_id,booster_id,content,created_at,updated_at) VALUES(@messageId,1,'yes',@now,@now)",new { messageId,now });
        var blobId=site.Db.Scalar<long>("INSERT INTO active_storage_blobs(key,filename,byte_size,service_name,created_at) VALUES('ban-attachment','image.jpg',1,'local',@now) RETURNING id",new { now });
        var textId=site.Db.Scalar<long>("SELECT id FROM action_text_rich_texts WHERE record_type='Message' AND record_id=@messageId",new { messageId });
        site.Db.Execute("INSERT INTO active_storage_attachments(record_type,record_id,blob_id,name,created_at) VALUES('Message',@messageId,@blobId,'attachment',@now); INSERT INTO active_storage_attachments(record_type,record_id,blob_id,name,created_at) VALUES('ActionText::RichText',@textId,@blobId,'embeds',@now)",new { messageId,textId,blobId,now });
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"POST","/users/"+memberId+"/ban",csrf)).StatusCode);
        Assert.Equal(1,site.Db.Scalar<int>("SELECT count(*) FROM messages WHERE id=@messageId",new { messageId }));
        await ((Jobs)site.App.Services.GetRequiredService<IBackgroundJobs>()).Drain();
        Assert.Contains(blobId,((Media)site.App.Services.GetRequiredService<IMediaService>()).Purged); Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM active_storage_attachments WHERE blob_id=@blobId",new { blobId }));
        Assert.Equal(2,site.Db.Scalar<int>("SELECT status FROM users WHERE id=@memberId",new { memberId })); Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM sessions WHERE user_id=@memberId",new { memberId })); Assert.Equal(1,site.Db.Scalar<int>("SELECT count(*) FROM bans WHERE user_id=@memberId AND ip_address='192.0.2.10'",new { memberId }));
        Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM messages WHERE id=@messageId",new { messageId })); Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM action_text_rich_texts WHERE record_type='Message' AND record_id=@messageId",new { messageId })); Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM message_search_index WHERE rowid=@messageId",new { messageId })); Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM boosts WHERE message_id=@messageId",new { messageId }));
        var events = (Realtime)site.App.Services.GetRequiredService<IRealtimeEvents>(); Assert.Contains(events.Removed,m => m.ClientId == "client-ban" && m.MessageId==messageId);
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"DELETE","/users/"+memberId+"/ban",csrf)).StatusCode); Assert.Equal(0,site.Db.Scalar<int>("SELECT status FROM users WHERE id=@memberId",new { memberId })); Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM bans WHERE user_id=@memberId",new { memberId }));
        var directId = site.Db.Scalar<long>("INSERT INTO rooms(name,type,creator_id,created_at,updated_at) VALUES(NULL,'Rooms::Direct',1,@now,@now) RETURNING id",new { now });
        site.Db.Execute("INSERT INTO memberships(room_id,user_id,created_at,updated_at) VALUES(@directId,@memberId,@now,@now); INSERT INTO searches(user_id,query,created_at,updated_at) VALUES(@memberId,'history',@now,@now); INSERT INTO push_subscriptions(user_id,endpoint,created_at,updated_at) VALUES(@memberId,'https://push.example.invalid',@now,@now)",new { memberId,directId,now });
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"DELETE","/account/users/"+memberId,csrf)).StatusCode);
        Assert.Equal(1,site.Db.Scalar<int>("SELECT count(*) FROM memberships WHERE user_id=@memberId AND room_id=@directId",new { memberId,directId })); Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM memberships WHERE user_id=@memberId AND room_id<>@directId",new { memberId,directId })); Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM searches WHERE user_id=@memberId",new { memberId })); Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM push_subscriptions WHERE user_id=@memberId",new { memberId })); Assert.Contains("-deactivated-",site.Db.Scalar<string>("SELECT email_address FROM users WHERE id=@memberId",new { memberId }));
    }
    [Fact]
    public async Task AccountSettingsInvitationRotationAndProfileWriteOnlyPermittedFields()
    {
        await using var site = await Site.Create(); var csrf = await Bootstrap(site); var code = site.Db.Scalar<string>("SELECT join_code FROM accounts");
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"PATCH","/account",csrf,("account[name]","Team"),("account[settings][restrict_room_creation_to_administrators]","true"))).StatusCode); Assert.Equal("Team",site.Db.Scalar<string>("SELECT name FROM accounts")); Assert.True(JsonNode.Parse(site.Db.Scalar<string>("SELECT settings FROM accounts"))!["restrict_room_creation_to_administrators"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"POST","/account/join_code",csrf)).StatusCode); Assert.NotEqual(code,site.Db.Scalar<string>("SELECT join_code FROM accounts")); Assert.Equal(HttpStatusCode.NotFound,(await site.NewClient().GetAsync("/join/"+code)).StatusCode);
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"PATCH","/users/999/profile",csrf,("user[name]","Renamed"),("user[bio]","<script>bad</script>"),("user[role]","member"),("user[password]","changed"))).StatusCode);
        var user=site.Db.Single<UserRecord>("SELECT * FROM users WHERE id=1")!; Assert.Equal("Renamed",user.Name); Assert.Equal(1,user.Role); Assert.True(BCrypt.Net.BCrypt.Verify("changed",user.PasswordDigest));
        var profile=await site.Client.GetAsync("/users/me/profile"); var markup=await profile.Content.ReadAsStringAsync(); Assert.Contains("&lt;script&gt;bad&lt;/script&gt;",markup); Assert.Contains("data-controller=\"upload-preview\"",markup); Assert.Contains("data-controller=\"sessions\"",markup); Assert.Contains("data-controller=\"copy-to-clipboard\"",markup);
    }
    [Fact]
    public void AuthenticationIsCachedOnlyInOneRequestAndFreshSessionsDoNotWrite()
    {
        var directory=Path.Combine(Path.GetTempPath(),"campfire-auth-"+Guid.NewGuid()); var real=new SqliteDataStore(Config(directory)); var now=RequestUser.Timestamp();
        real.Execute("INSERT INTO users(id,name,role,created_at,updated_at) VALUES(1,'Owner',1,@now,@now); INSERT INTO sessions(user_id,token,created_at,updated_at,last_active_at) VALUES(1,'token',@now,@now,@now)",new { now });
        var counted=new CountingStore(real); var crypto=Crypto(); var auth=new AuthService(counted,crypto); var cookie="session_token="+Uri.EscapeDataString(crypto.SignCookie("session_token","token"));
        HttpContext Request() { var c=new DefaultHttpContext();c.Request.Headers.Cookie=cookie;return c; }
        var first=Request(); Assert.True(auth.Current(first)!.IsAdmin); Assert.Equal(0,counted.Writes); real.Execute("UPDATE users SET role=0 WHERE id=1"); Assert.True(auth.Current(first)!.IsAdmin); Assert.False(auth.Current(Request())!.IsAdmin); Assert.Equal(0,counted.Writes);
        real.Execute("UPDATE sessions SET last_active_at='2020-01-01 00:00:00' WHERE token='token'");Assert.NotNull(auth.Current(Request()));Assert.Equal(1,counted.Writes);
        real.Execute("DELETE FROM sessions WHERE token='token'");Assert.Null(auth.Current(Request()));
    }
    [Fact]
    public void RailsSessionOldCsrfSurvivesRenderingAndCrossOriginIsRejected()
    {
        var db=new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(),"campfire-csrf-"+Guid.NewGuid())));var crypto=Crypto();var auth=new AuthService(db,crypto);var v=Vectors["session"]!;
        var c=new DefaultHttpContext();c.Request.Scheme="http";c.Request.Host=new HostString("campfire.test");c.Request.Path="/session";c.Request.Method="POST";c.Request.Headers.Cookie="_campfire_session="+Uri.EscapeDataString(v["session_cookie_raw"]!.ToString());
        Assert.True(auth.ValidateCsrf(c,v["session_form_token"]!.ToString()));var newToken=auth.CsrfToken(c);Assert.True(auth.ValidateCsrf(c,newToken));Assert.False(c.Response.Headers.ContainsKey("Set-Cookie"));
        c.Request.Headers.Origin="https://evil.example";Assert.False(auth.ValidateCsrf(c,newToken));c.Request.Headers.Remove("Origin");c.Request.Headers["Sec-Fetch-Site"]="cross-site";Assert.True(auth.ValidateCsrf(c,newToken));
    }
    private static async Task<string> Bootstrap(Site site)
    {
        var page=await site.Client.GetAsync("/first_run"); var csrf=await site.Token(site.Client,page); var reply=await site.Send(site.Client,"POST","/first_run",csrf,("user[name]","Owner"),("user[email_address]","owner@example.test"),("user[password]","secret")); Assert.Equal(HttpStatusCode.Found,reply.StatusCode);site.Cookies(site.Client,reply);return csrf;
    }
    [Fact]
    public async Task PostCommitCallbacksKeepWriterLeaseAndPreserveCommittedRowsOnFailure()
    {
        using var db = new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(), "campfire-after-commit-" + Guid.NewGuid())));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var waiting = new ManualResetEventSlim();
        var now = RequestUser.Timestamp();
        object? physicalHandle = null;
        long committedId = 0;
        var first = Task.Run(() => db.Write((connection, transaction) =>
        {
            physicalHandle = connection.Handle;
            Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle!));
            return connection.ExecuteScalar<long>("INSERT INTO users(name,created_at,updated_at) VALUES('Committed callback owner',@now,@now) RETURNING id", new { now }, transaction);
        }, (connection, id) =>
        {
            committedId = id;
            Assert.Same(physicalHandle, connection.Handle);
            Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle!));
            connection.Execute("INSERT INTO searches(user_id,query,created_at,updated_at) VALUES(@id,'Committed callback',@now,@now)", new { id, now });
            // An independent reader sees both the main transaction and the
            // callback's separate autocommit before the callback returns.
            Assert.Equal(1, db.Read(c => c.ExecuteScalar<long>("SELECT count(*) FROM searches WHERE user_id=@id", new { id })));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            connection.Execute("INSERT INTO sessions(user_id,token,created_at,updated_at,last_active_at) VALUES(-1,'failed-callback',@now,@now,@now)", new { now });
        }));
        Task<int>? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run(() => { waiting.Set(); return db.Write((connection, transaction) => connection.Execute("UPDATE users SET bio='Next writer' WHERE id=@id", new { id = committedId }, transaction)); });
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(100)));
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () => await first);
        Assert.NotNull(second);
        Assert.Equal(1, await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Next writer", db.Read(c => c.ExecuteScalar<string>("SELECT bio FROM users WHERE id=@id", new { id = committedId })));
        Assert.Equal(1, db.Read(c => c.ExecuteScalar<long>("SELECT count(*) FROM searches WHERE user_id=@id", new { id = committedId })));
        Assert.Equal(0, db.Read(c => c.ExecuteScalar<long>("SELECT count(*) FROM sessions WHERE token='failed-callback'")));
    }
    [Fact]
    public async Task EveryReadAndWriteConnectionUsesPinnedRailsPragmas()
    {
        using var db = new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(), "campfire-pragmas-" + Guid.NewGuid())));
        static void AssertPragmas(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction? transaction = null)
        {
            // Actual adapter values, not an assertion on the initialization SQL text.
            Assert.Equal("wal", connection.ExecuteScalar<string>("PRAGMA journal_mode", transaction: transaction));
            Assert.Equal(1, connection.ExecuteScalar<long>("PRAGMA synchronous", transaction: transaction));
            Assert.Equal(1, connection.ExecuteScalar<long>("PRAGMA foreign_keys", transaction: transaction));
            Assert.Equal(2000, connection.ExecuteScalar<long>("PRAGMA cache_size", transaction: transaction));
            Assert.Equal(67108864, connection.ExecuteScalar<long>("PRAGMA journal_size_limit", transaction: transaction));
            Assert.Equal(134217728, connection.ExecuteScalar<long>("PRAGMA mmap_size", transaction: transaction));
            Assert.Equal(5000, connection.ExecuteScalar<long>("PRAGMA busy_timeout", transaction: transaction));
        }
        var handles = new HashSet<object>(ReferenceEqualityComparer.Instance);
        db.Read(first =>
        {
            AssertPragmas(first);
            handles.Add(first.Handle!);
            // Keep a lease open so the nested read must configure a second physical connection.
            db.Read(second => { AssertPragmas(second); Assert.NotSame(first.Handle, second.Handle); handles.Add(second.Handle!); return 0; });
            return 0;
        });
        // A new managed lease reuses an actual native handle and retains Rails defaults.
        db.Read(connection => { Assert.Contains(connection.Handle!, handles); AssertPragmas(connection); return 0; });
        db.Write((connection, transaction) => { AssertPragmas(connection, transaction); return 0; });
        await db.WriteAsync((connection, transaction) => { AssertPragmas(connection, transaction); return Task.FromResult(0); });
        // The store now owns its persistent connections; provider pool clearing
        // does not close them. Explicitly closing a lease forces replacement.
        db.Read(connection => { Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection); Assert.Contains(connection.Handle!, handles); connection.Close(); return 0; });
        db.Read(connection =>
        {
            AssertPragmas(connection);
            db.Read(replacement => { Assert.DoesNotContain(replacement.Handle!, handles); AssertPragmas(replacement); return 0; });
            return 0;
        });
    }
    [Fact]
    public void FreshDatabaseHasCompletePinnedRailsMigrationMetadata()
    {
        using var db = new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(),"campfire-metadata-"+Guid.NewGuid())));
        Assert.Equal(15,db.Scalar<int>("SELECT count(*) FROM schema_migrations"));
        Assert.Equal("production",db.Scalar<string>("SELECT value FROM ar_internal_metadata WHERE key='environment'"));
        Assert.Equal("f75da8dad38bfb179ffd757bd7a7c2b3f818bc29",db.Scalar<string>("SELECT value FROM ar_internal_metadata WHERE key='schema_sha1'"));
        Assert.Equal(5000,db.Scalar<int>("PRAGMA busy_timeout"));
    }
    [Fact]
    public void OldPortMetadataRepairsOnlyExactSchemaAndRespectsRailsEnvironment()
    {
        var directory=Path.Combine(Path.GetTempPath(),"campfire-repair-"+Guid.NewGuid());var config=Config(directory);
        using(var db=new SqliteDataStore(config)) db.Execute("DELETE FROM schema_migrations WHERE version<>'20251212154340'; DELETE FROM ar_internal_metadata; INSERT INTO users(name,created_at,updated_at) VALUES('Preserved',@now,@now)",new {now=RequestUser.Timestamp()});
        using(var repaired=new SqliteDataStore(config))
        {
            Assert.Equal(15,repaired.Scalar<int>("SELECT count(*) FROM schema_migrations"));Assert.Equal("Preserved",repaired.Scalar<string>("SELECT name FROM users"));
            repaired.Execute("DELETE FROM schema_migrations WHERE version<>'20251212154340'; DELETE FROM ar_internal_metadata; CREATE INDEX unexpected_users_email ON users(email_address)");
        }
        Assert.Contains("cannot be repaired",Assert.Throws<InvalidOperationException>(()=>new SqliteDataStore(config)).Message);
        var testConfig=new ConfigurationBuilder().AddConfiguration(Config(Path.Combine(Path.GetTempPath(),"campfire-env-"+Guid.NewGuid()))).AddInMemoryCollection(new Dictionary<string,string?>{["RAILS_ENV"]="test"}).Build();
        using var testDb=new SqliteDataStore(testConfig);Assert.EndsWith("test.sqlite3",testDb.DatabasePath);Assert.Equal("test",testDb.Scalar<string>("SELECT value FROM ar_internal_metadata WHERE key='environment'"));
    }
    [Fact]
    public async Task WireCookiesDecodeOnceAndNestedJsonRequiresObjectsWithoutLosingNullFields()
    {
        await using var site=await Site.Create();
        var page=await site.Client.GetAsync("/first_run");var csrf=await site.Token(site.Client,page);
        var raw=page.Headers.GetValues("Set-Cookie").First(x=>x.StartsWith("_campfire_session=",StringComparison.Ordinal)).Split(';')[0].Split('=',2)[1];
        var decoded=Uri.UnescapeDataString(raw);
        Assert.NotNull(Crypto().DecryptCookie("_campfire_session",decoded));
        Assert.Equal(24,decoded.Split("--")[2].Length);
        var missing=await site.Send(site.Client,"POST","/first_run",csrf,("irrelevant","1"));
        Assert.Equal(HttpStatusCode.BadRequest,missing.StatusCode);Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM accounts"));
        csrf=await Bootstrap(site);
        using var request=new HttpRequestMessage(HttpMethod.Patch,"/users/me/profile") {Content=new StringContent("{\"user\":{\"bio\":\"<b>JSON bio</b>\",\"name\":null}}",System.Text.Encoding.UTF8,"application/json")};
        request.Headers.Add("X-CSRF-Token",csrf);
        Assert.Equal(HttpStatusCode.Found,(await site.Client.SendAsync(request)).StatusCode);
        var user=site.Db.Single<UserRecord>("SELECT * FROM users WHERE role=1")!;Assert.Equal("<b>JSON bio</b>",user.Bio);Assert.Equal("Owner",user.Name);Assert.Equal("owner@example.test",user.EmailAddress);
        Assert.Equal(HttpStatusCode.BadRequest,(await site.Send(site.Client,"PATCH","/account",csrf,("irrelevant","1"))).StatusCode);
        site.Client.DefaultRequestHeaders.Add("X-CSRF-Token",csrf);
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"PATCH","/account","invalid",("account[name]","Valid header"))).StatusCode);
        Assert.Equal("Valid header",site.Db.Scalar<string>("SELECT name FROM accounts"));
    }
    [Fact]
    public async Task LongPasswordsSettingsAndWebhookLifecycleFollowRails()
    {
        await using var site=await Site.Create();var csrf=await Bootstrap(site);
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"PATCH","/users/me/profile",csrf,("user[password]",new string('p',73)))).StatusCode);
        Assert.True(BCrypt.Net.BCrypt.Verify(new string('p',73),site.Db.Scalar<string>("SELECT password_digest FROM users WHERE role=1")));
        foreach(var (value,expected) in new[]{("banana","true"),("OFF","false"),("","null")})
        {
            Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"PATCH","/account",csrf,("account[settings][restrict_room_creation_to_administrators]",value))).StatusCode);
            Assert.Contains(":"+expected,site.Db.Scalar<string>("SELECT settings FROM accounts"));
        }
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"POST","/account/bots",csrf,("user[name]","Webhook"),("user[webhook_url]",""))).StatusCode);
        var bot=site.Db.Scalar<long>("SELECT id FROM users WHERE role=2");var first=site.Db.Scalar<long>("SELECT id FROM webhooks WHERE user_id=@bot",new {bot});
        await site.Send(site.Client,"PATCH","/account/bots/"+bot,csrf,("user[webhook_url]","http://127.0.0.1:9/owned"));Assert.Equal(first,site.Db.Scalar<long>("SELECT id FROM webhooks WHERE user_id=@bot",new {bot}));
        await site.Send(site.Client,"PATCH","/account/bots/"+bot,csrf,("user[webhook_url]","   "));Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM webhooks WHERE user_id=@bot",new {bot}));
    }
    [Fact]
    public async Task LoginUsesSharedAtomicCounterAndRailsFailsafeOnUnavailableRedis()
    {
        await using var site=await Site.Create();var csrf=await Bootstrap(site);var redis=(Redis)site.App.Services.GetRequiredService<IRedisTransport>();
        for(var i=0;i<10;i++) Assert.Equal(HttpStatusCode.Unauthorized,(await site.Send(site.Client,"POST","/session",csrf,("email_address","missing@example.test"),("password","wrong"))).StatusCode);
        Assert.Equal((HttpStatusCode)429,(await site.Send(site.Client,"POST","/session",csrf,("email_address","missing@example.test"),("password","wrong"))).StatusCode);
        Assert.Equal("EVAL",redis.Commands[0][0]);Assert.Equal("180",redis.Commands[0][4]);Assert.Equal(11,redis.Commands.Count);
        redis.Fail=true;Assert.Equal(HttpStatusCode.Unauthorized,(await site.Send(site.Client,"POST","/session",csrf,("email_address","missing@example.test"),("password","wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"POST","/session",csrf,("email_address","owner@example.test"),("password","secret"))).StatusCode);
    }
    [Fact]
    public async Task LogoutStillDeletesSessionWhenDisconnectFails()
    {
        await using var site=await Site.Create();var csrf=await Bootstrap(site);
        ((Realtime)site.App.Services.GetRequiredService<IRealtimeEvents>()).DisconnectFails=true;
        var response=await site.Send(site.Client,"DELETE","/session",csrf);Assert.Equal(HttpStatusCode.Found,response.StatusCode);Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM sessions"));
        var raw=response.Headers.GetValues("Set-Cookie").First(x=>x.StartsWith("_campfire_session=",StringComparison.Ordinal)).Split(';')[0].Split('=',2)[1];var reset=Crypto().DecryptCookie("_campfire_session",Uri.UnescapeDataString(raw))!;
        Assert.NotNull(reset["session_id"]);Assert.Null(reset["_csrf_token"]);Assert.Null(reset["flash"]);
    }
    [Fact]
    public async Task BanQueueFailureRollsBackAndSuccessfulBanDefersContentWithRoomTouch()
    {
        await using var site=await Site.Create();var csrf=await Bootstrap(site);var now=RequestUser.Timestamp();
        var id=site.Db.Scalar<long>("INSERT INTO users(name,created_at,updated_at) VALUES('Ban target',@now,@now) RETURNING id",new {now});
        site.Db.Execute("INSERT INTO sessions(user_id,token,ip_address,created_at,updated_at,last_active_at) VALUES(@id,'space',@blank,@now,@now,@now); INSERT INTO messages(room_id,creator_id,client_message_id,created_at,updated_at) SELECT id,@id,'queue',@now,@now FROM rooms LIMIT 1; UPDATE rooms SET updated_at='2000-01-01 00:00:00'",new {id,now,blank=" \t\r\n\u3000\u00a0"});
        var queue=(Jobs)site.App.Services.GetRequiredService<IBackgroundJobs>();queue.Fail=true;
        Assert.Equal(HttpStatusCode.InternalServerError,(await site.Send(site.Client,"POST","/users/"+id+"/ban",csrf)).StatusCode);
        Assert.Equal(0,site.Db.Scalar<int>("SELECT status FROM users WHERE id=@id",new {id}));Assert.Equal(1,site.Db.Scalar<int>("SELECT count(*) FROM sessions WHERE user_id=@id",new {id}));
        queue.Fail=false;Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"POST","/users/"+id+"/ban",csrf)).StatusCode);
        Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM bans WHERE user_id=@id",new {id}));Assert.Equal(1,site.Db.Scalar<int>("SELECT count(*) FROM messages WHERE creator_id=@id",new {id}));
        await queue.Drain();Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM messages WHERE creator_id=@id",new {id}));Assert.NotEqual("2000-01-01 00:00:00",site.Db.Scalar<string>("SELECT updated_at FROM rooms LIMIT 1"));
    }
    [Fact]
    public async Task FirstRunRoomFailureKeepsCommittedAccountAndDeactivationFailurePreservesUser()
    {
        await using var fresh=await Site.Create();
        var page=await fresh.Client.GetAsync("/first_run");var csrf=await fresh.Token(fresh.Client,page);
        fresh.Db.Execute("CREATE TRIGGER fail_room BEFORE INSERT ON rooms BEGIN SELECT RAISE(ABORT,'room failure'); END");
        Assert.Equal(HttpStatusCode.InternalServerError,(await fresh.Send(fresh.Client,"POST","/first_run",csrf,("user[name]","Owner"),("user[email_address]","owner@example.test"),("user[password]","secret"))).StatusCode);
        Assert.Equal(1,fresh.Db.Scalar<int>("SELECT count(*) FROM accounts"));Assert.Equal(0,fresh.Db.Scalar<int>("SELECT count(*) FROM users"));
        await using var site=await Site.Create();csrf=await Bootstrap(site);var now=RequestUser.Timestamp();
        var id=site.Db.Scalar<long>("INSERT INTO users(name,email_address,created_at,updated_at) VALUES('Preserved','preserved@example.test',@now,@now) RETURNING id",new {now});
        site.Db.Execute("INSERT INTO sessions(user_id,token,created_at,updated_at,last_active_at) VALUES(@id,'preserved',@now,@now,@now)",new {id,now});
        ((Realtime)site.App.Services.GetRequiredService<IRealtimeEvents>()).DisconnectFails=true;
        Assert.Equal(HttpStatusCode.InternalServerError,(await site.Send(site.Client,"DELETE","/account/users/"+id,csrf)).StatusCode);
        Assert.Equal(0,site.Db.Scalar<int>("SELECT status FROM users WHERE id=@id",new {id}));Assert.Equal("preserved@example.test",site.Db.Scalar<string>("SELECT email_address FROM users WHERE id=@id",new {id}));Assert.Equal(1,site.Db.Scalar<int>("SELECT count(*) FROM sessions WHERE user_id=@id",new {id}));
    }
    [Fact]
    public async Task AsyncWriterRollsBackCancellationAndReleasesWriterGate()
    {
        using var db=new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(),"campfire-async-"+Guid.NewGuid())));
        using var cancellation=new CancellationTokenSource();
        var callbackRan=false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>db.WriteAsync(async(connection,transaction)=>
        {
            connection.Execute("INSERT INTO users(name,created_at,updated_at) VALUES('Canceled',@now,@now)",new {now=RequestUser.Timestamp()},transaction);
            await Task.Yield();cancellation.Cancel();return 1;
        },(_,_)=>callbackRan=true,cancellation.Token));
        Assert.False(callbackRan);
        Assert.Equal(0,db.Scalar<int>("SELECT count(*) FROM users"));
        Assert.Equal(1,db.Write((connection,transaction)=>connection.Execute("INSERT INTO users(name,created_at,updated_at) VALUES('Committed',@now,@now)",new {now=RequestUser.Timestamp()},transaction)));
    }
    [Fact]
    public async Task AsyncAdmissionCancellationDoesNotRunOperationOrCommittedCallback()
    {
        using var db=new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(),"campfire-admission-"+Guid.NewGuid())));
        using var cancellation=new CancellationTokenSource();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=db.WriteAsync(async(connection,transaction)=>
        {
            await release.Task;
            return connection.Execute("INSERT INTO users(name,created_at,updated_at) VALUES('Admitted',@now,@now)",new {now=RequestUser.Timestamp()},transaction);
        });
        var operationRan=false;var callbackRan=false;
        var waiting=db.WriteAsync((_,_)=>{operationRan=true;return Task.FromResult(0);},(_,_)=>callbackRan=true,cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting); }
        finally { release.SetResult(); }
        Assert.Equal(1,await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(operationRan);Assert.False(callbackRan);
        Assert.Equal(1,await db.WriteAsync((connection,transaction)=>Task.FromResult(connection.Execute("UPDATE users SET bio='Lease released'",transaction:transaction))));
    }
    [Fact]
    public async Task AsyncCommittedCallbacksFinishAfterCancellationAndReleaseLeaseOnFailure()
    {
        using var db=new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(),"campfire-committed-async-"+Guid.NewGuid())));
        using var cancellation=new CancellationTokenSource();
        var now=RequestUser.Timestamp();long committedId=0;object? handle=null;
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>db.WriteAsync((connection,transaction)=>
        {
            handle=connection.Handle;
            return Task.FromResult(connection.ExecuteScalar<long>("INSERT INTO users(name,created_at,updated_at) VALUES('Async committed',@now,@now) RETURNING id",new {now},transaction));
        },(connection,id)=>
        {
            committedId=id;cancellation.Cancel();
            Assert.Same(handle,connection.Handle);
            Assert.Equal(1,SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle!));
            connection.Execute("INSERT INTO searches(user_id,query,created_at,updated_at) VALUES(@id,'After cancellation',@now,@now)",new {id,now});
            throw new InvalidOperationException("Committed callback failed");
        },cancellation.Token));
        Assert.Equal("Committed callback failed",error.Message);
        Assert.Equal(1,db.Read(connection=>connection.ExecuteScalar<long>("SELECT count(*) FROM searches WHERE user_id=@id",new {id=committedId})));
        var nextCallbackRan=false;
        Assert.Equal(1,await db.WriteAsync((connection,transaction)=>Task.FromResult(connection.Execute("UPDATE users SET bio='Async released' WHERE id=@id",new {id=committedId},transaction)),(_,_)=>nextCallbackRan=true).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(nextCallbackRan);
        Assert.Equal("Async released",db.Read(connection=>connection.ExecuteScalar<string>("SELECT bio FROM users WHERE id=@id",new {id=committedId})));
    }
    [Fact]
    public async Task DuplicateProfileEmailAndUnknownSettingRaiseWithoutPartialMutation()
    {
        await using var site=await Site.Create();var csrf=await Bootstrap(site);var now=RequestUser.Timestamp();
        site.Db.Execute("INSERT INTO users(name,email_address,created_at,updated_at) VALUES('Other','other@example.test',@now,@now)",new {now});
        Assert.Equal(HttpStatusCode.InternalServerError,(await site.Send(site.Client,"PATCH","/users/me/profile",csrf,("user[name]","Should rollback"),("user[bio]","Should rollback"),("user[email_address]","other@example.test"))).StatusCode);
        var owner=site.Db.Single<UserRecord>("SELECT * FROM users WHERE role=1")!;Assert.Equal("Owner",owner.Name);Assert.Equal("owner@example.test",owner.EmailAddress);Assert.Null(owner.Bio);
        var before=site.Db.Scalar<string>("SELECT settings FROM accounts");
        Assert.Equal(HttpStatusCode.InternalServerError,(await site.Send(site.Client,"PATCH","/account",csrf,("account[name]","Should not save"),("account[settings][unknown]","true"))).StatusCode);
        Assert.Equal("Campfire",site.Db.Scalar<string>("SELECT name FROM accounts"));Assert.Equal(before,site.Db.Scalar<string>("SELECT settings FROM accounts"));
    }
    [Fact]
    public async Task JsonNullNameIsConstraintFailureWhileNullStylesClearsOnlySubmittedAttribute()
    {
        await using var site=await Site.Create();var csrf=await Bootstrap(site);
        async Task<HttpStatusCode> JsonPatch(string path,string json)
        {
            using var request=new HttpRequestMessage(HttpMethod.Patch,path){Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")};request.Headers.Add("X-CSRF-Token",csrf);return (await site.Client.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.InternalServerError,await JsonPatch("/account","{\"account\":{\"name\":null}}"));Assert.Equal("Campfire",site.Db.Scalar<string>("SELECT name FROM accounts"));
        site.Db.Execute("UPDATE accounts SET custom_styles='preserve'");
        Assert.Equal(HttpStatusCode.Found,await JsonPatch("/account/custom_styles","{\"account\":{\"unpermitted\":true}}"));Assert.Equal("preserve",site.Db.Scalar<string>("SELECT custom_styles FROM accounts"));
        Assert.Equal(HttpStatusCode.Found,await JsonPatch("/account/custom_styles","{\"account\":{\"custom_styles\":null}}"));Assert.Null(site.Db.Single<string?>("SELECT custom_styles FROM accounts"));
        Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"POST","/account/bots",csrf,("user[name]","Bot"))).StatusCode);var bot=site.Db.Scalar<long>("SELECT id FROM users WHERE role=2");
        Assert.Equal(HttpStatusCode.InternalServerError,await JsonPatch("/account/bots/"+bot,"{\"user\":{\"name\":null}}"));Assert.Equal("Bot",site.Db.Scalar<string>("SELECT name FROM users WHERE id=@bot",new {bot}));
    }
    [Fact]
    public async Task AttachmentPreparationFailureRollsBackProfileAndBotAndCompletesAfterCommit()
    {
        await using var site=await Site.Create();var csrf=await Bootstrap(site);var media=(Media)site.App.Services.GetRequiredService<IMediaService>();media.FailPrepare=true;
        Assert.Equal(HttpStatusCode.InternalServerError,(await site.Send(site.Client,"PATCH","/users/me/profile",csrf,("user[name]","Should rollback"),("user[avatar]","signed-blob-fixture"))).StatusCode);
        Assert.Equal("Owner",site.Db.Scalar<string>("SELECT name FROM users WHERE role=1"));Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM active_storage_blobs"));Assert.Equal(0,media.Completed);
        Assert.Equal(HttpStatusCode.InternalServerError,(await site.Send(site.Client,"POST","/account/bots",csrf,("user[name]","Should rollback"),("user[avatar]","signed-blob-fixture"))).StatusCode);
        Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM users WHERE role=2"));Assert.Equal(0,site.Db.Scalar<int>("SELECT count(*) FROM active_storage_attachments"));
        media.FailPrepare=false;Assert.Equal(HttpStatusCode.Found,(await site.Send(site.Client,"PATCH","/users/me/profile",csrf,("user[name]","Updated"),("user[avatar]","signed-blob-fixture"))).StatusCode);
        Assert.Equal("Updated",site.Db.Scalar<string>("SELECT name FROM users WHERE role=1"));Assert.Equal(1,site.Db.Scalar<int>("SELECT count(*) FROM active_storage_attachments"));Assert.Equal(1,media.Completed);Assert.Equal(1,media.Disposed);
    }
    [Fact]
    public void ImportedSessionsUseTokenLookupAndBotKeyUsesAuthenticationMethod()
    {
        using var db=new SqliteDataStore(Config(Path.Combine(Path.GetTempPath(),"campfire-imported-"+Guid.NewGuid())));var crypto=Crypto();var auth=new AuthService(db,crypto);var now=RequestUser.Timestamp();
        var id=db.Scalar<long>("INSERT INTO users(name,status,created_at,updated_at) VALUES('Imported',1,@now,@now) RETURNING id",new {now});
        db.Execute("INSERT INTO sessions(user_id,token,created_at,updated_at,last_active_at) VALUES(@id,'imported',@now,@now,@now)",new {id,now});
        var request=new DefaultHttpContext();request.Request.Headers.Cookie="session_token="+Uri.EscapeDataString(crypto.SignCookie("session_token","imported"));Assert.Equal(id,auth.Current(request)!.Id);
        db.Execute("DELETE FROM sessions WHERE token='imported'");Assert.Null(auth.Current(new DefaultHttpContext()));
        var bot=db.Scalar<long>("INSERT INTO users(name,role,bot_token,created_at,updated_at) VALUES('Bot',2,'alphanumeric',@now,@now) RETURNING id",new {now});
        request=new DefaultHttpContext();request.Request.QueryString=new QueryString("?bot_key="+bot+"-alphanumeric");Assert.Equal(bot,auth.Current(request)!.Id);Assert.True(request.Items.ContainsKey(AuthService.BotAuthentication));Assert.True(auth.ValidateCsrf(request));
        request=new DefaultHttpContext();request.Request.Path="/session/new";request.Request.QueryString=new QueryString("?bot_key="+bot+"-alphanumeric");Assert.Null(auth.Current(request));Assert.False(request.Items.ContainsKey(AuthService.BotAuthentication));
    }
    [Fact]
    public async Task RailsScalarRootsStringCastsAndNoopTimestampsArePreserved()
    {
        await using var site=await Site.Create();var csrf=await Bootstrap(site);
        async Task<HttpStatusCode> JsonPatch(string path,string json)
        {
            using var request=new HttpRequestMessage(HttpMethod.Patch,path){Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")};request.Headers.Add("X-CSRF-Token",csrf);return (await site.Client.SendAsync(request)).StatusCode;
        }
        foreach(var value in new[]{"true","false","\"nonblank\"","[1]"}) Assert.Equal(HttpStatusCode.InternalServerError,await JsonPatch("/users/me/profile","{\"user\":"+value+"}"));
        foreach(var value in new[]{"null","\"  \"","{}","[]"}) Assert.Equal(HttpStatusCode.BadRequest,await JsonPatch("/users/me/profile","{\"user\":"+value+"}"));
        Assert.Equal(HttpStatusCode.Found,await JsonPatch("/users/me/profile","{\"user\":{\"name\":true,\"bio\":false}}"));Assert.Equal("t",site.Db.Scalar<string>("SELECT name FROM users WHERE role=1"));Assert.Equal("f",site.Db.Scalar<string>("SELECT bio FROM users WHERE role=1"));
        site.Db.Execute("UPDATE users SET updated_at='2000-01-01 00:00:00' WHERE role=1; UPDATE accounts SET updated_at='2000-01-01 00:00:00'");
        Assert.Equal(HttpStatusCode.Found,await JsonPatch("/users/me/profile","{\"user\":{\"bio\":\"f\"}}"));Assert.Equal("2000-01-01 00:00:00",site.Db.Scalar<string>("SELECT updated_at FROM users WHERE role=1"));
        Assert.Equal(HttpStatusCode.Found,await JsonPatch("/account","{\"account\":{\"name\":\"Campfire\"}}"));Assert.Equal("2000-01-01 00:00:00",site.Db.Scalar<string>("SELECT updated_at FROM accounts"));
        Assert.Equal(HttpStatusCode.InternalServerError,await JsonPatch("/users/me/profile","{\"user\":{\"password\":23}}"));
    }
    [Fact]
    public async Task ProfileControlsAndDeactivatedCardsHaveOriginalActions()
    {
        await using var site=await Site.Create();await Bootstrap(site);var page=await site.Client.GetAsync("/users/me/profile");var html=await page.Content.ReadAsStringAsync();
        Assert.Contains("/involvement?involvement=everything",html);Assert.Contains("role=\"checkbox\"",html);Assert.Contains("notification-bell-mentions.svg",html);
        Assert.Equal(2,System.Text.RegularExpressions.Regex.Matches(html,"type=\"file\" name=\"user\\[avatar\\]\"").Count);
        Assert.Equal(2,System.Text.RegularExpressions.Regex.Matches(html,"upload-preview#previewImage change->form#submit").Count);
        html=await (await site.Client.GetAsync("/account/edit")).Content.ReadAsStringAsync();
        Assert.Equal(2,System.Text.RegularExpressions.Regex.Matches(html,"type=\"file\" name=\"account\\[logo\\]\"").Count);Assert.Contains("class=\"version-badge\"",html);Assert.Contains("change->form#submit",html);
        html=await (await site.Client.GetAsync("/session/new")).Content.ReadAsStringAsync();Assert.Contains("<figure class=\"account-logo avatar",html);Assert.Contains("lifebuoy.svg",html);Assert.Contains("mailto:&quot;Owner&quot; &lt;owner@example.test&gt;",html);Assert.Contains("class=\"version-badge\"",html);
        var now=RequestUser.Timestamp();var id=site.Db.Scalar<long>("INSERT INTO users(name,email_address,bio,status,created_at,updated_at) VALUES('Gone','private@example.test','Private bio',1,@now,@now) RETURNING id",new {now});
        html=await (await site.Client.GetAsync("/users/"+id)).Content.ReadAsStringAsync();Assert.DoesNotContain("Remove ban",html);Assert.DoesNotContain("private@example.test",html);Assert.DoesNotContain("Private bio",html);
    }
    [Fact]
    public async Task ProfileReadsRejectJsonFormatsWhileJsonUpdateStillRedirects()
    {
        await using var site=await Site.Create(profileFormatSuffixes:true);
        using(var anonymous=site.NewClient())
            Assert.Equal(HttpStatusCode.Found,(await anonymous.GetAsync("/users/me/profile.json")).StatusCode);
        var csrf=await Bootstrap(site);
        foreach(var method in new[]{"GET","HEAD"})
        {
            using var suffix=new HttpRequestMessage(new HttpMethod(method),"/users/me/profile.json");
            suffix.Headers.Accept.ParseAdd("text/html");
            Assert.Equal(HttpStatusCode.NotAcceptable,(await site.Client.SendAsync(suffix)).StatusCode);
            using var accepted=new HttpRequestMessage(new HttpMethod(method),"/users/me/profile");
            accepted.Headers.Accept.ParseAdd("application/json");
            Assert.Equal(HttpStatusCode.NotAcceptable,(await site.Client.SendAsync(accepted)).StatusCode);
        }
        foreach(var accepted in new[]{"text/html","application/xhtml+xml","*/*","text/*","application/json,text/html;q=0.5"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,"/users/me/profile");request.Headers.Accept.ParseAdd(accepted);
            Assert.Equal(HttpStatusCode.OK,(await site.Client.SendAsync(request)).StatusCode);
        }
        using(var htmlSuffix=new HttpRequestMessage(HttpMethod.Get,"/users/me/profile.html"))
        {
            htmlSuffix.Headers.Accept.ParseAdd("application/json");
            Assert.Equal(HttpStatusCode.OK,(await site.Client.SendAsync(htmlSuffix)).StatusCode);
        }
        using var update=new HttpRequestMessage(HttpMethod.Patch,"/users/me/profile.json")
        {Content=new StringContent("{\"user\":{\"bio\":\"JSON format mutation\"}}",System.Text.Encoding.UTF8,"application/json")};
        update.Headers.Accept.ParseAdd("application/json");update.Headers.Add("X-CSRF-Token",csrf);
        var response=await site.Client.SendAsync(update);
        Assert.Equal(HttpStatusCode.Found,response.StatusCode);Assert.Equal("/users/me/profile",response.Headers.Location?.OriginalString);
        Assert.Equal("JSON format mutation",site.Db.Scalar<string>("SELECT bio FROM users WHERE id=1"));
        Assert.Equal(HttpStatusCode.OK,(await site.Client.GetAsync("/users/me/profile")).StatusCode);
    }
    private sealed class CountingStore(IDataStore inner) : IDataStore
    {
        public Task<T> WriteAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection,Microsoft.Data.Sqlite.SqliteTransaction,Task<T>> operation,Action<Microsoft.Data.Sqlite.SqliteConnection,T> afterCommit,CancellationToken ct=default) { Writes++;return inner.WriteAsync(operation,afterCommit,ct); }
        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection,Microsoft.Data.Sqlite.SqliteTransaction,T> operation,Action<Microsoft.Data.Sqlite.SqliteConnection,T> afterCommit) { Writes++;return inner.Write(operation,afterCommit); }
        public Task<T> WriteAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection,Microsoft.Data.Sqlite.SqliteTransaction,Task<T>> operation,CancellationToken ct=default) { Writes++;return inner.WriteAsync(operation,ct); }
        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection,T> operation) => inner.Read(operation);
        public int Writes { get; private set; } public string DatabasePath=>inner.DatabasePath; public List<T> Query<T>(string sql,object? parameters=null)=>inner.Query<T>(sql,parameters); public T? Single<T>(string sql,object? parameters=null)=>inner.Single<T>(sql,parameters);public T Scalar<T>(string sql,object? parameters=null)=>inner.Scalar<T>(sql,parameters);public int Execute(string sql,object? parameters=null){Writes++;return inner.Execute(sql,parameters);}public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection,Microsoft.Data.Sqlite.SqliteTransaction,T> op){Writes++;return inner.Write(op);}
    }
    private sealed class Site : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public IDataStore Db => App.Services.GetRequiredService<IDataStore>();
        private readonly Dictionary<HttpClient, Dictionary<string, string>> jars = new();
        public static async Task<Site> Create(bool profileFormatSuffixes = false)
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Configuration.AddConfiguration(Config(Path.Combine(Path.GetTempPath(), "campfire-http-" + Guid.NewGuid()))); builder.Services.AddIdentityFeature(builder.Configuration); builder.Services.AddSingleton<IMediaService, Media>(); builder.Services.AddSingleton<IRealtimeEvents, Realtime>(); builder.Services.AddSingleton<IPageRenderer, TestLayout>(); builder.Services.AddSingleton<IRedisTransport, Redis>(); builder.Services.AddSingleton<IBackgroundJobs,Jobs>();
            var app = builder.Build();
            if (profileFormatSuffixes)
            {
                app.Use(async (context, next) =>
                {
                    // The production host forwards canonicalized suffixes through
                    // RailsFormat. Exercise that boundary without linking its UI.
                    foreach (var format in new[] { "json", "html" })
                        if (context.Request.Path.Value?.EndsWith("/profile." + format, StringComparison.Ordinal) == true)
                        {
                            context.Items["RailsFormat"] = format;
                            context.Request.Path = context.Request.Path.Value[..^(format.Length + 1)];
                            break;
                        }
                    await next(context);
                });
                app.UseRouting();
            }
            app.MapIdentityFeature(); await app.StartAsync(); return new Site { App = app, Client = app.GetTestClient() };
        }
        public HttpClient NewClient() => App.GetTestClient();
        public void Cookies(HttpClient client, HttpResponseMessage response)
        {
            if (!jars.TryGetValue(client, out var jar)) jars[client] = jar = new();
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies)) foreach (var cookie in cookies) { var part = cookie.Split(';')[0]; var equal = part.IndexOf('='); jar[part[..equal]] = part[(equal + 1)..]; }
            client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value)));
        }
        public async Task<string> Token(HttpClient client, HttpResponseMessage response) { Cookies(client, response); return WebUtility.HtmlDecode(Regex.Match(await response.Content.ReadAsStringAsync(), "name=\"authenticity_token\" value=\"([^\"]+)\"").Groups[1].Value); }
        public async Task<HttpResponseMessage> Send(HttpClient client, string method, string path, string token, params (string Key, string Value)[] values) { var data = values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)).Append(new("authenticity_token", token)); return await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = new FormUrlEncodedContent(data) }); }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }
    private sealed class TestLayout : IPageRenderer
    {
        public string Layout(UserRecord user,string body,string title="Campfire",string nav="",string footer="",string sidebar="",string bodyClass="") => "<html><head><title>"+WebUtility.HtmlEncode(title)+"</title></head><body>"+nav+body+footer+"</body></html>";
        public string Asset(string logical) => "/assets/"+logical;
        public string Icon(string name,int size=20) => "<img src=\""+Asset(name)+"\">";
        public string Avatar(UserRecord user) => "<img src=\"/users/"+user.Id+"/avatar\">";
    }
    private sealed class Media : IMediaService
    {
        public bool FailPrepare {get;set;} public int Completed {get;private set;} public int Disposed {get;private set;}
        public Task<IPreparedRecordUpload> SaveRecordUploadInTransactionAsync(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction t,string type,long id,string name,IFormFile file,CancellationToken ct=default)=>Prepare(c,t,type,id,name);
        public Task<IPreparedRecordUpload> SaveRecordUploadInTransactionAsync(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction t,string type,long id,string name,string attachable,CancellationToken ct=default)=>Prepare(c,t,type,id,name);
        private Task<IPreparedRecordUpload> Prepare(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction t,string type,long id,string name)
        {
            var blob=c.ExecuteScalar<long>("INSERT INTO active_storage_blobs(key,filename,content_type,byte_size,service_name,created_at) VALUES(@key,'avatar.png','image/png',4,'local',@now) RETURNING id",new {key=Guid.NewGuid().ToString(),now=RequestUser.Timestamp()},t);
            c.Execute("INSERT INTO active_storage_attachments(name,record_type,record_id,blob_id,created_at) VALUES(@name,@type,@id,@blob,@now)",new {name,type,id,blob,now=RequestUser.Timestamp()},t);
            if(FailPrepare)throw new IOException("Upload preparation failed");
            return Task.FromResult<IPreparedRecordUpload>(new Pending(this));
        }
        private sealed class Pending(Media owner) : IPreparedRecordUpload {public Task CompleteAsync(CancellationToken ct=default){owner.Completed++;return Task.CompletedTask;}public ValueTask DisposeAsync(){owner.Disposed++;return ValueTask.CompletedTask;}}
        public Task ProcessMessageAttachmentAsync(long id,CancellationToken ct=default)=>Task.CompletedTask;public List<long> Purged {get;}=[];public Task PurgeBlobsAsync(IEnumerable<long> ids){Purged.AddRange(ids);return Task.CompletedTask;}public string AttachmentHtml(long id)=>"";public Task SaveUploadAsync(long id,IFormFile f,CancellationToken ct=default)=>throw new NotSupportedException();public Task SaveRecordUploadAsync(string type,long id,string name,IFormFile f,CancellationToken ct=default)=>throw new NotSupportedException();
    }
    private sealed class Redis : IRedisTransport { public bool Enabled=>true; public bool Fail {get;set;} public List<string[]> Commands {get;}=[]; public Task<long> IntegerAsync(string[] command,CancellationToken ct=default) { if(Fail)throw new IOException("Unavailable"); Commands.Add(command);return Task.FromResult((long)Commands.Count); } public Task<string?> StringAsync(string[] command,CancellationToken ct=default)=>throw new NotSupportedException(); public Task<string?[]> ArrayAsync(string[] command,CancellationToken ct=default)=>throw new NotSupportedException(); }
    private sealed class Jobs : IBackgroundJobs
    {
        private readonly Dictionary<string,Func<JsonElement[],CancellationToken,Task>> handlers=[];private readonly List<(string Name,JsonElement[] Args)> pending=[];public bool Fail {get;set;}
        public void Register(string name,Func<JsonElement[],CancellationToken,Task> handler)=>handlers[name]=handler;
        public Task EnqueueAsync(string name,JsonNode?[] args,CancellationToken ct=default) {if(Fail)throw new IOException("Unavailable");using var json=JsonDocument.Parse(new JsonArray(args.Select(x=>x?.DeepClone()).ToArray()).ToJsonString());pending.Add((name,json.RootElement.EnumerateArray().Select(x=>x.Clone()).ToArray()));return Task.CompletedTask;}
        public async Task Drain(){foreach(var job in pending.ToArray())await handlers[job.Name](job.Args,CancellationToken.None);pending.Clear();}
    }
    private sealed class Realtime : IRealtimeEvents { public bool DisconnectFails {get;set;} public List<(long MessageId,string? ClientId)> Removed {get;}=[]; public Task MessageChangedAsync(long r, long m, string action = "append", string? clientMessageId = null) { if(action=="remove")Removed.Add((m,clientMessageId));return Task.CompletedTask; } public Task DisconnectUserAsync(long u, bool reconnect = false) => DisconnectFails?Task.FromException(new IOException("Unavailable")):Task.CompletedTask; public Task TurboStreamAsync(string s, string a, string t, string? html = null, bool scroll = false) => Task.CompletedTask; }
}










