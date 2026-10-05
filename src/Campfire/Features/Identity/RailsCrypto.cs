using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;
using Campfire.Contracts;

namespace Campfire.Features.Identity;

public sealed class RailsCrypto : IRailsCrypto
{
    private readonly string secret;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> keys = new();
    public RailsCrypto(IConfiguration configuration)
    {
        secret = configuration["SECRET_KEY_BASE"] ?? throw new InvalidOperationException("SECRET_KEY_BASE is required to preserve Rails cookie compatibility.");
        if (secret.Length == 0) throw new InvalidOperationException("SECRET_KEY_BASE must not be empty.");
    }
    private byte[] Key(string salt, int length = 64) => keys.GetOrAdd(salt + ":" + length, _ => Rfc2898DeriveBytes.Pbkdf2(secret, Encoding.UTF8.GetBytes(salt), 1000, HashAlgorithmName.SHA256, length));
    private static string Json(string? value) => NormalizeJson(JsonSerializer.Serialize(value, IdentityJsonContext.Relaxed.String));
    private static string Json(long value) => NormalizeJson(JsonSerializer.Serialize(value, IdentityJsonContext.Relaxed.Int64));
    private static string NormalizeJson(string serialized)
    {
        if (!serialized.Contains("\\u2028", StringComparison.Ordinal) && !serialized.Contains("\\u2029", StringComparison.Ordinal)) return serialized;
        var output = new StringBuilder();
        for (var i = 0; i < serialized.Length; i++)
        {
            if (serialized[i] == '\\' && i + 1 < serialized.Length)
            {
                if (i + 5 < serialized.Length && (serialized.Substring(i, 6) == "\\u2028" || serialized.Substring(i, 6) == "\\u2029")) { output.Append(serialized[i + 5] == '8' ? '\u2028' : '\u2029'); i += 5; }
                else { output.Append(serialized[i]); output.Append(serialized[++i]); }
            }
            else output.Append(serialized[i]);
        }
        return output.ToString();
    }
    private static string RailsJson(string? value) => Json(value).Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026");
    private static string? StringValue(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static string B64(byte[] value, bool url = false) => url ? Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_') : Convert.ToBase64String(value);
    public static byte[] Decode64(string value) { value = value.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')); }
    private string Generate(string json, string salt, string purpose, bool sha256, bool url, DateTimeOffset? expires = null, bool legacy = false, bool urlPadded = false)
    {
        string payload;
        if (legacy) payload = "{\"_rails\":{\"message\":" + Json(B64(Encoding.UTF8.GetBytes(json))) + ",\"exp\":" + Json(expires?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)) + ",\"pur\":" + Json(purpose) + "}}";
        else payload = string.IsNullOrEmpty(purpose) && expires == null ? json : "{\"_rails\":{\"data\":" + json + (expires == null ? "" : ",\"exp\":" + Json(expires.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))) + (purpose.Length == 0 ? "" : ",\"pur\":" + Json(purpose)) + "}}";
        var encoded = B64(Encoding.UTF8.GetBytes(payload), url);
        if (urlPadded) encoded = encoded.Replace('+', '-').Replace('/', '_');
        var mac = sha256 ? HMACSHA256.HashData(Key(salt), Encoding.UTF8.GetBytes(encoded)) : HMACSHA1.HashData(Key(salt), Encoding.UTF8.GetBytes(encoded));
        return encoded + "--" + Convert.ToHexString(mac).ToLowerInvariant();
    }
    private static JsonNode? Unpack(byte[] bytes, string purpose, bool allowPlain = false, DateTimeOffset? now = null, bool allowMarshal = false)
    {
        var value = LoadValue(bytes, allowMarshal);
        if (value is JsonObject obj && obj["_rails"] is JsonObject metadata)
        {
            if (allowPlain && metadata["message"] == null) return value;
            var actualPurpose = metadata["pur"]?.GetValue<string>() ?? "";
            if (actualPurpose != purpose && !(allowPlain && actualPurpose.Length == 0)) return null;
            if (metadata["exp"] is JsonNode exp && (!DateTimeOffset.TryParse(exp.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry) || expiry <= (now ?? DateTimeOffset.UtcNow))) return null;
            return metadata["message"] is JsonNode message ? LoadValue(Decode64(message.GetValue<string>()), allowMarshal) : metadata["data"]?.DeepClone();
        }
        return purpose.Length == 0 || allowPlain ? value : null;
    }
    // Only Ruby Marshal strings are accepted for old SGIDs/app verifiers. Never deserialize objects.
    private static JsonNode? LoadValue(byte[] bytes, bool allowMarshal)
    {
        if (bytes.Length >= 2 && bytes[0] == 4 && bytes[1] == 8)
        {
            if (!allowMarshal) return null;
            var index = 2;
            if (index < bytes.Length && bytes[index] == (byte)'I') index++;
            if (index + 1 >= bytes.Length || bytes[index++] != (byte)'"') return null;
            var first = (sbyte)bytes[index++]; long length;
            if (first == 0) length = 0;
            else if (first is >= 1 and <= 4)
            {
                if (index + first > bytes.Length) return null;
                length = 0; for (var i = 0; i < first; i++) length |= (long)bytes[index++] << (i * 8);
            }
            else length = first > 4 ? first - 5 : first + 5;
            if (length < 0 || length > bytes.Length - index) return null;
            return JsonValue.Create(Encoding.UTF8.GetString(bytes, index, (int)length));
        }
        return JsonNode.Parse(bytes);
    }
    private JsonNode? Read(string token, string salt, string purpose, bool sha256, bool allowPlain = false, DateTimeOffset? now = null)
    {
        try
        {
            var length = sha256 ? 64 : 40;
            var split = token.Length - length - 2;
            if (split <= 0 || token.Substring(split, 2) != "--") return null;
            var encoded = token[..split];
            var mac = sha256 ? HMACSHA256.HashData(Key(salt), Encoding.UTF8.GetBytes(encoded)) : HMACSHA1.HashData(Key(salt), Encoding.UTF8.GetBytes(encoded));
            if (!CryptographicOperations.FixedTimeEquals(mac, Convert.FromHexString(token[(split + 2)..]))) return null;
            return Unpack(Decode64(encoded), purpose, allowPlain, now, salt != "signed cookie" && !sha256);
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or ArgumentException) { return null; }
    }
    public string Sign(string value, string purpose = "") => Generate(Json(value), "signed cookie", purpose, false, false);
    public string? Verify(string value, string purpose = "") => StringValue(Read(value, "signed cookie", purpose, false));
    public string SignCookie(string name, string value, DateTimeOffset? expires = null) => Generate(RailsJson(value), "signed cookie", "cookie." + name, false, false, expires, true);
    public string? VerifyCookie(string name, string value, DateTimeOffset? now = null) => StringValue(Read(value, "signed cookie", "cookie." + name, false, true, now));
    public string SignedId(string model, long id, string purpose) => Generate(Json(id), "active_record/signed_id", Purpose(model, purpose), true, true, purpose == "transfer" ? DateTimeOffset.UtcNow.AddHours(4) : null);
    public long? VerifySignedId(string token, string model, string purpose) => VerifySignedIdAt(token, model, purpose, DateTimeOffset.UtcNow);
    public long? VerifySignedIdAt(string token, string model, string purpose, DateTimeOffset now)
    {
        var value = Read(token, "active_record/signed_id", Purpose(model, purpose), true, now: now) ?? Read(token, "active_record/signed_id", Purpose(model, purpose), false, now: now);
        return value is null ? null : long.TryParse(value.ToString(), out var id) ? id : null;
    }
    private static string Purpose(string model, string purpose)
    {
        model = Regex.Replace(Regex.Replace(model.Replace("::", "/"), "([A-Z]+)([A-Z][a-z])", "$1_$2"), "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
        return purpose.Length == 0 ? model : model + "/" + purpose;
    }
    public string SignStream(string stream) => Generate(Json(stream), "turbo/signed_stream_verifier_key", "", true, false);
    public string? VerifyStream(string token) => StringValue(Read(token, "turbo/signed_stream_verifier_key", "", true));
    public string SignStorage(string rawJson, string purpose, DateTimeOffset? expires = null)
    {
        using var parsed = JsonDocument.Parse(rawJson);
        return Generate(parsed.RootElement.GetRawText(), "ActiveStorage", purpose, false, false, expires);
    }
    public string? VerifyStorage(string token, string purpose) => Read(token, "ActiveStorage", purpose, false)?.ToJsonString();
    public string SignedGlobalId(string model, long id, string purpose = "attachable")
    {
        var value = "gid://campfire/" + model + "/" + id + (purpose == "attachable" ? "?expires_in" : "");
        return Generate(Json(value), "signed_global_ids", purpose, false, false, urlPadded: true);
    }
    public long? VerifySignedGlobalId(string token, string model, string purpose = "attachable") => VerifySignedGlobalIdAt(token, model, purpose, DateTimeOffset.UtcNow);
    public long? VerifySignedGlobalIdAt(string token, string model, string purpose, DateTimeOffset now)
    {
        var value = Read(token, "signed_global_ids", purpose, false, now: now);
        if (value == null && Read(token, "signed_global_ids", "", false, now: now) is JsonObject metadata)
        {
            if (StringValue(metadata["purpose"]) != purpose) return null;
            if (metadata["expires_at"] is JsonNode expires && (!DateTimeOffset.TryParse(StringValue(expires), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry) || now > expiry)) return null;
            value = metadata["gid"];
        }
        var uri = StringValue(value);
        if (uri == null) return null;
        if (!uri.StartsWith("gid://", StringComparison.Ordinal)) { try { uri = Encoding.UTF8.GetString(Decode64(uri)); } catch (FormatException) { return null; } }
        var prefix = "gid://campfire/" + model + "/";
        return uri.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(uri[prefix.Length..].Split('?')[0], out var id) ? id : null;
    }
    public string EncryptCookie(string name, JsonObject value, DateTimeOffset? expires = null)
    {
        var payload = Encoding.UTF8.GetBytes("{\"_rails\":{\"message\":" + Json(B64(Encoding.UTF8.GetBytes(value.ToJsonString()))) + ",\"exp\":" + Json(expires?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)) + ",\"pur\":" + Json("cookie." + name) + "}}");
        var nonce = RandomNumberGenerator.GetBytes(12); var encrypted = new byte[payload.Length]; var tag = new byte[16];
        using var aes = new AesGcm(Key("authenticated encrypted cookie", 32), 16);
        aes.Encrypt(nonce, payload, encrypted, tag);
        return B64(encrypted) + "--" + B64(nonce) + "--" + B64(tag);
    }
    public JsonObject? DecryptCookie(string name, string value, DateTimeOffset? now = null) => DecryptCookieValue(name, value, now) as JsonObject;
    public JsonNode? DecryptCookieValue(string name, string value, DateTimeOffset? now = null)
    {
        try
        {
            var parts = value.Split("--"); if (parts.Length != 3) return null;
            var encrypted = Decode64(parts[0]); var output = new byte[encrypted.Length];
            using var aes = new AesGcm(Key("authenticated encrypted cookie", 32), 16);
            aes.Decrypt(Decode64(parts[1]), encrypted, Decode64(parts[2]), output);
            return Unpack(output, "cookie." + name, true, now);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException or ArgumentException or InvalidOperationException) { return null; }
    }
}






