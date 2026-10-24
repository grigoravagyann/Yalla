using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Auth;
using Yalla.Application.Media;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Application.Messaging;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Tabs: opening one by scanning, inviting others, and the permission model between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opening is convergent.</b> Whatever two phones at the same table do at the same moment, the
/// answer is one tab, one host and one pending guest. The service does not achieve that with a
/// lock. It relies on two unique indexes the database already enforces - one open session per
/// table, one tab per session - and on the table's <c>RowVersion</c>, and treats losing any of
/// those races as new information: drop the failed attempt, look at the table again, and take
/// whichever of the four table-state cases now applies. See <c>docs/tabs.md</c>.
/// </para>
/// <para>
/// <b>Seating goes through the state machine.</b> A free table is occupied by
/// <see cref="ITableStateService.SeatQrScanAsync"/>, which opens the session and writes the audit
/// row in one commit like every other transition. The tab and its host are a second commit
/// against that session. A crash between the two leaves an occupied session with no tab, which is
/// precisely the "seated party, no tab yet" case the next scan already handles - so the gap
/// self-heals rather than needing a sweeper.
/// </para>
/// <para>
/// <b>Who may do what is decided here.</b> The host check and the staff check are in the service,
/// not in an endpoint, so they hold for every caller.
/// </para>
/// </remarks>
internal sealed class TabService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ITableStateService tableState,
    ITabQuery query,
    ITokenAuthorityCheck authority,
    TabParticipantTokens tokens,
    IOptions<TabOptions> options,
    TabLedger ledger,
    IOutbox outbox,
    StaffBranchGuard branchGuard,
    IActivityLog activity,
    ILogger<TabService> logger) : ITabService
{
    /// <summary>
    /// How many times a scan re-reads the table after losing a race before giving up. Each loss
    /// means another phone got there first; two losses in a row on one table would already be
    /// remarkable. The number is a backstop against a bug looping forever, not an expected path -
    /// the losing scanner walks through at most seat-conflict, then tab-conflict, then join, so
    /// three attempts already cover the real worst case and the margin is only there so a slow
    /// commit on the winning side cannot turn a legitimate join into a spurious failure.
    /// </summary>
    private const int MaxOpenAttempts = 5;

    // ---------------------------------------------------------------- opening and joining

    public async Task<TabAccessResult> OpenAsync(
        OpenTabCommand command,
        CancellationToken cancellationToken = default)
    {
        var device = RequireDevice(command.DeviceId);
        // The stored form: every token is written lower-case, and the column compares exactly.
        var qrToken = DiningTable.NormaliseQrToken(command.QrToken);

        RequireOpening(command.ClientCommandId, command.PartySize);

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
        var device = RequireDevice(command.DeviceId);

        RequireOpening(command.ClientCommandId, command.PartySize);

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

    /// <summary>
    /// What every opening must carry. Named <c>command</c>, which the API reports as a refusal of the
    /// request as a whole rather than of a field - the filter in front has already refused a missing
    /// command id with its own sentence, so this is the backstop for any other caller.
    /// </summary>
    private static void RequireOpening(Guid clientCommandId, int? partySize)
    {
        if (clientCommandId == Guid.Empty)
        {
            throw new ArgumentException("A clientCommandId is required.", "command");
        }

        if (partySize is { } size && size <= 0)
        {
            throw new ArgumentOutOfRangeException("command", size, "Party size must be greater than zero.");
        }
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
            RequirePaid(table.Branch);

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
            var existingTab = await LoadTabBySessionAsync(session.Id, cancellationToken);

            if (existingTab is not null)
            {
                TabParticipant joined;

                try
                {
                    joined = await JoinExistingAsync(
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

                return await ResultAsync(existingTab, joined, TabOpenOutcome.JoinedExistingTab, wasReplay: false, cancellationToken);
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
                tab.Id, NameOrDefault(opening.DisplayName, 1), opening.Device, nowUtc, actor.DinerUserId);

            tab.SetHostParticipant(host.Id);
            session.AttachTab(tab.Id);

            db.Tabs.Add(tab);
            db.TabParticipants.Add(host);

            // Onto the branch's change stream in the same commit, so every tablet learns a table is
            // waiting for somebody to look at it - a scan onto a table a waiter seated changes no table
            // state, and a scan that seated the table committed its own row before the tab existed.
            if (!tab.IsAdmitted)
            {
                SignalTable(tab, TableChangeSignal.TabAwaitingAdmission, "a scan is waiting to be let in", staffId: null, nowUtc);
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

            return await ResultAsync(tab, host, outcome, wasReplay: false, cancellationToken);
        }

        throw new DomainStateException(
            "Several phones are opening this table at the same moment. Scan again in a second.");
    }

    public async Task<TabAccessResult> JoinAsync(
        JoinTabCommand command,
        CancellationToken cancellationToken = default)
    {
        var device = RequireDevice(command.DeviceId);
        var nowUtc = clock.UtcNow;
        var value = (command.JoinToken ?? string.Empty).Trim();

        var invitation = await db.TabJoinTokens
            .FirstOrDefaultAsync(t => t.Token == value, cancellationToken);

        if (invitation is null || !invitation.IsUsableAt(nowUtc))
        {
            // Unknown, revoked and expired all answer the same way. A screenshot from last Tuesday
            // must not learn anything about the tab it once pointed at.
            throw new AuthenticationFailedException(
                "join-token-invalid", "That invitation is no longer valid. Ask the host for a new one.");
        }

        var tab = await LoadTabAsync(invitation.TabId, cancellationToken);

        // Nobody new joins at a venue that is no longer open for business. The people already on
        // the tab keep reading and settling it - LoadTabAsync deliberately does not gate on this.
        VenueGate.RequireOpenForBusiness(tab.Branch);

        var participant = await JoinExistingAsync(tab, device, command.DisplayName, cancellationToken);

        return await ResultAsync(tab, participant, TabOpenOutcome.JoinedExistingTab, wasReplay: false, cancellationToken);
    }

    /// <summary>
    /// Puts a device on an existing tab, or recognises it if it is already there.
    /// </summary>
    /// <remarks>
    /// Re-scanning after the phone locked, or reopening the app, must not put a second person on
    /// the bill: the device is what recognises someone with no account. Somebody new lands pending
    /// - the host taps approve - and only while the tab is still open, so a person who has paid
    /// and left cannot find a stranger's dessert added afterwards.
    /// </remarks>
    private async Task<TabParticipant> JoinExistingAsync(
        Tab tab,
        string device,
        string? displayName,
        CancellationToken cancellationToken,
        bool asBooker = false)
    {
        var nowUtc = clock.UtcNow;

        // The booker is recognised by their account as well as the phone: a second phone of theirs,
        // or the app reinstalled, is still the person whose booking this sitting is.
        var bookerId = asBooker ? actor.DinerUserId : null;

        var existing = tab.Participants.FirstOrDefault(p => p.DeviceId == device && !p.IsRemoved)
                       ?? (bookerId is { } id
                           ? tab.Participants.FirstOrDefault(p => p.UserId == id && !p.IsRemoved)
                           : null);

        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                existing.SetDisplayName(displayName);
            }

            // The booker is here: their booking's sitting is theirs, so a tab a friend's unverified scan
            // opened on it is admitted - the booking is the proof a waiter would otherwise give.
            if (asBooker)
            {
                AdmitInContext(tab, TabAdmissionReason.Booker, staffId: null, nowUtc);
            }

            if (asBooker && !existing.IsApproved)
            {
                await ApproveBookerAsync(tab, existing, nowUtc, cancellationToken);
            }
            else
            {
                await ledger.SaveAppendedAsync(cancellationToken);
            }

            return existing;
        }

        if (!tab.AcceptsNewParticipants)
        {
            throw new DomainStateException(tab.Status == TabStatus.Closing
                ? "This tab is being settled and no longer takes new people. Ask a member of staff."
                : "That tab is closed. Scan the table's QR code to open a new one.");
        }

        // A tab staff opened has nobody on it. Somebody landing on it pending would wait for a
        // host who does not exist, so the first person to arrive hosts it - exactly as if they
        // had scanned the table first. The tab row changes, so two phones racing for the role
        // meet on its row version and the loser comes back round as an ordinary guest.
        if (tab.HostParticipantId is null)
        {
            var host = TabParticipant.Host(
                tab.Id,
                NameOrDefault(displayName, tab.Participants.Count + 1),
                device,
                nowUtc,
                actor.DinerUserId);

            tab.SetHostParticipant(host.Id);
            db.TabParticipants.Add(host);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Participant {ParticipantId} became host of tab {TabId}, which staff opened with nobody on it.",
                host.Id, tab.Id);

            return host;
        }

        var guest = TabParticipant.Guest(
            tab.Id,
            NameOrDefault(displayName, tab.Participants.Count + 1),
            device,
            nowUtc,
            tab.HideTotalFromGuests,
            actor.DinerUserId);

        db.TabParticipants.Add(guest);

        // It is their booking: the party at the table is theirs, so nobody has to let them on. The
        // host - the friend who scanned first - stays host, and the tab may order from now on.
        if (asBooker)
        {
            AdmitInContext(tab, TabAdmissionReason.Booker, staffId: null, nowUtc);

            await ApproveBookerAsync(tab, guest, nowUtc, cancellationToken);

            logger.LogInformation(
                "Booker {ParticipantId} joined tab {TabId} on their own booking's sitting, approved.", guest.Id, tab.Id);

            return guest;
        }

        AnnouncePendingJoin(tab, guest, displayName);

        await ledger.SaveAppendedAsync(cancellationToken);

        logger.LogInformation(
            "Participant {ParticipantId} joined tab {TabId} pending the host's approval.", guest.Id, tab.Id);

        return guest;
    }

    /// <summary>
    /// Tells the host somebody is waiting to be let on: on the tab's stream, which every phone at the
    /// table polls, and on the host's lock screen when the host scanned with an account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only for a join that lands pending. The first phone onto a tab staff opened hosts it, and the
    /// booker on their own booking's sitting is approved on the spot and announced by
    /// <c>ParticipantApproved</c>. Neither is waiting for anybody, and a second event for the same
    /// moment would only make every phone refetch twice.
    /// </para>
    /// <para>
    /// The push has one address, the host's account - see <see cref="TabParticipant.UserId"/>. Most
    /// hosts scanned without one, and then the stream is all there is. The host's own account on a
    /// second phone lands pending like anybody else, and is not told that it wants to join itself.
    /// </para>
    /// <para>
    /// Somebody the host already turned away on this tab - the same phone, or the same account on
    /// another - is on the stream again but not pushed again. The host sees them on the bill and can
    /// still let them on; they are not woken for every retry of a question they answered.
    /// </para>
    /// </remarks>
    private void AnnouncePendingJoin(Tab tab, TabParticipant joiner, string? givenName)
    {
        // The shape of every other roster event, so the redactor's participant rule already decides
        // it: a pending reader sees who it is about and nothing that says who they are.
        ledger.Append(tab.Id, TabEventType.ParticipantJoined, new
        {
            participantId = joiner.Id,
            displayName = joiner.DisplayName,
            status = (int)joiner.Status,
            canOrder = joiner.CanOrder,
            canSeeTableTotal = joiner.CanSeeTableTotal,
            canPay = joiner.CanPay,
        });

        var hostUserId = tab.Participants.FirstOrDefault(p => p.Id == tab.HostParticipantId)?.UserId;

        if (hostUserId is not { } dinerUserId || joiner.UserId == dinerUserId)
        {
            return;
        }

        var askedBefore = tab.Participants.Any(p =>
            p.WasTurnedAway
            && (p.DeviceId == joiner.DeviceId || (joiner.UserId is { } account && p.UserId == account)));

        if (askedBefore)
        {
            logger.LogInformation(
                "Participant {ParticipantId} is back on tab {TabId} after the host turned them away; not pushing again.",
                joiner.Id, tab.Id);

            return;
        }

        outbox.Enqueue(
            OutboxMessageTypes.JoinRequested,
            new
            {
                tabId = tab.Id,
                participantId = joiner.Id,
                dinerUserId,
                tableLabel = tab.DiningTable?.Label ?? string.Empty,
                // The name they gave, or none. "Guest 3" is the roster's placeholder in English; the
                // push says "a guest" in the host's own language instead.
                guestName = string.IsNullOrWhiteSpace(givenName) ? null : joiner.DisplayName,
            },
            clock.UtcNow,
            OutboxMessageTypes.KeyFor("participant", joiner.Id, "join-requested"));
    }

    /// <summary>
    /// Lets the booker onto their own booking's tab without the host's tap, recorded on the ledger as
    /// an approval so the other phones' rosters move as if the host had approved them.
    /// </summary>
    private async Task ApproveBookerAsync(
        Tab tab,
        TabParticipant participant,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        participant.Approve(nowUtc);

        ledger.Append(tab.Id, TabEventType.ParticipantApproved, new
        {
            participantId = participant.Id,
            displayName = participant.DisplayName,
            status = (int)participant.Status,
            canOrder = participant.CanOrder,
            canSeeTableTotal = participant.CanSeeTableTotal,
            canPay = participant.CanPay,
        });

        await ledger.SaveAppendedAsync(cancellationToken);

        authority.InvalidateParticipant(tab.Id, participant.Id);
    }

    // ---------------------------------------------------------------- invitations

    public async Task<TabJoinTokenResult> CreateJoinTokenAsync(
        Guid tabId,
        Guid actingParticipantId,
        CancellationToken cancellationToken = default)
    {
        var tab = await LoadTabAsync(tabId, cancellationToken, includeJoinTokens: true);
        var host = RequireHost(tab, actingParticipantId, "Inviting others");

        if (!tab.AcceptsNewParticipants)
        {
            throw new DomainStateException("This tab is being settled; nobody new can join it.");
        }

        var nowUtc = clock.UtcNow;

        // One live invitation at a time. Asking for another is how the host refreshes an expired
        // one, and it also kills any earlier token that is still circulating - a refresh is a
        // statement that the old one should stop working.
        foreach (var live in tab.JoinTokens.Where(t => t.IsUsableAt(nowUtc)))
        {
            live.Revoke(nowUtc);
        }

        var token = new TabJoinToken(tab.Id, host.Id, nowUtc);
        db.TabJoinTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        return new TabJoinTokenResult(
            token.Token,
            options.Value.JoinUrlTemplate.Replace("{token}", Uri.EscapeDataString(token.Token), StringComparison.Ordinal),
            token.ExpiresAtUtc,
            tab.Id);
    }

    // ---------------------------------------------------------------- participants

    public Task<TabParticipantView> ApproveParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        ChangeParticipantAsync(
            tabId, actingParticipantId, participantId, "Approving a participant",
            TabEventType.ParticipantApproved,
            (participant, nowUtc) => participant.Approve(nowUtc), cancellationToken,
            alreadyAnswered: participant => participant.IsApproved);

    public Task<TabParticipantView> RejectParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        ChangeParticipantAsync(
            tabId, actingParticipantId, participantId, "Rejecting a participant",
            TabEventType.ParticipantRejected,
            (participant, nowUtc) => participant.Reject(nowUtc), cancellationToken,
            alreadyAnswered: participant => participant.WasTurnedAway);

    /// <summary>
    /// A status change, never a delete. The participant's order lines and payments reference the
    /// row and must stay answerable after they have gone.
    /// </summary>
    public Task<TabParticipantView> RemoveParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        ChangeParticipantAsync(
            tabId, actingParticipantId, participantId, "Removing a participant",
            TabEventType.ParticipantRemoved,
            (participant, nowUtc) => participant.Remove(nowUtc), cancellationToken);

    /// <summary>
    /// A participant takes themself off the tab - the same status change a host's remove makes, so
    /// their lines and payments stay answerable and they stop being put on shared lines.
    /// </summary>
    /// <remarks>
    /// The app used to fake this: it cleared the tab on the phone while the server kept the guest
    /// approved, on every shared bottle ordered after they walked out and in the split. A host hands
    /// over to the longest-standing approved guest, which is what the app already tells them; a host
    /// with nobody approved to hand to is refused, rather than leaving a tab nobody can let anyone
    /// onto.
    /// </remarks>
    public async Task<TabParticipantView> LeaveAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        var tab = await LoadTabAsync(tabId, cancellationToken);

        var leaving = tab.Participants.FirstOrDefault(p => p.Id == participantId && !p.IsRemoved)
                      ?? throw new TabPermissionException("Leaving this tab", "a participant on this tab");

        TabParticipant? successor = null;

        if (leaving.IsHost && tab.HostParticipantId == leaving.Id)
        {
            // Approved only: somebody still waiting to be let on cannot let anybody else on. Join
            // order, then approval order for two phones that scanned in the same instant.
            successor = tab.Participants
                .Where(p => p.Id != leaving.Id && p.Status == ParticipantStatus.Approved)
                .OrderBy(p => p.JoinedAtUtc)
                .ThenBy(p => p.ApprovedAtUtc)
                .FirstOrDefault()
                ?? throw new DomainStateException(
                    "You are the only one on this tab, so there is nobody to hand it to. Ask a waiter "
                    + "to take it over or close it.");

            // The order ReassignHostAsync keeps: the tab refuses a closed bill before either row is
            // touched, and the new host must be approved before they can take the role.
            tab.ReassignHost(successor.Id);
            successor.BecomeHost();
            leaving.BecomeGuest();

            ledger.Append(tab.Id, TabEventType.HostReassigned, new
            {
                fromParticipantId = leaving.Id,
                toParticipantId = successor.Id,
            });
        }

        leaving.Remove(clock.UtcNow);

        // The roster event every phone at the table already handles, marked as their own doing.
        ledger.Append(tab.Id, TabEventType.ParticipantRemoved, new
        {
            participantId = leaving.Id,
            displayName = leaving.DisplayName,
            status = (int)leaving.Status,
            canOrder = leaving.CanOrder,
            canSeeTableTotal = leaving.CanSeeTableTotal,
            canPay = leaving.CanPay,
            left = true,
        });

        await ledger.SaveAppendedAsync(cancellationToken);

        // Their token has to stop working now rather than when the cached standing lapses, and the
        // new host's standing changed with the role.
        authority.InvalidateParticipant(tab.Id, leaving.Id);

        if (successor is not null)
        {
            authority.InvalidateParticipant(tab.Id, successor.Id);
        }

        logger.LogInformation(
            "Participant {ParticipantId} left tab {TabId}; the host is now {HostParticipantId}.",
            leaving.Id, tab.Id, tab.HostParticipantId);

        return await ToViewAsync(leaving, tab.Status, tab.Admission, cancellationToken);
    }

    /// <summary>
    /// The entity refuses <c>CanPay</c> without <c>CanSeeTableTotal</c>; nothing here corrects the
    /// request silently, so the refusal reaches the host as a 400 that says why.
    /// </summary>
    public Task<TabParticipantView> SetPermissionsAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        SetParticipantPermissionsCommand permissions,
        CancellationToken cancellationToken = default) =>
        ChangeParticipantAsync(
            tabId, actingParticipantId, participantId, "Changing permissions",
            TabEventType.ParticipantPermissionsChanged,
            (participant, _) => participant.SetPermissions(
                permissions.CanOrder, permissions.CanSeeTableTotal, permissions.CanPay),
            cancellationToken);

    public async Task<TabParticipantView> SetDisplayNameAsync(
        Guid tabId,
        Guid participantId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var tab = await LoadTabAsync(tabId, cancellationToken);

        var participant = tab.Participants.FirstOrDefault(p => p.Id == participantId && !p.IsRemoved)
                          ?? throw new KeyNotFoundException("No such participant on that tab.");

        participant.SetDisplayName(displayName);

        ledger.Append(tab.Id, TabEventType.ParticipantRenamed, new
        {
            participantId = participant.Id,
            displayName = participant.DisplayName,
        });

        await ledger.SaveAppendedAsync(cancellationToken);

        return await ToViewAsync(participant, tab.Status, tab.Admission, cancellationToken);
    }

    private async Task<TabParticipantView> ChangeParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        string operation,
        TabEventType eventType,
        Action<TabParticipant, DateTime> change,
        CancellationToken cancellationToken,
        Func<TabParticipant, bool>? alreadyAnswered = null)
    {
        var tab = await LoadTabAsync(tabId, cancellationToken);
        RequireHost(tab, actingParticipantId, operation);

        var target = tab.Participants.FirstOrDefault(p => p.Id == participantId)
                     ?? throw new KeyNotFoundException("No such participant on that tab.");

        // Answered already: the banner and the People screen offer the same tap, and a retry on
        // flaky wifi sends it twice. The answer is the one the first tap got. A second event would
        // tell every phone it happened twice, and a second approval push collides with the first on
        // its idempotency key.
        if (alreadyAnswered?.Invoke(target) == true)
        {
            return await ToViewAsync(target, tab.Status, tab.Admission, cancellationToken);
        }

        change(target, clock.UtcNow);

        // The other phones at the table are showing this roster. Written in the same SaveChanges as
        // the change, so the stream can never disagree with the tab.
        // The one message worth pushing on this path: the guest is sitting at the table with the
        // phone in their pocket, on a screen that says "waiting for the host". Only reaches somebody
        // whose phone was carrying a diner token when it scanned - see TabParticipant.UserId.
        if (eventType == TabEventType.ParticipantApproved && target.UserId is { } dinerUserId)
        {
            outbox.Enqueue(
                OutboxMessageTypes.ParticipantApproved,
                new
                {
                    tabId = tab.Id,
                    participantId = target.Id,
                    dinerUserId,
                    tableLabel = tab.DiningTable?.Label ?? string.Empty,
                },
                clock.UtcNow,
                OutboxMessageTypes.KeyFor("participant", target.Id, "approved"));
        }

        ledger.Append(tab.Id, eventType, new
        {
            participantId = target.Id,
            displayName = target.DisplayName,
            status = (int)target.Status,
            canOrder = target.CanOrder,
            canSeeTableTotal = target.CanSeeTableTotal,
            canPay = target.CanPay,
        });

        try
        {
            await ledger.SaveAppendedAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.OutboxIdempotency))
        {
            // Two approvals at the same instant both read the joiner as pending, and the other one
            // committed first: its push took the key this one was about to write. The joiner is
            // approved either way, so this answers with the approval that won, as a second tap
            // after it would have been answered.
            logger.LogInformation(
                "{Operation} on tab {TabId} lost the race for participant {ParticipantId}; answering with the one that won.",
                operation, tabId, participantId);

            db.ChangeTracker.Clear();
            ledger.DiscardPending();
            authority.InvalidateParticipant(tabId, participantId);

            var current = await LoadTabAsync(tabId, cancellationToken);

            return await ToViewAsync(
                current.Participants.First(p => p.Id == participantId), current.Status, current.Admission, cancellationToken);
        }

        // Their standing just changed, so the cached answer about it is wrong. A participant the
        // host has just removed must be refused on their next call, not on the one after the cache
        // lapses - the approval flow is the whole reason the token is not enough on its own.
        authority.InvalidateParticipant(tab.Id, target.Id);

        logger.LogInformation(
            "{Operation} on tab {TabId}: participant {ParticipantId} is now {Status}.",
            operation, tab.Id, target.Id, target.Status);

        return await ToViewAsync(target, tab.Status, tab.Admission, cancellationToken);
    }

    // ---------------------------------------------------------------- settlement mode

    public async Task<TabView> SetSettlementModeAsync(
        Guid tabId,
        Guid actingParticipantId,
        SettlementMode settlementMode,
        CancellationToken cancellationToken = default)
    {
        var tab = await LoadTabAsync(tabId, cancellationToken);
        var host = RequireHost(tab, actingParticipantId, "Changing the settlement mode");

        // The lock is stamped the first time a change is attempted after money has landed. A
        // reserved payment counts: the hold was computed under the current split, and changing
        // the split underneath it is exactly what the lock exists to stop.
        if (tab.SettlementModeLockedAtUtc is null
            && await db.Payments.AnyAsync(
                p => p.TabId == tab.Id
                     && (p.Status == PaymentStatus.Reserved || p.Status == PaymentStatus.Succeeded),
                cancellationToken))
        {
            tab.LockSettlementMode(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Throws DomainStateException when locked.
        tab.SetSettlementMode(settlementMode);

        ledger.Append(tab.Id, TabEventType.SettlementModeChanged, new
        {
            settlementMode = (int)settlementMode,
            lockedAtUtc = tab.SettlementModeLockedAtUtc,
        });

        await ledger.SaveAppendedAsync(cancellationToken);

        return await query.GetForParticipantAsync(tab.Id, host.Id, cancellationToken)
               ?? throw new InvalidOperationException($"Tab {tab.Id} vanished while its settlement mode was being set.");
    }

    // ---------------------------------------------------------------- staff

    public async Task<TabStaffView> OpenForStaffAsync(
        OpenTabForStaffCommand command,
        CancellationToken cancellationToken = default)
    {
        RequireStaff("Open tab");
        RequireOpening(command.ClientCommandId, partySize: null);

        if (await FindStaffReplayAsync(command, cancellationToken) is { } replay)
        {
            return replay;
        }

        for (var attempt = 1; attempt <= MaxOpenAttempts; attempt++)
        {
            // The branch is the route's, which the policy has already matched to the token: a table
            // at another branch is simply not found.
            var table = await db.DiningTables
                .Include(t => t.Branch)
                    .ThenInclude(b => b.Venue)
                .FirstOrDefaultAsync(
                    t => t.Id == command.TableId && t.BranchId == command.BranchId && t.IsActive,
                    cancellationToken)
                ?? throw new KeyNotFoundException("No table in service with that id at this branch.");

            VenueGate.RequireOpenForBusiness(table.Branch);
            RequirePaid(table.Branch);

            if (table.Status == TableStatus.OutOfService)
            {
                throw new DomainStateException($"Table {table.Label} is out of service.");
            }

            var session = await db.TableSessions
                .FirstOrDefaultAsync(s => s.DiningTableId == table.Id && s.ClosedAtUtc == null, cancellationToken);

            if (session is null)
            {
                if (table.Status != TableStatus.Occupied)
                {
                    throw new TableNotSeatedException(table.Id, table.Label, table.Status);
                }

                // Occupied with no sitting: the party paid in full - which closes the sitting but
                // not the table - and is still there, now wanting something more. Seating them
                // again is refused as Occupied to Occupied, so the new bill gets a sitting of its
                // own here, for the same party, and the paid one stays closed.
                session = await ReopenSittingAsync(table, cancellationToken);

                if (session is null)
                {
                    // Somebody else gave the table a sitting in the same instant. Look again.
                    db.ChangeTracker.Clear();
                    continue;
                }
            }

            // Somebody scanned first, or this is the second tap: that tab, not a second one.
            if (await LoadTabBySessionAsync(session.Id, cancellationToken) is { } existing)
            {
                return await StaffViewAsync(existing.Id, cancellationToken);
            }

            var tab = new Tab(
                table.BranchId,
                table.Id,
                session.Id,
                clock.UtcNow,
                table.Branch.ReservationPolicy.ServiceChargePercent,
                TabAdmissionReason.OpenedByStaff,
                clientCommandId: command.ClientCommandId,
                admittedByStaffId: actor.StaffMemberId);

            session.AttachTab(tab.Id);
            db.Tabs.Add(tab);

            // Whoever opens the tab on a table nobody serves yet serves it (docs/activity.md).
            if (actor.StaffMemberId is { } openerId)
            {
                session.ClaimServerIfNone(openerId, tab.OpenedAtUtc);
            }

            await activity.RecordAsync(
                new ActivityRecord(ActivityCategory.Table, ActivityActions.TableTabOpened, ActivitySubjects.Tab, tab.Id)
                {
                    BranchId = table.BranchId,
                    VenueId = table.Branch.VenueId,
                    TableSessionId = session.Id,
                    TableLabel = table.Label,
                    Summary = new { tableId = table.Id, partySize = session.PartySize },
                },
                cancellationToken);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TabPerSession))
            {
                // A phone at the table opened one in the same instant. Answer with theirs.
                db.ChangeTracker.Clear();
                continue;
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TabClientCommand))
            {
                db.ChangeTracker.Clear();

                return await FindStaffReplayAsync(command, cancellationToken)
                       ?? throw new ClientCommandIdAlreadyUsedException(command.ClientCommandId);
            }

            logger.LogInformation(
                "Staff {StaffId} opened tab {TabId} on table {TableId} with nobody on it.",
                actor.StaffMemberId, tab.Id, table.Id);

            return await StaffViewAsync(tab.Id, cancellationToken);
        }

        throw new DomainStateException("This table is being opened from several places at once. Try again in a second.");
    }

    /// <summary>
    /// A new sitting on a table that is still Occupied after its last bill was paid in full, for
    /// the party that never left. Null when another sitting won the table's one open slot first.
    /// </summary>
    /// <remarks>
    /// Not a state-machine transition: the table's physical status does not change, exactly as it
    /// did not when the payment closed the previous sitting. The party size is carried over from
    /// that sitting, because it is the same people.
    /// </remarks>
    private async Task<TableSession?> ReopenSittingAsync(DiningTable table, CancellationToken cancellationToken)
    {
        var previous = await db.TableSessions
            .AsNoTracking()
            .Where(s => s.DiningTableId == table.Id)
            .OrderByDescending(s => s.SeatedAtUtc)
            .Select(s => (int?)s.PartySize)
            .FirstOrDefaultAsync(cancellationToken);

        var session = TableSession.SeatWalkIn(
            table.BranchId, table.Id, previous ?? 1, clock.UtcNow, actor.StaffMemberId);

        db.TableSessions.Add(session);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.OpenSessionPerTable))
        {
            return null;
        }

        logger.LogInformation(
            "Staff {StaffId} gave occupied table {TableId} a new sitting {SessionId} after its bill was paid.",
            actor.StaffMemberId, table.Id, session.Id);

        return session;
    }

    /// <summary>
    /// The same staff tap arriving twice. A command id that opened a tab at another table - or that a
    /// phone used for its scan - is a client bug, and the honest answer is that the id is taken.
    /// </summary>
    private async Task<TabStaffView?> FindStaffReplayAsync(
        OpenTabForStaffCommand command,
        CancellationToken cancellationToken)
    {
        var tab = await db.Tabs
            .AsNoTracking()
            .Where(t => t.ClientCommandId == command.ClientCommandId)
            .Select(t => new { t.Id, t.DiningTableId, t.BranchId })
            .FirstOrDefaultAsync(cancellationToken);

        if (tab is null)
        {
            return null;
        }

        if (tab.DiningTableId != command.TableId || tab.BranchId != command.BranchId)
        {
            throw new ClientCommandIdAlreadyUsedException(command.ClientCommandId);
        }

        return await StaffViewAsync(tab.Id, cancellationToken);
    }

    public async Task<TabStaffView> ReassignHostAsync(
        Guid tabId,
        Guid newHostParticipantId,
        CancellationToken cancellationToken = default)
    {
        RequireStaff("Reassign host");

        var tab = await LoadTabAsync(tabId, cancellationToken);

        var newHost = tab.Participants.FirstOrDefault(p => p.Id == newHostParticipantId)
                      ?? throw new KeyNotFoundException("No such participant on that tab.");

        var oldHost = tab.Participants.FirstOrDefault(p => p.Id == tab.HostParticipantId);

        // Order matters: the tab refuses a no-op and a closed tab before either row is touched,
        // and the new host must be approved before they can take the role.
        tab.ReassignHost(newHost.Id);
        newHost.BecomeHost();
        oldHost?.BecomeGuest();

        ledger.Append(tab.Id, TabEventType.HostReassigned, new
        {
            fromParticipantId = oldHost?.Id,
            toParticipantId = newHost.Id,
        });

        await LogTabAsync(
            tab,
            ActivityActions.TableHostReassigned,
            new { fromName = oldHost?.DisplayName, toName = newHost.DisplayName },
            cancellationToken);

        await ledger.SaveAppendedAsync(cancellationToken);

        logger.LogInformation(
            "Staff {StaffId} moved the host of tab {TabId} from {OldHost} to {NewHost}.",
            actor.StaffMemberId, tab.Id, oldHost?.Id, newHost.Id);

        return await StaffViewAsync(tab.Id, cancellationToken);
    }

    public async Task<TabStaffView> BeginClosingAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        RequireStaff("Close tab");

        var tab = await LoadTabAsync(tabId, cancellationToken, includeJoinTokens: true);
        var nowUtc = clock.UtcNow;

        tab.BeginClosing();

        // Every participant on this tab may now read it but not change it, so the cached answers
        // about all of them are wrong. Dropped together rather than one at a time.
        authority.InvalidateTab(tab.Id);

        // No new participants means no live invitation either.
        foreach (var live in tab.JoinTokens.Where(t => t.IsUsableAt(nowUtc)))
        {
            live.Revoke(nowUtc);
        }

        ledger.Append(tab.Id, TabEventType.TabClosing, new { atUtc = nowUtc });

        await LogTabAsync(
            tab, ActivityActions.TableBillPresented, new { totalAmd = tab.TotalAmd, remainingAmd = tab.RemainingAmd }, cancellationToken);

        await ledger.SaveAppendedAsync(cancellationToken);

        return await StaffViewAsync(tab.Id, cancellationToken);
    }

    // ---------------------------------------------------------------- admission

    /// <summary>What admitting a tab is recorded as in <c>ProcessedCommands</c>.</summary>
    internal const string AdmitCommandType = "tab.admit";

    /// <summary>What turning a tab away is recorded as in <c>ProcessedCommands</c>.</summary>
    internal const string RejectCommandType = "tab.reject";

    public async Task<TabStaffView> AdmitAsync(AdmitTabCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            return await UnderTabAdmissionLockAsync(command.TabId, () => AdmitCoreAsync(command, cancellationToken), cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (await AfterLostAdmissionRaceAsync(command.TabId, TabAdmission.Admitted, cancellationToken) is { } answer)
            {
                return answer;
            }

            throw;
        }
    }

    private async Task<TabStaffView> AdmitCoreAsync(AdmitTabCommand command, CancellationToken cancellationToken)
    {

        var staffId = RequireStaffId("Let a table order");
        RequireOpening(command.ClientCommandId, partySize: null);

        var tab = await LoadTabAsync(command.TabId, cancellationToken);

        // The stored row's branch, not only the token's: a waiter moved to another branch is refused at
        // once, as for acknowledging a call.
        await branchGuard.RequireAsync(staffId, tab.BranchId, "Letting this table order", "tab", cancellationToken);

        if (await IsAdmissionReplayAsync(command.ClientCommandId, AdmitCommandType, tab.Id, cancellationToken))
        {
            return await StaffViewAsync(tab.Id, cancellationToken);
        }

        var nowUtc = clock.UtcNow;

        // Throws TabAdmissionDecidedException for a tab already turned away. An admitted tab is answered
        // as it is - two waiters tapping the same card is the normal case - and writes no second event.
        if (AdmitInContext(tab, TabAdmissionReason.Waiter, staffId, nowUtc))
        {
            logger.LogInformation("Staff {StaffId} let tab {TabId} order.", staffId, tab.Id);

            // The waiter who came over and let the table in serves it, when nobody did yet.
            var sitting = await db.TableSessions.FirstOrDefaultAsync(s => s.Id == tab.TableSessionId, cancellationToken);
            sitting?.ClaimServerIfNone(staffId, nowUtc);

            await LogTabAsync(tab, ActivityActions.TableTabAdmitted, null, cancellationToken);
        }

        RecordAdmissionCommand(command.ClientCommandId, AdmitCommandType, tab.Id, staffId, nowUtc);

        await SaveAdmissionAsync(command.ClientCommandId, AdmitCommandType, tab.Id, cancellationToken);

        return await StaffViewAsync(tab.Id, cancellationToken);
    }

    public async Task<TabStaffView> RejectAdmissionAsync(RejectTabCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var view = await UnderTabAdmissionLockAsync(
                command.TabId, () => RejectAdmissionCoreAsync(command, cancellationToken), cancellationToken);

            // Everybody on it may still read the receipt-grace view, and nobody may change anything.
            authority.InvalidateTab(command.TabId);

            return view;
        }
        catch (DbUpdateConcurrencyException)
        {
            if (await AfterLostAdmissionRaceAsync(command.TabId, TabAdmission.Rejected, cancellationToken) is { } answer)
            {
                return answer;
            }

            throw;
        }
    }

    /// <summary>
    /// Runs one admission decision with the tab's admission lock held: Admit and Reject on one card, or one
    /// tap twice, queue here, and the second reads what the first decided (409 <c>tab-admission-decided</c>,
    /// or the same answer) instead of both writing - which collided on the tab's event numbering inside the
    /// reject's transaction and answered 500 (review).
    /// </summary>
    /// <remarks>
    /// Owned by the session, not a transaction, so each save inside keeps its own all-or-nothing
    /// transaction (and the ledger's renumbering retry stays safe against a phone writing to the same
    /// stream). The connection is held open for the duration and the lock released explicitly.
    /// </remarks>
    private async Task<T> UnderTabAdmissionLockAsync<T>(Guid tabId, Func<Task<T>> decide, CancellationToken cancellationToken)
    {
        const int timeoutMs = 10_000;
        var resource = "tab-admission:" + tabId.ToString("N");

        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var granted = await db.Database
                .SqlQuery<int>(
                    $"""
                     DECLARE @result int;
                     EXEC @result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive',
                          @LockOwner = 'Session', @LockTimeout = {timeoutMs};
                     SELECT @result AS [Value];
                     """)
                .ToListAsync(cancellationToken);

            // 0 granted, 1 granted after waiting; negative is a timeout, a deadlock victim or a refusal.
            if (granted is not [>= 0])
            {
                var table = await db.Tabs
                    .AsNoTracking()
                    .Where(t => t.Id == tabId)
                    .Select(t => new { t.DiningTableId, t.DiningTable.Label })
                    .FirstOrDefaultAsync(cancellationToken);

                throw new TableLockTimeoutException(table?.DiningTableId ?? Guid.Empty, table?.Label ?? string.Empty, timeoutMs);
            }

            try
            {
                return await decide();
            }
            finally
            {
                await db.Database.ExecuteSqlAsync(
                    $"EXEC sp_releaseapplock @Resource = {resource}, @LockOwner = 'Session';",
                    CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Another waiter's tap on the same waiting tab saved first, and this one lost on the tab's row version.
    /// Answers as the tab now stands: decided the other way is <c>tab-admission-decided</c> (409), as it would
    /// have been a moment later; decided the same way is answered as done, as a second tap on a decided card
    /// is. Null when the tab still waits - something else changed the row - and the conflict stands.
    /// </summary>
    private async Task<TabStaffView?> AfterLostAdmissionRaceAsync(
        Guid tabId,
        TabAdmission wanted,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        ledger.DiscardPending();

        var now = await db.Tabs
            .AsNoTracking()
            .Where(t => t.Id == tabId)
            .Select(t => t.Admission)
            .FirstAsync(cancellationToken);

        if (now == TabAdmission.AwaitingAdmission)
        {
            return null;
        }

        if (now != wanted)
        {
            throw new TabAdmissionDecidedException(tabId, now);
        }

        return await StaffViewAsync(tabId, cancellationToken);
    }

    private async Task<TabStaffView> RejectAdmissionCoreAsync(RejectTabCommand command, CancellationToken cancellationToken)
    {

        var staffId = RequireStaffId("Turn a table away");
        RequireOpening(command.ClientCommandId, partySize: null);

        var reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();

        if (reason is { Length: > FieldLengths.Reason })
        {
            throw new ArgumentException($"A reason is at most {FieldLengths.Reason} characters.", "reason");
        }
        var tab = await LoadTabAsync(command.TabId, cancellationToken, includeJoinTokens: true);

        await branchGuard.RequireAsync(staffId, tab.BranchId, "Turning this table away", "tab", cancellationToken);

        if (await IsAdmissionReplayAsync(command.ClientCommandId, RejectCommandType, tab.Id, cancellationToken))
        {
            return await StaffViewAsync(tab.Id, cancellationToken);
        }

        var nowUtc = clock.UtcNow;

        // A waiter keyed orders on it: somebody is there. Turning it away would close a tab with the
        // kitchen's work on it; the waiter moves the host or removes the phone instead, then admits.
        if (tab.Admission == TabAdmission.AwaitingAdmission
            && await db.TabOrders.AnyAsync(o => o.TabId == tab.Id, cancellationToken))
        {
            throw new DomainStateException(
                "A waiter has ordered on this tab, so it cannot be turned away. Move the host or remove the phone, then let it order.");
        }

        // Throws TabAdmissionDecidedException for a tab already admitted. One already turned away is
        // answered as it is.
        if (!tab.RejectAdmission(nowUtc, staffId))
        {
            RecordAdmissionCommand(command.ClientCommandId, RejectCommandType, tab.Id, staffId, nowUtc);
            await SaveAdmissionAsync(command.ClientCommandId, RejectCommandType, tab.Id, cancellationToken);

            return await StaffViewAsync(tab.Id, cancellationToken);
        }

        var session = await db.TableSessions.FirstAsync(s => s.Id == tab.TableSessionId, cancellationToken);

        // Did the scan seat the table? A walk-in sitting with no waiter and no booking behind it is one
        // only a phone opened - the phantom this whole step exists for - and turning it away gives the
        // table back. A party a waiter or a booking seated is really there: only the tab goes, and they
        // can scan again.
        var seatedByScan = session.IsOpen
                           && session.Source == TableSessionSource.WalkIn
                           && session.SeatedByStaffId is null
                           && session.ReservationId is null;

        // What the scan took the table from. A table held for a late booked party goes back to Held, not to
        // Free: the hold is exactly the window a phantom could use to take a table from a party on its way.
        var seatedFrom = seatedByScan
            ? await db.TableStateChanges
                .AsNoTracking()
                .Where(c => c.TableSessionId == session.Id && c.ToStatus == TableStatus.Occupied && c.Signal == null)
                .OrderBy(c => c.Sequence)
                .Select(c => (TableStatus?)c.FromStatus)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        // Nobody new onto a tab that is gone.
        foreach (var live in tab.JoinTokens.Where(t => t.IsUsableAt(nowUtc)))
        {
            live.Revoke(nowUtc);
        }

        // Its open calls are answered by the waiter who came over and found nobody - acknowledged, so the
        // rail drops them, by the same person, at the same moment.
        var calls = await db.ServiceRequests
            .Where(r => r.TabId == tab.Id && r.AcknowledgedAtUtc == null && r.SupersededAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var call in calls)
        {
            call.Acknowledge(nowUtc, staffId);
        }

        ledger.Append(tab.Id, TabEventType.TabAdmissionRejected, new
        {
            byStaffId = staffId,
            atUtc = nowUtc,
            tableFreed = seatedByScan,
        });

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Table, ActivityActions.TableTabTurnedAway, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                VenueId = tab.Branch.VenueId,
                TableSessionId = tab.TableSessionId,
                TableLabel = tab.DiningTable.Label,
                Reason = reason,
                Summary = new { tableFreed = seatedByScan },
            },
            cancellationToken);

        RecordAdmissionCommand(command.ClientCommandId, RejectCommandType, tab.Id, staffId, nowUtc);

        if (!seatedByScan)
        {
            session.DetachRejectedTab(tab.Id);

            SignalTable(tab, TableChangeSignal.TabAdmissionRejected, reason ?? "scan turned away", staffId, nowUtc);

            await SaveAdmissionAsync(command.ClientCommandId, RejectCommandType, tab.Id, cancellationToken);
        }
        else
        {
            // The rejection and the table going free commit together: the free is the state machine's
            // own transition (its audit row, its row-version check against a waiter acting on the same
            // table), run inside this transaction after the tab's rows are saved and numbered.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (!await SaveAdmissionAsync(command.ClientCommandId, RejectCommandType, tab.Id, cancellationToken))
            {
                // The same tap, racing itself: the other one is freeing the table.
                return await StaffViewAsync(tab.Id, cancellationToken);
            }

            var freeCommandId = Guid.CreateVersion7();

            await tableState.FreeTableAsync(
                new TableStateCommand(tab.BranchId, tab.DiningTableId, freeCommandId, reason ?? "scan turned away"),
                cancellationToken);

            var freed = await db.TableStateChanges.FirstAsync(c => c.ClientCommandId == freeCommandId, cancellationToken);
            freed.WithSignal(TableChangeSignal.TabAdmissionRejected, tab.Id);

            await db.SaveChangesAsync(cancellationToken);

            if (seatedFrom == TableStatus.Held)
            {
                await tableState.HoldForLatePartyAsync(
                    new TableStateCommand(
                        tab.BranchId, tab.DiningTableId, Guid.CreateVersion7(), "hold restored: the scan that took it was turned away"),
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Staff {StaffId} turned tab {TabId} away; table {TableId} {Freed}.",
            staffId, tab.Id, tab.DiningTableId, seatedByScan ? "freed" : "left seated");

        return await StaffViewAsync(tab.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<TabAwaitingAdmissionView>> ListAwaitingAdmissionAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaffId("Read the tables waiting to be let in");

        await branchGuard.RequireAsync(staffId, branchId, "Reading this branch's waiting tables", "branch", cancellationToken);

        // One statement, served by the filtered index on the waiting tabs: a handful per branch at most.
        var rows = await db.Tabs
            .AsNoTracking()
            .Where(t => t.BranchId == branchId
                        && t.Admission == TabAdmission.AwaitingAdmission
                        && t.Status == TabStatus.Open)
            .OrderBy(t => t.OpenedAtUtc)
            .ThenBy(t => t.Id)
            .Take(200)
            .Select(t => new TabAwaitingAdmissionView(
                t.Id,
                t.DiningTableId,
                t.DiningTable.Label,
                t.DiningTable.FloorArea == null ? null : t.DiningTable.FloorArea.Name,
                t.OpenedAtUtc,
                t.Participants.Where(p => p.Id == t.HostParticipantId).Select(p => p.DisplayName).FirstOrDefault(),
                t.Participants.Count(p => p.Status != ParticipantStatus.Removed),
                t.TableSession.ClosedAtUtc == null
                && t.TableSession.Source == TableSessionSource.WalkIn
                && t.TableSession.SeatedByStaffId == null
                && t.TableSession.ReservationId == null))
            .ToListAsync(cancellationToken);

        return rows;
    }

    /// <summary>
    /// Admits a tab that was waiting, in the caller's unit of work: the tab, its stream's
    /// <c>TabAdmitted</c>, and the branch stream's signal. False, staging nothing, when it already was.
    /// </summary>
    /// <exception cref="TabAdmissionDecidedException">A waiter already turned it away.</exception>
    /// <summary>Something staff did to a tab, for the activity log, in the caller's unit of work.</summary>
    private Task LogTabAsync(Tab tab, string action, object? summary, CancellationToken cancellationToken) =>
        activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Table, action, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                VenueId = tab.Branch.VenueId,
                TableSessionId = tab.TableSessionId,
                TableLabel = tab.DiningTable.Label,
                Summary = summary,
            },
            cancellationToken);

    private bool AdmitInContext(Tab tab, TabAdmissionReason because, Guid? staffId, DateTime nowUtc) =>
        TabAdmissions.Admit(db, ledger, tab, because, staffId, SignalActor(staffId), nowUtc);

    /// <summary>
    /// Puts a signal about the table's tab on the branch change stream - a same-status row, as the
    /// hold-extended row is - in the caller's unit of work, so every tablet following the stream moves.
    /// </summary>
    private void SignalTable(Tab tab, TableChangeSignal signal, string reason, Guid? staffId, DateTime nowUtc) =>
        TabAdmissions.Signal(db, tab, signal, reason, SignalActor(staffId), nowUtc);

    /// <summary>Who a branch-stream row names: the waiter, else the diner account on the request, else the system.</summary>
    private (ActorType Type, Guid? Id) SignalActor(Guid? staffId) =>
        staffId is { } staff
            ? (ActorType.Staff, staff)
            : actor.Type == ActorType.Diner && actor.DinerUserId is { } diner
                ? (ActorType.Diner, diner)
                : (ActorType.System, null);

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

    /// <summary>
    /// Whether this admit or reject was already applied - or throws when the id belongs to another command,
    /// or to the same command on another tab.
    /// </summary>
    private async Task<bool> IsAdmissionReplayAsync(
        Guid clientCommandId,
        string commandType,
        Guid tabId,
        CancellationToken cancellationToken)
    {
        var processed = await db.ProcessedCommands
            .AsNoTracking()
            .Where(c => c.ClientCommandId == clientCommandId)
            .Select(c => new { c.CommandType, c.ResponseJson })
            .FirstOrDefaultAsync(cancellationToken);

        if (processed is null)
        {
            return false;
        }

        if (processed.CommandType != commandType || !processed.ResponseJson.Contains(tabId.ToString(), StringComparison.Ordinal))
        {
            throw new ClientCommandIdAlreadyUsedException(clientCommandId);
        }

        return true;
    }

    /// <summary>Records the admission command in the caller's unit of work: which tab, nothing more.</summary>
    private void RecordAdmissionCommand(Guid clientCommandId, string commandType, Guid tabId, Guid staffId, DateTime nowUtc) =>
        db.ProcessedCommands.Add(new Domain.Audit.ProcessedCommand(
            clientCommandId,
            commandType,
            ActorType.Staff,
            staffId,
            System.Text.Json.JsonSerializer.Serialize(new { tabId }),
            StatusCodes200,
            nowUtc));

    private const int StatusCodes200 = 200;

    /// <summary>
    /// Saves the admission's rows, numbering its events. Two taps of one command racing each other: the
    /// loser finds the winner's record and answers as a replay; another command's id is refused.
    /// </summary>
    /// <returns>True when this request's rows were written; false when it was answered as a replay.</returns>
    private async Task<bool> SaveAdmissionAsync(
        Guid clientCommandId,
        string commandType,
        Guid tabId,
        CancellationToken cancellationToken)
    {
        try
        {
            await ledger.SaveAppendedAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.ProcessedCommandId))
        {
            db.ChangeTracker.Clear();
            ledger.DiscardPending();

            if (!await IsAdmissionReplayAsync(clientCommandId, commandType, tabId, cancellationToken))
            {
                throw;
            }

            return false;
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

        return await ResultAsync(
            tab,
            mine,
            seatedTheTable ? TabOpenOutcome.OpenedNewSession : TabOpenOutcome.OpenedOnExistingSession,
            wasReplay: true,
            cancellationToken);
    }

    // ---------------------------------------------------------------- permissions

    /// <summary>
    /// The acting participant must be on the tab and be its host. Decided here, not in an
    /// endpoint, so it holds for every caller.
    /// </summary>
    private static TabParticipant RequireHost(Tab tab, Guid actingParticipantId, string operation)
    {
        var acting = tab.Participants.FirstOrDefault(p => p.Id == actingParticipantId && !p.IsRemoved)
                     ?? throw new TabPermissionException(operation, "a participant on this tab");

        if (!acting.IsHost || tab.HostParticipantId != acting.Id)
        {
            throw new TabPermissionException(operation, "the host of this tab");
        }

        return acting;
    }

    private Guid RequireStaffId(string operation)
    {
        RequireStaff(operation);

        return actor.StaffMemberId!.Value;
    }

    private void RequireStaff(string operation)
    {
        if (actor.Type != ActorType.Staff || actor.StaffMemberId is null)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }
    }

    // ---------------------------------------------------------------- loading and shaping

    private async Task<Tab> LoadTabAsync(Guid tabId, CancellationToken cancellationToken, bool includeJoinTokens = false)
    {
        IQueryable<Tab> tabs = db.Tabs
            .Include(t => t.Participants)
            .Include(t => t.DiningTable)
            .Include(t => t.Branch)
                .ThenInclude(b => b.Venue);

        if (includeJoinTokens)
        {
            tabs = tabs.Include(t => t.JoinTokens);
        }

        var tab = await tabs.FirstOrDefaultAsync(t => t.Id == tabId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Tab {tabId} was not found.");

        RequirePaid(tab.Branch);

        return tab;
    }

    /// <summary>
    /// Tabs and ordering are a paid feature, per branch. Checked in the service so it holds for
    /// every caller; the answer is "not enabled for this branch", not a 403 - the caller is
    /// allowed to be here, the branch has not paid for what they asked.
    /// </summary>
    private static void RequirePaid(Branch branch)
    {
        if (!branch.IsPaid)
        {
            throw new FeatureNotEnabledException("Tabs and ordering", branch.Id, branch.SubscriptionTier);
        }
    }

    /// <summary>
    /// The sitting's tab - never one a waiter turned away, which gave the sitting's tab slot back so the
    /// party really there can open their own.
    /// </summary>
    private Task<Tab?> LoadTabBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        db.Tabs
            .Include(t => t.Participants)
            .FirstOrDefaultAsync(
                t => t.TableSessionId == sessionId && t.Admission != TabAdmission.Rejected, cancellationToken);

    private async Task<TabAccessResult> ResultAsync(
        Tab tab,
        TabParticipant participant,
        TabOpenOutcome outcome,
        bool wasReplay,
        CancellationToken cancellationToken)
    {
        var token = tokens.Issue(tab, participant);

        var view = await query.GetForParticipantAsync(tab.Id, participant.Id, cancellationToken)
                   ?? throw new InvalidOperationException(
                       $"Participant {participant.Id} was just written to tab {tab.Id} but cannot be read back.");

        return new TabAccessResult(token, view, outcome, wasReplay);
    }

    private async Task<TabStaffView> StaffViewAsync(Guid tabId, CancellationToken cancellationToken) =>
        await query.GetForStaffAsync(tabId, cancellationToken)
        ?? throw new InvalidOperationException($"Tab {tabId} was just written but cannot be read back.");

    /// <summary>
    /// One participant as the command that just changed them answers, picture included.
    /// </summary>
    /// <remarks>
    /// The same face the tab read shows - the profile photo of a live diner account the phone
    /// scanned with - so a client that updates its roster from this answer does not blank an avatar
    /// the next read would draw. One indexed read, on a path a host taps a few times an evening.
    /// </remarks>
    private async Task<TabParticipantView> ToViewAsync(
        TabParticipant participant,
        TabStatus tabStatus,
        TabAdmission admission,
        CancellationToken cancellationToken)
    {
        var photo = participant.UserId is { } userId
            ? await db.DinerUsers
                .AsNoTracking()
                .Where(d => d.Id == userId && d.DeletedAtUtc == null && d.PhotoId != null)
                .Select(d => d.Photo!)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return TabProjection.ToParticipantView(
            new TabParticipantSnapshot(
                participant.Id,
                participant.DisplayName,
                participant.Role,
                participant.Status,
                participant.CanOrder,
                participant.CanSeeTableTotal,
                participant.CanPay,
                participant.JoinedAtUtc,
                photo is null ? null : PhotoView.From(photo)),
            tabStatus,
            admission);
    }

    private static string RequireDevice(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId)
            ? throw new ArgumentException("A device identifier is required.", nameof(deviceId))
            : deviceId.Trim();

    private static string NameOrDefault(string? displayName, int ordinal) =>
        string.IsNullOrWhiteSpace(displayName) ? $"Guest {ordinal}" : displayName.Trim();
}
