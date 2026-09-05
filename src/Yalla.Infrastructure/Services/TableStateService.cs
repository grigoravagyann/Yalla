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
internal sealed class TableStateService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    TableLock tableLock,
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

        table.Occupy(session.Id);

        db.TableSessions.Add(session);
        db.TableStateChanges.Add(NewChange(
            table, fromStatus, TableStatus.Occupied, command.Reason ?? "seated walk-in",
            nowUtc, command.ClientCommandId, staffId, tableSessionId: session.Id));

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
        var (actorType, actorId) = ResolveActor();

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

        table.Occupy(session.Id);

        db.TableSessions.Add(session);
        db.TableStateChanges.Add(new TableStateChange(
            table.BranchId,
            table.Id,
            fromStatus,
            TableStatus.Occupied,
            command.Reason ?? "opened by QR scan",
            actorType,
            nowUtc,
            command.ClientCommandId,
            actorId,
            tableSessionId: session.Id));

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
        var reservation = await LoadReservationAsync(command.ReservationId, table, cancellationToken);

        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;

        // Excludes the booking being seated: warning about the party you are seating would be noise.
        var next = await FindNextReservationAsync(table.Id, nowUtc, reservation.Id, cancellationToken);

        // Throws if the booking is not Confirmed - a cancelled or already-seated booking is a
        // conflict, not a bad request.
        reservation.MarkSeated();

        var session = TableSession.SeatReservation(
            table.BranchId,
            table.Id,
            reservation.Id,
            command.PartySize ?? reservation.PartySize,
            nowUtc,
            staffId);

        table.Occupy(session.Id);

        db.TableSessions.Add(session);
        db.TableStateChanges.Add(NewChange(
            table, fromStatus, TableStatus.Occupied,
            command.Reason ?? $"seated reservation {reservation.Code}",
            nowUtc, command.ClientCommandId, staffId,
            reservationId: reservation.Id, tableSessionId: session.Id));

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
        var staffId = RequireStaff("Hold table");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;
        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        table.PlaceHold();

        db.TableStateChanges.Add(NewChange(
            table, fromStatus, TableStatus.Held, command.Reason ?? "held for late party",
            nowUtc, command.ClientCommandId, staffId, reservationId: next.ReservationId));

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
        var staffId = RequireStaff("Release hold");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;
        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        table.ReleaseHold();

        db.TableStateChanges.Add(NewChange(
            table, fromStatus, TableStatus.Free, command.Reason ?? "hold released",
            nowUtc, command.ClientCommandId, staffId));

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
        }

        var next = await FindNextReservationAsync(table.Id, nowUtc, reservation?.Id, cancellationToken);

        // A hold is usually placed for a late booking, but staff also hold tables for a party
        // that just phoned. Both are legitimate; the session records which.
        var session = reservation is null
            ? TableSession.SeatWalkIn(table.BranchId, table.Id, command.PartySize, nowUtc, staffId)
            : TableSession.SeatReservation(
                table.BranchId, table.Id, reservation.Id, command.PartySize, nowUtc, staffId);

        table.Occupy(session.Id);

        db.TableSessions.Add(session);
        db.TableStateChanges.Add(NewChange(
            table, fromStatus, TableStatus.Occupied, command.Reason ?? "seated held party",
            nowUtc, command.ClientCommandId, staffId,
            reservationId: reservation?.Id, tableSessionId: session.Id));

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
        var staffId = RequireStaff("Free table");

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

        // Throws InvalidTableTransitionException unless the table is Occupied, so an accidental
        // double-free is refused rather than silently writing a second audit row.
        table.Vacate();

        var warnings = new List<TableStateWarning>();
        long? outstandingAmd = null;
        Guid? tabId = null;

        if (session is null)
        {
            // The cache said Occupied but there is no open session. Vacating fixes the cache;
            // the disagreement itself is worth knowing about.
            logger.LogWarning(
                "Table {TableId} was {FromStatus} with no open session. The cached status was stale.",
                table.Id, fromStatus);
        }
        else
        {
            session.Close(nowUtc);

            if (session.ReservationId is { } reservationId)
            {
                var reservation = await db.Reservations
                    .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

                // Only from Seated. A booking cancelled mid-service is left as it is rather than
                // forced through a transition it never made.
                if (reservation?.Status == ReservationStatus.Seated)
                {
                    reservation.MarkCompleted();
                }
            }

            var tab = await db.Tabs.FirstOrDefaultAsync(
                t => t.TableSessionId == session.Id
                     && (t.Status == TabStatus.Open || t.Status == TabStatus.Closing),
                cancellationToken);

            if (tab is not null)
            {
                tabId = tab.Id;

                if (tab.RemainingAmd == 0L)
                {
                    tab.Close(nowUtc);
                }
                else
                {
                    // The diners have physically left. Refusing to free the table would make the
                    // floor plan lie, which costs more than an unresolved tab - so the table goes
                    // free, the tab stays open, and staff are told the number.
                    outstandingAmd = tab.RemainingAmd;
                    warnings.Add(new TableStateWarning(
                        TableStateWarning.OutstandingBalance,
                        $"table {table.Label} freed with {tab.RemainingAmd} AMD outstanding"));
                }
            }
        }

        db.TableStateChanges.Add(NewChange(
            table, fromStatus, TableStatus.Free, command.Reason ?? "table freed",
            nowUtc, command.ClientCommandId, staffId,
            reservationId: session?.ReservationId, tabId: tabId, tableSessionId: session?.Id));

        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.Free, nowUtc, command.ClientCommandId, next,
                tableSessionId: session?.Id,
                reservationId: session?.ReservationId,
                tabId: tabId,
                outstandingAmd: outstandingAmd,
                warnings: warnings),
            cancellationToken);
    }

    public async Task<TableStateChangeResult> MarkOutOfServiceAsync(
        TableStateCommand command,
        CancellationToken cancellationToken = default)
    {
        // Deliberately not manager-only. A broken chair is a Friday-night fact, and routing it
        // through a manager makes the floor state go stale exactly when accuracy matters most.
        var staffId = RequireStaff("Mark out of service");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;

        // Refuses an occupied table: a table with diners at it cannot be marked broken.
        table.MarkOutOfService();

        db.TableStateChanges.Add(NewChange(
            table, fromStatus, TableStatus.OutOfService, command.Reason ?? "out of service",
            nowUtc, command.ClientCommandId, staffId));

        return await CommitAsync(
            table,
            fromStatus,
            command.ClientCommandId,
            () => Success(
                table, fromStatus, TableStatus.OutOfService, nowUtc, command.ClientCommandId,
                NextReservation.None),
            cancellationToken);
    }

    public async Task<TableStateChangeResult> ReturnToServiceAsync(
        TableStateCommand command,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaff("Return to service");

        if (await TryReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var table = await LoadTableAsync(command.BranchId, command.TableId, cancellationToken);
        RequirePrecondition(command, table);
        var nowUtc = clock.UtcNow;
        var fromStatus = table.Status;
        var next = await FindNextReservationAsync(table.Id, nowUtc, null, cancellationToken);

        table.ReturnToService();

        db.TableStateChanges.Add(NewChange(
            table, fromStatus, TableStatus.Free, command.Reason ?? "returned to service",
            nowUtc, command.ClientCommandId, staffId));

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
        RequireManager("Abandon tab");

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

        tab.MarkAbandoned(nowUtc);
        await db.SaveChangesAsync(cancellationToken);

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

    // ---------------------------------------------------------------- commit and conflicts

    /// <summary>
    /// The single write point. Everything staged by the caller goes in one
    /// <c>SaveChanges</c>, and the two races that can lose are translated into answers a client
    /// can act on.
    /// </summary>
    private async Task<TableStateChangeResult> CommitAsync(
        DiningTable table,
        TableStatus attemptedFromStatus,
        Guid clientCommandId,
        Func<TableStateChangeResult> buildResult,
        CancellationToken cancellationToken,
        TableLockScope? locked = null,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try
        {
            // Built before the save, not after, so the answer can be stored in the same
            // SaveChanges as the work it describes. There is then no state in which the command
            // happened and the record of what it answered did not - which is the whole point: a
            // replayed seat-walk-in has to be able to return the session id it created, and the
            // audit row alone never knew it.
            var result = buildResult();
            RecordProcessedCommand(clientCommandId, CommandTypeFor(caller), result);

            await db.SaveChangesAsync(cancellationToken);

            // Seatings hold the table's write lock; everything else runs in EF's implicit
            // transaction and has nothing to commit here.
            if (locked is not null)
            {
                await locked.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch (Exception ex) when (locked is not null && TableLock.IsTimeout(ex))
        {
            // Contention, not refusal: this writer never got its turn, so nothing was decided and
            // the same command sent again will very likely work. Kept distinct from the conflict
            // below, which will never succeed no matter how often it is repeated.
            throw new TableLockTimeoutException(locked.TableId, locked.TableLabel, locked.TimeoutMilliseconds);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The table's RowVersion moved: somebody else changed this table first.
            throw await ConflictAsync(table, attemptedFromStatus, cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.OpenSessionPerTable))
        {
            // The database backstop fired: another session is already open on this table. Same
            // situation as a RowVersion clash, so the caller gets the same answer.
            throw await ConflictAsync(table, attemptedFromStatus, cancellationToken);
        }
        catch (DbUpdateException ex)
            when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TableStateChangeClientCommand)
                  || UniqueViolation.IsOn(ex, DatabaseIndexNames.ProcessedCommandId))
        {
            // Two replays of the same offline command raced. The other one won and did the work;
            // this one reports its result.
            logger.LogInformation(
                "Command {ClientCommandId} was applied concurrently by another request; replaying its result.",
                clientCommandId);

            db.ChangeTracker.Clear();

            return await TryReplayAsync(clientCommandId, cancellationToken)
                   ?? throw new InvalidOperationException(
                       $"Command {clientCommandId} violated the idempotency index but no record was found.");
        }
    }

    /// <summary>
    /// Re-reads the table and builds the conflict, so the caller can tell the user what actually
    /// happened instead of that something failed.
    /// </summary>
    private async Task<TableStateConflictException> ConflictAsync(
        DiningTable table,
        TableStatus attemptedFromStatus,
        CancellationToken cancellationToken)
    {
        // AsNoTracking, because the change tracker is holding the failed attempt.
        var current = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == table.Id)
            .Select(t => new { t.Status, t.CurrentSessionId })
            .FirstOrDefaultAsync(cancellationToken);

        logger.LogInformation(
            "Lost a race on table {TableId} ({TableLabel}): attempted from {AttemptedFrom}, now {CurrentStatus}.",
            table.Id, table.Label, attemptedFromStatus, current?.Status);

        return new TableStateConflictException(
            table.Id,
            table.Label,
            attemptedFromStatus,
            current?.Status ?? table.Status,
            current?.CurrentSessionId);
    }

    // ---------------------------------------------------------------- idempotency

    private Task<TableStateChange?> FindReplayAsync(Guid clientCommandId, CancellationToken cancellationToken) =>
        db.TableStateChanges
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientCommandId == clientCommandId, cancellationToken);

    /// <summary>
    /// Rebuilds the original answer from the audit row, which is why that row records the session
    /// it created. Reports the transition as it happened, not the table's state now.
    /// </summary>
    private async Task<TableStateChangeResult> BuildReplayResultAsync(
        TableStateChange change,
        CancellationToken cancellationToken)
    {
        var table = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == change.DiningTableId)
            .Select(t => new { t.Label, t.Branch.ReservationPolicy.BufferMinutes })
            .FirstOrDefaultAsync(cancellationToken);

        var nowUtc = clock.UtcNow;
        var next = await FindNextReservationAsync(change.DiningTableId, nowUtc, null, cancellationToken);
        var bufferMinutes = table?.BufferMinutes ?? 0;

        logger.LogInformation(
            "Command {ClientCommandId} was already applied at {AtUtc}; returning the original result.",
            change.ClientCommandId, change.AtUtc);

        return new TableStateChangeResult
        {
            BranchId = change.BranchId,
            TableId = change.DiningTableId,
            TableLabel = table?.Label ?? string.Empty,
            FromStatus = change.FromStatus,
            ToStatus = change.ToStatus,
            State = TableStateProjection.Derive(
                change.ToStatus, next.StartUtc, nowUtc, bufferMinutes),
            TableSessionId = change.TableSessionId,
            ReservationId = change.ReservationId,
            TabId = change.TabId,
            NextReservationStartUtc = next.StartUtc,
            FreeUntilUtc = TableStateProjection.FreeUntil(next.StartUtc, bufferMinutes),
            AtUtc = change.AtUtc,
            ClientCommandId = change.ClientCommandId,
            WasReplay = true,
        };
    }

    // ---------------------------------------------------------------- permissions

    private Guid RequireStaff(string operation)
    {
        if (actor.Type != ActorType.Staff || actor.StaffMemberId is not { } staffId)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }

        return staffId;
    }

    /// <summary>
    /// Who the audit row should name for a transition anyone may perform. A staff member or a
    /// diner with an account is named; a tab participant has no account by design and a request
    /// with no token has nobody, so both are recorded as the system - which is what the audit
    /// row's invariant allows a null actor for.
    /// </summary>
    private (ActorType Type, Guid? Id) ResolveActor() => actor.Type switch
    {
        ActorType.Staff when actor.StaffMemberId is { } staffId => (ActorType.Staff, staffId),
        ActorType.Diner when actor.DinerUserId is { } dinerId => (ActorType.Diner, dinerId),
        _ => (ActorType.System, null),
    };

    /// <summary>Managers and owners. Waiters and kitchen staff do not move money.</summary>
    private void RequireManager(string operation)
    {
        RequireStaff(operation);

        if (actor.Role is not (StaffRole.Manager or StaffRole.Owner))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }
    }

    // ---------------------------------------------------------------- loading

    private async Task<DiningTable> LoadTableAsync(Guid branchId, Guid tableId, CancellationToken cancellationToken)
    {
        // Tracked, and with the branch so the policy thresholds are available. Tracking is what
        // makes the RowVersion check happen on save.
        var table = await db.DiningTables
            .Include(t => t.Branch)
            .FirstOrDefaultAsync(t => t.Id == tableId, cancellationToken);

        if (table is null)
        {
            throw new KeyNotFoundException($"Table {tableId} was not found.");
        }

        if (table.BranchId != branchId)
        {
            // A branch mismatch is a bad request, not a missing table: answering 404 would tell a
            // caller the table does not exist when it does.
            throw new ArgumentException(
                $"Table {tableId} does not belong to branch {branchId}.", nameof(branchId));
        }

        return table;
    }

    private async Task<Reservation> LoadReservationAsync(
        Guid reservationId,
        DiningTable table,
        CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation is null)
        {
            throw new KeyNotFoundException($"Reservation {reservationId} was not found.");
        }

        if (reservation.DiningTableId != table.Id)
        {
            throw new ArgumentException(
                $"Reservation {reservation.Code} is for a different table than {table.Label}.",
                nameof(reservationId));
        }

        return reservation;
    }

    /// <summary>
    /// The next booking that still matters for this table. This lookup is the whole reason
    /// <c>TableStatus</c> needs no stored <c>Reserved</c> member.
    /// </summary>
    /// <summary>
    /// The answer this command was already given, if it has been applied before.
    /// </summary>
    /// <remarks>
    /// The stored body is returned rather than a recomputed one. Recomputing would answer with the
    /// world as it is now, which is not what the caller asked: a tablet replaying its offline queue
    /// wants the result of the command it sent, and "the table is Free now" is a different fact
    /// from "your seating succeeded". Only <c>WasReplay</c> is changed, so the caller can tell.
    /// </remarks>
    private async Task<TableStateChangeResult?> TryReplayAsync(
        Guid clientCommandId,
        CancellationToken cancellationToken)
    {
        var processed = await db.ProcessedCommands
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientCommandId == clientCommandId, cancellationToken);

        if (processed is not null)
        {
            var stored = JsonSerializer.Deserialize<TableStateChangeResult>(processed.ResponseJson, ResponseJson);

            if (stored is not null)
            {
                logger.LogInformation(
                    "Command {ClientCommandId} was already applied; returning the stored answer.", clientCommandId);

                return stored with { WasReplay = true };
            }
        }

        // Commands applied before this store existed have only their audit row. Recomputing from it
        // is worse than the stored answer and better than refusing, so it stays as a fallback.
        return await FindReplayAsync(clientCommandId, cancellationToken) is { } audited
            ? await BuildReplayResultAsync(audited, cancellationToken)
            : null;
    }

    /// <summary>Adds the answer to the unit of work, alongside the change it describes.</summary>
    private void RecordProcessedCommand(Guid clientCommandId, string commandType, TableStateChangeResult result)
    {
        var (actorType, actorId) = ResolveActor();

        db.ProcessedCommands.Add(new ProcessedCommand(
            clientCommandId,
            commandType,
            actorType,
            actorId,
            JsonSerializer.Serialize(result, ResponseJson),
            StatusCodes.Ok,
            clock.UtcNow));
    }

    /// <summary>
    /// Refuses a queued command that describes a table which has since moved.
    /// </summary>
    /// <remarks>
    /// Idempotency stops a command being applied twice and says nothing about it being stale. A
    /// queued command must therefore say what the waiter was looking at; a live one need not,
    /// because it was made from a fresh read and the table's row version already guards the race.
    /// </remarks>
    private static void RequirePrecondition(ITableStateCommand command, DiningTable table)
    {
        if (command.ExpectedFromStatus is not { } expected)
        {
            if (command.Queued)
            {
                throw new ArgumentException(
                    "A queued command must carry the status the table had when it was tapped, so a "
                    + "stale replay can be told from a live one.",
                    nameof(command));
            }

            return;
        }

        if (table.Status != expected)
        {
            throw new CommandPreconditionFailedException(
                table.Id, table.Label, expected, table.Status, command.ClientCommandId);
        }
    }

    /// <summary>
    /// The command slug recorded against a processed command, derived from the method that ran it
    /// so the two can never disagree.
    /// </summary>
    private static string CommandTypeFor(string caller)
    {
        var name = caller.EndsWith("Async", StringComparison.Ordinal) ? caller[..^5] : caller;
        var slug = string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

        return $"table.{slug}";
    }

    private async Task<NextReservation> FindNextReservationAsync(
        Guid tableId,
        DateTime nowUtc,
        Guid? excludingReservationId,
        CancellationToken cancellationToken)
    {
        var next = await db.Reservations
            .AsNoTracking()
            .Where(r => r.DiningTableId == tableId
                        && r.EndUtc > nowUtc
                        && (r.Status == ReservationStatus.Confirmed
                            || r.Status == ReservationStatus.PendingApproval)
                        && (excludingReservationId == null || r.Id != excludingReservationId))
            .OrderBy(r => r.StartUtc)
            .Select(r => new { r.Id, r.StartUtc, r.PartySize })
            .FirstOrDefaultAsync(cancellationToken);

        return next is null
            ? NextReservation.None
            : new NextReservation(next.Id, next.StartUtc, next.PartySize);
    }

    /// <summary>
    /// How a stored response is written and read. Fixed here so a replay deserialises what was
    /// serialised however the host's own JSON options change later.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web);

    /// <summary>The status a successful transition answers with, stored alongside its body.</summary>
    private static class StatusCodes
    {
        public const int Ok = 200;
    }

    // ---------------------------------------------------------------- results

    private TableStateChange NewChange(
        DiningTable table,
        TableStatus fromStatus,
        TableStatus toStatus,
        string reason,
        DateTime nowUtc,
        Guid clientCommandId,
        Guid staffId,
        Guid? reservationId = null,
        Guid? tabId = null,
        Guid? tableSessionId = null) =>
        new(
            table.BranchId,
            table.Id,
            fromStatus,
            toStatus,
            reason,
            actor.Type,
            nowUtc,
            clientCommandId,
            staffId,
            reservationId,
            tabId,
            tableSessionId);

    private static TableStateChangeResult Success(
        DiningTable table,
        TableStatus fromStatus,
        TableStatus toStatus,
        DateTime nowUtc,
        Guid clientCommandId,
        NextReservation next,
        Guid? tableSessionId = null,
        Guid? reservationId = null,
        Guid? tabId = null,
        long? outstandingAmd = null,
        IReadOnlyList<TableStateWarning>? warnings = null)
    {
        var bufferMinutes = table.Branch.ReservationPolicy.BufferMinutes;

        return new TableStateChangeResult
        {
            BranchId = table.BranchId,
            TableId = table.Id,
            TableLabel = table.Label,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            State = TableStateProjection.Derive(toStatus, next.StartUtc, nowUtc, bufferMinutes),
            TableSessionId = tableSessionId,
            ReservationId = reservationId,
            TabId = tabId,
            NextReservationStartUtc = next.StartUtc,
            FreeUntilUtc = TableStateProjection.FreeUntil(next.StartUtc, bufferMinutes),
            AtUtc = nowUtc,
            ClientCommandId = clientCommandId,
            WasReplay = false,
            OutstandingAmd = outstandingAmd,
            Warnings = warnings ?? [],
        };
    }

    /// <summary>
    /// Warns, never blocks. The waiter may know the 20:00 party cancelled, or that these four are
    /// only having coffee.
    /// </summary>
    private static List<TableStateWarning> SeatingWarnings(
        DiningTable table,
        DateTime nowUtc,
        NextReservation next)
    {
        var warnings = new List<TableStateWarning>();
        var policy = table.Branch.ReservationPolicy;

        if (TableStateProjection.SeatingCollidesWithReservation(
                next.StartUtc, nowUtc, policy.TurnTimeMinutes))
        {
            warnings.Add(new TableStateWarning(
                TableStateWarning.UpcomingReservation,
                $"table {table.Label} reserved {FormatBranchLocalTime(next.StartUtc!.Value, table.Branch.TimeZoneId)}"));
        }

        // The tighter window: the booked party is nearly here. Carries the time and the size,
        // because "reserved 20:00 for 6" is what a waiter can actually act on - they know whether
        // six people will fit around the party they are about to seat.
        if (SessionOccupancy.WithinWalkInHoldback(next.StartUtc, nowUtc, policy.WalkInHoldbackMinutes))
        {
            var at = FormatBranchLocalTime(next.StartUtc!.Value, table.Branch.TimeZoneId);
            var forParty = next.PartySize is { } size ? $" for {size}" : string.Empty;

            warnings.Add(new TableStateWarning(
                TableStateWarning.WalkInHoldback,
                $"table {table.Label} reserved {at}{forParty}, within {policy.WalkInHoldbackMinutes} minutes"));
        }

        return warnings;
    }

    /// <summary>
    /// Renders an instant as wall-clock time at the branch, because "reserved 20:00" is what the
    /// waiter can act on and "reserved 16:00Z" is not.
    /// </summary>
    private static string FormatBranchLocalTime(DateTime utc, string timeZoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
                .ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A misconfigured zone must not break seating a table. Say plainly that this is UTC
            // rather than print a local-looking time that is wrong by four hours.
            return utc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }
    }

    /// <summary>The next relevant booking for a table, or none.</summary>
    /// <param name="PartySize">
    /// How many the booking is for. Carried so the holdback warning can say "reserved 20:00 for 4"
    /// - the number is most of what the waiter needs to judge whether the seating is safe.
    /// </param>
    private readonly record struct NextReservation(Guid? ReservationId, DateTime? StartUtc, int? PartySize)
    {
        public static NextReservation None => new(null, null, null);
    }
}
