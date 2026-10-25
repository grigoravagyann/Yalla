using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// To the guest who said yes - or for whom the floor said it after calling: "please free the table by
/// 19:40". Sent while the booking still holds the table.
/// </summary>
internal sealed class TableRequestDecidedHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<TableRequestDecidedHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationTableRequestDecided;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TableRequestNotice>(payloadJson);

        var status = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => (ReservationStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval or ReservationStatus.Seated))
        {
            logger.LogInformation(
                "Skipping the free-by reminder for booking {ReservationId}: it is {Status}.",
                notice.ReservationId, status);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.TableRequestAccepted(locale, notice.VenueName, notice.FreeByLocalTime);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "table-request-decided",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["requestId"] = notice.RequestId.ToString(),
                    ["accepted"] = "true",
                    ["freeByTime"] = notice.FreeByLocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    ["freeByUtc"] = notice.FreeByUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                }),
            cancellationToken);
    }
}
