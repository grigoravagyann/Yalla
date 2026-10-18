using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Declines the short-notice bookings nobody at the venue answered in time, and tells their diners.
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
/// No table is released. The booking was never promised, so there is no hold to let go and nothing
/// on the floor changes; what this ends is a question nobody answered.
/// </para>
/// </remarks>
internal sealed class ApprovalExpirySweep(
    YallaDbContext db,
    IClock clock,
    IOutbox outbox,
    ILogger<ApprovalExpirySweep> logger)
{
    /// <summary>
    /// The most requests one pass declines. A pass every twenty seconds that finds more than this has
    /// the rest on the next one; the bound keeps a backlog after a long sleep from becoming one long
    /// pass that holds a scope open.
    /// </summary>
    internal const int BatchSize = 50;

    /// <summary>One pass. Returns how many bookings it declined.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;

        // Served by IX_Reservations_ApprovalExpiresAtUtc, which holds only the requests still open:
        // the column is cleared whenever a booking stops waiting. The status is asked again anyway,
        // because a row is what it is and not what an index promised.
        var lapsed = await db.Reservations
            .AsNoTracking()
            .Where(r => r.ApprovalExpiresAtUtc != null
                        && r.ApprovalExpiresAtUtc <= nowUtc
                        && r.Status == ReservationStatus.PendingApproval)
            .OrderBy(r => r.ApprovalExpiresAtUtc)
            .Select(r => r.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var declined = 0;

        foreach (var reservationId in lapsed)
        {
            if (await DeclineAsync(reservationId, nowUtc, cancellationToken))
            {
                declined++;
            }
        }

        return declined;
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

        var branch = await db.Branches
            .AsNoTracking()
            .Include(b => b.Venue)
            .FirstAsync(b => b.Id == reservation.BranchId, cancellationToken);

        reservation.DeclineForNoAnswer(nowUtc);

        // It is not going to happen, so its reminder and late nudge must not fire, and the reminder's
        // feed entry that has not appeared yet goes with them - as for any decline.
        await outbox.CancelAsync(OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);
        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);

        // The apology, not the venue's "no": expired is what the push says and what the feed carries.
        ReservationService.TellDinerDecided(
            db,
            outbox,
            reservation,
            branch,
            approved: false,
            expired: true,
            OutboxMessageTypes.KeyFor("reservation", reservation.Id, "expired"),
            nowUtc);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A waiter decided it, the diner cancelled or moved it, or another process declined it,
            // since it was read. Whatever they did stands; nothing here was written.
            logger.LogInformation(
                "Booking {Code} changed while its lapsed request was being declined; it is left as it now stands.",
                reservation.Code);

            return false;
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.OutboxIdempotency))
        {
            // Another process declined it and told the diner first.
            logger.LogInformation(
                "Booking {Code} was declined for no answer by another sweep first; nothing more to do.",
                reservation.Code);

            return false;
        }

        logger.LogInformation(
            "Booking {Code} at branch {BranchId} was declined: nobody answered by {Deadline:o}. The diner has been told.",
            reservation.Code, reservation.BranchId, deadline);

        return true;
    }
}
