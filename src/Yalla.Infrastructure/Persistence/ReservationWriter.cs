using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// The only sanctioned way a <see cref="Reservation"/> row is created, or moved to another slot.
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
/// <b>Moving a booking is the same hazard</b>, so it lives here too. A booking moved onto table 7 at
/// eight collides with nothing a row version can see - it updates its own row, and the booking it
/// now overlaps is a different one - so it takes the target table's lock and re-checks inside it,
/// exactly as an insert does.
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
    /// The re-checks happen <b>inside</b> the lock and in this order, which is not arbitrary:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>Replay first.</b> Two retries of the same booking can race, the first committing while
    /// the second waits here. By the time the second gets in, the first is visible - and an overlap
    /// check run first would call the diner's own booking a conflict and answer 409 for a table
    /// they already have.
    /// </item>
    /// <item>
    /// <b>Then the table's evening</b>, by <see cref="TableSequencing"/>: the bookings around the start
    /// (ninety minutes after the previous one, forty-five before the next, the original overlap rule
    /// underneath), the questions open to their guests, and the people actually sitting there -
    /// <see cref="TableSession"/> is the authoritative occupancy record, and the conflict rule was
    /// blind to it until Prompt 7.
    /// </item>
    /// <item>
    /// <b>Then the time to leave by.</b> When a full sitting would run into the next booking, the
    /// diner must have accepted exactly that time (<paramref name="acceptsFreeByUtc"/>).
    /// </item>
    /// </list>
    /// <para>
    /// Only then does <paramref name="stageAsync"/> build the booking - with the end, the status and any
    /// question to the guest before that the verdict calls for - staging whatever else goes with it in
    /// the same context; all of it commits in this one transaction or none of it does. It must not save.
    /// </para>
    /// </remarks>
    /// <returns>
    /// What happened, so the caller can build the answer outside the lock. Only
    /// <see cref="InsertOutcome.Booking"/> set means a row was written.
    /// </returns>
    /// <exception cref="TableLockTimeoutException">
    /// Another writer held the table for longer than the wait allows. Nothing was written, and the
    /// same command sent again will very likely succeed.
    /// </exception>
    public async Task<InsertOutcome> InsertUnderTableLockAsync(
        Guid clientCommandId,
        DiningTable table,
        DateTime startUtc,
        ReservationPolicy policy,
        Guid dinerUserId,
        DateTime? acceptsFreeByUtc,
        Func<TableSequencingVerdict, CancellationToken, Task<Reservation>> stageAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(stageAsync);

        try
        {
            await using var locked = await tableLock.AcquireAsync(table.Id, table.Label, cancellationToken);

            // The account's lock too (DinerAccountLock), inside the table's. A booking committed behind
            // the account's deletion would keep the deleted person's id on the venue's record; one that
            // gets here second is refused with session-revoked, and deletion cuts loose one that was first.
            await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);

            if (await FindReplayAsync(clientCommandId, dinerUserId, cancellationToken) is { } original)
            {
                return InsertOutcome.Replayed(original);
            }

            // The table itself, re-read inside the lock. Validation ran against a copy read before
            // it, and a waiter marking the table broken in between is precisely the race the lock
            // was added for - without this re-check the lock only makes the two commits serial and
            // still lets a confirmed reservation land on a table nobody can sit at.
            await ReadTableStatusUnderLockAsync(table, cancellationToken);

            // The table's evening - the bookings around the start, the open questions to their guests,
            // the party at it now - judged by the one rule every read shares.
            var (verdict, sitting) = await TableEvening.JudgeAsync(
                db, table.Id, startUtc, policy, excludingReservationId: Guid.Empty, cancellationToken);

            if (Refusal(verdict, sitting) is { } refused)
            {
                // Nothing was written. Leave the lock and build the answer outside it.
                return refused.Clash is { } clash ? InsertOutcome.Conflicted(clash) : InsertOutcome.OccupiedBy(refused.Sitting!);
            }

            if (verdict.MustFreeByUtc is { } required && !SameMinute(acceptsFreeByUtc, required))
            {
                return InsertOutcome.FreeByRequired(new FreeByRequirement(required, verdict.Next!.Value.StartUtc));
            }

            // Only now is the booking built: its end, its status and any question to the guest before
            // follow from what the lock found.
            var reservation = await stageAsync(verdict, cancellationToken);

            db.Reservations.Add(reservation);

            if (await SaveWithFreshCodeAsync(reservation, dinerUserId, cancellationToken) is { } winner)
            {
                return InsertOutcome.Replayed(winner);
            }

            await locked.CommitAsync(cancellationToken);

            return InsertOutcome.Inserted(reservation);
        }
        catch
        {
            // A lock that could not be had, a refusal from the domain while staging, a save that
            // failed: whatever was staged for this booking leaves the context with it, so the next
            // unrelated save on the request does not write a booking nobody made.
            DetachStaged();
            throw;
        }
    }

    /// <summary>
    /// Takes the target table's lock, re-checks the new slot inside it, and moves the booking there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The insert's protocol, step for step, with two differences that are the whole point of a
    /// move:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>The booking being moved is not a conflict.</b> It is left out of the table's evening - and so
    /// is a question it asked the guest before it - so moving dinner by half an hour on the same table is
    /// not refused because of itself. A time to leave by it already promised for the same start need not
    /// be accepted again.
    /// </item>
    /// <item>
    /// <b>The write is an update, guarded by the booking's row version.</b> The booking was read
    /// before the lock, and a cancel, an approval or a seating that committed in between moved its
    /// <c>RowVersion</c>: the update then matches no row and EF raises
    /// <see cref="DbUpdateConcurrencyException"/> - a 409 <c>concurrent-update</c> - rather than
    /// resurrecting a cancelled booking at a new time.
    /// </item>
    /// </list>
    /// <para>
    /// Only the <b>target</b> table is locked. Leaving the old one only ever frees it, which narrows
    /// nothing anybody else finds - a booker on the old table who still sees this booking there is
    /// told no a moment too long, never yes wrongly.
    /// </para>
    /// <para>
    /// <paramref name="applyAsync"/> stages the change itself - the domain's
    /// <see cref="Reservation.Reschedule"/> with the end and status the verdict it is handed calls for,
    /// the questions to guests withdrawn or asked, the replaced reminders, the record of the command - and
    /// is called inside the lock once every re-check has passed, so all of it commits in this one
    /// transaction or none of it does. It must not save.
    /// </para>
    /// </remarks>
    /// <returns>What happened. Only <see cref="MoveOutcome.Moved"/> means the booking was changed.</returns>
    /// <exception cref="TableLockTimeoutException">
    /// Another writer held the table for longer than the wait allows. Nothing was changed.
    /// </exception>
    /// <exception cref="DbUpdateConcurrencyException">The booking changed since it was read.</exception>
    public async Task<MoveOutcome> MoveUnderTableLockAsync(
        Reservation reservation,
        DiningTable table,
        DateTime startUtc,
        ReservationPolicy policy,
        Guid dinerUserId,
        Guid clientCommandId,
        DateTime? acceptsFreeByUtc,
        bool keepsItsSlot,
        Func<TableSequencingVerdict, CancellationToken, Task> applyAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(applyAsync);

        await using var locked = await tableLock.AcquireAsync(table.Id, table.Label, cancellationToken);

        try
        {
            // The account's lock inside the table's, as for an insert: a move committed behind the
            // account's deletion would put the deleted person's reminder back in the outbox.
            await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);

            // Replay first, for the same reason as an insert: a retry that waited here while the
            // original committed must be answered as a retry, not judged again against a table the
            // original has since taken.
            if (await db.ProcessedCommands.AnyAsync(c => c.ClientCommandId == clientCommandId, cancellationToken))
            {
                return MoveOutcome.Replayed;
            }

            await ReadTableStatusUnderLockAsync(table, cancellationToken);

            // The booking being moved is not in its own way, nor is a question it asked, nor a booking
            // that is only asking for its table: all are left out (TableEvening).
            var (verdict, sitting) = await TableEvening.JudgeAsync(
                db, table.Id, startUtc, policy, excludingReservationId: reservation.Id, cancellationToken);

            // A confirmed booking keeping its table and start is not judged again by the table's evening:
            // nothing the venue said yes to got harder to keep, whoever booked around it since. Only a
            // party sitting into it now still refuses it, as it always has.
            var refusal = Refusal(verdict, sitting);

            if (refusal is { } refused && (!keepsItsSlot || refused.Sitting is not null))
            {
                return refused.Clash is { } clash ? MoveOutcome.Conflicted(clash) : MoveOutcome.OccupiedBy(refused.Sitting!);
            }

            // A promise the booking already made for exactly this time need not be made again.
            if (!keepsItsSlot
                && verdict.MustFreeByUtc is { } required
                && !SameMinute(acceptsFreeByUtc, required)
                && !(reservation.StartUtc == startUtc && SameMinute(reservation.PromisedFreeByUtc, required)))
            {
                return MoveOutcome.FreeByRequired(new FreeByRequirement(required, verdict.Next!.Value.StartUtc));
            }

            await applyAsync(verdict, cancellationToken);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.ProcessedCommandId))
            {
                // The id was recorded by another request between the re-check above and this save -
                // one that took a different table's lock under the same id, or another command
                // altogether. Nothing of this one is kept - the lock scope rolls back - and the caller
                // decides outside the lock whether that was this change, answered as a replay, or a
                // collision.
                logger.LogInformation(
                    "Change command {ClientCommandId} was recorded concurrently; not applying it again.",
                    clientCommandId);

                db.ChangeTracker.Clear();

                return MoveOutcome.Replayed;
            }

            await locked.CommitAsync(cancellationToken);

            return MoveOutcome.Moved;
        }
        catch (Exception ex) when (TableLock.IsTimeout(ex))
        {
            // Inside the lock, a wait on a row somebody else holds - the booking's own, mid-cancel -
            // past the bound. Contention, not refusal, exactly as for the table's own lock.
            throw new TableLockTimeoutException(locked.TableId, locked.TableLabel, locked.TimeoutMilliseconds);
        }
    }

    /// <summary>
    /// Takes the lock of the table a waiting change came from, decides whether the booking can go back
    /// to the confirmed slot it had, and has the caller stage that - or the decline - inside the lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other direction of a move, and the same hazard: since the booking left that slot, another
    /// diner may have booked it, a walk-in may be sitting into it, or the table may have gone out of
    /// service. So the slot is judged under the table's lock by the table's evening
    /// (<see cref="TableEvening"/>) exactly as an insert or a move judges one - the booking itself left
    /// out - and it is free to go back to only when nothing refuses it, nobody has to be asked, and no
    /// booking since needs it gone before it ends. The answer, with whatever the caller stages on it,
    /// commits in this one transaction.
    /// </para>
    /// <para>
    /// A slot that has already ended cannot be gone back to whatever else is true of it.
    /// </para>
    /// <para>
    /// <paramref name="applyAsync"/> is told whether the slot is still free and stages either way. It
    /// must not save. The booking's row version guards the save as it guards a move: a booking decided
    /// or changed since it was read raises <see cref="DbUpdateConcurrencyException"/> and nothing is
    /// written.
    /// </para>
    /// </remarks>
    /// <returns>Whether the slot was still free - what <paramref name="applyAsync"/> was told.</returns>
    /// <exception cref="TableLockTimeoutException">The table's lock could not be had in time. Nothing was changed.</exception>
    /// <exception cref="DbUpdateConcurrencyException">The booking changed since it was read.</exception>
    public async Task<bool> ReturnUnderTableLockAsync(
        Reservation reservation,
        Guid tableId,
        string tableLabel,
        BookedInterval slot,
        ReservationPolicy policy,
        DateTime nowUtc,
        Func<bool, CancellationToken, Task> applyAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(applyAsync);

        await using var locked = await tableLock.AcquireAsync(tableId, tableLabel, cancellationToken);

        try
        {
            var free = slot.EndUtc > nowUtc
                       && await TakesBookingsUnderLockAsync(tableId, cancellationToken)
                       && TableEvening.Holds(
                           (await TableEvening.JudgeAsync(
                               db, tableId, slot.StartUtc, policy, excludingReservationId: reservation.Id, cancellationToken))
                           .Verdict,
                           slot.EndUtc);

            await applyAsync(free, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await locked.CommitAsync(cancellationToken);

            return free;
        }
        catch (Exception ex) when (TableLock.IsTimeout(ex))
        {
            throw new TableLockTimeoutException(locked.TableId, locked.TableLabel, locked.TimeoutMilliseconds);
        }
    }

    /// <summary>
    /// Whether the table still takes bookings, read inside its lock - the answer
    /// <see cref="ReadTableStatusUnderLockAsync"/> gives by refusing, given as a yes or no.
    /// </summary>
    private async Task<bool> TakesBookingsUnderLockAsync(Guid tableId, CancellationToken cancellationToken) =>
        await db.DiningTables
            .AsNoTracking()
            .AnyAsync(
                t => t.Id == tableId && t.IsActive && t.IsBookable && t.Status != TableStatus.OutOfService,
                cancellationToken);

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
                // check-then-insert would have let both through. Everything staged with this booking -
                // a question to the guest before, its push - goes with it.
                DetachStaged();

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

    /// <summary>What a refused verdict comes to: the booking behind it, or the party sitting there.</summary>
    private static (BookingClash? Clash, TableSession? Sitting)? Refusal(TableSequencingVerdict verdict, TableSession? sitting)
    {
        if (verdict.Refusal is not { } reason)
        {
            return null;
        }

        if (reason == ReservationRejectionReason.TableCurrentlyOccupied && sitting is not null)
        {
            return (null, sitting);
        }

        var because = verdict.RefusedBecauseOf!.Value;

        return (new BookingClash(because.ReservationId, new BookedInterval(because.StartUtc, because.EndUtc), reason), null);
    }

    /// <summary>
    /// Whether the time a diner accepted is the time required, to the minute - the instants are built
    /// from local times on whole minutes, and a client that rounds the seconds away has not disagreed.
    /// </summary>
    internal static bool SameMinute(DateTime? accepted, DateTime required) =>
        accepted is { } value && Math.Abs((ToUtc(value) - required).TotalSeconds) < 60;

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    /// <summary>
    /// Drops everything staged for a booking that was not written from the change tracker, so the
    /// caller's context is clean.
    /// </summary>
    /// <remarks>
    /// Every path out of the lock that did not insert comes through here. Leaving a rejected booking -
    /// or the question to the guest before it, or its push - tracked would see it swept into the next
    /// unrelated <c>SaveChanges</c> on the same request: a booking nobody made, for a table that was
    /// refused. Only additions are dropped; nothing that was already a row is touched.
    /// </remarks>
    private void DetachStaged()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}

/// <summary>A booking that refuses another one a table at a time, and which rule did.</summary>
/// <param name="ReservationId">The booking in the way.</param>
/// <param name="Interval">Its interval as stored - the buffer is not in it.</param>
/// <param name="Reason">
/// <c>TableAlreadyBooked</c>, <c>TableJustBooked</c>, <c>NextBookingTooSoon</c> or <c>EarlierGuestBeingAsked</c>.
/// </param>
internal sealed record BookingClash(Guid ReservationId, BookedInterval Interval, ReservationRejectionReason Reason);

/// <summary>A full sitting would run into the next booking, and the diner did not accept the time to leave by.</summary>
/// <param name="FreeByUtc">The time they have to accept.</param>
/// <param name="NextStartUtc">When the next booking starts.</param>
internal sealed record FreeByRequirement(DateTime FreeByUtc, DateTime NextStartUtc);

/// <summary>
/// What the locked move decided. At most one of these is set, and only <see cref="Moved"/> - none
/// of them - means the booking was changed.
/// </summary>
/// <param name="Conflict">Another booking holds the table over the new slot, or the table's evening refuses it.</param>
/// <param name="Occupied">A party sitting there now runs into the new slot.</param>
/// <param name="WasReplay">This command was already applied; nothing was changed now.</param>
/// <param name="FreeBy">The new slot needs a time to leave by the change did not accept.</param>
internal readonly record struct MoveOutcome(
    BookingClash? Conflict,
    TableSession? Occupied,
    bool WasReplay,
    FreeByRequirement? FreeBy = null)
{
    public static MoveOutcome Moved => new(null, null, false);

    public static MoveOutcome Replayed => new(null, null, true);

    public static MoveOutcome Conflicted(BookingClash conflict) => new(conflict, null, false);

    public static MoveOutcome OccupiedBy(TableSession session) => new(null, session, false);

    public static MoveOutcome FreeByRequired(FreeByRequirement requirement) => new(null, null, false, requirement);
}

/// <summary>
/// What the locked insert decided. Exactly one of these is set, and only <see cref="Booking"/> means a
/// row was written.
/// </summary>
internal readonly record struct InsertOutcome(
    BookingClash? Conflict,
    Reservation? Replay,
    TableSession? Occupied,
    FreeByRequirement? FreeBy = null,
    Reservation? Booking = null)
{
    public static InsertOutcome Inserted(Reservation booking) => new(null, null, null, null, booking);

    public static InsertOutcome Conflicted(BookingClash conflict) => new(conflict, null, null);

    public static InsertOutcome Replayed(Reservation winner) => new(null, winner, null);

    public static InsertOutcome OccupiedBy(TableSession session) => new(null, null, session);

    public static InsertOutcome FreeByRequired(FreeByRequirement requirement) => new(null, null, null, requirement);
}
