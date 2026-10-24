using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yalla.Infrastructure.Notifications;

namespace Yalla.UnitTests;

/// <summary>
/// The Web Push message encryption (RFC 8291) and the VAPID header (RFC 8292), pinned to the RFC's own vector.
/// </summary>
/// <remarks>
/// A push the browser cannot decrypt is dropped silently by the browser, and the server is told 201 Created all the
/// same - so nothing but a byte-for-byte vector can say this is right.
/// </remarks>
public sealed class WebPushCryptoTests
{
    // RFC 8291, Appendix A.
    private const string Plaintext = "When I grow up, I want to be a watermelon";
    private const string ServerPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string ServerPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string UserAgentPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UserAgentPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Header = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string Ciphertext = "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ";

    [Fact]
    public void The_rfc_8291_test_vector_encrypts_byte_for_byte()
    {
        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode(ServerPrivate),
            Q = Point(ServerPublic),
        });

        var body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(Plaintext),
            Base64Url.Decode(UserAgentPublic),
            Base64Url.Decode(AuthSecret),
            server,
            Base64Url.Decode(Salt));

        var header = Base64Url.Decode(Header);

        Assert.Equal(86, header.Length);
        Assert.Equal(header, body[..86]);
        Assert.Equal(Base64Url.Decode(Ciphertext), body[86..]);
    }

    [Fact]
    public void What_a_fresh_key_encrypts_the_browser_decrypts()
    {
        var payload = Encoding.UTF8.GetBytes("{\"title\":\"Սեղան 2 · հաշիվ\",\"kind\":\"tableCalls\"}");
        var auth = RandomNumberGenerator.GetBytes(16);

        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = browser.ExportParameters(false);

        var body = WebPushCrypto.Encrypt(payload, [0x04, .. publicKey.Q.X!, .. publicKey.Q.Y!], auth);

        Assert.Equal(payload, Decrypt(body, browser, auth));
    }

    [Fact]
    public void A_push_body_never_passes_the_4096_bytes_a_push_service_must_accept()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = browser.ExportParameters(false).Q;
        byte[] publicKey = [0x04, .. q.X!, .. q.Y!];
        var auth = RandomNumberGenerator.GetBytes(16);

        // RFC 8291 section 4: 3993 bytes of plaintext is the most, and makes a body of exactly 4096.
        Assert.Equal(3993, WebPushCrypto.MaxPlaintext);

        var largest = WebPushCrypto.Encrypt(new byte[WebPushCrypto.MaxPlaintext], publicKey, auth);
        Assert.Equal(4096, largest.Length);
        Assert.Equal(new byte[WebPushCrypto.MaxPlaintext], Decrypt(largest, browser, auth));

        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt(new byte[WebPushCrypto.MaxPlaintext + 1], publicKey, auth));
    }

    [Fact]
    public void The_rfc_vector_decrypts_with_the_user_agents_key()
    {
        using var browser = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode(UserAgentPrivate),
            Q = Point(UserAgentPublic),
        });

        var body = (byte[])[.. Base64Url.Decode(Header), .. Base64Url.Decode(Ciphertext)];

        Assert.Equal(Plaintext, Encoding.UTF8.GetString(Decrypt(body, browser, Base64Url.Decode(AuthSecret))));
    }

    [Fact]
    public void The_vapid_header_is_an_es256_jwt_for_the_endpoints_origin_signed_by_the_configured_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        var publicKey = Base64Url.Encode([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]);
        var privateKey = Base64Url.Encode(parameters.D!);
        var now = new DateTime(2026, 10, 24, 18, 0, 0, DateTimeKind.Utc);

        using var signer = VapidKeys.Signer(publicKey, privateKey);
        var header = VapidKeys.BuildAuthorization(
            signer, publicKey, "mailto:ops@example.com", new Uri("https://fcm.googleapis.com/fcm/send/abc:def"), now);

        Assert.StartsWith("vapid t=", header, StringComparison.Ordinal);
        Assert.EndsWith($", k={publicKey}", header, StringComparison.Ordinal);

        var jwt = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = jwt.Split('.');

        Assert.Equal(3, parts.Length);
        Assert.True(key.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.Decode(parts[2]), HashAlgorithmName.SHA256));

        using var claims = JsonDocument.Parse(Base64Url.Decode(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:ops@example.com", claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal(new DateTimeOffset(now.AddHours(12)).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());

        using var alg = JsonDocument.Parse(Base64Url.Decode(parts[0]));
        Assert.Equal("ES256", alg.RootElement.GetProperty("alg").GetString());
    }

    [Fact]
    public void A_key_pair_that_does_not_belong_together_is_refused()
    {
        using var one = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var a = one.ExportParameters(true);
        var b = other.ExportParameters(true);

        var options = new WebPushOptions
        {
            Subject = "mailto:ops@example.com",
            PublicKey = Base64Url.Encode([0x04, .. a.Q.X!, .. a.Q.Y!]),
            PrivateKey = Base64Url.Encode(b.D!),
        };

        Assert.NotNull(options.Problem());
        Assert.NotNull(new WebPushOptions { PublicKey = options.PublicKey }.Problem());
        Assert.NotNull(new WebPushOptions { PublicKey = options.PublicKey, PrivateKey = Base64Url.Encode(a.D!) }.Problem());
        Assert.Null(new WebPushOptions { Subject = "https://shurj.am", PublicKey = options.PublicKey, PrivateKey = Base64Url.Encode(a.D!) }.Problem());
        Assert.Null(new WebPushOptions().Problem());
    }

    /// <summary>What a browser does with a push: RFC 8291 from the receiving side.</summary>
    internal static byte[] Decrypt(byte[] body, ECDiffieHellman browser, byte[] auth)
    {
        var salt = body[..16];
        var idLength = body[20];
        var serverPublic = body[21..(21 + idLength)];
        var sealedRecord = body[(21 + idLength)..];

        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
        });

        var mine = browser.ExportParameters(false);
        byte[] browserPublic = [0x04, .. mine.Q.X!, .. mine.Q.Y!];

        var prkKey = browser.DeriveKeyFromHmac(server.PublicKey, HashAlgorithmName.SHA256, auth);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, [.. "WebPush: info"u8, 0x00, .. browserPublic, .. serverPublic]);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, [.. "Content-Encoding: aes128gcm"u8, 0x00]);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, [.. "Content-Encoding: nonce"u8, 0x00]);

        var cipher = sealedRecord[..^16];
        var plain = new byte[cipher.Length];

        using (var aes = new AesGcm(cek, 16))
        {
            aes.Decrypt(nonce, cipher, sealedRecord[^16..], plain);
        }

        Assert.Equal(0x02, plain[^1]);

        return plain[..^1];
    }

    private static ECPoint Point(string base64Url)
    {
        var bytes = Base64Url.Decode(base64Url);

        return new ECPoint { X = bytes[1..33], Y = bytes[33..65] };
    }
}
