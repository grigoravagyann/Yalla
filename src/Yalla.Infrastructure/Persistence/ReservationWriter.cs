using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// The only sanctioned way a <see cref="Reservation"/> row is created.
/// </summary>
/// <remarks>
/// <para>
/// Nothing else in the codebase calls <c>db.Reservations.Add</c>, and nothing else should. An
/// insert that skipped this class would skip the table lock with it, and the failure would not look
/// like a bug: both bookings return 201, both diners get a confirmation, and the venue finds out at
/// eight o'clock when two parties arrive for table 7. There is no constraint in SQL Server that
/// catches it afterwards - see <c>docs/reservations.md</c> - so the discipline of one entry point is
/// the guarantee, which is why the insert lives behind a method and not in a service body where the
/// next feature can quietly add a second one.
/// </para>
/// <para>
/// The service layer above keeps everything that is not concurrency-critical: validation, the
/// approval decision, and building the conflict payloads. Those are wide reads, and holding a
/// table's lock while assembling a response body would queue every other booker behind it.
/// </para>
/// </remarks>
internal sealed class ReservationWriter(
    YallaDbContext db,
    TableLock tableLock,
    ILogger<ReservationWriter> logger)
{
    /// <summary>
    /// How many times to re-roll a colliding reservation code before giving up.
    /// </summary>
    /// <remarks>
    /// One in ~887 million per attempt, so three is already superstition. It exists because the
    /// unique index - not the generator - is what guarantees the code is unique, and a system that
    /// relies on an index must have an answer for the index firing.
    /// </remarks>
    private const int CodeAttempts = 3;

    /// <summary>
    /// Takes the table's lock, re-checks the slot inside it, and inserts the booking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three re-checks happen <b>inside</b> the lock and in this order, which is not arbitrary:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>Replay first.</b> Two retries of the same booking can race, the first committing while
    /// the second waits here. By the time the second gets in, the first is visible - and an overlap
    /// check run first would call the diner's own booking a conflict and answer 409 for a table
    /// they already have.
    /// </item>
    /// <item><b>Then overlapping bookings</b>, the rule the unit tests pin at its boundaries.</item>
    /// <item>
    /// <b>Then the people actually sitting there.</b> <see cref="TableSession"/> is the
    /// authoritative occupancy record and the conflict rule was blind to it until Prompt 7: a
    /// waiter seating a walk-in at seven did not stop a phone booking the same table for eight, and
    /// the diner arrived to find it still occupied.
    /// </item>
    /// </list>
    /// </remarks>
    /// <returns>
    /// What happened, so the caller can build the answer outside the lock. Only
    /// <see cref="InsertOutcome.Inserted"/> means a row was written.
    /// </returns>
    /// <exception cref="TableLockTimeoutException">
    /// Another writer held the table for longer than the wait allows. Nothing was written, and the
    /// same command sent again will very likely succeed.
    /// </exception>
    public async Task<InsertOutcome> InsertUnderTableLockAsync(
        Reservation reservation,
        DiningTable table,
        BookedInterval proposed,
        ReservationPolicy policy,
        Guid dinerUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(table);

        try
        {
            await using var locked = await tableLock.AcquireAsync(table.Id, table.Label, cancellationToken);

            if (await FindReplayAsync(reservation.ClientCommandId, dinerUserId, cancellationToken) is { } original)
            {
                Detach(reservation);
                return InsertOutcome.Replayed(original);
            }

            // The table itself, re-read inside the lock. Validation ran against a copy read before
            // it, and a waiter marking the table broken in between is precisely the race the lock
            // was added for - without this re-check the lock only makes the two commits serial and
            // still lets a confirmed reservation land on a table nobody can sit at.
            await ReadTableStatusUnderLockAsync(table, cancellationToken);

            if (await FindConflictAsync(table.Id, proposed, policy, cancellationToken) is { } conflict)
            {
                // Nothing was written. Leave the lock and build the answer outside it.
                Detach(reservation);
                return InsertOutcome.Conflicted(conflict);
            }

            if (await FindOccupyingSessionAsync(table.Id, proposed, policy, cancellationToken) is { } sitting)
            {
                Detach(reservation);
                return InsertOutcome.OccupiedBy(sitting);
            }

            db.Reservations.Add(reservation);

            if (await SaveWithFreshCodeAsync(reservation, dinerUserId, cancellationToken) is { } winner)
            {
                return InsertOutcome.Replayed(winner);
            }

            await locked.CommitAsync(cancellationToken);

            return InsertOutcome.Inserted;
        }
        catch (TableLockTimeoutException)
        {
            Detach(reservation);
            throw;
        }
    }

    /// <summary>
    /// Refuses a table that stopped being bookable while this booking was being validated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Freeing and holding only ever narrow what a booking finds, which is why they take no lock.
    /// Going out of service is different in kind: it makes the table unusable, and a booking that
    /// committed a moment later would be confirmed onto it. Nobody would notice until the party
    /// arrived, because nothing else looks at a booking again once it is made.
    /// </para>
    /// <para>
    /// A fresh read rather than the tracked entity: the caller's copy was loaded before the lock and
    /// says whatever was true then, which is the thing being guarded against.
    /// </para>
    /// </remarks>
    private async Task ReadTableStatusUnderLockAsync(DiningTable table, CancellationToken cancellationToken)
    {
        var current = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == table.Id)
            .Select(t => new { t.Status, t.IsBookable, t.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (current is null || !current.IsActive || !current.IsBookable)
        {
            throw new TableNotBookableException(table.Id, table.Label);
        }

        if (current.Status == TableStatus.OutOfService)
        {
            throw new TableOutOfServiceException(table.Id, table.Label);
        }
    }

    /// <summary>
    /// The booking this command already created, if it has one.
    /// </summary>
    /// <remarks>
    /// The diner is half the question, not a refinement of it. A replay is the same caller sending
    /// the same command again; the same id from a different caller is a collision, and answering it
    /// with the booking that holds the id would disclose that booking's door code, guest name and
    /// phone number to somebody who only had to reuse a Guid.
    /// </remarks>
    public Task<Reservation?> FindReplayAsync(
        Guid clientCommandId,
        Guid dinerUserId,
        CancellationToken cancellationToken) =>
        db.Reservations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.ClientCommandId == clientCommandId && r.DinerUserId == dinerUserId,
                cancellationToken);

    /// <summary>
    /// The overlap re-check, as one indexed range read.
    /// </summary>
    /// <remarks>
    /// The range predicate is the overlap rule rearranged so SQL Server can seek it - see
    /// <see cref="ReservationOverlap.SearchWindow"/> - and every row it returns is then put through
    /// <see cref="ReservationOverlap"/> itself. The rule proved at its boundaries by the unit tests
    /// is therefore the rule that decides the insert, and the SQL is only an index-friendly way of
    /// narrowing the candidates.
    /// </remarks>
    private async Task<Reservation?> FindConflictAsync(
        Guid tableId,
        BookedInterval proposed,
        ReservationPolicy policy,
        CancellationToken cancellationToken)
    {
        var (searchFromUtc, searchToUtc) = ReservationOverlap.SearchWindow(proposed, policy.BufferMinutes);

        var candidates = await db.Reservations
            .AsNoTracking()
            .Where(r => r.DiningTableId == tableId
                        && (r.Status == ReservationStatus.Confirmed
                            || r.Status == ReservationStatus.PendingApproval
                            || r.Status == ReservationStatus.Seated)
                        && r.EndUtc > searchFromUtc
                        && r.StartUtc < searchToUtc)
            .OrderBy(r => r.StartUtc)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(r => ReservationOverlap.Conflicts(
            proposed, BookedInterval.Of(r), r.Status, policy.BufferMinutes));
    }

    /// <summary>The open sitting whose projected interval runs into the requested one, if any.</summary>
    private async Task<TableSession?> FindOccupyingSessionAsync(
        Guid tableId,
        BookedInterval proposed,
        ReservationPolicy policy,
        CancellationToken cancellationToken)
    {
        var open = await db.TableSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.DiningTableId == tableId && s.ClosedAtUtc == null, cancellationToken);

        if (open is null)
        {
            return null;
        }

        return SessionOccupancy.Conflicts(
            proposed, open.SeatedAtUtc, policy.TurnTimeMinutes, policy.BufferMinutes)
            ? open
            : null;
    }

    /// <summary>
    /// Saves the booking, re-rolling the door code if it collides, and answering a lost
    /// idempotency race with the booking that won.
    /// </summary>
    /// <returns>The winning booking when this was a racing replay, otherwise null.</returns>
    private async Task<Reservation?> SaveWithFreshCodeAsync(
        Reservation reservation,
        Guid dinerUserId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return null;
            }
            catch (DbUpdateException ex)
                when (UniqueViolation.IsOn(ex, DatabaseIndexNames.ReservationClientCommand))
            {
                // Two retries of the same offline command raced each other. The other one did the
                // work; this one answers from its row. The index is what makes this safe - a
                // check-then-insert would have let both through.
                Detach(reservation);

                // Scoped to the caller. The index is unique across every diner, so the row that
                // won may not be theirs at all - and handing it back would answer a stranger with
                // somebody else's door code, guest name and telephone number.
                var winner = await FindReplayAsync(
                    reservation.ClientCommandId, dinerUserId, cancellationToken);

                if (winner is null)
                {
                    logger.LogWarning(
                        "Booking command {ClientCommandId} is already held by another diner's booking.",
                        reservation.ClientCommandId);

                    throw new ClientCommandIdAlreadyUsedException(reservation.ClientCommandId);
                }

                logger.LogInformation(
                    "Booking command {ClientCommandId} was applied concurrently; replaying {Code}.",
                    reservation.ClientCommandId, winner.Code);

                return winner;
            }
            catch (DbUpdateException ex)
                when (attempt < CodeAttempts && UniqueViolation.IsOn(ex, DatabaseIndexNames.ReservationCode))
            {
                logger.LogWarning(
                    "Reservation code {Code} was already taken; generating another.", reservation.Code);

                reservation.ReplaceCode(ReservationCode.Generate());
            }
        }
    }

    /// <summary>
    /// Drops the unsaved booking from the change tracker so the caller's context is clean.
    /// </summary>
    /// <remarks>
    /// Every path out of the lock that did not insert comes through here. Leaving a rejected
    /// booking tracked would see it swept into the next unrelated <c>SaveChanges</c> on the same
    /// request - a booking nobody made, for a table that was refused.
    /// </remarks>
    private void Detach(Reservation reservation) => db.Entry(reservation).State = EntityState.Detached;
}

/// <summary>
/// What the locked insert decided. Exactly one of these is true, and only
/// <see cref="Inserted"/> means a row was written.
/// </summary>
internal readonly record struct InsertOutcome(
    Reservation? Conflict,
    Reservation? Replay,
    TableSession? Occupied)
{
    public static InsertOutcome Inserted => new(null, null, null);

    public static InsertOutcome Conflicted(Reservation conflict) => new(conflict, null, null);

    public static InsertOutcome Replayed(Reservation winner) => new(null, winner, null);

    public static InsertOutcome OccupiedBy(TableSession session) => new(null, null, session);
}
