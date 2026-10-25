using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Declines the waiting bookings nobody answered in time - short-notice ones the venue did not get to,
/// and ones waiting on the guest before them on the table - and tells their diners.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sweep and not a message.</b> The outbox could carry "decline this at 19:15" as a
/// scheduled message, and on any evening the machine slept through 19:15 it would arrive more than an
/// hour late and be dead-lettered as stale - leaving a diner on a "waiting for the venue" screen with
/// no end. A request nobody answered must end however late the server notices, so this reads the
/// bookings themselves: every pending one whose <c>ApprovalExpiresAtUtc</c> has passed, through one
/// small filtered index.
/// </para>
/// <para>
/// <b>Each booking on its own, under its own row version.</b> Read fresh, checked again, declined,
/// saved - with the apology push, its feed entry and the cancelling of its reminders in the same
/// save. A waiter who approved it a moment ago moved its row version, so the save matches nothing and
/// the booking is left as the waiter left it; a second process sweeping the same booking loses the
/// same way. Either way the loser writes nothing, so a booking is declined - and its diner told -
/// exactly once.
/// </para>
/// <para>
/// <b>One booking that fails does not stall the rest.</b> Anything one of them throws is logged with
/// its code and the pass moves on; that booking is not tried again in the same pass, and the next pass
/// - twenty seconds on - tries it afresh.
/// </para>
/// <para>
/// <b>A change of a confirmed booking</b> nobody answered is a no to the change, not to the booking:
/// it goes back to the slot it had when that slot is still free (<see cref="PendingDecline"/>). And a
/// request the sweep only reaches after the booking would have started - the machine slept - is
/// declined without an apology for an evening that has already gone.
/// </para>
/// <para>
/// No table is released. The booking was never promised, so there is no hold to let go and nothing
/// on the floor changes; what this ends is a question nobody answered.
/// </para>
/// </remarks>
internal sealed class ApprovalExpirySweep(
    YallaDbContext db,
    IClock clock,
    ReservationWriter writer,
    ILogger<ApprovalExpirySweep> logger)
{
    /// <summary>
    /// How many lapsed requests are read at a time. A pass keeps reading until none is left that it has
    /// not tried, so a backlog after a long sleep is cleared in one pass without one long read.
    /// </summary>
    internal const int BatchSize = 50;

    /// <summary>One pass. Returns how many bookings it declined, or sent back to the slot they had.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;
        var tried = new HashSet<Guid>();
        var declined = 0;

        while (true)
        {
            var exclude = tried.ToList();

            // Served by IX_Reservations_ApprovalExpiresAtUtc, which holds only the requests still open:
            // the column is cleared whenever a booking stops waiting. The status is asked again anyway,
            // because a row is what it is and not what an index promised.
            var lapsed = await db.Reservations
                .AsNoTracking()
                .Where(r => r.ApprovalExpiresAtUtc != null
                            && r.ApprovalExpiresAtUtc <= nowUtc
                            && r.Status == ReservationStatus.PendingApproval
                            && !exclude.Contains(r.Id))
                .OrderBy(r => r.ApprovalExpiresAtUtc)
                .Select(r => new { r.Id, r.Code })
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (lapsed.Count == 0)
            {
                return declined;
            }

            foreach (var booking in lapsed)
            {
                tried.Add(booking.Id);

                try
                {
                    if (await DeclineAsync(booking.Id, nowUtc, cancellationToken))
                    {
                        declined++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Whatever went wrong with this one - a lock that could not be had, a row the
                    // domain refuses, the database hiccuping - is this one's, and the rest of the pass
                    // goes on. Nothing of it was saved; the next pass tries it again.
                    db.ChangeTracker.Clear();

                    logger.LogError(
                        ex,
                        "Could not decline lapsed booking {Code} ({ReservationId}); the sweep will try it again on its next pass.",
                        booking.Code, booking.Id);
                }
            }
        }
    }

    /// <summary>Declines one lapsed request, or leaves it if somebody decided it first.</summary>
    private async Task<bool> DeclineAsync(Guid reservationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        // A clean unit of work per booking, so one that lost its race leaves nothing tracked behind it
        // for the next to save by accident.
        db.ChangeTracker.Clear();

        var reservation = await db.Reservations
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        // Approved, declined or cancelled between the list and this read.
        if (reservation is null || !reservation.ApprovalHasLapsedAt(nowUtc))
        {
            return false;
        }

        var deadline = reservation.ApprovalExpiresAtUtc;

        // Tracked, as the request paths load it, so the decline's handlers read its names from the tracker.
        var branch = await db.Branches
            .Include(b => b.Venue)
            .FirstAsync(b => b.Id == reservation.BranchId, cancellationToken);

        // Whose silence: the guest before this booking on the table, when it was waiting on their answer
        // in the app; otherwise the venue's.
        var waitedOnEarlierGuest = reservation.ApprovalTrigger == ApprovalTrigger.EarlierGuestConsent
                                   && await db.TableShareRequests.AnyAsync(
                                       q => q.LaterReservationId == reservation.Id
                                            && q.Status == TableShareRequestStatus.Pending
                                            && q.AsksEarlierGuest,
                                       cancellationToken);

        PendingDeclineOutcome outcome;

        try
        {
            // Saved inside, with the apology - unless the booking would already have started - and the History's
            // line, the system's (the decline's handlers).
            outcome = await PendingDecline.DeclineAsync(
                db,
                writer,
                reservation,
                branch,
                waitedOnEarlierGuest ? DeclineReason.EarlierGuestNoAnswer : DeclineReason.NoAnswer,
                reason: null,
                nowUtc,
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A waiter decided it, the diner cancelled or moved it, or another process declined it,
            // since it was read. Whatever they did stands; nothing here was written.
            db.ChangeTracker.Clear();

            logger.LogInformation(
                "Booking {Code} changed while its lapsed request was being declined; it is left as it now stands.",
                reservation.Code);

            return false;
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.OutboxIdempotency))
        {
            // Another process declined it and told the diner first.
            db.ChangeTracker.Clear();

            logger.LogInformation(
                "Booking {Code} was declined for no answer by another sweep first; nothing more to do.",
                reservation.Code);

            return false;
        }

        logger.LogInformation(
            "Booking {Code} at branch {BranchId}: nobody answered by {Deadline:o}. {What} {Told}",
            reservation.Code,
            reservation.BranchId,
            deadline,
            outcome.OriginalKept
                ? "The change was declined and the booking is back at the slot it had."
                : outcome.WasChange
                    ? "The change was declined, and the slot it had was gone too, so it is declined."
                    : "It is declined.",
            outcome.DinerTold ? "The diner has been told." : "It would already have started, so the diner was not sent an apology.");

        return true;
    }
}
