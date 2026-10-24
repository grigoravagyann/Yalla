namespace Yalla.Application.Notifications;

/// <summary>Whether this server sends Web Push, and the key a browser subscribes with.</summary>
/// <param name="Enabled">False when no VAPID keys are configured: pushes are logged, not sent - offer no switch.</param>
/// <param name="PublicKey">
/// The VAPID public key, base64url - <c>applicationServerKey</c> for <c>pushManager.subscribe</c>. When it differs from
/// the one the browser's subscription was made with, unsubscribe and subscribe again (a Development server makes a
/// new one every start). Absent when disabled.
/// </param>
public sealed record StaffPushConfig(bool Enabled, string? PublicKey);

/// <summary>A browser's push subscription, as <c>PushSubscription.toJSON()</c> gives it.</summary>
/// <param name="Endpoint">The push service URL.</param>
/// <param name="P256dh">The browser's P-256 key, base64url.</param>
/// <param name="Auth">The browser's auth secret, base64url.</param>
public sealed record StaffPushSubscriptionInput(string Endpoint, string P256dh, string Auth);

/// <summary>What a staff device is pushed, and when.</summary>
/// <param name="Approvals">A booking waits for the floor's yes or no.</param>
/// <param name="GuestMessages">A guest wrote, or answered "are you coming?".</param>
/// <param name="TableCalls">A table called a waiter or asked for the bill.</param>
/// <param name="Takeaway">A new takeaway order waits to be accepted.</param>
/// <param name="KitchenReady">The kitchen marked an order ready for the person's table. Off by default.</param>
/// <param name="OnlyDuringShift">
/// A phone's pushes wait for its person's shift at the branch. Defaults on for a waiter, off for a manager and above;
/// always false on a tablet, which has no person and is pushed whenever it is subscribed.
/// </param>
public sealed record StaffNotificationPreferences(
    bool Approvals,
    bool GuestMessages,
    bool TableCalls,
    bool Takeaway,
    bool KitchenReady,
    bool OnlyDuringShift);

/// <summary>A device's notification settings as the staff app's switches show them: the six switches, flat.</summary>
/// <param name="Approvals">See <see cref="StaffNotificationPreferences"/>.</param>
/// <param name="GuestMessages">See <see cref="StaffNotificationPreferences"/>.</param>
/// <param name="TableCalls">See <see cref="StaffNotificationPreferences"/>.</param>
/// <param name="Takeaway">See <see cref="StaffNotificationPreferences"/>.</param>
/// <param name="KitchenReady">See <see cref="StaffNotificationPreferences"/>.</param>
/// <param name="OnlyDuringShift">See <see cref="StaffNotificationPreferences"/>.</param>
/// <param name="Locale">The language pushes are written in (<c>hy</c>, <c>ru</c>, <c>en</c>); absent means Armenian.</param>
/// <param name="Subscribed">This device has a push subscription on file.</param>
/// <param name="IsDefault">Nothing was chosen yet: these are the defaults for the device and its person's role.</param>
public sealed record StaffNotificationSettings(
    bool Approvals,
    bool GuestMessages,
    bool TableCalls,
    bool Takeaway,
    bool KitchenReady,
    bool OnlyDuringShift,
    string? Locale,
    bool Subscribed,
    bool IsDefault);

/// <summary>A staff device's own Web Push: its subscription and its switches.</summary>
/// <remarks>
/// Everything acts on the device the caller's PIN session is open on - phones and tablets alike - and nothing takes
/// an id, so there is no other device to name.
/// </remarks>
public interface IStaffPushService
{
    /// <summary>Whether pushes are sent, and the public key to subscribe with.</summary>
    StaffPushConfig GetConfig();

    /// <summary>Stores or replaces the device's subscription. A browser subscription held by another device moves here.</summary>
    /// <exception cref="Domain.FieldValidationException">
    /// <c>endpoint</c> is not an https URL of a known push service, or <c>keys.p256dh</c> / <c>keys.auth</c> are not
    /// usable keys.
    /// </exception>
    Task SaveSubscriptionAsync(Guid deviceId, StaffPushSubscriptionInput subscription, CancellationToken cancellationToken = default);

    /// <summary>Forgets the device's subscription. Succeeds when there was none.</summary>
    Task DeleteSubscriptionAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>The device's switches, its language and whether it is subscribed.</summary>
    Task<StaffNotificationSettings> GetSettingsAsync(Guid deviceId, Guid staffMemberId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the device's switches, and its language when <paramref name="locale"/> is given.</summary>
    Task<StaffNotificationSettings> SaveSettingsAsync(
        Guid deviceId,
        Guid staffMemberId,
        StaffNotificationPreferences preferences,
        string? locale,
        CancellationToken cancellationToken = default);
}
