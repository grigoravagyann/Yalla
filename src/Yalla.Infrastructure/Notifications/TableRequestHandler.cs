using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// "Someone would like your table at 19:45. Will you have left by 19:40?" - to the guest with the table
/// first, with Yes and No on the notification (the <c>table-request</c> category).
/// </summary>
/// <remarks>
/// Sent only while the question is still open: answered from the booking's card, withdrawn because one
/// of the two bookings moved or ended, or past its deadline, the push would ask about something already
/// settled - and teach the guest to ignore the next one.
/// </remarks>
internal sealed class TableRequestHandler(
    YallaDbContext db,
    IClock clock,
    INotificationChannel channel,
    ILogger<TableRequestHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationTableRequest;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TableRequestNotice>(payloadJson);
        var nowUtc = clock.UtcNow;

        var open = await db.TableShareRequests
            .AsNoTracking()
            .AnyAsync(
                r => r.Id == notice.RequestId
                     && r.Status == TableShareRequestStatus.Pending
                     && r.ExpiresAtUtc > nowUtc,
                cancellationToken);

        if (!open)
        {
            logger.LogInformation(
                "Skipping the request for booking {ReservationId}'s table: request {RequestId} is no longer open.",
                notice.ReservationId, notice.RequestId);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.TableRequested(
            locale, notice.VenueName, notice.RequestedLocalTime, notice.FreeByLocalTime);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "table-request",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["requestId"] = notice.RequestId.ToString(),
                    ["requestedTime"] = notice.RequestedLocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    ["freeByTime"] = notice.FreeByLocalTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    ["freeByUtc"] = notice.FreeByUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                },
                CategoryId: "table-request"),
            cancellationToken);
    }
}
