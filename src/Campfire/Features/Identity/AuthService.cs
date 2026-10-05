using Dapper;
using Campfire.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Globalization;

namespace Campfire.Features.Identity;

public sealed class AuthService(IDataStore db, RailsCrypto crypto) : IAuthService
{
    private const string UserCache = "campfire.current_user";
    private const string SessionCache = "campfire.browser_session";
    private static readonly object globalCsrfKey = new();
    private sealed record GlobalCsrf(JsonObject Session, JsonNode? Stored, byte[] Hash);
    public const string BotAuthentication = "campfire.authentication.bot_key";
    private static CookieOptions Options(HttpContext context) => new() { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps, Expires = DateTimeOffset.UtcNow.AddYears(20) };
    public UserRecord? Current(HttpContext context)
    {
        if (context.Items.TryGetValue(UserCache, out var cached)) return cached as UserRecord;
        var path = context.Request.Path.Value ?? "";
        var publicController = path is "/first_run" or "/session/new" || path == "/session" && context.Request.Method == "POST" || path.StartsWith("/session/transfers/", StringComparison.Ordinal);
        if (publicController) { context.Items[UserCache] = null; return null; }
        UserRecord? user = null;
        if (context.Request.Cookies.TryGetValue("session_token", out var raw))
        {
            var token = crypto.VerifyCookie("session_token", Uri.UnescapeDataString(raw));
            if (token is not null)
            {
                var sessionUser = db.Read(queryConnection1 => queryConnection1.QuerySingleOrDefault<SessionUser>("SELECT u.*,s.last_active_at FROM users u JOIN sessions s ON s.user_id=u.id WHERE s.token=@token",new { token }));
                user = sessionUser;
                if (user != null) context.Response.Cookies.Append("session_token", crypto.SignCookie("session_token", token, DateTimeOffset.UtcNow.AddYears(20)), Options(context));
                if (sessionUser is not null && DateTime.TryParse(sessionUser.LastActiveAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var lastActive) && lastActive < DateTime.UtcNow.AddHours(-1))
                {
                    db.Write((queryConnection2,queryTransaction2) => queryConnection2.Execute("UPDATE sessions SET last_active_at=@now,updated_at=@now,ip_address=@ip,user_agent=@agent WHERE token=@token AND last_active_at<@threshold",new { token, now = RequestUser.Timestamp(), threshold = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), ip = RailsRemoteIp.Address(context), agent = context.Request.Headers.UserAgent.ToString() },transaction: queryTransaction2));
                }
            }
        }
        var suppliedKey = context.Request.RouteValues["bot_key"]?.ToString() ?? context.Request.Query["bot_key"].FirstOrDefault();
        if (user == null && !path.StartsWith("/join/", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(suppliedKey))
        {
            var parts = suppliedKey.Trim().Split('-');
            if (parts.Length > 1 && long.TryParse(parts[0], out var id)) user = db.Read(queryConnection3 => queryConnection3.QuerySingleOrDefault<UserRecord>("SELECT * FROM users WHERE id=@id AND bot_token=@token AND role=2 AND status=0",new { id, token = parts[1] }));
            if (user != null) context.Items[BotAuthentication] = true;
        }
        context.Items[UserCache] = user;
        return user;
    }
    internal sealed class SessionUser
    {
        public long Id { get; set; } public string Name { get; set; } = ""; public string? EmailAddress { get; set; } public string? PasswordDigest { get; set; } public string? Bio { get; set; } public string? BotToken { get; set; } public int Role { get; set; } public int Status { get; set; } public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; public string LastActiveAt { get; set; } = "";
        public static implicit operator UserRecord?(SessionUser? value) => value == null ? null : new UserRecord { Id=value.Id,Name=value.Name,EmailAddress=value.EmailAddress,PasswordDigest=value.PasswordDigest,Bio=value.Bio,BotToken=value.BotToken,Role=value.Role,Status=value.Status,CreatedAt=value.CreatedAt,UpdatedAt=value.UpdatedAt };
    }
    private JsonObject Session(HttpContext context)
    {
        if (context.Items.TryGetValue(SessionCache, out var item) && item is JsonObject cached) return cached;
        var data = context.Request.Cookies.TryGetValue("_campfire_session", out var raw) ? crypto.DecryptCookie("_campfire_session", Uri.UnescapeDataString(raw)) : null;
        data ??= new JsonObject { ["session_id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() };
        context.Items[SessionCache] = data;
        return data;
    }
    private void SaveSession(HttpContext context, JsonObject session) => context.Response.Cookies.Append("_campfire_session", crypto.EncryptCookie("_campfire_session", session, DateTimeOffset.UtcNow.AddYears(20)), Options(context));
    private byte[] RealToken(HttpContext context)
    {
        var session = Session(context);
        try { if (session["_csrf_token"] is JsonNode token) { var bytes = RailsCrypto.Decode64(token.GetValue<string>()); if (bytes.Length == 32) return bytes; } } catch (Exception e) when (e is FormatException or InvalidOperationException) { }
        var real = RandomNumberGenerator.GetBytes(32);
        session["_csrf_token"] = Convert.ToBase64String(real).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        SaveSession(context, session);
        return real;
    }
    public string CsrfToken(HttpContext context)
    {
        var session = Session(context);
        byte[] global;
        if (context.Items.TryGetValue(globalCsrfKey, out var item) && item is GlobalCsrf cached &&
            ReferenceEquals(cached.Session, session) && ReferenceEquals(cached.Stored, session["_csrf_token"]))
            global = cached.Hash;
        else
        {
            global = HMACSHA256.HashData(RealToken(context), "!real_csrf_token"u8);
            context.Items[globalCsrfKey] = new GlobalCsrf(session, session["_csrf_token"], global);
        }
        // Only the unmasked derivation is request-local. Each returned mask is
        // fresh, and replacing/resetting the session invalidates the derivation.
        var mask = RandomNumberGenerator.GetBytes(32); var masked = new byte[64];
        mask.CopyTo(masked, 0); for (var i = 0; i < 32; i++) masked[i + 32] = (byte)(mask[i] ^ global[i]);
        return Convert.ToBase64String(masked).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    public bool ValidateCsrf(HttpContext context, string? formToken = null)
    {
        Current(context);
        if (context.Items.ContainsKey(BotAuthentication)) return true;
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !string.Equals(uri.GetLeftPart(UriPartial.Authority), context.Request.Scheme + "://" + context.Request.Host, StringComparison.OrdinalIgnoreCase))) return false;
        return ValidCsrfToken(context, formToken) || ValidCsrfToken(context, context.Request.Headers["X-CSRF-Token"].FirstOrDefault());
    }
    private bool ValidCsrfToken(HttpContext context, string? provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        var session = Session(context);
        if (session["_csrf_token"] is not JsonNode stored) return false;
        try
        {
            var real = RailsCrypto.Decode64(stored.GetValue<string>()); if (real.Length != 32) return false;
            var decoded = RailsCrypto.Decode64(provided);
            if (decoded.Length == 32) return CryptographicOperations.FixedTimeEquals(decoded, real);
            if (decoded.Length == 64) { var result = new byte[32]; for (var i = 0; i < 32; i++) result[i] = (byte)(decoded[i] ^ decoded[i + 32]); decoded = result; }
            if (decoded.Length != 32) return false;
            var global = HMACSHA256.HashData(real, Encoding.UTF8.GetBytes("!real_csrf_token"));
            var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
            var perForm = HMACSHA256.HashData(real, Encoding.UTF8.GetBytes(path + "#" + context.Request.Method.ToLowerInvariant()));
            return CryptographicOperations.FixedTimeEquals(decoded, real) || CryptographicOperations.FixedTimeEquals(decoded, global) || CryptographicOperations.FixedTimeEquals(decoded, perForm);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException) { return false; }
    }
    public void CaptureReturnTo(HttpContext context)
    {
        var session = Session(context);
        session["return_to_after_authenticating"] = context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        SaveSession(context, session);
    }
    public string ConsumeReturnTo(HttpContext context)
    {
        var session = Session(context);
        var target = session["return_to_after_authenticating"]?.ToString();
        if (target != null) { session.Remove("return_to_after_authenticating"); SaveSession(context, session); }
        if (target is not null && target.StartsWith('/') && !target.StartsWith("//", StringComparison.Ordinal) && !target.StartsWith("/\\", StringComparison.Ordinal)) return target;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) == context.Request.Scheme + "://" + context.Request.Host) return uri.PathAndQuery;
        return "/";
    }
    public void SetNotice(HttpContext context, string notice)
    {
        var session = Session(context); session["flash"] = new JsonObject { ["discard"] = new JsonArray(), ["flashes"] = new JsonObject { ["notice"] = notice } }; SaveSession(context, session);
    }
    public string? ConsumeNotice(HttpContext context)
    {
        var session = Session(context); var notice = (session["flash"] as JsonObject)?["flashes"]?["notice"]?.ToString();
        if (notice != null) { session.Remove("flash"); SaveSession(context, session); }
        return notice;
    }
    public void SignIn(HttpContext context, long userId)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var token = new string(Enumerable.Range(0, 24).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()); var now = RequestUser.Timestamp();
        db.Write((queryConnection4,queryTransaction4) => queryConnection4.Execute("INSERT INTO sessions(user_id,token,created_at,updated_at,last_active_at,ip_address,user_agent) VALUES(@userId,@token,@now,@now,@now,@ip,@agent)",new { userId, token, now, ip = RailsRemoteIp.Address(context), agent = context.Request.Headers.UserAgent.ToString() },transaction: queryTransaction4));
        context.Response.Cookies.Append("session_token", crypto.SignCookie("session_token", token, DateTimeOffset.UtcNow.AddYears(20)), Options(context));
        context.Items.Remove(UserCache);
    }
    public void SignOut(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue("session_token", out var raw)) { var token = crypto.VerifyCookie("session_token", Uri.UnescapeDataString(raw)); if (token != null) db.Write((queryConnection5,queryTransaction5) => queryConnection5.Execute("DELETE FROM sessions WHERE token=@token",new { token },transaction: queryTransaction5)); }
        context.Response.Cookies.Delete("session_token", new CookieOptions { Path = "/" });
        var reset = new JsonObject { ["session_id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() };
        context.Items[SessionCache] = reset;
        SaveSession(context, reset);
        context.Items[UserCache] = null; context.Items.Remove(BotAuthentication);
    }
}






