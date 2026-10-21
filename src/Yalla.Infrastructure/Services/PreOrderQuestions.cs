using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// "Are you on your way?" - the push and the feed entry asked before a confirmed booking - written, withdrawn and
/// rewritten in the caller's unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every booking is asked</b> (2026-10-21, the owner's decision): a guest who says yes is not called by the waiter.
/// 30 minutes before the start (<see cref="PreOrderRules.OnYourWayMinutesBeforeWithoutPreOrder"/>), or 45 while a
/// pre-order waits (<see cref="PreOrderRules.OnYourWayMinutesBefore"/>) - the kitchen needs the answer earlier than the
/// floor does. Saving a pre-order moves the question to 45 and clearing it moves it back (<see cref="AskAgainAsync"/>);
/// a pre-order sent to the kitchen or a party marked not coming withdraws it, because the waiter has decided.
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

    /// <summary>Every question for the booking's current start, at either time.</summary>
    public static string StartPrefixFor(Reservation reservation) =>
        OutboxMessageTypes.KeyFor(
            "reservation",
            reservation.Id,
            $"on-your-way:{reservation.StartUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}");

    /// <summary>
    /// The question for the booking's current start. The pre-order's keeps the key it had before every booking was
    /// asked; the plain one is told apart by its minutes, so moving between the two is a withdraw and a write.
    /// </summary>
    public static string KeyFor(Reservation reservation, bool preOrderWaiting = true) =>
        preOrderWaiting
            ? StartPrefixFor(reservation)
            : $"{StartPrefixFor(reservation)}:{PreOrderRules.OnYourWayMinutesBeforeWithoutPreOrder}";

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
    /// <param name="preOrderWaiting">A pre-order waits for the kitchen: asked at 45 minutes rather than 30.</param>
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
        bool preOrderWaiting,
        DateTime nowUtc)
    {
        var askAt = PreOrderRules.OnYourWayAtUtc(reservation.StartUtc, preOrderWaiting);
        var key = KeyFor(reservation, preOrderWaiting);

        // A booking made or moved inside its last half hour is not followed by "are you on your way?" seconds later:
        // the guest who just booked it has said as much. The waiter's card shows the number to call instead. Nor is a
        // question for this start asked twice, at either time.
        if (askAt <= nowUtc || written.Contains(key) || written.Any(k => k.StartsWith(StartPrefixFor(reservation), StringComparison.Ordinal)))
        {
            return;
        }

        outbox.Enqueue(OutboxMessageTypes.ReservationOnYourWay, payload, askAt, key);

        if (writeFeed)
        {
            DinerNotices.BookingOnYourWay(db, reservation, venueName, branchName, askAt);
        }
    }

    /// <summary>
    /// Asks again at the time the pre-order now calls for - 45 minutes while it waits, 30 once it is cleared - unless the
    /// question for this start already went out: a guest is not asked twice.
    /// </summary>
    public static async Task AskAgainAsync(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        object payload,
        string venueName,
        string branchName,
        bool writeFeed,
        bool preOrderWaiting,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var forThisStart = StartPrefixFor(reservation);

        if (await db.OutboxMessages.AnyAsync(
                m => m.IdempotencyKey.StartsWith(forThisStart) && m.SentAtUtc != null, cancellationToken))
        {
            return;
        }

        await WithdrawAsync(db, outbox, reservation.Id, nowUtc, cancellationToken);

        Enqueue(
            db, outbox, reservation, payload, venueName, branchName, writeFeed, new HashSet<string>(), preOrderWaiting,
            nowUtc);
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
