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
    Yalla.Application.Abstractions.IClock clock,
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
        BookerKey booker,
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
            // A web visitor booking without an account has none to lock.
            if (booker.DinerUserId is { } dinerUserId)
            {
                await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);
            }

            if (await FindReplayAsync(clientCommandId, booker, cancellationToken) is { } original)
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

            // One table at a time for one diner at a branch, over the sitting this booking would have -
            // cut to the time to leave by when the next booking needs one. Before the table's own
            // refusals, so the diner's own booking on this table is named as theirs. Under the account's
            // lock, so two bookings sent at once on two tables are judged one after the other.
            if (booker.DinerUserId is { } account)
            {
                await RefuseOverlapWithOwnAsync(
                    account,
                    table.BranchId,
                    new BookedInterval(startUtc, verdict.MustFreeByUtc ?? startUtc.AddMinutes(policy.TurnTimeMinutes)),
                    excludingReservationId: Guid.Empty,
                    cancellationToken);
            }

            if (Refusal(verdict, sitting) is { } refused)
            {
                // Nothing was written. Leave the lock and build the answer outside it.
                return refused.Clash is { } clash ? InsertOutcome.Conflicted(clash) : InsertOutcome.OccupiedBy(refused.Sitting!);
            }

            // One active booking per phone number, anywhere - and, without a proved number, one per browser and
            // no more than the branch's waiting room holds. After the replay, so a retry is never refused by its
            // own booking; AFTER the table's own refusals, so a request naming a taken table answers "taken"
            // whatever number it carries: asking whether a number holds a booking somewhere has to cost a real
            // booking the venue sees, not a refused request.
            await RefuseSecondActiveBookingAsync(booker, table, cancellationToken);

            if (verdict.MustFreeByUtc is { } required && !SameMinute(acceptsFreeByUtc, required))
            {
                return InsertOutcome.FreeByRequired(new FreeByRequirement(required, verdict.Next!.Value.StartUtc));
            }

            // Only now is the booking built: its end, its status and any question to the guest before
            // follow from what the lock found.
            var reservation = await stageAsync(verdict, cancellationToken);

            db.Reservations.Add(reservation);

            if (await SaveWithFreshCodeAsync(reservation, booker, cancellationToken) is { } winner)
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

            // Nor is it in its own way here: only the diner's other bookings at the branch count, over the
            // sitting the move would have - cut to the time to leave by the next booking needs, or to the
            // one this booking already promised for the same start, as the move itself cuts it.
            // A change that keeps the table, the start and the end - a party size - makes no overlap that was
            // not already there, so it is not judged: a pair that overlapped before the rule stays editable.
            var turnEndUtc = startUtc.AddMinutes(policy.TurnTimeMinutes);
            var sittingEndUtc = verdict.MustFreeByUtc
                                ?? (reservation.StartUtc == startUtc
                                    && reservation.PromisedFreeByUtc is { } kept
                                    && kept > startUtc
                                    && kept < turnEndUtc
                                        ? kept
                                        : turnEndUtc);

            var sameSitting = table.Id == reservation.DiningTableId
                              && startUtc == reservation.StartUtc
                              && sittingEndUtc == reservation.EndUtc;

            if (!sameSitting)
            {
                await RefuseOverlapWithOwnAsync(
                    dinerUserId,
                    table.BranchId,
                    new BookedInterval(startUtc, sittingEndUtc),
                    excludingReservationId: reservation.Id,
                    cancellationToken);
            }

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
    /// <b>Nor while the diner holds another table then.</b> A booking waiting on a change does not hold
    /// the slot it came from, so the diner may have booked that time again elsewhere at the branch -
    /// tomorrow's 20:00 moved to tonight, then table 3 tomorrow at 20:00. Going back would give them two
    /// tables at once, which no booking or move may (<see cref="RefuseOverlapWithOwnAsync"/>); the slot
    /// is not free, and the booking ends with its change, the diner told as for any slot that was gone.
    /// Judged under the account's lock too, taken inside the table's as every booking takes it.
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
                           slot.EndUtc)
                       && !await HoldsAnotherTableThenAsync(reservation, slot, cancellationToken);

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
    /// Refuses a sitting that overlaps one of the diner's own live bookings at the same branch - one
    /// diner, one table there at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found in QA: one diner held table 1 at 19:45 and table 2 at 19:30 the same evening, two tables
    /// kept from everybody else for one party. Live is pending, confirmed or seated
    /// (<see cref="ReservationOverlap.Blocks"/>); sittings that only touch do not overlap, and no clearing
    /// time is added - it is the same people, not a table to reset. Both sittings are the ones the bookings
    /// have: the new one cut to its time to leave by when the table's next booking needs one (the 13:00 that
    /// promises 13:55 does not run into the same diner's 14:00), the existing one to its stored end, which
    /// a promise already cut.
    /// </para>
    /// <para>
    /// Called inside the table's lock, after the account's lock and the replay check - so a retry of the
    /// same booking is answered as a replay, never refused as overlapping itself, and two bookings sent at
    /// once on two tables queue on the account's row rather than both getting through. A move that keeps
    /// its table, start and end - a change of party size - is not judged: it makes no overlap that was not
    /// already there.
    /// </para>
    /// </remarks>
    /// <exception cref="ReservationOverlapsOwnException">The diner already has a booking then, named.</exception>
    private async Task RefuseOverlapWithOwnAsync(
        Guid dinerUserId,
        Guid branchId,
        BookedInterval sitting,
        Guid excludingReservationId,
        CancellationToken cancellationToken)
    {
        if (await OwnBookingOverlappingAsync(dinerUserId, branchId, sitting, excludingReservationId, cancellationToken)
            is { } own)
        {
            throw new ReservationOverlapsOwnException(own.Id, own.Code, own.LocalDate, own.LocalStartTime, own.TableLabel);
        }
    }

    /// <summary>
    /// Whether the diner of a booking going back to the slot it had holds another table at the branch
    /// then - see <see cref="ReturnUnderTableLockAsync"/>. Taken under the account's lock; a booking whose
    /// account has gone has nobody to hold anything.
    /// </summary>
    private async Task<bool> HoldsAnotherTableThenAsync(
        Reservation reservation,
        BookedInterval slot,
        CancellationToken cancellationToken)
    {
        if (reservation.DinerUserId is not { } dinerUserId)
        {
            return false;
        }

        await DinerAccountLock.IsLiveAsync(db, dinerUserId, cancellationToken);

        return await OwnBookingOverlappingAsync(dinerUserId, reservation.BranchId, slot, reservation.Id, cancellationToken)
            is not null;
    }

    /// <summary>The diner's earliest live booking at the branch whose sitting overlaps this one, or null.</summary>
    private Task<OwnBooking?> OwnBookingOverlappingAsync(
        Guid dinerUserId,
        Guid branchId,
        BookedInterval sitting,
        Guid excludingReservationId,
        CancellationToken cancellationToken)
    {
        var (startUtc, endUtc) = (sitting.StartUtc, sitting.EndUtc);

        return db.Reservations
            .AsNoTracking()
            .Where(r => r.DinerUserId == dinerUserId
                        && r.BranchId == branchId
                        && r.Id != excludingReservationId
                        && (r.Status == ReservationStatus.PendingApproval
                            || r.Status == ReservationStatus.Confirmed
                            || r.Status == ReservationStatus.Seated)
                        && r.StartUtc < endUtc
                        && r.EndUtc > startUtc)
            .OrderBy(r => r.StartUtc)
            .Select(r => new OwnBooking(r.Id, r.Code, r.LocalDate, r.LocalStartTime, r.DiningTable.Label))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>One of the diner's own bookings, as an overlap names it.</summary>
    private sealed record OwnBooking(Guid Id, string Code, DateOnly LocalDate, TimeOnly LocalStartTime, string TableLabel);

    /// <summary>
    /// The booking this command already created, if it has one.
    /// </summary>
    /// <remarks>
    /// The diner is half the question, not a refinement of it. A replay is the same caller sending
    /// the same command again; the same id from a different caller is a collision, and answering it
    /// with the booking that holds the id would disclose that booking's door code, guest name and
    /// phone number to somebody who only had to reuse a Guid. A web visitor with no account is the
    /// browser that made it: no account on the booking, and the same device hash.
    /// </remarks>
    public Task<Reservation?> FindReplayAsync(
        Guid clientCommandId,
        BookerKey booker,
        CancellationToken cancellationToken)
    {
        var replays = db.Reservations
            .AsNoTracking()
            .Where(r => r.ClientCommandId == clientCommandId);

        replays = booker.DinerUserId is { } dinerUserId
            ? replays.Where(r => r.DinerUserId == dinerUserId)
            : replays.Where(r => r.DinerUserId == null && r.BookerDeviceHash != null && r.BookerDeviceHash == booker.DeviceHash);

        return replays.FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses a new booking for a phone number that already holds an active one, or from a browser that
    /// already holds one made without a proved number, or one without a proved number at a branch whose
    /// waiting room is full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Active</b> is pending or confirmed and not over yet (<c>EndUtc</c> after now). Seated, finished,
    /// cancelled, declined and missed bookings hold nothing.
    /// </para>
    /// <para>
    /// <b>Which holds count depends on proof</b> (decided 2026-10-20, after review). A booker whose number is
    /// proved is held back only by a proved hold, or by their own account's: a stranger typing their number on
    /// the public page must never stop the number's owner from booking - the owner sees such bookings among
    /// their own and can cancel them. A booking without proof is held back by any active hold on the number.
    /// </para>
    /// <para>
    /// <b>Why an application lock and not a unique index.</b> "Not over yet" is about the clock, which a
    /// filtered index cannot express - a confirmed booking nobody seated or released would hold the number
    /// for ever. So each check takes <c>sp_getapplock</c> on the number (and the browser), exclusive and owned
    /// by the table lock's transaction, before it reads: two bookings for one number on two tables - or at
    /// two venues - meet on it, and the second reads the first's committed row. The order is table, account,
    /// number, browser for every insert; nothing else takes these locks, so there is no cycle.
    /// </para>
    /// </remarks>
    private async Task RefuseSecondActiveBookingAsync(BookerKey booker, DiningTable table, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        if (booker.PhoneE164 is { } phone)
        {
            await TakeApplicationLockAsync("booking-phone:" + phone, table, cancellationToken);

            var holds = db.Reservations
                .AsNoTracking()
                .Where(r => r.BookerPhoneE164 == phone
                            && (r.Status == ReservationStatus.PendingApproval || r.Status == ReservationStatus.Confirmed)
                            && r.EndUtc > nowUtc);

            if (booker.PhoneProved)
            {
                var caller = booker.DinerUserId;

                holds = holds.Where(r => !r.PhoneUnverified || (caller != null && r.DinerUserId == caller));
            }

            var held = await holds
                .OrderBy(r => r.StartUtc)
                .Select(r => new
                {
                    r.DinerUserId,
                    Facts = new ActiveBookingFacts(
                        r.Id, r.Code, r.StartUtc, r.LocalDate, r.LocalStartTime, r.Branch.Venue.Name, r.Branch.Name),
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (held is not null)
            {
                // Named only to the account that holds it.
                throw new PhoneHasActiveBookingException(
                    booker.DinerUserId is { } caller && held.DinerUserId == caller ? held.Facts : null);
            }
        }

        if (!booker.PhoneProved)
        {
            // The branch's waiting room. Taken under a lock of its own so two bookings at two tables of one
            // branch cannot both see nineteen.
            await TakeApplicationLockAsync("booking-unverified-branch:" + table.BranchId.ToString("N"), table, cancellationToken);

            var waiting = await db.Reservations
                .AsNoTracking()
                .CountAsync(
                    r => r.BranchId == table.BranchId
                         && r.PhoneUnverified
                         && r.Status == ReservationStatus.PendingApproval
                         && r.StartUtc > nowUtc
                         && (r.ApprovalExpiresAtUtc == null || r.ApprovalExpiresAtUtc > nowUtc),
                    cancellationToken);

            if (waiting >= UnverifiedBookingRules.OutstandingPendingPerBranch)
            {
                throw new BranchUnverifiedQueueFullException();
            }
        }

        if (booker.DeviceHash is { } device)
        {
            await TakeApplicationLockAsync("booking-device:" + device, table, cancellationToken);

            if (await db.Reservations
                    .AsNoTracking()
                    .AnyAsync(
                        r => r.BookerDeviceHash == device
                             && (r.Status == ReservationStatus.PendingApproval || r.Status == ReservationStatus.Confirmed)
                             && r.EndUtc > nowUtc,
                        cancellationToken))
            {
                throw new DeviceHasActiveBookingException();
            }
        }
    }

    /// <summary>
    /// Takes an exclusive application lock owned by the open transaction, waiting no longer than the table
    /// lock does. A wait that runs out is the table lock's timeout - retryable, the question never asked.
    /// </summary>
    private async Task TakeApplicationLockAsync(string resource, DiningTable table, CancellationToken cancellationToken)
    {
        var timeoutMs = tableLock.TimeoutMilliseconds;

        var granted = await db.Database
            .SqlQuery<int>(
                $"""
                 DECLARE @result int;
                 EXEC @result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive',
                      @LockOwner = 'Transaction', @LockTimeout = {timeoutMs};
                 SELECT @result AS [Value];
                 """)
            .ToListAsync(cancellationToken);

        // 0 granted, 1 granted after waiting; negative is a timeout, a deadlock victim or a refusal.
        if (granted is not [>= 0])
        {
            throw new TableLockTimeoutException(table.Id, table.Label, timeoutMs);
        }
    }

    /// <summary>
    /// Saves the booking, re-rolling the door code if it collides, and answering a lost
    /// idempotency race with the booking that won.
    /// </summary>
    /// <returns>The winning booking when this was a racing replay, otherwise null.</returns>
    private async Task<Reservation?> SaveWithFreshCodeAsync(
        Reservation reservation,
        BookerKey booker,
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
                    reservation.ClientCommandId, booker, cancellationToken);

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

/// <summary>
/// Who is booking, as the locked insert needs to know them: their account, or - for a web visitor with no
/// account - their browser; and the phone number the one-active-booking rule keys on.
/// </summary>
/// <param name="DinerUserId">The account, or null for a web visitor booking without one.</param>
/// <param name="PhoneE164">The number the rule keys on (E.164), or null for a booking the rule exempts.</param>
/// <param name="DeviceHash">The browser's device hash, for a booking with no account; null otherwise.</param>
/// <param name="PhoneProved">
/// Whether the booker's number is proved: which holds on the number count against them, and whether the
/// unverified limits apply at all.
/// </param>
internal sealed record BookerKey(Guid? DinerUserId, string? PhoneE164, string? DeviceHash, bool PhoneProved);

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
