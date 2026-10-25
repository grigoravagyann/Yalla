using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Opening a tab - by scanning the table's code, or from the diner's booking - and the race between phones
/// that makes it convergent: see <see cref="TabService"/>.
/// </summary>
internal sealed class TabOpener(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ITableStateService tableState,
    TabLedger ledger,
    ILogger<TabService> logger,
    TabLookups lookups,
    TabSignals signals,
    TabJoining joining)
{
    /// <summary>
    /// How many times a scan re-reads the table after losing a race before giving up. Each loss
    /// means another phone got there first; two losses in a row on one table would already be
    /// remarkable. The number is a backstop against a bug looping forever, not an expected path -
    /// the losing scanner walks through at most seat-conflict, then tab-conflict, then join, so
    /// three attempts already cover the real worst case and the margin is only there so a slow
    /// commit on the winning side cannot turn a legitimate join into a spurious failure.
    /// </summary>
    internal const int MaxOpenAttempts = 5;

    // ---------------------------------------------------------------- opening

    public async Task<TabAccessResult> OpenAsync(
        OpenTabCommand command,
        CancellationToken cancellationToken = default)
    {
        var device = TabLookups.RequireDevice(command.DeviceId);
        // The stored form: every token is written lower-case, and the column compares exactly.
        var qrToken = DiningTable.NormaliseQrToken(command.QrToken);

        TabLookups.RequireOpening(command.ClientCommandId, command.PartySize);

        if (await FindReplayAsync(command.ClientCommandId, device, cancellationToken) is { } replay)
        {
            return replay;
        }

        // A scanner whose diner account has a proved number is somebody the venue could reach; anybody
        // else - most scans, and a photographed code scanned from home - waits for a waiter to admit the
        // tab before it can order (decided 2026-10-20).
        var admitAs = await ScannerHasProvedNumberAsync(cancellationToken)
            ? TabAdmissionReason.VerifiedHost
            : (TabAdmissionReason?)null;

        return await OpenAtTableAsync(
            new TabOpening(
                device,
                command.ClientCommandId,
                command.DisplayName,
                command.SettlementMode,
                command.HideTotalFromGuests,
                admitAs),
            async token =>
            {
                var table = await db.DiningTables
                    .Include(t => t.Branch)
                        .ThenInclude(b => b.Venue)
                    .FirstOrDefaultAsync(t => t.QrToken == qrToken && t.IsActive, token)
                    ?? throw new KeyNotFoundException("That QR code does not belong to a table in service.");

                // A phone that was on a tab a waiter just turned away, or a table whose phantom was just turned
                // away, opens nothing for a while - otherwise the same photographed code opens the next phantom
                // at once. Only where the scan would wait for a waiter at all: a proved scanner is traceable.
                if (admitAs is null && table.Branch.ReservationPolicy.UnverifiedScansNeedAdmission)
                {
                    await RefuseTurnedAwayScanAsync(table, device, token);
                }

                // Nobody knows who scanned, so a free table is seated as a walk-in.
                return new TableToOpen(
                    table,
                    seatToken => tableState.SeatQrScanAsync(
                        new SeatQrScanCommand(table.BranchId, table.Id, command.PartySize ?? 1, command.ClientCommandId),
                        seatToken));
            },
            cancellationToken);
    }

    public async Task<TabAccessResult> OpenByBookingAsync(
        OpenTabByBookingCommand command,
        CancellationToken cancellationToken = default)
    {
        var device = TabLookups.RequireDevice(command.DeviceId);

        TabLookups.RequireOpening(command.ClientCommandId, command.PartySize);

        var dinerUserId = RequireVerifiedDiner();

        // Read from the row: the token says a diner account, not that its number was ever proved.
        await DinerPhoneGate.RequireVerifiedPhoneAsync(
            db, dinerUserId, "Opening a tab from a booking", cancellationToken);

        // Whose booking it is, before anything else - before the replay and before the table is
        // looked at - so nothing further down can tell a caller anything about a booking that is not
        // theirs.
        var reservationId = await FindOwnBookingAsync(command.BookingCode, dinerUserId, cancellationToken);

        if (await FindReplayAsync(command.ClientCommandId, device, cancellationToken) is { } replay)
        {
            return replay;
        }

        // The booking's own diner, whose number the gate above has just read as proved.
        return await OpenAtTableAsync(
            new TabOpening(
                device,
                command.ClientCommandId,
                command.DisplayName,
                command.SettlementMode,
                command.HideTotalFromGuests,
                TabAdmissionReason.Booker),
            async token =>
            {
                // Read again on every attempt. A waiter seating the party between two attempts moves
                // the booking to Seated and puts a sitting on the table, and the next pass has to see
                // both rather than try to seat the party a second time.
                var booking = await db.Reservations.FirstAsync(r => r.Id == reservationId, token);

                var table = await db.DiningTables
                    .Include(t => t.Branch)
                        .ThenInclude(b => b.Venue)
                    .FirstAsync(t => t.Id == booking.DiningTableId, token);

                // Expecting its party, and the table theirs by now - the booking's own rule, with the
                // branch's walk-in holdback as the moment the table starts being kept for them.
                booking.RequireTableIsTheirsAt(clock.UtcNow, table.Branch.ReservationPolicy.WalkInHoldbackMinutes);

                // Settled, but not cleared yet. Paying the last of a bill closes the sitting and
                // leaves the table occupied on purpose - the party is still sitting there - and only
                // a waiter freeing the table completes the booking. Between the two the booking still
                // reads Seated with no sitting left to join, and seating the party a second time
                // would throw the state machine's "Only a confirmed reservation can be seated",
                // which reaches the app as a bare conflicting-state carrying none of the booking's
                // facts. Decided here instead, so the answer stays in the booking family.
                if (booking.Status == ReservationStatus.Seated
                    && !await db.TableSessions.AnyAsync(
                        s => s.DiningTableId == table.Id && s.ClosedAtUtc == null, token))
                {
                    throw new BookingTabRefusedException(
                        BookingTabRefusedException.Ended,
                        booking,
                        booking.TableIsTheirsFromUtc(table.Branch.ReservationPolicy.WalkInHoldbackMinutes),
                        $"The bill for booking {booking.Code} has been settled. Ask a member of staff to "
                        + "order anything more - the table has not been cleared yet.");
                }

                // Taken off the floor plan with the booking still on it. A scan cannot find such a
                // table at all; this diner holds a booking for it and needs a waiter, so they are told
                // what an out-of-service table tells a scanner.
                if (!table.IsActive)
                {
                    throw OutOfService(table);
                }

                // A free table is seated as the booking, not as a walk-in - see SeatBookedPartyAsync.
                return new TableToOpen(
                    table,
                    seatToken => tableState.SeatBookedPartyAsync(
                        new SeatBookedPartyCommand(
                            table.BranchId, table.Id, booking.Id, command.ClientCommandId, command.PartySize),
                        seatToken),
                    booking);
            },
            cancellationToken);
    }

    /// <summary>
    /// The caller's own booking with this code, or the one refusal for every other case.
    /// </summary>
    /// <remarks>
    /// Matched in the stored form (<see cref="ReservationCode.Normalise"/>), so "dfj-fqy" finds
    /// DFJFQY. Somebody else's booking is refused with exactly the answer a code nobody holds gets:
    /// the code is read out at the door and proves nothing, and a different answer would tell anybody
    /// with an account which codes are live.
    /// </remarks>
    private async Task<Guid> FindOwnBookingAsync(
        string? bookingCode,
        Guid dinerUserId,
        CancellationToken cancellationToken)
    {
        var code = ReservationCode.Normalise(bookingCode);

        // Not a code this system could have issued, so there is nothing to ask the database.
        if (!ReservationCode.IsWellFormed(code))
        {
            throw new BookingNotFoundException();
        }

        var booking = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Code == code)
            .Select(r => new { r.Id, r.DinerUserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (booking is null || booking.DinerUserId != dinerUserId)
        {
            throw new BookingNotFoundException();
        }

        return booking.Id;
    }

    /// <summary>
    /// The calling diner's account. The <c>VerifiedDiner</c> policy on the route has already refused
    /// anyone else; this is the id the booking is looked up under, and it fails closed if the two
    /// ever disagree.
    /// </summary>
    private Guid RequireVerifiedDiner()
    {
        if (actor.Type != ActorType.Diner || actor.DinerUserId is not { } dinerUserId || dinerUserId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Opening a tab from a booking requires a verified diner account.");
        }

        return dinerUserId;
    }

    private static DomainStateException OutOfService(DiningTable table) =>
        new($"Table {table.Label} is out of service. Ask a member of staff for another table.");

    /// <summary>
    /// What an opening asks for, whichever way it found its table - and whether a tab it opens may order
    /// at once (<paramref name="AdmitAs"/>), or waits for a waiter (null).
    /// </summary>
    private sealed record TabOpening(
        string Device,
        Guid ClientCommandId,
        string? DisplayName,
        SettlementMode? SettlementMode,
        bool? HideTotalFromGuests,
        TabAdmissionReason? AdmitAs);

    /// <summary>
    /// Whether the caller scanned with a diner account whose phone number is proved - read from the row,
    /// as the booking gate reads it, because the token cannot carry a fact that changes under it.
    /// </summary>
    private async Task<bool> ScannerHasProvedNumberAsync(CancellationToken cancellationToken) =>
        actor.Type == ActorType.Diner
        && actor.DinerUserId is { } dinerUserId
        && await db.DinerUsers
            .AsNoTracking()
            .AnyAsync(
                d => d.Id == dinerUserId && d.PhoneVerifiedAtUtc != null && d.IsActive && d.DeletedAtUtc == null,
                cancellationToken);

    /// <summary>
    /// The table an opening is for, and how to seat it when nobody is sitting there yet - the one step
    /// a scan and a booking do differently.
    /// </summary>
    private sealed record TableToOpen(
        DiningTable Table,
        Func<CancellationToken, Task<TableStateChangeResult>> SeatFreeTable,
        Reservation? Booking = null);

    /// <summary>
    /// The four table-state cases of <c>docs/tabs.md</c>, for a scan and a booking alike.
    /// </summary>
    /// <remarks>
    /// One loop, so the host, the pending guest, the closing tab and every race between two phones
    /// are the same whichever way the table was found. <paramref name="findTable"/> runs on every
    /// attempt, because losing a race is new information about the table, and the change tracker is
    /// cleared before looking again.
    /// </remarks>
    private async Task<TabAccessResult> OpenAtTableAsync(
        TabOpening opening,
        Func<CancellationToken, Task<TableToOpen>> findTable,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxOpenAttempts; attempt++)
        {
            var (table, seatFreeTable, booking) = await findTable(cancellationToken);

            // The sticker on the table outlives the business relationship. A suspended or deleted
            // venue seats nobody new, whatever is still printed on the furniture.
            VenueGate.RequireOpenForBusiness(table.Branch);
            TabLookups.RequirePaid(table.Branch);

            if (table.Status == TableStatus.OutOfService)
            {
                throw OutOfService(table);
            }

            // The authoritative answer to "is anyone sitting here", rather than the cached
            // status: the session row is what the unique index is on.
            var session = await db.TableSessions
                .FirstOrDefaultAsync(s => s.DiningTableId == table.Id && s.ClosedAtUtc == null, cancellationToken);

            var outcome = TabOpenOutcome.OpenedOnExistingSession;

            if (session is null)
            {
                // Case 1 - free table. Seat the party through the state machine, which writes the
                // audit row and takes part in the same concurrency handling as a waiter's seating.
                TableStateChangeResult seated;

                try
                {
                    seated = await seatFreeTable(cancellationToken);
                }
                catch (TableStateConflictException ex)
                {
                    // Somebody else seated this table between our read and our write - almost
                    // always another phone at the same table, or a waiter seating the booking. Their
                    // session is now the open one. The change tracker still holds our failed
                    // attempt, so clear it before looking again; on the next pass the table is
                    // occupied and we join.
                    logger.LogInformation(
                        "Opening a tab lost the seating race on table {TableId} ({TableLabel}); now {CurrentStatus}. Re-reading.",
                        ex.TableId, ex.TableLabel, ex.CurrentStatus);

                    db.ChangeTracker.Clear();
                    continue;
                }

                session = await db.TableSessions.FirstAsync(
                    s => s.Id == seated.TableSessionId!.Value, cancellationToken);

                outcome = TabOpenOutcome.OpenedNewSession;
            }

            // A booking opens only its own sitting. Anybody else still sitting at the table - a
            // walk-in nobody cleared, another party the waiter put there - is not this booker's
            // company, and joining their tab would hand a stranger's bill to the booker and the
            // booker to a stranger's approval. A waiter has to free the table first.
            if (booking is not null && session.ReservationId != booking.Id)
            {
                throw new BookingTabRefusedException(
                    BookingTabRefusedException.TableOccupied,
                    booking,
                    booking.TableIsTheirsFromUtc(table.Branch.ReservationPolicy.WalkInHoldbackMinutes),
                    $"Table {table.Label} still has another party seated. Ask a member of staff to free it.",
                    table.Label);
            }

            // Case 2 - the session already has a tab. No second tab: the newcomer joins it.
            var existingTab = await lookups.LoadTabBySessionAsync(session.Id, cancellationToken);

            if (existingTab is not null)
            {
                TabParticipant joined;

                try
                {
                    joined = await joining.JoinExistingAsync(
                        existingTab, opening.Device, opening.DisplayName, cancellationToken, asBooker: booking is not null);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Two phones reached a tab staff opened at the same moment and both went for the
                    // host role. The other one has it; look again and join as a guest.
                    logger.LogInformation(
                        "Joining tab {TabId} lost the race to host it; re-reading.", existingTab.Id);

                    db.ChangeTracker.Clear();
                    ledger.DiscardPending();
                    continue;
                }

                return await lookups.ResultAsync(existingTab, joined, TabOpenOutcome.JoinedExistingTab, wasReplay: false, cancellationToken);
            }

            // Case 1 continued, or case 3 - a seated party with no tab yet. Open one on the
            // session with this phone as host.
            var nowUtc = clock.UtcNow;

            var tab = new Tab(
                table.BranchId,
                table.Id,
                session.Id,
                nowUtc,
                table.Branch.ReservationPolicy.ServiceChargePercent,
                // Who opened it decides first; a scan with no proved number waits for a waiter only where the
                // branch has admission on (the default) - off, it orders at once, as every scan did before.
                opening.AdmitAs ?? (table.Branch.ReservationPolicy.UnverifiedScansNeedAdmission
                    ? null
                    : TabAdmissionReason.NotRequired),
                opening.SettlementMode ?? SettlementMode.AnyonePaysAnyAmount,
                opening.HideTotalFromGuests ?? false,
                opening.ClientCommandId);

            var host = TabParticipant.Host(
                tab.Id, TabLookups.NameOrDefault(opening.DisplayName, 1), opening.Device, nowUtc, actor.DinerUserId);

            tab.SetHostParticipant(host.Id);
            session.AttachTab(tab.Id);

            db.Tabs.Add(tab);
            db.TabParticipants.Add(host);

            // Onto the branch's change stream in the same commit, so every tablet learns a table is
            // waiting for somebody to look at it - a scan onto a table a waiter seated changes no table
            // state, and a scan that seated the table committed its own row before the tab existed.
            if (!tab.IsAdmitted)
            {
                signals.SignalTable(tab, TableChangeSignal.TabAwaitingAdmission, "a scan is waiting to be let in", staffId: null, nowUtc);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TabPerSession))
            {
                // Another phone attached a tab to this very session first. Drop ours and join theirs.
                logger.LogInformation(
                    "Opening a tab lost the tab race on session {SessionId}; joining the tab that won.", session.Id);

                db.ChangeTracker.Clear();
                continue;
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TabClientCommand))
            {
                // The same request arrived twice at once and the other copy won. Answer from it.
                db.ChangeTracker.Clear();

                return await FindReplayAsync(opening.ClientCommandId, opening.Device, cancellationToken)
                       ?? throw new InvalidOperationException(
                           $"Command {opening.ClientCommandId} violated the tab idempotency index but no tab was found.");
            }

            logger.LogInformation(
                "Tab {TabId} opened on table {TableId}; outcome {Outcome}.",
                tab.Id, table.Id, outcome);

            return await lookups.ResultAsync(tab, host, outcome, wasReplay: false, cancellationToken);
        }

        throw new DomainStateException(
            "Several phones are opening this table at the same moment. Scan again in a second.");
    }

    /// <summary>
    /// Refuses a scan from a phone that opened (hosted) a tab a waiter turned away at this branch within
    /// <see cref="ScanCooldown.Device"/>, and - when the scan would open the table's tab - of a table whose
    /// last scan was turned away within <see cref="ScanCooldown.Table"/>. 409 <c>scan-turned-away</c>.
    /// </summary>
    /// <remarks>
    /// Both read the branch's turned-away tabs through one small filtered index. Only the host's phone: a real
    /// party that scanned onto a phantom's tab before the waiter came is not punished for it. The phone's id is
    /// its own choice, so that half is friction against the honest-but-rejected; the table's pause is what stops a
    /// script re-opening a phantom the moment it is closed. A table that already has a live tab is joined as
    /// always: the pause is about opening one.
    /// </remarks>
    private async Task RefuseTurnedAwayScanAsync(DiningTable table, string device, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;
        var deviceSince = nowUtc - ScanCooldown.Device;

        var deviceTurnedAwayAt = await db.Tabs
            .AsNoTracking()
            .Where(t => t.BranchId == table.BranchId
                        && t.Admission == TabAdmission.Rejected
                        && t.AdmissionDecidedAtUtc > deviceSince
                        && t.Participants.Any(p => p.Id == t.HostParticipantId && p.DeviceId == device))
            .MaxAsync(t => t.AdmissionDecidedAtUtc, cancellationToken);

        if (deviceTurnedAwayAt is { } byDevice)
        {
            throw new ScanTurnedAwayException(byDevice + ScanCooldown.Device);
        }

        var tableSince = nowUtc - ScanCooldown.Table;

        var tableTurnedAwayAt = await db.Tabs
            .AsNoTracking()
            .Where(t => t.BranchId == table.BranchId
                        && t.DiningTableId == table.Id
                        && t.Admission == TabAdmission.Rejected
                        && t.AdmissionDecidedAtUtc > tableSince)
            .MaxAsync(t => t.AdmissionDecidedAtUtc, cancellationToken);

        if (tableTurnedAwayAt is { } byTable
            && !await db.Tabs.AnyAsync(
                t => t.DiningTableId == table.Id
                     && t.Status == TabStatus.Open
                     && t.Admission != TabAdmission.Rejected,
                cancellationToken))
        {
            throw new ScanTurnedAwayException(byTable + ScanCooldown.Table);
        }
    }

    // ---------------------------------------------------------------- idempotency

    /// <summary>
    /// The tab a command already opened, answered again for the same device.
    /// </summary>
    /// <remarks>
    /// Scoped to the device on purpose. A replay is the <i>same phone</i> sending the same scan
    /// again; a different phone presenting somebody else's command id is a client bug, and the
    /// honest answer is that the id is taken - not a token onto the tab it names.
    /// </remarks>
    private async Task<TabAccessResult?> FindReplayAsync(Guid clientCommandId, string device, CancellationToken cancellationToken)
    {
        var tab = await db.Tabs
            .Include(t => t.Participants)
            .FirstOrDefaultAsync(t => t.ClientCommandId == clientCommandId, cancellationToken);

        if (tab is null)
        {
            return null;
        }

        var mine = tab.Participants.FirstOrDefault(p => p.DeviceId == device)
                   ?? throw new ClientCommandIdAlreadyUsedException(clientCommandId);

        // Whether the original scan seated the table is recorded by the state machine: it wrote an
        // audit row under this command id only if it did.
        var seatedTheTable = await db.TableStateChanges
            .AsNoTracking()
            .AnyAsync(c => c.ClientCommandId == clientCommandId, cancellationToken);

        logger.LogInformation(
            "Scan {ClientCommandId} was already applied; returning tab {TabId} again.", clientCommandId, tab.Id);

        return await lookups.ResultAsync(
            tab,
            mine,
            seatedTheTable ? TabOpenOutcome.OpenedNewSession : TabOpenOutcome.OpenedOnExistingSession,
            wasReplay: true,
            cancellationToken);
    }
}
