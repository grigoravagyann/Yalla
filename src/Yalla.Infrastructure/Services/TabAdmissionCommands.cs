using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Auth;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A waiter lets a waiting tab order, or turns it away - one decision per tab at a time - and the list of
/// tabs still waiting.
/// </summary>
internal sealed class TabAdmissionCommands(
    YallaDbContext db,
    IClock clock,
    ITableStateService tableState,
    ITokenAuthorityCheck authority,
    TabLedger ledger,
    StaffBranchGuard branchGuard,
    IActivityLog activity,
    ILogger<TabService> logger,
    TabLookups lookups,
    TabSignals signals)
{
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
        var staffId = lookups.RequireStaffId("Let a table order");
        TabLookups.RequireOpening(command.ClientCommandId, partySize: null);

        var tab = await lookups.LoadTabAsync(command.TabId, cancellationToken);

        // The stored row's branch, not only the token's: a waiter moved to another branch is refused at
        // once, as for acknowledging a call.
        await branchGuard.RequireAsync(staffId, tab.BranchId, "Letting this table order", "tab", cancellationToken);

        if (await IsAdmissionReplayAsync(command.ClientCommandId, AdmitCommandType, tab.Id, cancellationToken))
        {
            return await lookups.StaffViewAsync(tab.Id, cancellationToken);
        }

        var nowUtc = clock.UtcNow;

        // Throws TabAdmissionDecidedException for a tab already turned away. An admitted tab is answered
        // as it is - two waiters tapping the same card is the normal case - and writes no second event.
        if (signals.AdmitInContext(tab, TabAdmissionReason.Waiter, staffId, nowUtc))
        {
            logger.LogInformation("Staff {StaffId} let tab {TabId} order.", staffId, tab.Id);

            // The waiter who came over and let the table in serves it, when nobody did yet.
            var sitting = await db.TableSessions.FirstOrDefaultAsync(s => s.Id == tab.TableSessionId, cancellationToken);
            sitting?.ClaimServerIfNone(staffId, nowUtc);

            await signals.LogTabAsync(tab, ActivityActions.TableTabAdmitted, null, cancellationToken);
        }

        RecordAdmissionCommand(command.ClientCommandId, AdmitCommandType, tab.Id, staffId, nowUtc);

        await SaveAdmissionAsync(command.ClientCommandId, AdmitCommandType, tab.Id, cancellationToken);

        return await lookups.StaffViewAsync(tab.Id, cancellationToken);
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

        return await lookups.StaffViewAsync(tabId, cancellationToken);
    }

    private async Task<TabStaffView> RejectAdmissionCoreAsync(RejectTabCommand command, CancellationToken cancellationToken)
    {
        var staffId = lookups.RequireStaffId("Turn a table away");
        TabLookups.RequireOpening(command.ClientCommandId, partySize: null);

        var reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();

        if (reason is { Length: > FieldLengths.Reason })
        {
            throw new ArgumentException($"A reason is at most {FieldLengths.Reason} characters.", "reason");
        }

        var tab = await lookups.LoadTabAsync(command.TabId, cancellationToken, includeJoinTokens: true);

        await branchGuard.RequireAsync(staffId, tab.BranchId, "Turning this table away", "tab", cancellationToken);

        if (await IsAdmissionReplayAsync(command.ClientCommandId, RejectCommandType, tab.Id, cancellationToken))
        {
            return await lookups.StaffViewAsync(tab.Id, cancellationToken);
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

            return await lookups.StaffViewAsync(tab.Id, cancellationToken);
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

            signals.SignalTable(tab, TableChangeSignal.TabAdmissionRejected, reason ?? "scan turned away", staffId, nowUtc);

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
                return await lookups.StaffViewAsync(tab.Id, cancellationToken);
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

        return await lookups.StaffViewAsync(tab.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<TabAwaitingAdmissionView>> ListAwaitingAdmissionAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        var staffId = lookups.RequireStaffId("Read the tables waiting to be let in");

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
}
