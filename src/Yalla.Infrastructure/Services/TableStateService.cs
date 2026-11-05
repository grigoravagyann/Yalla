using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Tables;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The table state machine.
/// </summary>
/// <remarks>
/// <para>
/// Three guarantees hold for every method here, and they are the reason this is one class rather
/// than logic spread across endpoints:
/// </para>
/// <list type="number">
/// <item>
/// <b>One SaveChanges.</b> The table update, the session, the audit row and any reservation or tab
/// change go in a single call, inside EF Core's implicit transaction. A state change therefore
/// cannot exist without the row that records it.
/// </item>
/// <item>
/// <b>Optimistic concurrency, never a retry.</b> Both competing writers update the same
/// <c>DiningTable</c> row, so its <c>RowVersion</c> catches the race for free - no locks, no
/// hints. The loser gets <see cref="TableStateConflictException"/> carrying the table's current
/// state. Retrying would seat a walk-in at a table the diner's reservation just took.
/// </item>
/// <item>
/// <b>Idempotent on ClientCommandId.</b> A replayed offline command returns the original result
/// and writes nothing.
/// </item>
/// </list>
/// </remarks>
internal sealed partial class TableStateService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    TableLock tableLock,
    TabLedger ledger,
    DomainEventDispatcher domainEvents,
    ILogger<TableStateService> logger) : ITableStateService
{
    public async Task<TableStateChangeResult> SeatWalkInAsync(
        SeatWalkInCommand command,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaff("Seat walk-in");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        // Everything below is read and written inside the table's write lock. A booking
        // re-checks TableSessions while holding it, so a seating that skipped it could slip a
        // session in between that check and the booking's commit - and both would succeed.
        await using var locked = await tableLock.AcquireAsync(command.TableId, null, cancellationToken);

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;
        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        var session = TableSession.SeatWalkIn(
            table.BranchId, table.Id, command.PartySize, nowUtc, staffId);

        // TableSeated's handlers write the change stream's row and the History's line (docs/domain-events.md).
        table.Occupy(session, TableSeating.WalkIn, booking: null, command.Reason, command.ClientCommandId, nowUtc);

        db.TableSessions.Add(session);

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.Occupied, nowUtc, command.ClientCommandId, next,
                tableSessionId: session.Id,
                warnings: SeatingWarnings(table, nowUtc, next)),
            cancellationToken,
            locked);
    }

    public async Task<TableStateChangeResult> SeatQrScanAsync(
        SeatQrScanCommand command,
        CancellationToken cancellationToken = default)
    {
        // Deliberately no RequireStaff. This is the one seating a diner performs, from their own
        // phone, and usually with no account at all - the walk-in who scanned the code on table 7.
        // The audit row names whoever the token says is acting, or the system when nobody is yet
        // known; the tab that follows, and the host participant on it, are what make the scan
        // traceable to a phone.
        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        // Everything below is read and written inside the table's write lock. A booking
        // re-checks TableSessions while holding it, so a seating that skipped it could slip a
        // session in between that check and the booking's commit - and both would succeed.
        await using var locked = await tableLock.AcquireAsync(command.TableId, null, cancellationToken);

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;
        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        // A walk-in seating with no waiter behind it. Free or Held to Occupied, like every other
        // seating; Occupied and OutOfService are refused by the entity and the tab service turns
        // the occupied case into "join the existing tab" before it ever gets here.
        var session = TableSession.SeatWalkIn(table.BranchId, table.Id, command.PartySize, nowUtc);

        table.Occupy(session, TableSeating.QrScan, booking: null, command.Reason, command.ClientCommandId, nowUtc);

        db.TableSessions.Add(session);

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.Occupied, nowUtc, command.ClientCommandId, next,
                tableSessionId: session.Id,
                warnings: SeatingWarnings(table, nowUtc, next)),
            cancellationToken,
            locked);
    }

    public async Task<TableStateChangeResult> SeatBookedPartyAsync(
        SeatBookedPartyCommand command,
        CancellationToken cancellationToken = default)
    {
        // No RequireStaff, like the QR scan: the party seats themselves from their own phone. The
        // tab service has already decided the booking is the caller's and its table theirs now, so
        // what is left is the seating - through the state machine, under the lock, with an audit
        // row, like every other seating.
        var (actorType, actorId) = TableStateActors.Resolve(actor);

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        // Everything below is read and written inside the table's write lock. A booking
        // re-checks TableSessions while holding it, so a seating that skipped it could slip a
        // session in between that check and the booking's commit - and both would succeed.
        await using var locked = await tableLock.AcquireAsync(command.TableId, null, cancellationToken);

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var reservation = await LoadReservationAsync(command.ReservationId, table, cancellationToken, assignIfBookedWithoutTable: true);

        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;

        // Excludes the booking being seated: warning about the party you are seating would be noise.
        var next = await FindNextReservationAsync(table.Id, nowUtc, reservation.Id, cancellationToken);

        // Against the booking, never as a walk-in. The sitting names it and the booking is Seated,
        // so the floor stops treating a party that is eating as one that has not arrived: no late
        // flag, no nudge, nothing for a waiter to mark a no-show. Throws unless Confirmed.
        reservation.MarkSeated();

        // Nor a reminder: the feed entry that has not appeared yet goes with the seating (K12). The
        // push itself is skipped by its handler, which reads the booking's status when it is due.
        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);

        var session = TableSession.SeatReservation(
            table.BranchId,
            table.Id,
            reservation.Id,
            command.PartySize ?? reservation.PartySize,
            nowUtc);

        table.Occupy(session, TableSeating.BookingByDiner, reservation, command.Reason, command.ClientCommandId, nowUtc);

        db.TableSessions.Add(session);

        // A pre-order the kitchen already has goes on their tab as they sit - see PreOrderSittings.
        await PreOrderSittings.PutHeldOnNewSittingAsync(
            db, reservation, session, table.Branch.ReservationPolicy.ServiceChargePercent, actorType, actorId, nowUtc,
            cancellationToken);

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.Occupied, nowUtc, command.ClientCommandId, next,
                tableSessionId: session.Id,
                reservationId: reservation.Id,
                warnings: SeatingWarnings(table, nowUtc, next)),
            cancellationToken,
            locked);
    }

    public async Task<TableStateChangeResult> SeatReservationAsync(
        SeatReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaff("Seat reservation");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        // Everything below is read and written inside the table's write lock. A booking
        // re-checks TableSessions while holding it, so a seating that skipped it could slip a
        // session in between that check and the booking's commit - and both would succeed.
        await using var locked = await tableLock.AcquireAsync(command.TableId, null, cancellationToken);

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var reservation = await LoadReservationAsync(command.ReservationId, table, cancellationToken, assignIfBookedWithoutTable: true);

        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;

        // Excludes the booking being seated: warning about the party you are seating would be noise.
        var next = await FindNextReservationAsync(table.Id, nowUtc, reservation.Id, cancellationToken);

        // Throws if the booking is not Confirmed - a cancelled or already-seated booking is a
        // conflict, not a bad request.
        reservation.MarkSeated();

        // A party seated before its reminder is due is not reminded in its feed (K12).
        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);

        var session = TableSession.SeatReservation(
            table.BranchId,
            table.Id,
            reservation.Id,
            command.PartySize ?? reservation.PartySize,
            nowUtc,
            staffId);

        table.Occupy(session, TableSeating.Booking, reservation, command.Reason, command.ClientCommandId, nowUtc);

        db.TableSessions.Add(session);

        // A pre-order the kitchen already has goes on their tab as they sit - see PreOrderSittings.
        await PreOrderSittings.PutHeldOnNewSittingAsync(
            db, reservation, session, table.Branch.ReservationPolicy.ServiceChargePercent, ActorType.Staff, staffId, nowUtc,
            cancellationToken);

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.Occupied, nowUtc, command.ClientCommandId, next,
                tableSessionId: session.Id,
                reservationId: reservation.Id,
                warnings: SeatingWarnings(table, nowUtc, next)),
            cancellationToken,
            locked);
    }

    public async Task<TableStateChangeResult> HoldForLatePartyAsync(
        TableStateCommand command,
        CancellationToken cancellationToken = default)
    {
        RequireStaff("Hold table");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;
        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        // The row and the History's line name the table's next booking: who the hold is presumably for.
        table.PlaceHold(next.ReservationId, command.Reason, command.ClientCommandId, nowUtc);

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.Held, nowUtc, command.ClientCommandId, next,
                reservationId: next.ReservationId),
            cancellationToken);
    }

    public async Task<TableStateChangeResult> ReleaseHoldAsync(
        TableStateCommand command,
        CancellationToken cancellationToken = default)
    {
        RequireStaff("Release hold");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;
        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        table.ReleaseHold(command.Reason, command.ClientCommandId, nowUtc);

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(table, fromStatus, TableStatus.Free, nowUtc, command.ClientCommandId, next),
            cancellationToken);
    }

    public async Task<TableStateChangeResult> SeatHeldPartyAsync(
        SeatHeldPartyCommand command,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaff("Seat held party");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        // Everything below is read and written inside the table's write lock. A booking
        // re-checks TableSessions while holding it, so a seating that skipped it could slip a
        // session in between that check and the booking's commit - and both would succeed.
        await using var locked = await tableLock.AcquireAsync(command.TableId, null, cancellationToken);

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;

        Reservation? reservation = null;
        if (command.ReservationId is { } reservationId)
        {
            reservation = await LoadReservationAsync(reservationId, table, cancellationToken);
            reservation.MarkSeated();

            // A party seated before its reminder is due is not reminded in its feed (K12).
            await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);
        }

        var next = await FindNextReservationAsync(table.Id, nowUtc, reservation?.Id, cancellationToken);

        // A hold is usually placed for a late booking, but staff also hold tables for a party
        // that just phoned. Both are legitimate; the session records which.
        var session = reservation is null
            ? TableSession.SeatWalkIn(table.BranchId, table.Id, command.PartySize, nowUtc, staffId)
            : TableSession.SeatReservation(
                table.BranchId, table.Id, reservation.Id, command.PartySize, nowUtc, staffId);

        table.Occupy(session, TableSeating.HeldParty, reservation, command.Reason, command.ClientCommandId, nowUtc);

        db.TableSessions.Add(session);

        if (reservation is not null)
        {
            // A pre-order the kitchen already has goes on their tab as they sit - see PreOrderSittings.
            await PreOrderSittings.PutHeldOnNewSittingAsync(
                db, reservation, session, table.Branch.ReservationPolicy.ServiceChargePercent, ActorType.Staff, staffId,
                nowUtc, cancellationToken);
        }

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.Occupied, nowUtc, command.ClientCommandId, next,
                tableSessionId: session.Id,
                reservationId: reservation?.Id,
                warnings: SeatingWarnings(table, nowUtc, next)),
            cancellationToken,
            locked);
    }

    public async Task<TableStateChangeResult> FreeTableAsync(
        TableStateCommand command,
        CancellationToken cancellationToken = default)
    {
        RequireStaff("Free table");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;

        var session = await db.TableSessions
            .FirstOrDefaultAsync(s => s.DiningTableId == table.Id && s.ClosedAtUtc == null, cancellationToken);

        // A bill paid in full closes its sitting and deliberately leaves the table occupied - a party
        // that has paid usually sits on - so the sitting being ended here may already be closed. Its
        // booking is still Seated, and freeing the table is what completes it. Without this the booking
        // stayed Seated for good, and the diner's app kept it under "upcoming" as "at the table".
        var settled = session is null
            ? await db.TableSessions
                .AsNoTracking()
                .Where(s => s.DiningTableId == table.Id && s.ClosedAtUtc != null)
                .OrderByDescending(s => s.ClosedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var sittingReservationId = session?.ReservationId ?? settled?.ReservationId;

        // The console finishes one booking's sitting, from a row that may be stale: the waiter may have
        // freed the table since and seated somebody else. Free only that booking's sitting, still seated.
        if (command.ExpectedReservationId is { } expected
            && (sittingReservationId != expected
                || !await db.Reservations.AnyAsync(
                    r => r.Id == expected && r.Status == ReservationStatus.Seated, cancellationToken)))
        {
            throw new TableSittingChangedException(table.Id, table.Label, expected, table.Status, session?.Id);
        }

        // The sitting's open tab, read before the table moves so the change can say what became of it: closed when
        // nothing is owed, left open with what is - the diners have physically left, and refusing to free the table
        // would make the floor plan lie, which costs more than an unresolved tab.
        var tab = session is null
            ? null
            : await db.Tabs.FirstOrDefaultAsync(
                t => t.TableSessionId == session.Id
                     && (t.Status == TabStatus.Open || t.Status == TabStatus.Closing),
                cancellationToken);

        var tabId = tab?.Id;
        long? outstandingAmd = tab is { RemainingAmd: not 0L } ? tab.RemainingAmd : null;
        var sittingId = session?.Id ?? settled?.Id;

        // Throws InvalidTableTransitionException unless the table is Occupied, so an accidental
        // double-free is refused rather than silently writing a second audit row. TableFreed's handlers write the
        // change stream's row and the History's table.freed.
        table.Vacate(
            sittingId, sittingReservationId, tabId, outstandingAmd, command.Reason, command.ClientCommandId, nowUtc);

        var warnings = new List<TableStateWarning>();

        if (session is null)
        {
            if (settled is null)
            {
                // The cache said Occupied but there is no sitting at all. Vacating fixes the cache;
                // the disagreement itself is worth knowing about.
                logger.LogWarning(
                    "Table {TableId} was {FromStatus} with no open session. The cached status was stale.",
                    table.Id, fromStatus);
            }
            else if (settled.ReservationId is { } settledReservationId)
            {
                await CompleteSeatedAsync(settledReservationId, nowUtc, cancellationToken);
            }
        }
        else
        {
            session.Close(nowUtc);

            if (session.ReservationId is { } reservationId)
            {
                await CompleteSeatedAsync(reservationId, nowUtc, cancellationToken);
            }

            if (tab is not null)
            {
                if (outstandingAmd is null)
                {
                    // Nothing owed: the tab closes with the table, and raises nothing of its own - TableFreed names it
                    // (docs/domain-events.md, tables).
                    tab.Close(nowUtc);
                }
                else
                {
                    // The table goes free, the tab stays open, and staff are told the number.
                    warnings.Add(new TableStateWarning(
                        TableStateWarning.OutstandingBalance,
                        $"table {table.Label} freed with {tab.RemainingAmd} AMD outstanding"));
                }
            }
        }

        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.Free, nowUtc, command.ClientCommandId, next,
                tableSessionId: session?.Id ?? settled?.Id,
                reservationId: sittingReservationId,
                tabId: tabId,
                outstandingAmd: outstandingAmd,
                warnings: warnings),
            cancellationToken);
    }

    /// <summary>The booking a sitting came from is over, if it was still seated.</summary>
    private async Task CompleteSeatedAsync(Guid reservationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        // Only from Seated. A booking cancelled mid-service is left as it is rather than forced
        // through a transition it never made, and one already completed is not completed twice.
        if (reservation?.Status == ReservationStatus.Seated)
        {
            reservation.MarkCompleted();

            // Seating already dropped its reminder; a booking seated before that did not, and a
            // finished meal is no more a reason to be reminded of it (K12).
            await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);
        }
    }

    public async Task<TableStateChangeResult> RecordHoldExtendedAsync(
        TableStateCommand command,
        Guid reservationId,
        DateTime newHoldExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // No RequireStaff: the diner taps this from the late nudge, on their own phone, and the
        // actor is whoever the token says. The reservation service has already checked that they own
        // the booking - that is the permission, and it is not one this method can re-derive.
        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        var nowUtc = clock.UtcNow;
        var status = table.Status;
        var next = await FindNextReservationAsync(table.Id, nowUtc, reservationId, cancellationToken);

        // From-status equals to-status on purpose (TableHoldExtended's handler writes the row). Nothing about the table
        // changed; something happened at it, and the branch sequence is how every other tablet finds out.
        table.NoteHoldExtended(reservationId, newHoldExpiresAtUtc, command.Reason, command.ClientCommandId, nowUtc);

        return await CommitAsync(
            table,
            status,
            command.ClientCommandId,
            () => Success(
                table, status, status, nowUtc, command.ClientCommandId, next,
                reservationId: reservationId),
            cancellationToken);
    }

    public async Task<TableStateChangeResult> MarkOutOfServiceAsync(
        TableStateCommand command,
        CancellationToken cancellationToken = default)
    {
        // Deliberately not manager-only. A broken chair is a Friday-night fact, and routing it
        // through a manager makes the floor state go stale exactly when accuracy matters most.
        RequireStaff("Mark out of service");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        // Under the table's write lock, unlike freeing and holding.
        //
        // Prompt 7 exempted the three "narrowing" commands on the grounds that they only ever make a
        // booking's re-check more conservative - a worse answer, never a wrong one. That is true of
        // freeing and holding and false of this one: a booking validated while the table was Free,
        // committing after this commits, leaves a confirmed reservation on a broken table. That is a
        // different kind of wrong, and no amount of retrying finds it. Marking a table broken is
        // also rare, so the throughput argument that justifies the other two does not apply.
        await using var locked = await tableLock.AcquireAsync(command.TableId, null, cancellationToken);

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;

        // Read inside the lock, before the transition, so no booking can slip in behind it.
        var stranded = await FutureBookingsAsync(table.Id, nowUtc, cancellationToken);

        // Refuses an occupied table: a table with diners at it cannot be marked broken.
        table.MarkOutOfService(stranded.Count, command.Reason, command.ClientCommandId, nowUtc);

        if (stranded.Count > 0)
        {
            logger.LogWarning(
                "Table {TableLabel} at branch {BranchId} was marked out of service with {Count} "
                + "booking(s) still to come tonight. They were NOT cancelled - a waiter decides.",
                table.Label, table.BranchId, stranded.Count);
        }

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.OutOfService, nowUtc, command.ClientCommandId,
                NextReservation.None) with
            {
                AffectedReservations = stranded,
            },
            cancellationToken,
            locked);
    }

    public async Task<TableStateChangeResult> ReturnToServiceAsync(
        TableStateCommand command,
        CancellationToken cancellationToken = default)
    {
        RequireStaff("Return to service");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;
        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        table.ReturnToService(command.Reason, command.ClientCommandId, nowUtc);

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(table, fromStatus, TableStatus.Free, nowUtc, command.ClientCommandId, next),
            cancellationToken);
    }

    public async Task<TabAbandonResult> AbandonTabAsync(
        AbandonTabCommand command,
        CancellationToken cancellationToken = default)
    {
        // The one manager-only operation in this service: writing off money, as opposed to
        // recording what happened on the floor.
        var staffId = RequireManager("Abandon tab");

        var tab = await db.Tabs.FirstOrDefaultAsync(
                      t => t.Id == command.TabId && t.BranchId == command.BranchId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Tab {command.TabId} was not found in branch {command.BranchId}.");

        // Naturally idempotent: an already-abandoned tab needs no ledger row to recognise a replay.
        if (tab.Status == TabStatus.Abandoned)
        {
            return new TabAbandonResult
            {
                TabId = tab.Id,
                BranchId = tab.BranchId,
                WrittenOffAmd = tab.RemainingAmd,
                AtUtc = tab.ClosedAtUtc ?? clock.UtcNow,
                WasReplay = true,
            };
        }

        var nowUtc = clock.UtcNow;
        var writtenOff = tab.RemainingAmd;

        // TabWrittenOff's handlers write the History's payment.unpaid and the stream's TabAbandoned, as from the payment
        // route (TabPaymentService) - which is why this saves through the ledger: it numbers the stream.
        tab.WriteOff(nowUtc, staffId, command.Reason);

        await ledger.SaveAppendedAsync(cancellationToken);

        logger.LogWarning(
            "Tab {TabId} abandoned by manager {StaffId} with {WrittenOffAmd} AMD written off. Reason: {Reason}",
            tab.Id, actor.StaffMemberId, writtenOff, command.Reason ?? "(none given)");

        return new TabAbandonResult
        {
            TabId = tab.Id,
            BranchId = tab.BranchId,
            WrittenOffAmd = writtenOff,
            AtUtc = nowUtc,
            WasReplay = false,
        };
    }
}
