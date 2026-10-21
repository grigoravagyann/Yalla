using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// "Are you on your way?" - the push and the feed entry asked 45 minutes before a booking with a pre-order - written,
/// withdrawn and rewritten in the caller's unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only bookings with a pre-order are asked.</b> The question exists so the kitchen knows whether to cook; asking
/// every booking would be a new push to every guest, which nobody decided. Generalising it is the one condition in
/// <c>ReservationService.EnqueueReminders</c> that passes <c>askOnYourWay</c>.
/// </para>
/// <para>
/// An outbox message scheduled ahead, like the reminder - not a sweep: its key carries the start it is for
/// (<c>reservation:{id}:on-your-way:{yyyyMMddHHmm}</c>), so it is written once however often the pre-order is saved, a
/// move asks again at the new time, and cancelling the booking - which cancels everything under
/// <c>reservation:{id}:</c> - withdraws it with the rest. The outbox drops what is over an hour overdue; a question
/// 45 minutes before the start that a sleeping machine missed is one nobody should be asked late anyway.
/// </para>
/// </remarks>
internal static class PreOrderQuestions
{
    /// <summary>Everything this booking was ever asked, whatever start it was for.</summary>
    public static string PrefixFor(Guid reservationId) =>
        OutboxMessageTypes.KeyFor("reservation", reservationId, "on-your-way:");

    /// <summary>The question for the booking's current start.</summary>
    public static string KeyFor(Reservation reservation) =>
        OutboxMessageTypes.KeyFor(
            "reservation",
            reservation.Id,
            $"on-your-way:{reservation.StartUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Writes the question for the booking's current start - push and, when <paramref name="writeFeed"/>, the feed
    /// entry - unless its moment has passed or one under the same key was already written (<paramref name="written"/>).
    /// </summary>
    /// <param name="db">The caller's unit of work, for the feed entry.</param>
    /// <param name="outbox">The caller's outbox.</param>
    /// <param name="reservation">The booking, at its current start.</param>
    /// <param name="payload">The push's payload: the reminder's notice, so the handler reads the same shape.</param>
    /// <param name="venueName">The venue, as the feed entry names it.</param>
    /// <param name="branchName">The branch, as the feed entry names it.</param>
    /// <param name="writeFeed">The account is live, under its lock: the feed entry may be written.</param>
    /// <param name="written">Keys already written - sent or waiting: one under the same key is not written again.</param>
    /// <param name="nowUtc">Now.</param>
    public static void Enqueue(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        object payload,
        string venueName,
        string branchName,
        bool writeFeed,
        IReadOnlySet<string> written,
        DateTime nowUtc)
    {
        var askAt = PreOrderRules.OnYourWayAtUtc(reservation.StartUtc);
        var key = KeyFor(reservation);

        // A move into the last 45 minutes is not followed by "are you on your way?" seconds later: the guest who just
        // moved it has said as much. The waiter's card shows the number to call instead.
        if (askAt <= nowUtc || written.Contains(key))
        {
            return;
        }

        outbox.Enqueue(OutboxMessageTypes.ReservationOnYourWay, payload, askAt, key);

        if (writeFeed)
        {
            DinerNotices.BookingOnYourWay(db, reservation, venueName, branchName, askAt);
        }
    }

    /// <summary>Withdraws the booking's unsent question and its feed entry that has not appeared.</summary>
    public static async Task WithdrawAsync(
        YallaDbContext db, IOutbox outbox, Guid reservationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await outbox.CancelAsync(PrefixFor(reservationId), cancellationToken);

        var unshown = await db.DinerNotifications
            .Where(n => n.ReservationId == reservationId
                        && n.Kind == DinerNotificationKinds.BookingOnYourWay
                        && n.CreatedAtUtc > nowUtc)
            .ToListAsync(cancellationToken);

        db.DinerNotifications.RemoveRange(unshown);
    }
}
