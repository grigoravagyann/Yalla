using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Notifications;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>A staff device's own Web Push subscription and switches.</summary>
internal sealed class StaffPushService(
    YallaDbContext db,
    VapidKeys keys,
    IClock clock,
    ILogger<StaffPushService> logger) : IStaffPushService
{
    public StaffPushConfig GetConfig() => new(keys.Enabled, keys.PublicKey);

    public async Task SaveSubscriptionAsync(
        Guid deviceId,
        StaffPushSubscriptionInput subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var endpoint = (subscription.Endpoint ?? string.Empty).Trim();
        var violations = new List<FieldViolation>();

        if (!IsAllowedEndpoint(endpoint))
        {
            violations.Add(new FieldViolation(
                "endpoint",
                "Not a push service this server sends to: an https URL of Google's, Mozilla's, Apple's or Microsoft's push service.",
                FieldBounds.Required));
        }

        if (!WebPushCrypto.IsPublicKey(subscription.P256dh))
        {
            violations.Add(new FieldViolation("keys.p256dh", "Not a P-256 public key in base64url.", FieldBounds.Required));
        }

        if (!AuthSecretIsUsable(subscription.Auth))
        {
            violations.Add(new FieldViolation("keys.auth", "Not a 16-byte auth secret in base64url.", FieldBounds.Required));
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        var device = await LiveDeviceAsync(deviceId, cancellationToken);
        var nowUtc = clock.UtcNow;
        var hash = Secrets.Hash(endpoint);

        // The same browser subscription on another device row: a phone re-paired, a tablet re-enrolled. It is this
        // device's now - the old row would push the same browser twice. Deleted first, in its own statement, so the
        // unique index never sees the two at once.
        await db.StaffPushSubscriptions
            .Where(s => s.EndpointHash == hash && s.StaffDeviceId != device.Id)
            .ExecuteDeleteAsync(cancellationToken);

        var mine = await db.StaffPushSubscriptions.FirstOrDefaultAsync(s => s.StaffDeviceId == device.Id, cancellationToken);

        if (mine is null)
        {
            db.StaffPushSubscriptions.Add(new StaffPushSubscription(
                device.Id, endpoint, hash, subscription.P256dh!.Trim(), subscription.Auth!.Trim(), nowUtc));
        }
        else
        {
            mine.Replace(endpoint, hash, subscription.P256dh!.Trim(), subscription.Auth!.Trim(), nowUtc);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.StaffPushEndpoint)
                                           || UniqueViolation.IsOn(ex, DatabaseIndexNames.StaffPushPerDevice))
        {
            throw new DomainStateException("This device subscribed twice at the same moment. Try again.");
        }

        logger.LogInformation(
            "Device {DeviceId} subscribed to staff push at {Host}.", device.Id, new Uri(endpoint).Host);
    }

    public async Task DeleteSubscriptionAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var mine = await db.StaffPushSubscriptions
            .Where(s => s.StaffDeviceId == deviceId)
            .ToListAsync(cancellationToken);

        if (mine.Count == 0)
        {
            return;
        }

        db.StaffPushSubscriptions.RemoveRange(mine);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<StaffNotificationSettings> GetSettingsAsync(
        Guid deviceId,
        Guid staffMemberId,
        CancellationToken cancellationToken = default)
    {
        var device = await LiveDeviceAsync(deviceId, cancellationToken);

        return await SettingsOfAsync(device, staffMemberId, cancellationToken);
    }

    public async Task<StaffNotificationSettings> SaveSettingsAsync(
        Guid deviceId,
        Guid staffMemberId,
        StaffNotificationPreferences preferences,
        string? locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        if (locale is not null && !NotificationText.Supported.Contains(locale.Trim().ToLowerInvariant()))
        {
            throw new FieldValidationException(new FieldViolation(
                "locale", "The language is hy, ru or en.", FieldBounds.Required, Value: locale));
        }

        var device = await LiveDeviceAsync(deviceId, cancellationToken);

        var kinds = StaffPushKinds.None;
        kinds |= preferences.Approvals ? StaffPushKinds.Approvals : StaffPushKinds.None;
        kinds |= preferences.GuestMessages ? StaffPushKinds.GuestMessages : StaffPushKinds.None;
        kinds |= preferences.TableCalls ? StaffPushKinds.TableCalls : StaffPushKinds.None;
        kinds |= preferences.Takeaway ? StaffPushKinds.Takeaway : StaffPushKinds.None;
        kinds |= preferences.KitchenReady ? StaffPushKinds.KitchenReady : StaffPushKinds.None;

        device.SetPushPreferences(kinds, preferences.OnlyDuringShift);

        if (locale is not null)
        {
            device.SetLocale(locale.Trim().ToLowerInvariant());
        }

        await db.SaveChangesAsync(cancellationToken);

        return await SettingsOfAsync(device, staffMemberId, cancellationToken);
    }

    /// <summary>Whether a browser's endpoint is an https URL on one of the configured push services.</summary>
    internal bool IsAllowedEndpoint(string endpoint) =>
        endpoint.Length is > 0 and <= StaffPushSubscription.EndpointMaxLength
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttps
        && url.IsDefaultPort
        && string.IsNullOrEmpty(url.UserInfo)
        && !url.IsLoopback
        && keys.Options.EffectiveEndpointHosts.Any(
            host => url.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                    || url.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    private static bool AuthSecretIsUsable(string? auth)
    {
        try
        {
            return auth is not null && Base64Url.Decode(auth).Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<StaffDevice> LiveDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        await db.StaffDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.RevokedAtUtc == null, cancellationToken)
        ?? throw new AuthenticationFailedException(
            "device-revoked", "This device is no longer enrolled. Ask a manager to set it up again.");

    private async Task<StaffNotificationSettings> SettingsOfAsync(
        StaffDevice device,
        Guid staffMemberId,
        CancellationToken cancellationToken)
    {
        // A phone's default follows its person's role; a tablet has no person and no shift.
        var role = device.Kind == StaffDeviceKind.Phone
            ? await db.StaffMembers
                .Where(s => s.Id == (device.BoundStaffMemberId ?? staffMemberId))
                .Select(s => (StaffRole?)s.Role)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var kinds = device.EffectivePushKinds;
        var onlyDuringShift = device.Kind == StaffDeviceKind.Phone
                              && (device.PushOnlyDuringShift ?? (role is { } r && StaffDevice.OnlyDuringShiftByDefault(r)));

        var subscribed = await db.StaffPushSubscriptions.AnyAsync(s => s.StaffDeviceId == device.Id, cancellationToken);

        return new StaffNotificationSettings(
            kinds.HasFlag(StaffPushKinds.Approvals),
            kinds.HasFlag(StaffPushKinds.GuestMessages),
            kinds.HasFlag(StaffPushKinds.TableCalls),
            kinds.HasFlag(StaffPushKinds.Takeaway),
            kinds.HasFlag(StaffPushKinds.KitchenReady),
            onlyDuringShift,
            device.Locale,
            subscribed,
            device.PushKinds is null);
    }
}
