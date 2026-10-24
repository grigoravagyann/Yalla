using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;

namespace Yalla.Infrastructure.Notifications;

/// <summary>Web Push for the staff app: the VAPID identity, the payload lifetime, and which push services to trust.</summary>
/// <remarks>
/// <para>
/// <b>The private key is a secret</b>, like <c>Jwt:SigningKey</c>: user secrets locally, <c>WebPush__PrivateKey</c> in
/// a deployment, never <c>appsettings.json</c> and never a commit. Whoever holds it can push to every staff phone
/// that subscribed with the public key.
/// </para>
/// <para>
/// <b>Unconfigured</b>, staff pushes are written to the log instead of sent, as the diner's <c>Log</c> channel does,
/// and <c>GET /api/staff/push-config</c> answers <c>enabled: false</c>. <b>In Development</b> with no keys at all,
/// a key pair is generated at startup and lives as long as the process: subscriptions made against it stop
/// working at the next restart, so the staff app re-subscribes whenever the public key it is given changes.
/// </para>
/// </remarks>
public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    /// <summary>
    /// The VAPID <c>sub</c>: a <c>mailto:</c> or <c>https:</c> contact for the push services. Required with keys.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>The VAPID public key: an uncompressed P-256 point, 65 bytes, base64url. Given to browsers.</summary>
    public string? PublicKey { get; set; }

    /// <summary>The VAPID private key: the P-256 scalar, 32 bytes, base64url. <b>A secret.</b></summary>
    public string? PrivateKey { get; set; }

    /// <summary>
    /// How long a push is worth delivering, in seconds - the <c>TTL</c> header, and the age past which the outbox
    /// drops one rather than buzz about a moment long gone. Default fifteen minutes.
    /// </summary>
    public int TtlSeconds { get; set; } = 900;

    /// <summary>
    /// The push services a subscription may point at, as host suffixes. Empty: <see cref="DefaultEndpointHosts"/>.
    /// </summary>
    /// <remarks>
    /// The endpoint comes from a browser, and this server POSTs to it - so it is held to the push services that
    /// exist rather than to any URL a client sends, which would make the outbox a request forwarder into the
    /// host's own network.
    /// </remarks>
    public string[]? AllowedEndpointHosts { get; set; }

    /// <summary>Chrome, Edge, Firefox and Safari's push services: Google's, Microsoft's, Mozilla's and Apple's.</summary>
    public static readonly string[] DefaultEndpointHosts =
    [
        "fcm.googleapis.com",
        "android.googleapis.com",
        "push.services.mozilla.com",
        "push.apple.com",
        "notify.windows.com",
    ];

    /// <summary>What the allowlist is in effect.</summary>
    public IReadOnlyList<string> EffectiveEndpointHosts =>
        AllowedEndpointHosts is { Length: > 0 } configured ? configured : DefaultEndpointHosts;

    /// <summary>
    /// Why this configuration cannot be used, or null when it can - including "nothing configured", which is
    /// usable: it means log instead of send.
    /// </summary>
    public string? Problem()
    {
        var hasPublic = !string.IsNullOrWhiteSpace(PublicKey);
        var hasPrivate = !string.IsNullOrWhiteSpace(PrivateKey);

        if (TtlSeconds is < 0 or > 2_419_200)
        {
            return $"WebPush:TtlSeconds is {TtlSeconds}; use 0 to 2419200 seconds";
        }

        if (!hasPublic && !hasPrivate)
        {
            return null;
        }

        if (hasPublic != hasPrivate)
        {
            return "WebPush:PublicKey and WebPush:PrivateKey come as a pair; one is set without the other";
        }

        if (string.IsNullOrWhiteSpace(Subject)
            || !(Subject.StartsWith("mailto:", StringComparison.Ordinal) || Subject.StartsWith("https://", StringComparison.Ordinal)))
        {
            return "WebPush:Subject must be a mailto: or https: contact when keys are configured";
        }

        try
        {
            using var _ = VapidKeys.Signer(PublicKey!, PrivateKey!);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return $"WebPush keys are not a matching P-256 pair in base64url ({ex.GetType().Name})";
        }

        return null;
    }
}

/// <summary>
/// The VAPID identity this process pushes with: the configured pair, an ephemeral one in Development, or none.
/// </summary>
internal sealed class VapidKeys
{
    private readonly ECParameters? _parameters;

    public VapidKeys(WebPushOptions options, IHostEnvironment environment, ILogger<VapidKeys> logger)
    {
        Options = options;

        if (options.Problem() is { } problem)
        {
            // The host refuses to start on this already (DependencyInjectionExtension); this is the braces.
            throw new InvalidOperationException(problem);
        }

        if (!string.IsNullOrWhiteSpace(options.PublicKey))
        {
            using var signer = Signer(options.PublicKey, options.PrivateKey!);
            _parameters = signer.ExportParameters(includePrivateParameters: true);
            PublicKey = options.PublicKey.Trim();
            Subject = options.Subject!;
            return;
        }

        if (environment.IsDevelopment())
        {
            using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _parameters = generated.ExportParameters(includePrivateParameters: true);
            PublicKey = Base64Url.Encode(PointOf(_parameters.Value));
            Subject = string.IsNullOrWhiteSpace(options.Subject) ? "mailto:staff-push@example.com" : options.Subject;
            IsEphemeral = true;

            // The public key only: the private one lives in this process and nowhere else.
            logger.LogWarning(
                "WebPush has no keys configured; generated a Development key pair for this run (public key {PublicKey}). "
                + "Staff phones re-subscribe after every restart. Configure WebPush:PublicKey/PrivateKey/Subject to keep them.",
                PublicKey);

            return;
        }

        logger.LogWarning("WebPush is not configured: staff pushes are written to the log instead of sent.");
    }

    public WebPushOptions Options { get; }

    /// <summary>Whether pushes are really sent.</summary>
    public bool Enabled => _parameters is not null;

    /// <summary>Generated for this run only (Development with nothing configured).</summary>
    public bool IsEphemeral { get; }

    /// <summary>The public key browsers subscribe with, base64url; null when disabled.</summary>
    public string? PublicKey { get; }

    /// <summary>The VAPID <c>sub</c>.</summary>
    public string? Subject { get; }

    /// <summary>
    /// The <c>Authorization</c> header for one push: <c>vapid t=&lt;JWT&gt;, k=&lt;public key&gt;</c> (RFC 8292), the JWT
    /// signed ES256 for the endpoint's origin, good for twelve hours.
    /// </summary>
    public string AuthorizationFor(Uri endpoint, DateTime nowUtc)
    {
        if (_parameters is not { } parameters)
        {
            throw new InvalidOperationException("WebPush is not configured.");
        }

        using var key = ECDsa.Create(parameters);

        return BuildAuthorization(key, PublicKey!, Subject!, endpoint, nowUtc);
    }

    /// <summary>The header for <paramref name="endpoint"/>, signed by <paramref name="key"/>. Exposed for the tests.</summary>
    internal static string BuildAuthorization(ECDsa key, string publicKey, string subject, Uri endpoint, DateTime nowUtc)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var expires = new DateTimeOffset(nowUtc.AddHours(12)).ToUnixTimeSeconds();

        var header = Base64Url.Encode("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8.ToArray());
        var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = expires,
            ["sub"] = subject,
        }));

        var signingInput = $"{header}.{claims}";

        // IEEE P1363 (r || s, 64 bytes) - what ES256 in a JWT is, and .NET's default for SignData.
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);

        return $"vapid t={signingInput}.{Base64Url.Encode(signature)}, k={publicKey}";
    }

    /// <summary>A signing key from a base64url pair, refused unless the two belong together.</summary>
    internal static ECDsa Signer(string publicKey, string privateKey)
    {
        var point = Base64Url.Decode(publicKey.Trim());
        var scalar = Base64Url.Decode(privateKey.Trim());

        if (point.Length != 65 || point[0] != 0x04 || scalar.Length != 32)
        {
            throw new FormatException("A VAPID public key is 65 bytes starting 0x04, a private key 32 bytes.");
        }

        var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
            D = scalar,
        });

        // Importing does not prove D belongs to Q on every platform; a signature verified by the public half does.
        var probe = key.SignData("vapid-key-check"u8.ToArray(), HashAlgorithmName.SHA256);

        using var publicOnly = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });

        if (!publicOnly.VerifyData("vapid-key-check"u8.ToArray(), probe, HashAlgorithmName.SHA256))
        {
            key.Dispose();
            throw new CryptographicException("The VAPID private key does not belong to the public key.");
        }

        return key;
    }

    private static byte[] PointOf(ECParameters parameters) => [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
}

/// <summary>
/// RFC 8291 message encryption for Web Push: <c>aes128gcm</c> content coding (RFC 8188) keyed by an ECDH between a
/// fresh server key and the browser's, mixed with the browser's auth secret.
/// </summary>
/// <remarks>
/// Written against the RFC rather than taken from a package: it is forty lines of the framework's own primitives
/// (P-256 ECDH, HKDF, AES-GCM), and the RFC's Appendix A vector pins it byte for byte in
/// <c>WebPushCryptoTests</c>.
/// </remarks>
internal static class WebPushCrypto
{
    /// <summary>The record size written in the header. One record carries the whole message.</summary>
    public const int RecordSize = 4096;

    /// <summary>The largest plaintext one record holds: the record, less the tag and the padding delimiter.</summary>
    public const int MaxPlaintext = RecordSize - 16 - 1;

    /// <summary>Encrypts <paramref name="plaintext"/> for one browser subscription.</summary>
    /// <param name="plaintext">The JSON the service worker receives.</param>
    /// <param name="userAgentPublicKey">The subscription's <c>p256dh</c>, decoded: 65 bytes.</param>
    /// <param name="authSecret">The subscription's <c>auth</c>, decoded: 16 bytes.</param>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] userAgentPublicKey, byte[] authSecret)
    {
        using var serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        return Encrypt(plaintext, userAgentPublicKey, authSecret, serverKey, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The same, with the server key and salt given - for the RFC's test vector.</summary>
    internal static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        byte[] userAgentPublicKey,
        byte[] authSecret,
        ECDiffieHellman serverKey,
        byte[] salt)
    {
        if (plaintext.Length > MaxPlaintext)
        {
            throw new ArgumentException($"A push payload is at most {MaxPlaintext} bytes.", nameof(plaintext));
        }

        using var userAgent = ImportPublic(userAgentPublicKey);

        var server = serverKey.ExportParameters(includePrivateParameters: false);
        byte[] serverPublic = [0x04, .. server.Q.X!, .. server.Q.Y!];

        // PRK_key = HMAC-SHA-256(auth_secret, ecdh_secret)
        var prkKey = serverKey.DeriveKeyFromHmac(userAgent.PublicKey, HashAlgorithmName.SHA256, authSecret);

        // IKM = HKDF-Expand(PRK_key, "WebPush: info" || 0x00 || ua_public || as_public, 32)
        byte[] keyInfo = [.. "WebPush: info"u8, 0x00, .. userAgentPublicKey, .. serverPublic];
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

        // PRK = HKDF-Extract(salt, IKM); CEK and NONCE from it (RFC 8188).
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, [.. "Content-Encoding: aes128gcm"u8, 0x00]);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, [.. "Content-Encoding: nonce"u8, 0x00]);

        // One record, the last: the plaintext and the 0x02 delimiter, no padding.
        byte[] record = [.. plaintext, 0x02];
        var cipher = new byte[record.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(cek, tagSizeInBytes: 16))
        {
            aes.Encrypt(nonce, record, cipher, tag);
        }

        // salt (16) || rs (4, big-endian) || idlen (1) || keyid = as_public (65)
        var header = new byte[16 + 4 + 1 + serverPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), RecordSize);
        header[20] = (byte)serverPublic.Length;
        serverPublic.CopyTo(header, 21);

        return [.. header, .. cipher, .. tag];
    }

    /// <summary>Whether a subscription's <c>p256dh</c> is a real P-256 point, base64url.</summary>
    public static bool IsPublicKey(string? p256dh)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(p256dh))
            {
                return false;
            }

            var point = Base64Url.Decode(p256dh);

            if (point.Length != 65 || point[0] != 0x04)
            {
                return false;
            }

            using var _ = ImportPublic(point);

            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static ECDiffieHellman ImportPublic(byte[] point)
    {
        if (point.Length != 65 || point[0] != 0x04)
        {
            throw new ArgumentException("A P-256 public key is 65 bytes starting 0x04.", nameof(point));
        }

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
    }
}

/// <summary>base64url without padding, both ways - what every Web Push key and the JWT use.</summary>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var standard = text.Trim().Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String((standard.Length % 4) switch
        {
            2 => standard + "==",
            3 => standard + "=",
            _ => standard,
        });
    }
}

/// <summary>One subscription to push to, and whose device it is.</summary>
internal sealed record StaffPushTarget(Guid SubscriptionId, Guid DeviceId, string Endpoint, string P256dh, string Auth);

/// <summary>What became of one push.</summary>
internal enum StaffPushOutcome
{
    /// <summary>The push service took it (or, unconfigured, it was logged).</summary>
    Delivered = 1,

    /// <summary>404 or 410: the subscription is gone for good and is deleted.</summary>
    Gone = 2,

    /// <summary>Anything else: the service had a bad minute, or refused us.</summary>
    Failed = 3,
}

/// <summary>
/// Sends one encrypted push to one browser subscription - or, with no VAPID keys, writes what it would have sent to
/// the log.
/// </summary>
internal sealed class StaffPushSender(
    IHttpClientFactory httpClients,
    VapidKeys keys,
    IClock clock,
    ILogger<StaffPushSender> logger)
{
    /// <summary>The named <c>HttpClient</c>, so a test can stand a push service in.</summary>
    public const string HttpClientName = "WebPush";

    public async Task<StaffPushOutcome> SendAsync(
        StaffPushTarget target,
        string payloadJson,
        string kind,
        string tag,
        CancellationToken cancellationToken)
    {
        if (!keys.Enabled)
        {
            // The kind and the deep link, never the words: a guest's message is not a log line.
            logger.LogInformation(
                "[staff push -> device {DeviceId}] {Kind} {Tag} (WebPush not configured; logged instead of sent)",
                target.DeviceId, kind, tag);

            return StaffPushOutcome.Delivered;
        }

        if (!Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return StaffPushOutcome.Gone;
        }

        var body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(payloadJson), Base64Url.Decode(target.P256dh), Base64Url.Decode(target.Auth));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", keys.Options.TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", "high");

        // A newer push about the same thing replaces one the phone has not fetched yet. The tag is hashed to the
        // 32 URL-safe characters the header allows.
        request.Headers.TryAddWithoutValidation("Topic", Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(tag)).AsSpan(0, 24)));
        request.Headers.TryAddWithoutValidation("Authorization", keys.AuthorizationFor(endpoint, clock.UtcNow));

        try
        {
            using var http = httpClients.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return StaffPushOutcome.Delivered;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return StaffPushOutcome.Gone;
            }

            // The host and the status only: the endpoint's path is the subscription's capability.
            logger.LogWarning(
                "Push service {Host} refused a staff push to device {DeviceId}: {Status}.",
                endpoint.Host, target.DeviceId, (int)response.StatusCode);

            return StaffPushOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(
                "Push service {Host} could not be reached for device {DeviceId}: {Error}.",
                endpoint.Host, target.DeviceId, ex.GetType().Name);

            return StaffPushOutcome.Failed;
        }
    }
}
