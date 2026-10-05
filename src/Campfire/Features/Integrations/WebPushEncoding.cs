using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Campfire.Features.Integrations;

public static class WebPushEncoding
{
    public static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    public static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static ECParameters Point(byte[] point)
    {
        if (point.Length != 65 || point[0] != 4) throw new ArgumentException("Invalid P-256 public key");
        return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] } };
    }
    public static bool ValidKeys(string? p256dh, string? auth)
    {
        try { using var key = ECDiffieHellman.Create(Point(Decode(p256dh ?? ""))); return Decode(auth ?? "").Length == 16; }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException) { return false; }
    }
    public static byte[] Encrypt(string payload, string p256dh, string auth)
    {
        var client = Decode(p256dh);
        using var receiver = ECDiffieHellman.Create(Point(client));
        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var senderPoint = sender.ExportParameters(false).Q;
        var publicKey = new byte[] { 4 }.Concat(senderPoint.X!).Concat(senderPoint.Y!).ToArray();
        var shared = sender.DeriveRawSecretAgreement(receiver.PublicKey);
        var info = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(client).Concat(publicKey).ToArray();
        var secret = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Decode(auth), info);
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        if (string.IsNullOrEmpty(payload)) throw new ArgumentException("message cannot be blank");
        var plain = Encoding.UTF8.GetBytes(payload).Concat(new byte[] { 2, 0 }).ToArray();
        if (plain.Length + 16 > 4096) throw new ArgumentException("encrypted payload is too big");
        var output = new byte[86 + plain.Length + 16];
        salt.CopyTo(output, 0); BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16, 4), (uint)(plain.Length + 16)); output[20] = 65; publicKey.CopyTo(output, 21);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, output.AsSpan(86, plain.Length), output.AsSpan(86 + plain.Length, 16));
        return output;
    }
    public static string Authorization(Uri endpoint, string privateKey, string publicKey, string subject)
    {
        var parameters = Point(Decode(publicKey)); parameters.D = Decode(privateKey);
        using var signing = ECDsa.Create(parameters);
        var header = Encode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new VapidClaims(endpoint.Scheme + "://" + endpoint.Host, DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(), subject), IntegrationsJson.Default.VapidClaims));
        var unsigned = header + "." + claims;
        var signature = signing.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={unsigned}.{Encode(signature)},k={publicKey}";
    }
}
