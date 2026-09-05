using Yalla.Domain.Common;

namespace Yalla.Domain.Identity;

/// <summary>Which app store the device came from. Expo needs it and so does message formatting.</summary>
public enum DevicePlatform
{
    Unknown = 0,
    Ios = 1,
    Android = 2,
}

/// <summary>
/// One phone a diner has the app on, and the token that reaches it.
/// </summary>
/// <remarks>
/// <para>
/// A person has more than one - a phone and a tablet, or a new phone and an old one they never
/// signed out of - so this is a collection per diner rather than a column on them, and a message
/// goes to every live device.
/// </para>
/// <para>
/// <b><see cref="Locale"/> lives here, not on the branch.</b> A message is written for the person
/// reading it. A Russian-speaking diner booking at an Armenian venue gets Russian, and a venue
/// serving three languages does not have to choose one on their diners' behalf.
/// </para>
/// <para>
/// <b>Revocation is how a dead token stops costing anything.</b> Expo answers
/// <c>DeviceNotRegistered</c> when the app has been uninstalled; retrying that for ever is the
/// default behaviour of every naive push implementation, and it is why push queues fill with
/// garbage. The receipt revokes the row instead.
/// </para>
/// </remarks>
public sealed class DinerDevice : Entity
{
    public Guid DinerUserId { get; private set; }

    public DinerUser DinerUser { get; private set; } = null!;

    /// <summary>The Expo push token, e.g. <c>ExponentPushToken[xxxx]</c>.</summary>
    public string PushToken { get; private set; } = null!;

    public DevicePlatform Platform { get; private set; }

    /// <summary>BCP-47, one of <c>hy</c>, <c>ru</c> or <c>en</c>. The person's, not the venue's.</summary>
    public string Locale { get; private set; } = null!;

    public DateTime LastSeenAtUtc { get; private set; }

    /// <summary>Set when the token stopped working. Never cleared - the app registers a new one.</summary>
    public DateTime? RevokedAtUtc { get; private set; }

    /// <summary>Why it was revoked, for the one time somebody asks why a diner stopped being told.</summary>
    public string? RevokedReason { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    private DinerDevice()
    {
    }

    public DinerDevice(
        Guid dinerUserId,
        string pushToken,
        DevicePlatform platform,
        string locale,
        DateTime atUtc)
        : base(Guid.CreateVersion7())
    {
        DinerUserId = Guard.NotEmpty(dinerUserId, nameof(dinerUserId));
        PushToken = Guard.NotBlank(pushToken, nameof(pushToken), FieldLengths.PushToken);
        Platform = Guard.Defined(platform, nameof(platform));
        Locale = Guard.NotBlank(locale, nameof(locale), FieldLengths.LocaleCode);
        LastSeenAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        StampCreatedAt(atUtc);
    }

    /// <summary>The app opened again: same token, possibly a new language.</summary>
    public void Refresh(string locale, DevicePlatform platform, DateTime atUtc)
    {
        Locale = Guard.NotBlank(locale, nameof(locale), FieldLengths.LocaleCode);
        Platform = Guard.Defined(platform, nameof(platform));
        LastSeenAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));

        // Re-registering a revoked token is how a reinstall comes back. The alternative is a diner
        // who reinstalls the app and silently never hears from us again.
        RevokedAtUtc = null;
        RevokedReason = null;
    }

    /// <summary>Records that something was successfully sent to it.</summary>
    /// <remarks>
    /// Enough to answer "is anybody still using this device", which is what makes a stale token
    /// obvious before Expo has to tell us about it.
    /// </remarks>
    public void Touch(DateTime atUtc) => LastSeenAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));

    /// <summary>The token is dead. Stop sending to it.</summary>
    public void Revoke(string reason, DateTime atUtc)
    {
        RevokedAtUtc ??= Guard.NotLocalTime(atUtc, nameof(atUtc));
        RevokedReason = Guard.NotBlank(reason, nameof(reason), FieldLengths.Reason);
    }
}
