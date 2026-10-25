using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A declined change of a confirmed booking, as the diner is told about it.
/// </summary>
/// <param name="OriginalKept">
/// Whether the booking went back to the confirmed slot it had - false when that slot was taken, sat
/// into or past by the time the change was declined, and the booking ended with the change.
/// </param>
/// <param name="RequestedDate">The local date the change asked for.</param>
/// <param name="RequestedTime">The local start time the change asked for.</param>
internal sealed record DeclinedChange(bool OriginalKept, DateOnly RequestedDate, TimeOnly RequestedTime);

/// <summary>What declining a waiting booking came to.</summary>
/// <param name="WasChange">Whether it was a change of a confirmed booking rather than a new booking.</param>
/// <param name="OriginalKept">Whether the booking went back to the confirmed slot it had.</param>
/// <param name="DinerTold">Whether a push and a feed entry were written.</param>
internal readonly record struct PendingDeclineOutcome(bool WasChange, bool OriginalKept, bool DinerTold);

/// <summary>
/// Declining a booking that is waiting for the venue - by a member of staff, or by the sweep when
/// nobody answered - in one place, saved.
/// </summary>
/// <remarks>
/// <para>
/// <b>A new booking</b> is declined: <c>CancelledByVenue</c>, its reminders cancelled, the diner told.
/// </para>
/// <para>
/// <b>A change of a confirmed booking</b> - tonight's 19:00 moved to 21:00, waiting for the floor - is
/// a no to the change, not to the booking. Under the lock of the table the booking came from, the
/// confirmed slot is judged again (another diner may have booked it since, a walk-in may be sitting
/// into it, it may already be over); when it is still free the booking goes back to it, confirmed as
/// before, with its reminders rewritten for it, and the diner is told their booking stands. When it is
/// not, the booking is declined as a new one would be, and the diner is told that too.
/// </para>
/// <para>
/// The diner is told in the same save: a <c>reservation.decided</c> push with <c>wasChange</c> and
/// <c>originalKept</c>, and a <c>booking-declined</c> feed entry carrying the same. The sweep passes
/// <c>quietIfAlreadyStarted</c>: a request it gets to only after the booking would have started -
/// a machine that slept - is declined without an apology for an evening already gone. A kept
/// booking still to come is told about whatever else is true.
/// </para>
/// </remarks>
internal static class PendingDecline
{
    /// <summary>Declines a waiting booking, or the waiting change to one, and saves.</summary>
    /// <exception cref="ReservationApprovalExpiredException">A staff decline after the answer window closed.</exception>
    /// <exception cref="DbUpdateConcurrencyException">The booking changed since it was read.</exception>
    /// <exception cref="TableLockTimeoutException">The previous table's lock could not be had in time.</exception>
    public static async Task<PendingDeclineOutcome> DeclineAsync(
        YallaDbContext db,
        IOutbox outbox,
        ReservationWriter writer,
        Reservation reservation,
        Branch branch,
        DeclineReason because,
        string? reason,
        bool quietIfAlreadyStarted,
        DateTime nowUtc,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? recordAsync = null)
    {
        var expired = Reservation.IsForNoAnswer(because);
        var key = await ReservationNotices.DecisionKeyAsync(
            db, reservation.Id, expired ? "expired" : "rejected", nowUtc, cancellationToken);

        // The question this booking was waiting on - to the guest before it on the table - ends with it:
        // a no, or nobody in time.
        await TableShares.CloseForLaterAsync(
            db,
            reservation.Id,
            expired ? TableShareRequestStatus.Expired : TableShareRequestStatus.Declined,
            nowUtc,
            cancellationToken);

        if (!reservation.HasConfirmedSlotToReturnTo)
        {
            var tell = !quietIfAlreadyStarted || reservation.StartUtc > nowUtc;

            End(reservation, because, reason, nowUtc);
            await CancelWhatWasQueuedAsync(db, outbox, reservation, nowUtc, cancellationToken);

            // And whoever was asking this booking's guest for the table no longer has to wait for them.
            await TableShares.EarlierChangedAsync(
                db, outbox, reservation, branch, nowUtc, cancellationToken);

            if (tell)
            {
                ReservationNotices.TellDinerDecided(
                    db, outbox, reservation, branch, approved: false, expired, key, nowUtc, because: because);
            }

            // The activity entry, in this save (docs/activity.md).
            if (recordAsync is not null)
            {
                await recordAsync(cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);

            return new PendingDeclineOutcome(WasChange: false, OriginalKept: false, DinerTold: tell);
        }

        var requestedDate = reservation.LocalDate;
        var requestedTime = reservation.LocalStartTime;
        var requestedStartUtc = reservation.StartUtc;
        var tableId = reservation.PreviousDiningTableId!.Value;
        var slot = new BookedInterval(reservation.PreviousStartUtc!.Value, reservation.PreviousEndUtc!.Value);

        var tableLabel = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == tableId)
            .Select(t => t.Label)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var told = false;

        var kept = await writer.ReturnUnderTableLockAsync(
            reservation,
            tableId,
            tableLabel,
            slot,
            branch.ReservationPolicy,
            nowUtc,
            async (free, token) =>
            {
                if (free)
                {
                    reservation.KeepConfirmedSlot(nowUtc, because);

                    // Its reminder and late nudge were rewritten for the change; they go back to the slot.
                    await ReservationReminders.ReplaceRemindersAsync(
                        db, outbox, reservation, branch, tableLabel, nowUtc, token);
                }
                else
                {
                    End(reservation, because, reason, nowUtc);
                    await CancelWhatWasQueuedAsync(db, outbox, reservation, nowUtc, token);
                }

                // Back at the slot it had, or gone: either way it moved, and a guest asked to free a
                // table for it - or whose table it asked for - is settled by where it now stands.
                await TableShares.EarlierChangedAsync(
                    db, outbox, reservation, branch, nowUtc, token);

                // A kept booking is worth telling about while it is still to come; a lost one while the
                // change it asked for still was.
                told = !quietIfAlreadyStarted || (free ? reservation.StartUtc : requestedStartUtc) > nowUtc;

                if (told)
                {
                    ReservationNotices.TellDinerDecided(
                        db,
                        outbox,
                        reservation,
                        branch,
                        approved: false,
                        expired,
                        key,
                        nowUtc,
                        new DeclinedChange(free, requestedDate, requestedTime),
                        because);
                }

                if (recordAsync is not null)
                {
                    await recordAsync(token);
                }
            },
            cancellationToken);

        return new PendingDeclineOutcome(WasChange: true, OriginalKept: kept, DinerTold: told);
    }

    /// <summary>
    /// Ends the booking: declined - by staff, or by the guest before it on the table - or for nobody
    /// answering in time.
    /// </summary>
    private static void End(Reservation reservation, DeclineReason because, string? reason, DateTime nowUtc)
    {
        if (Reservation.IsForNoAnswer(because))
        {
            reservation.DeclineForNoAnswer(nowUtc, because);
        }
        else
        {
            reservation.Reject(nowUtc, reason, because);
        }
    }

    /// <summary>
    /// A booking that is not going to happen: its unsent reminder and nudge, and its feed entry not yet
    /// shown, go with it.
    /// </summary>
    private static async Task CancelWhatWasQueuedAsync(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await outbox.CancelAsync(OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);
        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);
    }
}
