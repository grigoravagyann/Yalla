using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Notifications;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>Registering a phone against a diner, and taking a dead token out of the rotation.</summary>
internal sealed class DinerDeviceService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ILogger<DinerDeviceService> logger) : IDinerDeviceService
{
    public async Task<Guid> RegisterAsync(
        string pushToken,
        DevicePlatform platform,
        string locale,
        CancellationToken cancellationToken = default)
    {
        var dinerUserId = actor.DinerUserId
                          ?? throw new UnauthorizedAccessException(
                              "Registering a device needs a signed-in diner.");

        var normalised = NotificationText.Normalise(locale);

        // Idempotent on the token. The app calls this every launch, and a row per launch would mean
        // one diner accumulating a hundred devices and getting a hundred copies of every push.
        var existing = await db.DinerDevices
            .FirstOrDefaultAsync(d => d.PushToken == pushToken, cancellationToken);

        if (existing is not null)
        {
            if (existing.DinerUserId != dinerUserId)
            {
                // The same phone, a different person: somebody signed out and somebody else signed
                // in. The token belongs to whoever is holding it now, and the previous owner must
                // stop receiving their bookings on it.
                logger.LogInformation(
                    "Push token moved from diner {Previous} to {Current}; the old registration is revoked.",
                    existing.DinerUserId, dinerUserId);

                existing.Revoke("This device was registered to another diner.", clock.UtcNow);

                var replacement = new DinerDevice(dinerUserId, pushToken, platform, normalised, clock.UtcNow);

                db.DinerDevices.Add(replacement);
                await db.SaveChangesAsync(cancellationToken);

                return replacement.Id;
            }

            existing.Refresh(normalised, platform, clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);

            return existing.Id;
        }

        var device = new DinerDevice(dinerUserId, pushToken, platform, normalised, clock.UtcNow);

        db.DinerDevices.Add(device);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Registered a {Platform} device for diner {DinerUserId} in {Locale}.",
            platform, dinerUserId, normalised);

        return device.Id;
    }

    public async Task RevokeAsync(
        string pushToken,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var device = await db.DinerDevices
            .FirstOrDefaultAsync(d => d.PushToken == pushToken, cancellationToken);

        if (device is null || device.IsRevoked)
        {
            return;
        }

        device.Revoke(reason, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Revoked push token for device {DeviceId}: {Reason}", device.Id, reason);
    }
}
