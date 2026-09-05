using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Application.Tabs;
using Yalla.Domain;
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
    TabParticipantTokens tokens,
    IOptions<TabOptions> options,
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
        var qrToken = (command.QrToken ?? string.Empty).Trim();

        if (command.ClientCommandId == Guid.Empty)
        {
            throw new ArgumentException("A clientCommandId is required.", nameof(command));
        }

        if (command.PartySize is { } size && size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), size, "Party size must be greater than zero.");
        }

        if (await FindReplayAsync(command.ClientCommandId, device, cancellationToken) is { } replay)
        {
            return replay;
        }

        for (var attempt = 1; attempt <= MaxOpenAttempts; attempt++)
        {
            var table = await db.DiningTables
                .Include(t => t.Branch)
                .FirstOrDefaultAsync(t => t.QrToken == qrToken && t.IsActive, cancellationToken)
                ?? throw new KeyNotFoundException("That QR code does not belong to a table in service.");

            if (table.Status == TableStatus.OutOfService)
            {
                throw new DomainStateException(
                    $"Table {table.Label} is out of service. Ask a member of staff for another table.");
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
                    seated = await tableState.SeatQrScanAsync(
                        new SeatQrScanCommand(table.BranchId, table.Id, command.PartySize ?? 1, command.ClientCommandId),
                        cancellationToken);
                }
                catch (TableStateConflictException ex)
                {
                    // Somebody else seated this table between our read and our write - almost
                    // always another phone at the same table. Their session is now the open one.
                    // The change tracker still holds our failed attempt, so clear it before
                    // looking again; on the next pass the table is occupied and we join.
                    logger.LogInformation(
                        "QR scan lost the seating race on table {TableId} ({TableLabel}); now {CurrentStatus}. Re-reading.",
                        ex.TableId, ex.TableLabel, ex.CurrentStatus);

                    db.ChangeTracker.Clear();
                    continue;
                }

                session = await db.TableSessions.FirstAsync(
                    s => s.Id == seated.TableSessionId!.Value, cancellationToken);

                outcome = TabOpenOutcome.OpenedNewSession;
            }

            // Case 2 - the session already has a tab. No second tab: the scanner joins it.
            var existingTab = await LoadTabBySessionAsync(session.Id, cancellationToken);

            if (existingTab is not null)
            {
                var joined = await JoinExistingAsync(existingTab, device, command.DisplayName, cancellationToken);

                return await ResultAsync(existingTab, joined, TabOpenOutcome.JoinedExistingTab, wasReplay: false, cancellationToken);
            }

            // Case 1 continued, or case 3 - a seated party with no tab yet. Open one on the
            // session with this scanner as host.
            var nowUtc = clock.UtcNow;

            var tab = new Tab(
                table.BranchId,
                table.Id,
                session.Id,
                nowUtc,
                table.Branch.ReservationPolicy.ServiceChargePercent,
                command.SettlementMode ?? SettlementMode.AnyonePaysAnyAmount,
                command.HideTotalFromGuests ?? false,
                command.ClientCommandId);

            var host = TabParticipant.Host(tab.Id, NameOrDefault(command.DisplayName, 1), device, nowUtc, actor.DinerUserId);

            tab.SetHostParticipant(host.Id);
            session.AttachTab(tab.Id);

            db.Tabs.Add(tab);
            db.TabParticipants.Add(host);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TabPerSession))
            {
                // Another phone attached a tab to this very session first. Drop ours and join theirs.
                logger.LogInformation(
                    "QR scan lost the tab race on session {SessionId}; joining the tab that won.", session.Id);

                db.ChangeTracker.Clear();
                continue;
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TabClientCommand))
            {
                // The same scan arrived twice at once and the other copy won. Answer from it.
                db.ChangeTracker.Clear();

                return await FindReplayAsync(command.ClientCommandId, device, cancellationToken)
                       ?? throw new InvalidOperationException(
                           $"Command {command.ClientCommandId} violated the tab idempotency index but no tab was found.");
            }

            logger.LogInformation(
                "Tab {TabId} opened on table {TableId} by device with no account; outcome {Outcome}.",
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
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        var existing = tab.Participants.FirstOrDefault(p => p.DeviceId == device && !p.IsRemoved);

        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                existing.SetDisplayName(displayName);
                await db.SaveChangesAsync(cancellationToken);
            }

            return existing;
        }

        if (!tab.AcceptsNewParticipants)
        {
            throw new DomainStateException(tab.Status == TabStatus.Closing
                ? "This tab is being settled and no longer takes new people. Ask a member of staff."
                : "That tab is closed. Scan the table's QR code to open a new one.");
        }

        var guest = TabParticipant.Guest(
            tab.Id,
            NameOrDefault(displayName, tab.Participants.Count + 1),
            device,
            nowUtc,
            tab.HideTotalFromGuests,
            actor.DinerUserId);

        db.TabParticipants.Add(guest);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Participant {ParticipantId} joined tab {TabId} pending the host's approval.", guest.Id, tab.Id);

        return guest;
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
            (participant, nowUtc) => participant.Approve(nowUtc), cancellationToken);

    public Task<TabParticipantView> RejectParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        ChangeParticipantAsync(
            tabId, actingParticipantId, participantId, "Rejecting a participant",
            (participant, nowUtc) => participant.Reject(nowUtc), cancellationToken);

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
            (participant, nowUtc) => participant.Remove(nowUtc), cancellationToken);

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
        await db.SaveChangesAsync(cancellationToken);

        return ToView(participant, tab.Status);
    }

    private async Task<TabParticipantView> ChangeParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        string operation,
        Action<TabParticipant, DateTime> change,
        CancellationToken cancellationToken)
    {
        var tab = await LoadTabAsync(tabId, cancellationToken);
        RequireHost(tab, actingParticipantId, operation);

        var target = tab.Participants.FirstOrDefault(p => p.Id == participantId)
                     ?? throw new KeyNotFoundException("No such participant on that tab.");

        change(target, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Operation} on tab {TabId}: participant {ParticipantId} is now {Status}.",
            operation, tab.Id, target.Id, target.Status);

        return ToView(target, tab.Status);
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
        await db.SaveChangesAsync(cancellationToken);

        return await query.GetForParticipantAsync(tab.Id, host.Id, cancellationToken)
               ?? throw new InvalidOperationException($"Tab {tab.Id} vanished while its settlement mode was being set.");
    }

    // ---------------------------------------------------------------- staff

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

        await db.SaveChangesAsync(cancellationToken);

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

        // No new participants means no live invitation either.
        foreach (var live in tab.JoinTokens.Where(t => t.IsUsableAt(nowUtc)))
        {
            live.Revoke(nowUtc);
        }

        await db.SaveChangesAsync(cancellationToken);

        return await StaffViewAsync(tab.Id, cancellationToken);
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
        IQueryable<Tab> tabs = db.Tabs.Include(t => t.Participants);

        if (includeJoinTokens)
        {
            tabs = tabs.Include(t => t.JoinTokens);
        }

        return await tabs.FirstOrDefaultAsync(t => t.Id == tabId, cancellationToken)
               ?? throw new KeyNotFoundException($"Tab {tabId} was not found.");
    }

    private Task<Tab?> LoadTabBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        db.Tabs
            .Include(t => t.Participants)
            .FirstOrDefaultAsync(t => t.TableSessionId == sessionId, cancellationToken);

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

    private static TabParticipantView ToView(TabParticipant participant, TabStatus tabStatus) =>
        TabProjection.ToParticipantView(
            new TabParticipantSnapshot(
                participant.Id,
                participant.DisplayName,
                participant.Role,
                participant.Status,
                participant.CanOrder,
                participant.CanSeeTableTotal,
                participant.CanPay,
                participant.JoinedAtUtc),
            tabStatus);

    private static string RequireDevice(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId)
            ? throw new ArgumentException("A device identifier is required.", nameof(deviceId))
            : deviceId.Trim();

    private static string NameOrDefault(string? displayName, int ordinal) =>
        string.IsNullOrWhiteSpace(displayName) ? $"Guest {ordinal}" : displayName.Trim();
}
