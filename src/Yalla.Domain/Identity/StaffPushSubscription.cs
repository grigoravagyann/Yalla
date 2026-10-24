using Yalla.Domain.Common;

namespace Yalla.Domain.Identity;

/// <summary>
/// Where a staff device's browser receives Web Push: the push service's endpoint and the two keys the
/// payload is encrypted to (RFC 8291). One per device.
/// </summary>
/// <remarks>
/// <para>
/// <b>A credential, of a kind.</b> Whoever holds the endpoint can make the browser show a notification, and the
/// keys are what make the payload readable only there - so none of it is ever logged whole, and none of it is
/// on the wire back to a client.
/// </para>
/// <para>
/// <b>Deleted, not revoked, when the push service says it is gone</b> (404 or 410): the browser unsubscribed, the
/// app was removed, the permission was withdrawn. There is nothing to keep - a browser that subscribes again
/// gets a new endpoint and the device stores that instead.
/// </para>
/// </remarks>
public sealed class StaffPushSubscription : Entity
{
    /// <summary>The longest endpoint kept. Real ones are a few hundred characters.</summary>
    public const int EndpointMaxLength = 2000;

    /// <summary>The longest key kept, base64url. A P-256 public key is 87 characters, an auth secret 22.</summary>
    public const int KeyMaxLength = 200;

    private StaffPushSubscription()
    {
    }

    public StaffPushSubscription(
        Guid staffDeviceId,
        string endpoint,
        string endpointHash,
        string p256dh,
        string auth,
        DateTime subscribedAtUtc)
        : base(Guid.CreateVersion7())
    {
        StaffDeviceId = Guard.NotEmpty(staffDeviceId, nameof(staffDeviceId));
        StampCreatedAt(subscribedAtUtc);
        Replace(endpoint, endpointHash, p256dh, auth, subscribedAtUtc);
    }

    /// <summary>The device it belongs to - one subscription per device.</summary>
    public Guid StaffDeviceId { get; private set; }

    public StaffDevice StaffDevice { get; private set; } = null!;

    /// <summary>The push service URL the encrypted payload is POSTed to.</summary>
    public string Endpoint { get; private set; } = null!;

    /// <summary>SHA-256 of <see cref="Endpoint"/>, unique: one browser subscription belongs to one device row.</summary>
    public string EndpointHash { get; private set; } = null!;

    /// <summary>The browser's P-256 public key, base64url.</summary>
    public string P256dh { get; private set; } = null!;

    /// <summary>The browser's 16-byte authentication secret, base64url.</summary>
    public string Auth { get; private set; } = null!;

    /// <summary>When the browser last told us about this subscription.</summary>
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Points the device at a new or refreshed browser subscription.</summary>
    public void Replace(string endpoint, string endpointHash, string p256dh, string auth, DateTime atUtc)
    {
        Endpoint = Guard.NotBlank(endpoint, nameof(endpoint), EndpointMaxLength);
        EndpointHash = Guard.NotBlank(endpointHash, nameof(endpointHash), FieldLengths.TokenHash);
        P256dh = Guard.NotBlank(p256dh, nameof(p256dh), KeyMaxLength);
        Auth = Guard.NotBlank(auth, nameof(auth), KeyMaxLength);
        UpdatedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
    }
}
