using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>"Still coming?", with the one-tap hold extension behind it.</summary>
internal sealed class ReservationLateNudgeHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<ReservationLateNudgeHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationLateNudge;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);

        var status = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => (ReservationStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken);

        // Seated is the case this exists to avoid. Nudging somebody who is already at the table,
        // eating, is the notification that makes a diner turn them all off.
        if (status != ReservationStatus.Confirmed)
        {
            logger.LogInformation(
                "Skipping the late nudge for booking {ReservationId}: it is {Status}.",
                notice.ReservationId, status);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.LateNudge(
            locale, notice.VenueName, notice.TableLabel, notice.GraceExtensionMinutes);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-late-nudge",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["action"] = "extend-hold",
                    ["extensionMinutes"] = notice.GraceExtensionMinutes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                },
                CategoryId: "late-nudge"),
            cancellationToken);
    }
}
