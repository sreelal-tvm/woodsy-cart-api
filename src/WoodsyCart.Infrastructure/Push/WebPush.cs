using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoodsyCart.Infrastructure.Push;

/// <summary>The device a notification goes to: the browser's endpoint plus its public key and secret (all from the browser).</summary>
public record PushTarget(string Endpoint, string P256dh, string Auth);

/// <summary>Result of one send. Gone = the browser no longer has this subscription (remove it).</summary>
public record PushResult(bool Ok, bool Gone, string? Error);

/// <summary>
/// Standard Web Push with no outside service: the message is encrypted for the device (RFC 8291, aes128gcm) and signed with
/// the server's key (VAPID, RFC 8292), then posted to the browser's push service (Google, Apple, Mozilla or Microsoft).
/// </summary>
public class WebPushClient(IHttpClientFactory http)
{
    /// <summary>A new server key pair: (public = 65-byte point, private = 32-byte scalar), both base64url.</summary>
    public static (string Public, string Private) NewKeys()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = k.ExportParameters(true);
        return (B64.Encode([0x04, .. p.Q.X!, .. p.Q.Y!]), B64.Encode(p.D!));
    }

    public async Task<PushResult> SendAsync(PushTarget to, object payload, string publicKey, string privateKey, string subject, CancellationToken ct = default)
    {
        byte[] body;
        try { body = Encrypt(JsonSerializer.SerializeToUtf8Bytes(payload), B64.Decode(to.P256dh), B64.Decode(to.Auth)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return new(false, true, "The device's keys are not valid."); }

        var endpoint = new Uri(to.Endpoint);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("aes128gcm");
        req.Headers.Add("TTL", "86400");      // keep for a day if the phone is offline
        req.Headers.Add("Urgency", "high");   // new orders: show straight away
        req.Headers.Authorization = new AuthenticationHeaderValue("vapid",
            $"t={VapidToken(endpoint.GetLeftPart(UriPartial.Authority), subject, publicKey, privateKey)}, k={publicKey}");

        try
        {
            using var res = await http.CreateClient("push").SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return new(true, false, null);
            var text = await res.Content.ReadAsStringAsync(ct);
            var gone = res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;
            return new(false, gone, $"{(int)res.StatusCode} {res.ReasonPhrase}: {Trim(text)}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new(false, false, e.Message); }
    }

    /// <summary>Signed token saying this server sends to that push service (valid 12 hours).</summary>
    private static string VapidToken(string audience, string subject, string publicKey, string privateKey)
    {
        var pub = B64.Decode(publicKey);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, D = B64.Decode(privateKey),
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] }
        });
        var head = B64.Encode("""{"typ":"JWT","alg":"ES256"}"""u8.ToArray());
        var claims = B64.Encode(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(), sub = subject }));
        var signed = $"{head}.{claims}";
        var sig = key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signed}.{B64.Encode(sig)}";
    }

    /// <summary>
    /// RFC 8291 message encryption: one aes128gcm record. The server key pair and salt are new for every message; tests pass
    /// fixed ones to check against the RFC's example.
    /// </summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECParameters? serverKey = null, byte[]? salt = null)
    {
        using var server = serverKey is { } k ? ECDiffieHellman.Create(k) : ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sp = server.ExportParameters(false);
        byte[] asPublic = [0x04, .. sp.Q.X!, .. sp.Q.Y!];
        using var device = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] }
        });
        var ecdh = server.DeriveRawSecretAgreement(device.PublicKey);

        // IKM from the shared secret and the device's auth secret, bound to both public keys
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdh, authSecret);
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic];
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

        salt ??= RandomNumberGenerator.GetBytes(16);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        byte[] padded = [.. plaintext, 0x02]; // 0x02 = last (and only) record
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, padded, cipher, tag);

        // Header: salt · record size (4096) · key id length · server public key
        var rs = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rs, 4096);
        return [.. salt, .. rs, (byte)asPublic.Length, .. asPublic, .. cipher, .. tag];
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>base64url without padding, as Web Push uses everywhere.</summary>
public static class B64
{
    public static string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
