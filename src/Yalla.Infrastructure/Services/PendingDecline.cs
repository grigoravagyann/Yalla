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
/// What it tells the rest of the system is the decline's handlers' (<see cref="ReservationDeclined"/>,
/// <c>docs/domain-events.md</c>): the History's line, the reminders withdrawn or rewritten, the diner's
/// <c>reservation.decided</c> push and <c>booking-declined</c> feed entry with <c>wasChange</c> and <c>originalKept</c> - not
/// sent when it was the clock's decline of a booking that would already have started (a machine that slept) - the guest
/// before it told of their own no, and the change stream.
/// </para>
/// </remarks>
internal static class PendingDecline
{
    /// <summary>Declines a waiting booking, or the waiting change to one, and saves.</summary>
    /// <param name="db">The unit of work.</param>
    /// <param name="writer">The writer, for the previous table's lock and the save.</param>
    /// <param name="reservation">The booking, tracked.</param>
    /// <param name="branch">Its branch.</param>
    /// <param name="because">Whose no, or whose silence.</param>
    /// <param name="reason">Why, as somebody typed it.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="answeredRequest">The request the guest before said no to, when it is their answer.</param>
    /// <exception cref="ReservationApprovalExpiredException">A staff decline after the answer window closed.</exception>
    /// <exception cref="DbUpdateConcurrencyException">The booking changed since it was read.</exception>
    /// <exception cref="TableLockTimeoutException">The previous table's lock could not be had in time.</exception>
    public static async Task<PendingDeclineOutcome> DeclineAsync(
        YallaDbContext db,
        ReservationWriter writer,
        Reservation reservation,
        Branch branch,
        DeclineReason because,
        string? reason,
        DateTime nowUtc,
        CancellationToken cancellationToken,
        TableShareRequest? answeredRequest = null)
    {
        // The clock's decline of a booking that would already have started is not apologised for (its handler's rule).
        var quiet = Reservation.IsForNoAnswer(because);

        // The question this booking was waiting on - to the guest before it on the table - ends with it:
        // a no, or nobody in time.
        await TableShares.CloseForLaterAsync(
            db,
            reservation.Id,
            quiet ? TableShareRequestStatus.Expired : TableShareRequestStatus.Declined,
            nowUtc,
            cancellationToken);

        if (!reservation.HasConfirmedSlotToReturnTo)
        {
            var tell = !quiet || reservation.StartUtc > nowUtc;

            if (quiet)
            {
                reservation.DeclineForNoAnswer(nowUtc, because);
            }
            else
            {
                reservation.Reject(nowUtc, reason, because, answeredRequest);
            }

            // And whoever was asking this booking's guest for the table no longer has to wait for them.
            await TableShares.EarlierChangedAsync(db, reservation, branch, nowUtc, cancellationToken);

            await writer.SaveAsync(cancellationToken);

            return new PendingDeclineOutcome(WasChange: false, OriginalKept: false, DinerTold: tell);
        }

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
                // Back to the slot it had - its reminders rewritten for it - or gone with the change.
                if (free)
                {
                    reservation.KeepConfirmedSlot(nowUtc, because, reason, answeredRequest);
                }
                else
                {
                    reservation.DeclineChange(nowUtc, because, reason, answeredRequest);
                }

                // Either way it moved, and a guest asked to free a table for it - or whose table it asked for - is
                // settled by where it now stands.
                await TableShares.EarlierChangedAsync(db, reservation, branch, nowUtc, token);

                // A kept booking is worth telling about while it is still to come; a lost one while the change it
                // asked for still was.
                told = !quiet || (free ? reservation.StartUtc : requestedStartUtc) > nowUtc;
            },
            cancellationToken);

        return new PendingDeclineOutcome(WasChange: true, OriginalKept: kept, DinerTold: told);
    }
}
