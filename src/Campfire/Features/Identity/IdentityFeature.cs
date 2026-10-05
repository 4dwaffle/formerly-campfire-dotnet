using Campfire.Contracts;
using Campfire.Features.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Campfire.Features.Identity;

public static class IdentityFeature
{
    public static IServiceCollection AddIdentityFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDataStore, SqliteDataStore>();
        services.AddSingleton<RailsCrypto>();
        services.AddSingleton<IRailsCrypto>(s => s.GetRequiredService<RailsCrypto>());
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<LocalLoginLimiter>();
        services.AddSingleton<IdentityJobs>();
        services.AddHostedService<IdentityJobs>(s => s.GetRequiredService<IdentityJobs>());
        return services;
    }
    public static WebApplication MapIdentityFeature(this WebApplication app)
    {
        foreach (var (path, methods) in new (string, string[])[] {
            ("/session/new", ["GET", "HEAD"]), ("/session", ["POST", "DELETE"]), ("/first_run", ["GET", "HEAD", "POST"]),
            ("/join/{join_code}", ["GET", "HEAD", "POST"]), ("/account/edit", ["GET", "HEAD"]), ("/account", ["PATCH", "PUT"]),
            ("/account/users", ["GET", "HEAD"]), ("/account/users.turbo_stream", ["GET", "HEAD"]), ("/account/users/{id:long}", ["PATCH", "PUT", "DELETE"]),
            ("/account/bots", ["GET", "HEAD", "POST"]), ("/account/bots/new", ["GET", "HEAD"]), ("/account/bots/{id:long}/edit", ["GET", "HEAD"]),
            ("/account/bots/{id:long}", ["PATCH", "PUT", "DELETE"]), ("/account/bots/{id:long}/key", ["PATCH", "PUT"]), ("/account/join_code", ["POST"]),
            ("/account/custom_styles/edit", ["GET", "HEAD"]), ("/account/custom_styles", ["PATCH", "PUT"]), ("/users/{id:long}", ["GET", "HEAD"]),
            ("/users/{user_id}/profile", ["GET", "HEAD", "PATCH", "PUT"]), ("/users/{user_id:long}/ban", ["POST", "DELETE"]), ("/session/transfers/{id}", ["GET", "HEAD", "PATCH", "PUT"]) })
            app.MapMethods(path, methods, Handle).DisableAntiforgery();
        return app;
    }
    private static IResult Redirect(string path) => Results.Redirect(path);
    private static long Id(HttpContext c, string key = "id") => long.TryParse(c.Request.RouteValues[key]?.ToString(), out var id) ? id : 0;
    private static string Value(IdentityParameters parameters, string name, string? fallback = null) => parameters.Value(name, fallback) ?? "";
    private static bool IsGet(HttpContext c) => c.Request.Method is "GET" or "HEAD";
    private static bool ProfileHtmlRequested(HttpContext context)
    {
        // Profiles#show has only an HTML template. Explicit formats override
        // Accept; otherwise Rails may fall back to HTML within accepted types.
        if (context.Items["RailsFormat"] is string format) return format is "html" or "xhtml";
        var accepted = context.Request.Headers.Accept.ToString();
        if (accepted.Length == 0) return !string.Equals(context.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
        foreach (var entry in accepted.Split(','))
        {
            var mime = entry.Split(';')[0].Trim();
            if (mime.Equals("text/html", StringComparison.OrdinalIgnoreCase) || mime.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) || mime is "*/*" or "text/*") return true;
        }
        return false;
    }
    private static string JoinCode() { var s = RandomToken(12); return $"{s[..4]}-{s[4..8]}-{s[8..]}"; }
    private static string RandomToken(int count) { const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"; return new string(Enumerable.Range(0, count).Select(_ => alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()); }
    private static long CreateUser(SqliteConnection connection, SqliteTransaction transaction, string? name, string? email, string? password, int role = 0, string? botToken = null)
    {
        var now = RequestUser.Timestamp();
        var passwordDigest = string.IsNullOrEmpty(password) ? null : BCrypt.Net.BCrypt.HashPassword(password, 12);
        var id = connection.ExecuteScalar<long>("INSERT INTO users(name,email_address,password_digest,role,bot_token,created_at,updated_at,status) VALUES(@name,@email,@passwordDigest,@role,@botToken,@now,@now,0) RETURNING id", new { name, email, passwordDigest, role, botToken, now }, transaction);
        return id;
    }
    private static void GrantOpenMemberships(IDataStore db, long id) => db.Write((connection, transaction) => connection.Execute("INSERT OR IGNORE INTO memberships(room_id,user_id,created_at,updated_at) SELECT id,@id,@now,@now FROM rooms WHERE type='Rooms::Open'", new { id, now = RequestUser.Timestamp() }, transaction));
    private static Task<int> Deactivate(IDataStore db, IRealtimeEvents realtime, long id, CancellationToken cancellationToken)
    {
        return db.WriteAsync(async (c, t) =>
        {
            await realtime.DisconnectUserAsync(id);
            var email = c.ExecuteScalar<string?>("SELECT email_address FROM users WHERE id=@id", new { id }, t)?.Replace("@", "-deactivated-" + Guid.NewGuid() + "@");
            c.Execute("DELETE FROM memberships WHERE user_id=@id AND room_id IN (SELECT id FROM rooms WHERE type<>'Rooms::Direct'); DELETE FROM push_subscriptions WHERE user_id=@id; DELETE FROM searches WHERE user_id=@id; DELETE FROM sessions WHERE user_id=@id; UPDATE users SET status=1,email_address=@email,updated_at=@now WHERE id=@id", new { id, email, now = RequestUser.Timestamp() }, t);
            return 0;
        }, cancellationToken);
    }
    private static async Task<IResult> Handle(HttpContext c, IDataStore db, IAuthService auth, IRailsCrypto crypto, IMediaService media, IRealtimeEvents realtime, IPageRenderer renderer)
    {
        var html = new IdentityHtml(c, db, auth, crypto, renderer);
        var path = (c.Request.Path.Value ?? "").Replace(".turbo_stream", "", StringComparison.Ordinal);
        IdentityParameters parameters;
        try { parameters = await IdentityParameters.Read(c.Request); }
        catch (BadHttpRequestException) { return Results.BadRequest(); }
        var form = parameters.Form;
        await using var uploads = new PreparedIdentityUploads(media, parameters, c.RequestAborted);
        var user = auth.Current(c);
        var first = path == "/first_run";
        var login = path is "/session" or "/session/new";
        var join = path.StartsWith("/join/", StringComparison.Ordinal);
        var transfer = path.StartsWith("/session/transfers/", StringComparison.Ordinal);
        if (!first && !login && !join && !transfer && user == null) { auth.CaptureReturnTo(c); return Redirect("/session/new"); }
        if (c.Items.ContainsKey(AuthService.BotAuthentication)) return Results.StatusCode(403);
        if (!IsGet(c) && !auth.ValidateCsrf(c, parameters.Value("authenticity_token"))) return Results.StatusCode(422);
        try
        {
            if (first || join)
            {
                if (first && db.Read(queryConnection1 => queryConnection1.ExecuteScalar<long>("SELECT count(*) FROM accounts")) > 0 || join && user != null) return Redirect("/");
                if (join && db.Read(queryConnection2 => queryConnection2.ExecuteScalar<long>("SELECT count(*) FROM accounts WHERE join_code=@code",new { code = c.Request.RouteValues["join_code"]?.ToString() })) == 0) return Results.NotFound();
                if (IsGet(c)) return html.Signup(first);
                if (c.Request.Method != "POST") return Results.StatusCode(405);
                parameters.Require("user");
                var name = parameters.StringAttribute("user[name]"); var email = parameters.StringAttribute("user[email_address]");
                if (first) db.Write((connection, transaction) => connection.Execute("INSERT INTO accounts(name,join_code,settings,created_at,updated_at) VALUES('Campfire',@code,@settings,@now,@now)", new { code = JoinCode(), settings = "{\"restrict_room_creation_to_administrators\":false}", now = RequestUser.Timestamp() }, transaction));
                var password = parameters.Password("user[password]");
                var newId = await db.WriteAsync(async (connection, transaction) =>
                {
                    var id = CreateUser(connection, transaction, name, email, password, first ? 1 : 0);
                    if (first)
                    {
                        var now = RequestUser.Timestamp();
                        var roomId = connection.ExecuteScalar<long>("INSERT INTO rooms(name,type,creator_id,created_at,updated_at) VALUES('All Talk','Rooms::Open',@id,@now,@now) RETURNING id", new { id, now }, transaction);
                        connection.Execute("INSERT INTO memberships(room_id,user_id,created_at,updated_at) VALUES(@roomId,@id,@now,@now)", new { roomId, id, now }, transaction);
                    }
                    await uploads.Prepare(connection, transaction, "User", id, "avatar");
                    return id;
                }, c.RequestAborted);
                GrantOpenMemberships(db, newId);
                await uploads.Complete();
                auth.SignIn(c, newId); return Redirect("/");
            }
            if (login)
            {
                if (IsGet(c)) { if (db.Read(queryConnection3 => queryConnection3.ExecuteScalar<long>("SELECT count(*) FROM users")) == 0) return Redirect("/first_run"); return html.Login(); }
                if (c.Request.Method == "DELETE")
                {
                    if (user == null) return Redirect("/session/new");
                    var endpoint = Value(parameters, "push_subscription_endpoint"); if (endpoint.Length > 0) db.Write((queryConnection4,queryTransaction4) => queryConnection4.Execute("DELETE FROM push_subscriptions WHERE endpoint=@endpoint AND user_id=@id",new { endpoint, id = user.Id },transaction: queryTransaction4));
                    auth.SignOut(c);
                    try { await realtime.DisconnectUserAsync(user.Id, true); }
                    catch (Exception error) { c.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Campfire.Authentication").LogWarning(error, "Could not disconnect remote connections on sign out"); }
                    return Redirect("/");
                }
                if (c.Request.Method != "POST") return Results.StatusCode(405);
                var ip = RailsRemoteIp.Address(c) ?? "unknown";
                var redis = c.RequestServices.GetService<IRedisTransport>();
                long count;
                if (redis?.Enabled == true)
                {
                    try { count = await redis.IntegerAsync(["EVAL", "local n=redis.call('INCR',KEYS[1]); if n==1 then redis.call('EXPIRE',KEYS[1],ARGV[1]); end; return n", "1", "rate-limit:sessions:" + ip, "180"], c.RequestAborted); }
                    catch (Exception error) when (error is not OperationCanceledException) { c.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Campfire.Authentication").LogWarning(error, "Login rate-limit Redis is unavailable; the pinned Rails cache returns no counter"); count = 0; }
                }
                else
                {
                    count = c.RequestServices.GetRequiredService<IHostEnvironment>().IsProduction() ? 0 : c.RequestServices.GetRequiredService<LocalLoginLimiter>().Increment(ip);
                }
                if (count > 10) return html.Login(429);
                var email = parameters.StringAttribute("email_address"); var password = parameters.Password("password") ?? "";
                var candidate = db.Read(queryConnection5 => queryConnection5.QuerySingleOrDefault<UserRecord>("SELECT * FROM users WHERE email_address=@email AND status=0",new { email }));
                var valid = false;
                if (candidate?.PasswordDigest != null) { try { valid = BCrypt.Net.BCrypt.Verify(password, candidate.PasswordDigest); } catch (BCrypt.Net.SaltParseException) { } }
                if (!valid) return html.Login(401);
                auth.SignIn(c, candidate!.Id); return Redirect(auth.ConsumeReturnTo(c));
            }
            if (transfer)
            {
                var token = c.Request.RouteValues["id"]?.ToString() ?? "";
                if (IsGet(c)) return html.TransferPage();
                if (c.Request.Method is not ("PUT" or "PATCH")) return Results.StatusCode(405);
                var id = crypto.VerifySignedId(token, "User", "transfer");
                if (id == null || db.Read(queryConnection6 => queryConnection6.ExecuteScalar<long>("SELECT count(*) FROM users WHERE id=@id AND status=0",new { id })) == 0) return Results.BadRequest();
                auth.SignIn(c, id.Value); return Redirect(auth.ConsumeReturnTo(c));
            }
            if (path.StartsWith("/users/", StringComparison.Ordinal))
            {
                if (path.EndsWith("/profile", StringComparison.Ordinal))
                {
                    if (IsGet(c)) return ProfileHtmlRequested(c) ? html.Profile(user!) : Results.StatusCode(406);
                    if (c.Request.Method is not ("PUT" or "PATCH")) return Results.StatusCode(405);
                    parameters.Require("user");
                    var name = parameters.StringAttribute("user[name]", user!.Name); var email = parameters.StringAttribute("user[email_address]", user.EmailAddress); var bio = parameters.StringAttribute("user[bio]", user.Bio); var password = parameters.Password("user[password]") ?? "";
                    var digest = password.Length == 0 ? user.PasswordDigest : BCrypt.Net.BCrypt.HashPassword(password, 12);
                    await db.WriteAsync(async (connection,transaction) =>
                    {
                        connection.Execute("UPDATE users SET name=@name,email_address=@email,bio=@bio,password_digest=@digest,updated_at=@now WHERE id=@id AND (name IS NOT @name OR email_address IS NOT @email OR bio IS NOT @bio OR password_digest IS NOT @digest OR @attachment)",new { name, email, bio, digest, now = RequestUser.Timestamp(), id = user.Id, attachment = form.Files.GetFile("user[avatar]") != null || parameters.Has("user[avatar]") && parameters.Value("user[avatar]") != null },transaction);
                        await uploads.Prepare(connection,transaction,"User",user.Id,"avatar",compactNull:true);
                        return 0;
                    },c.RequestAborted);
                    await uploads.Complete();
                    (auth as AuthService)?.SetNotice(c, form.Files.GetFile("user[avatar]") != null || parameters.Has("user[avatar]") && parameters.Value("user[avatar]") != null ? "It may take up to 30 minutes to change everywhere." : "✓");
                    return Redirect("/users/me/profile");
                }
                var id = Id(c, path.EndsWith("/ban", StringComparison.Ordinal) ? "user_id" : "id"); var target = db.Read(queryConnection8 => queryConnection8.QuerySingleOrDefault<UserRecord>("SELECT * FROM users WHERE id=@id",new { id }));
                if (target == null) return Results.NotFound();
                if (path.EndsWith("/ban", StringComparison.Ordinal))
                {
                    if (!user!.IsAdmin) return Results.StatusCode(403);
                    if (c.Request.Method is not ("POST" or "DELETE")) return Results.StatusCode(405);
                    var ban = c.Request.Method == "POST";
                    var jobs = c.RequestServices.GetService<IBackgroundJobs>();
                    if (ban && jobs == null && c.RequestServices.GetRequiredService<IHostEnvironment>().IsProduction()) return Results.StatusCode(503);
                    await db.WriteAsync(async (connection, transaction) =>
                    {
                        var now = RequestUser.Timestamp();
                        if (ban)
                        {
                            var addresses = connection.Query<string?>("SELECT DISTINCT ip_address FROM sessions WHERE user_id=@id", new { id }, transaction).Materialize();
                            foreach (var address in addresses.Where(address => !string.IsNullOrWhiteSpace(address)))
                                connection.Execute("INSERT INTO bans(user_id,ip_address,created_at,updated_at) VALUES(@id,@address,@now,@now)", new { id, address, now }, transaction);
                            await realtime.DisconnectUserAsync(id);
                            connection.Execute("DELETE FROM sessions WHERE user_id=@id", new { id }, transaction);
                            if (jobs != null) await jobs.EnqueueAsync("RemoveBannedContentJob", [new System.Text.Json.Nodes.JsonObject { ["_aj_globalid"] = "gid://campfire/User/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) }], c.RequestAborted);
                        }
                        else connection.Execute("DELETE FROM bans WHERE user_id=@id", new { id }, transaction);
                        connection.Execute("UPDATE users SET status=@status,updated_at=@now WHERE id=@id", new { id, status = ban ? 2 : 0, now }, transaction);
                        return 0;
                    }, c.RequestAborted);
                    if (ban && jobs == null) await c.RequestServices.GetRequiredService<IdentityJobs>().RemoveUserContent(id, c.RequestAborted);
                    return Redirect($"/users/{id}");
                }
                if (!IsGet(c)) return Results.StatusCode(405);
                return html.User(target, user!);
            }
            if (path == "/account/edit" || path == "/account")
            {
                if (IsGet(c)) return html.Account(user!);
                if (!user!.IsAdmin) return Results.StatusCode(403);
                if (c.Request.Method is not ("PUT" or "PATCH")) return Results.StatusCode(405);
                parameters.Require("account");
                var settings = db.Read(queryConnection9 => queryConnection9.QuerySingleOrDefault<string>("SELECT settings FROM accounts LIMIT 1"));
                var obj = string.IsNullOrWhiteSpace(settings) ? new System.Text.Json.Nodes.JsonObject() : System.Text.Json.Nodes.JsonNode.Parse(settings) as System.Text.Json.Nodes.JsonObject ?? new();
                var submittedSettings = parameters.Settings();
                foreach (var pair in submittedSettings) obj[pair.Key] = IdentityParameters.Boolean(pair.Value);
                var updatedSettings = submittedSettings.Count == 0 ? settings : obj.ToJsonString();
                var accountName = parameters.Has("account[name]") ? parameters.StringAttribute("account[name]") : db.Read(queryConnection10 => queryConnection10.ExecuteScalar<string>("SELECT name FROM accounts LIMIT 1"));
                await db.WriteAsync(async (connection,transaction) =>
                {
                    connection.Execute("UPDATE accounts SET name=@name,settings=@settings,updated_at=@now WHERE name IS NOT @name OR settings IS NOT @settings OR @attachment",new { name = accountName, settings = updatedSettings, now = RequestUser.Timestamp(), attachment = form.Files.GetFile("account[logo]") != null || parameters.Has("account[logo]") },transaction);
                    var accountId = connection.ExecuteScalar<long>("SELECT id FROM accounts LIMIT 1",transaction:transaction);
                    await uploads.Prepare(connection,transaction,"Account",accountId,"logo");
                    return 0;
                },c.RequestAborted);
                await uploads.Complete();
                (auth as AuthService)?.SetNotice(c, "✓");
                return Redirect("/account/edit");
            }
            if (path == "/account/users" && IsGet(c)) return c.Request.Path.Value!.EndsWith(".turbo_stream", StringComparison.Ordinal) || c.Items["RailsFormat"]?.ToString() == "turbo_stream" || c.Request.Query["format"] == "turbo_stream" || c.Request.Headers.Accept.ToString().Contains("text/vnd.turbo-stream.html", StringComparison.Ordinal) ? html.AccountUsersResponse(user!) : Results.StatusCode(406);
            if (!user!.IsAdmin) return Results.StatusCode(403);
            if (path == "/account/join_code") { if (c.Request.Method != "POST") return Results.StatusCode(405); db.Write((queryConnection13,queryTransaction13) => queryConnection13.Execute("UPDATE accounts SET join_code=@code,updated_at=@now",new { code = JoinCode(), now = RequestUser.Timestamp() },transaction: queryTransaction13)); return Redirect("/account/edit"); }
            if (path.StartsWith("/account/custom_styles", StringComparison.Ordinal))
            {
                if (IsGet(c)) return html.CustomStyles();
                if (c.Request.Method is not ("PUT" or "PATCH")) return Results.StatusCode(405);
                parameters.Require("account");
                if (parameters.Has("account[custom_styles]")) db.Write((queryConnection14,queryTransaction14) => queryConnection14.Execute("UPDATE accounts SET custom_styles=@styles,updated_at=@now WHERE custom_styles IS NOT @styles",new { styles = parameters.StringAttribute("account[custom_styles]"), now = RequestUser.Timestamp() },transaction: queryTransaction14)); (auth as AuthService)?.SetNotice(c, "✓"); return Redirect("/account/custom_styles/edit");
            }
            if (path.StartsWith("/account/users/", StringComparison.Ordinal))
            {
                var id = Id(c); if (db.Read(queryConnection15 => queryConnection15.ExecuteScalar<long>("SELECT count(*) FROM users WHERE id=@id AND status=0",new { id })) == 0) return Results.NotFound();
                if (c.Request.Method == "DELETE") await Deactivate(db, realtime, id, c.RequestAborted);
                else if (c.Request.Method is "PATCH" or "PUT") { parameters.Require("user"); db.Write((queryConnection16,queryTransaction16) => queryConnection16.Execute("UPDATE users SET role=@role,updated_at=@now WHERE id=@id AND role<>@role",new { id, role = parameters.Value("user[role]") == "administrator" ? 1 : 0, now = RequestUser.Timestamp() },transaction: queryTransaction16)); }
                else return Results.StatusCode(405);
                return Redirect("/account/edit");
            }
            if (path.StartsWith("/account/bots", StringComparison.Ordinal)) return await Bots(c, db, auth, crypto, media, realtime, renderer, parameters);
            return Results.NotFound();
        }
        catch (BadHttpRequestException) { return Results.BadRequest(); }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode is 2067 or 1555)
        {
            if (first) return Redirect("/");
            if (join) return Redirect("/session/new?email_address=" + Uri.EscapeDataString(Value(parameters, "user[email_address]")));
            throw;
        }
    }
    private static async Task<IResult> Bots(HttpContext c, IDataStore db, IAuthService auth, IRailsCrypto crypto, IMediaService media, IRealtimeEvents realtime, IPageRenderer renderer, IdentityParameters parameters)
    {
        var html = new IdentityHtml(c, db, auth, crypto, renderer);
        var form = parameters.Form;
        await using var uploads = new PreparedIdentityUploads(media, parameters, c.RequestAborted);
        var path = c.Request.Path.Value!; var id = Id(c);
        UserRecord? bot = id == 0 ? null : db.Read(queryConnection17 => queryConnection17.QuerySingleOrDefault<UserRecord>("SELECT * FROM users WHERE id=@id AND role=2 AND status=0",new { id }));
        if (id != 0 && bot == null) return Results.NotFound();
        if (IsGet(c)) return path == "/account/bots" ? html.Bots() : html.BotForm(bot);
        if (path.EndsWith("/key", StringComparison.Ordinal)) { if (c.Request.Method is not ("PUT" or "PATCH")) return Results.StatusCode(405); db.Write((queryConnection18,queryTransaction18) => queryConnection18.Execute("UPDATE users SET bot_token=@token,updated_at=@now WHERE id=@id",new { id, token = RandomToken(12), now = RequestUser.Timestamp() },transaction: queryTransaction18)); }
        else if (c.Request.Method == "DELETE") { if (bot == null) return Results.NotFound(); await Deactivate(db, realtime, id, c.RequestAborted); }
        else if (c.Request.Method is "POST" or "PUT" or "PATCH")
        {
            if (id == 0 && c.Request.Method != "POST" || id != 0 && c.Request.Method == "POST") return Results.StatusCode(405);
            parameters.Require("user");
            var name = parameters.Has("user[name]") ? parameters.StringAttribute("user[name]") : bot?.Name; var webhook = parameters.StringAttribute("user[webhook_url]");
            var created = id == 0;
            if (created)
            {
                id = await db.WriteAsync(async (connection, transaction) =>
                {
                    var newId = CreateUser(connection, transaction, name, null, null, 2, RandomToken(12));
                    await uploads.Prepare(connection,transaction,"User",newId,"avatar");
                    return newId;
                },c.RequestAborted);
                GrantOpenMemberships(db, id);
                await uploads.Complete();
            }
            id = await db.WriteAsync(async (connection, transaction) =>
            {
                var now = RequestUser.Timestamp(); var userId = id;
                if (!created) connection.Execute("UPDATE users SET name=@name,updated_at=@now WHERE id=@id AND (name IS NOT @name OR @attachment)", new { name, now, id, attachment = form.Files.GetFile("user[avatar]") != null || parameters.Has("user[avatar]") }, transaction);
                if (!created && (string.IsNullOrWhiteSpace(webhook) || parameters.IsFalse("user[webhook_url]"))) connection.Execute("DELETE FROM webhooks WHERE user_id=@userId", new { userId }, transaction);
                else if (webhook != null && !parameters.IsFalse("user[webhook_url]"))
                {
                    connection.Execute("UPDATE webhooks SET url=@webhook,updated_at=@now WHERE user_id=@userId AND url IS NOT @webhook", new { userId, webhook, now }, transaction);
                    connection.Execute("INSERT INTO webhooks(user_id,url,created_at,updated_at) SELECT @userId,@webhook,@now,@now WHERE NOT EXISTS(SELECT 1 FROM webhooks WHERE user_id=@userId)", new { userId, webhook, now }, transaction);
                }
                if (!created) await uploads.Prepare(connection,transaction,"User",id,"avatar");
                return userId;
            },c.RequestAborted);
            if (!created) await uploads.Complete();
        }
        else return Results.StatusCode(405);
        return Redirect("/account/bots");
    }
    internal sealed class RemovedMessage { public long RoomId { get; set; } public long MessageId { get; set; } public string ClientMessageId { get; set; } = ""; }
}





