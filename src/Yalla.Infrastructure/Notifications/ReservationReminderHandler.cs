using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// Sends the reminder, if the booking is still worth reminding anybody about.
/// </summary>
/// <remarks>
/// <para>
/// The state check happens here rather than at write time, because hours pass in between. Cancelling
/// a booking deletes its unsent reminder, so this is the belt to that braces - a booking seated early
/// or cancelled by staff through some path that forgot still produces no push.
/// </para>
/// <para>
/// A booking still waiting for the venue is reminded - a large party next week the venue has not got
/// to yet is still the diner's plan - <b>except one waiting on a short-notice answer</b>. That one
/// has a deadline minutes away, and "your table is at 20:00" about a table nobody has promised is the
/// wrong thing to say; the answer itself says the right thing. Its reminder is not even written when
/// it would fall before the deadline, so this is the braces for a sweep running behind.
/// </para>
/// </remarks>
internal sealed class ReservationReminderHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<ReservationReminderHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationReminder;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);

        var booking = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => new { r.Status, AwaitsShortNoticeAnswer = r.ApprovalExpiresAtUtc != null })
            .FirstOrDefaultAsync(cancellationToken);

        var status = booking?.Status;

        if (status is not (ReservationStatus.Confirmed or ReservationStatus.PendingApproval))
        {
            logger.LogInformation(
                "Skipping the reminder for booking {ReservationId}: it is {Status}.",
                notice.ReservationId, status);

            return;
        }

        if (status == ReservationStatus.PendingApproval && booking!.AwaitsShortNoticeAnswer)
        {
            logger.LogInformation(
                "Skipping the reminder for booking {ReservationId}: it is still waiting on the venue's short-notice answer.",
                notice.ReservationId);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.Reminder(
            locale, notice.VenueName, notice.BranchName, notice.TableLabel, notice.LocalStartTime);

        // The action lives in the payload, so the diner cancels from the lock screen. That is the
        // whole feature: cancelling has to be easier than not showing up.
        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-reminder",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["code"] = notice.ReservationCode,
                    ["action"] = "cancel",
                    ["branch"] = notice.BranchName,
                },
                CategoryId: "reservation-reminder"),
            cancellationToken);
    }
}
