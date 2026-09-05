using Yalla.Domain.Identity;

namespace Yalla.Application.Notifications;

/// <summary>What a diner is about to be told, in their own language.</summary>
/// <param name="DinerUserId">Who.</param>
/// <param name="Title">The bold line on the lock screen.</param>
/// <param name="Body">The rest of it. Short - this is read at a glance.</param>
/// <param name="Data">
/// What the app does when it is tapped, and what the action buttons do. This is where the one-tap
/// cancel lives, and the reason it works without the diner hunting for a screen.
/// </param>
/// <param name="CategoryId">
/// Groups the actionable buttons the app registered. <c>reservation-reminder</c> carries cancel and
/// directions; <c>late-nudge</c> carries the hold extension.
/// </param>
public sealed record NotificationMessage(
    Guid DinerUserId,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
    string? CategoryId = null);

/// <summary>What happened to one device when a message was sent to it.</summary>
/// <param name="DeviceId">The device row.</param>
/// <param name="PushToken">Its token.</param>
/// <param name="Delivered">Whether the provider accepted it.</param>
/// <param name="ShouldRevokeToken">
/// True when the provider says this token will never work again - the app was uninstalled. The
/// token is revoked rather than retried; a push queue that retries dead tokens for ever is how push
/// queues fill with garbage.
/// </param>
/// <param name="Error">What the provider said, when it said anything.</param>
public sealed record NotificationDelivery(
    Guid DeviceId,
    string PushToken,
    bool Delivered,
    bool ShouldRevokeToken,
    string? Error);

/// <summary>
/// Where a notification actually goes. Same shape as <c>IVerificationCodeSender</c> from Prompt 3.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations ship: Expo, and a development logger that writes the rendered message and its
/// target to the log. The logger is what makes the whole scheduler testable without a phone in your
/// hand - and it is selected the same way Prompt 3 selects its code sender, by configuration.
/// </para>
/// <para>
/// Whether a second channel is SMS or a Telegram bot is an open commercial question. The interface
/// means it does not block anything, and nothing in this task integrates one.
/// </para>
/// </remarks>
public interface INotificationChannel
{
    /// <summary>What this channel is, for logs and the platform view.</summary>
    string Name { get; }

    /// <summary>
    /// Sends one message to every live device belonging to its diner.
    /// </summary>
    /// <returns>
    /// One result per device. An empty list means the diner has no live device - not a failure, and
    /// not something to retry: they have not installed the app, or they uninstalled it.
    /// </returns>
    Task<IReadOnlyList<NotificationDelivery>> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default);
}

/// <summary>Registering a phone, and taking a dead one out of the rotation.</summary>
public interface IDinerDeviceService
{
    /// <summary>
    /// Registers or refreshes this phone's push token. The diner's own token authorises it.
    /// </summary>
    /// <remarks>
    /// Idempotent on the push token: the app calls this on every launch, and it must not accumulate
    /// a row per launch. A token that was revoked comes back, because that is what a reinstall looks
    /// like.
    /// </remarks>
    Task<Guid> RegisterAsync(
        string pushToken,
        DevicePlatform platform,
        string locale,
        CancellationToken cancellationToken = default);

    /// <summary>Stops sending to a token the provider says is dead.</summary>
    Task RevokeAsync(string pushToken, string reason, CancellationToken cancellationToken = default);
}
