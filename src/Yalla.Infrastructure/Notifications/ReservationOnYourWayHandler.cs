using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// "Are you on your way?", 45 minutes before a booking with a pre-order, with "Yes, on my way" and "Can't come" behind
/// it - so the waiter knows whether to send the pre-order to the kitchen without phoning.
/// </summary>
/// <remarks>
/// <para>
/// Re-reads the state that decides whether to ask at all, as every handler here does. Asked of a <b>confirmed</b> booking
/// only - the answer refuses anything else, and a booking still waiting for the venue's yes is not expected yet. Every
/// booking is asked (2026-10-21); not once its pre-order was sent or the party marked not coming - the floor has decided
/// - or when the guest already said they are coming or running late, or the venue already asked. The wording says
/// whether a pre-order is waiting for the kitchen.
/// </para>
/// <para>
/// The venue's "Ask: are you coming?" is <b>not</b> sent through here: since guest messages (2026-10-23) it is a message
/// in the booking's thread, pushed as <c>message.to-guest</c> by <see cref="VenueMessageHandler"/>. <c>staffAsked</c> on
/// the notice is the earlier path, kept only so a message of that kind still queued when this shipped is delivered as
/// it was written - sent at once, whatever the floor decided. Nothing enqueues one any more.
/// </para>
/// </remarks>
internal sealed class ReservationOnYourWayHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<ReservationOnYourWayHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ReservationOnYourWay;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);

        var booking = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == notice.ReservationId)
            .Select(r => new
            {
                r.Status,
                Answered = r.GuestConfirmedOnWayAtUtc != null || r.GuestRunningLateAtUtc != null,
                StaffAsked = r.StaffAskedOnMyWayAtUtc != null,
                PreOrderWaiting = db.PreOrders.Any(p => p.ReservationId == r.Id
                                                        && p.SentAtUtc == null
                                                        && p.NotComingAtUtc == null
                                                        && p.Lines.Any()),
                FloorDecided = db.PreOrders.Any(p => p.ReservationId == r.Id
                                                     && (p.SentAtUtc != null || p.NotComingAtUtc != null)),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var expected = booking is { Status: ReservationStatus.Confirmed };

        // A venue's "ask now" queued before guest messages shipped goes whatever the floor decided about a pre-order -
        // somebody tapped it. The automatic question is not asked on top of the venue's: the guest has been asked already.
        if (!expected
            || booking!.Answered
            || (!notice.StaffAsked && (booking.FloorDecided || booking.StaffAsked)))
        {
            logger.LogInformation(
                "Not asking booking {ReservationId} whether they are on their way: it is {Status}, answered {Answered}, "
                + "decided by the floor {Decided}.",
                notice.ReservationId, booking?.Status, booking?.Answered, booking?.FloorDecided);

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);
        var (title, body) = booking.PreOrderWaiting
            ? NotificationText.OnYourWay(locale, notice.VenueName, notice.LocalStartTime)
            : NotificationText.OnYourWayWithoutPreOrder(locale, notice.VenueName, notice.LocalStartTime);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "reservation-on-your-way",
                    ["reservationId"] = notice.ReservationId.ToString(),
                    ["code"] = notice.ReservationCode,
                    ["actions"] = "on-my-way,cancel",
                },
                CategoryId: "on-your-way"),
            cancellationToken);
    }
}
