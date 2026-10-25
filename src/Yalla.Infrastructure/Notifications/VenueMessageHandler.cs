using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// "Lava restaurant: Of course, without onion." - somebody at the venue wrote in a booking's or an order's thread, or
/// asked "are you coming?".
/// </summary>
/// <remarks>
/// <para>
/// Sent while the thread is still the account's it was written for (a deletion cuts the link), and an ask only while it
/// is still a question: the booking confirmed and the guest not yet answered - "are you coming?" after "I'm on my way"
/// is the push that teaches a guest to switch them off.
/// </para>
/// <para>
/// <b>The category</b> - the actions the app hangs under the push - follows the branch's <c>guestsCanWrite</c> when it
/// is sent: a Reply the venue would refuse with 403 <c>guests-cannot-write</c> is not offered. Guests may write:
/// <c>venue-ask</c> ("I'm coming", "Late 15 min", a text reply) and <c>venue-message</c> (a text reply). They may not:
/// <c>venue-ask-buttons</c> (the two answers, no reply) and <c>venue-message-readonly</c> (no actions).
/// </para>
/// </remarks>
internal sealed class VenueMessageHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<VenueMessageHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.MessageToGuest;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<VenueMessageNotice>(payloadJson);

        var thread = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == notice.ConversationId && c.DinerUserId == notice.DinerUserId)
            .Select(c => new
            {
                GuestsCanWrite = db.Branches.Where(b => b.Id == c.BranchId).Select(b => b.GuestsCanMessage).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (thread is null)
        {
            logger.LogInformation(
                "Not telling anybody about message {MessageId}: its thread no longer belongs to the account it was written for.",
                notice.MessageId);

            return;
        }

        if (notice.Ask && notice.ReservationId is { } reservationId)
        {
            var booking = await db.Reservations
                .AsNoTracking()
                .Where(r => r.Id == reservationId)
                .Select(r => new
                {
                    r.Status,
                    Answered = r.GuestConfirmedOnWayAtUtc != null || r.GuestRunningLateAtUtc != null,
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (booking is not { Status: ReservationStatus.Confirmed, Answered: false })
            {
                logger.LogInformation(
                    "Not asking booking {ReservationId} whether they are coming: it is {Status}, answered {Answered}.",
                    reservationId, booking?.Status, booking?.Answered);

                return;
            }
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);
        var (title, body) = NotificationText.VenueMessage(locale, notice.VenueName, notice.Preview, notice.Ask, notice.LocalStartTime);

        var data = new Dictionary<string, string>
        {
            ["kind"] = "venue-message",
            ["conversationId"] = notice.ConversationId.ToString(),
            ["ask"] = notice.Ask ? "true" : "false",
        };

        if (notice.ReservationId is { } booked)
        {
            data["reservationId"] = booked.ToString();
        }

        if (notice.TakeawayOrderId is { } ordered)
        {
            data["takeawayOrderId"] = ordered.ToString();
        }

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                data,
                CategoryId: VenueMessageCategories.For(notice.Ask, thread.GuestsCanWrite)),
            cancellationToken);
    }
}
