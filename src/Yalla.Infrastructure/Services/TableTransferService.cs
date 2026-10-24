using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Handing a table to another waiter. See <c>docs/activity.md</c> §transfer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The waiter lives on the sitting</b> (<c>TableSession.ServerStaffMemberId</c>), not the tab: a party is seated, and
/// served, before anybody opens a bill, and the sitting has no row version for ten phones' orders to contend on.
/// </para>
/// <para>
/// <b>From now on, not back to the start.</b> Orders and payments carry the waiter they were taken under, so the sale
/// and the tips of what came before stay with the earlier waiter; the transfer moves only what comes after.
/// </para>
/// <para>
/// In this order: staff (403); the tab (404); the caller at its branch (403); the caller the table's waiter or a manager
/// or above (403); the sitting still live (409 <c>tab-not-transferable</c>); the target able to take it (422
/// <c>transfer-target-ineligible</c>). Already theirs is answered as done and writes nothing. The move, its History entry
/// and a branch change-stream signal (<c>TableTransferred</c>) go in one save.
/// </para>
/// </remarks>
internal sealed class TableTransferService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    StaffBranchGuard branchGuard,
    IActivityLog activity,
    ILogger<TableTransferService> logger) : ITableTransferService
{
    private const string Operation = "Transfer a table";

    /// <summary>The roles that serve tables - who may take one. Never the kitchen.</summary>
    private static readonly StaffRole[] Serving = [StaffRole.Waiter, StaffRole.Manager, StaffRole.GeneralManager, StaffRole.Owner];

    public async Task<TransferTargetsView> ListTargetsAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        if (actor.Type != ActorType.Staff || actor.StaffMemberId is not { } callerId)
        {
            throw new StaffPermissionException(Operation, actor.Role, StaffRole.Waiter);
        }

        var tab = await db.Tabs
                      .AsNoTracking()
                      .Where(t => t.Id == tabId)
                      .Select(t => new { t.BranchId, t.TableSessionId, t.Branch.VenueId, t.Branch.TimeZoneId })
                      .FirstOrDefaultAsync(cancellationToken)
                  ?? throw new KeyNotFoundException($"Tab {tabId} was not found.");

        await branchGuard.RequireAsync(callerId, tab.BranchId, "Listing who can take this table", "tab", cancellationToken);

        var callerRole = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == callerId)
            .Select(s => s.Role)
            .FirstAsync(cancellationToken);

        var serverId = await db.TableSessions
            .AsNoTracking()
            .Where(s => s.Id == tab.TableSessionId)
            .Select(s => s.ServerStaffMemberId)
            .FirstOrDefaultAsync(cancellationToken);

        // Who may hand it over may see who could take it - the transfer's own rule, so nobody else learns the team.
        var manages = callerRole == StaffRole.PlatformAdmin || StaffRoleRules.ManagesAtVenue(callerRole);

        if (!manages && serverId != callerId)
        {
            throw new StaffPermissionException(
                "Listing who can take another waiter's table", callerRole, StaffRole.Manager);
        }

        // The POST's target rule exactly: of the venue, active, serving, at this branch or venue-wide.
        var people = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.VenueId == tab.VenueId
                        && s.IsActive
                        && Serving.Contains(s.Role)
                        && (s.BranchId == tab.BranchId
                            || (s.BranchId == null && (s.Role == StaffRole.Owner || s.Role == StaffRole.GeneralManager)))
                        && s.Id != serverId)
            .Select(s => new
            {
                s.Id,
                s.FullName,
                s.Role,
                OpenTables = db.TableSessions.Count(t =>
                    t.BranchId == tab.BranchId && t.ClosedAtUtc == null && t.ServerStaffMemberId == s.Id),
            })
            .ToListAsync(cancellationToken);

        var onShift = await ShiftInstants.OnShiftAsync(db, tab.BranchId, tab.TimeZoneId, clock.UtcNow, cancellationToken);

        return new TransferTargetsView(
        [
            .. people
                .Select(p => new TransferTarget(p.Id, p.FullName, p.Role, onShift.Contains(p.Id), p.OpenTables))
                .OrderByDescending(t => t.OnShiftNow)
                .ThenBy(t => t.Name, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(t => t.StaffMemberId),
        ]);
    }

    public async Task<TableTransferResult> TransferAsync(
        TransferTableCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (actor.Type != ActorType.Staff || actor.StaffMemberId is not { } callerId)
        {
            throw new StaffPermissionException(Operation, actor.Role, StaffRole.Waiter);
        }

        var tab = await db.Tabs
                      .Include(t => t.DiningTable)
                      .FirstOrDefaultAsync(t => t.Id == command.TabId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Tab {command.TabId} was not found.");

        await branchGuard.RequireAsync(callerId, tab.BranchId, "Transferring this table", "tab", cancellationToken);

        var caller = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == callerId)
            .Select(s => new { s.Role, s.FullName })
            .FirstAsync(cancellationToken);

        // The sitting has no row version, so two transfers at once - a manager and the waiter, two tablets - would both
        // read the same waiter, both write, and log two hand-overs "from" the same person, the later silently winning.
        // Its row is held to the end of this transaction and read inside the hold, so they go one after the other and
        // the second sees what the first did: its caller re-checked as that table's waiter, its "from" the real one.
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT Id FROM TableSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = {tab.TableSessionId}", cancellationToken);

        var session = await db.TableSessions.FirstAsync(s => s.Id == tab.TableSessionId, cancellationToken);

        // The waiter whose table it is, or somebody who manages here - the stored role, not the token's.
        var manages = caller.Role == StaffRole.PlatformAdmin || StaffRoleRules.ManagesAtVenue(caller.Role);

        if (!manages && session.ServerStaffMemberId != callerId)
        {
            throw new StaffPermissionException(
                "Transferring another waiter's table", caller.Role, StaffRole.Manager);
        }

        if (tab.Status is TabStatus.Closed or TabStatus.Abandoned || !session.IsOpen)
        {
            throw new TabNotTransferableException(tab.Id, tab.Status);
        }

        var venueId = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == tab.BranchId)
            .Select(b => b.VenueId)
            .FirstAsync(cancellationToken);

        var target = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == command.ToStaffMemberId && s.VenueId == venueId)
            .Select(s => new { s.Id, s.FullName, s.Role, s.BranchId, s.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (target is null)
        {
            throw new TransferTargetIneligibleException(
                command.ToStaffMemberId, TransferTargetIneligibleException.NotFound, "Nobody by that id works at this venue.");
        }

        if (!target.IsActive)
        {
            throw new TransferTargetIneligibleException(
                target.Id, TransferTargetIneligibleException.Inactive, $"{target.FullName} no longer works here.");
        }

        if (target.Role is not (StaffRole.Waiter or StaffRole.Manager or StaffRole.GeneralManager or StaffRole.Owner))
        {
            throw new TransferTargetIneligibleException(
                target.Id,
                TransferTargetIneligibleException.DoesNotServe,
                $"{target.FullName} does not serve tables. Hand it to a waiter or a manager.");
        }

        if (target.BranchId != tab.BranchId && !(target.BranchId is null && StaffRoleRules.CoversWholeVenue(target.Role)))
        {
            throw new TransferTargetIneligibleException(
                target.Id, TransferTargetIneligibleException.OtherBranch, $"{target.FullName} works at another branch.");
        }

        var nowUtc = clock.UtcNow;

        // Theirs already - a second tap, two tablets at once. Done, nothing written.
        if (session.ServerStaffMemberId == target.Id)
        {
            return new TableTransferResult(
                tab.Id, target.Id, target.FullName, null, null, session.ServerAssignedAtUtc ?? nowUtc, session.Id,
                tab.DiningTable.Label, WasReplay: true);
        }

        var fromId = session.TransferServer(target.Id, nowUtc);

        var fromName = fromId is { } from
            ? fromId == callerId
                ? caller.FullName
                : await db.StaffMembers
                    .AsNoTracking()
                    .Where(s => s.Id == from)
                    .Select(s => s.FullName)
                    .FirstOrDefaultAsync(cancellationToken)
            : null;

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Table, ActivityActions.TableTransferred, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                VenueId = venueId,
                TableSessionId = session.Id,
                TableLabel = tab.DiningTable.Label,
                Before = new { staffMemberId = fromId, name = fromName },
                After = new { staffMemberId = target.Id, name = target.FullName },
                Summary = new
                {
                    fromStaffMemberId = fromId,
                    fromName,
                    toStaffMemberId = target.Id,
                    toName = target.FullName,
                },
            },
            cancellationToken);

        // Every tablet following the branch's stream re-reads the floor and sees the new waiter.
        TabAdmissions.Signal(
            db, tab, TableChangeSignal.TableTransferred, $"handed to {target.FullName}", (ActorType.Staff, callerId), nowUtc);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Table {TableLabel} (tab {TabId}) handed from {From} to {To} by staff {CallerId}.",
            tab.DiningTable.Label, tab.Id, fromId, target.Id, callerId);

        return new TableTransferResult(
            tab.Id, target.Id, target.FullName, fromId, fromName, nowUtc, session.Id, tab.DiningTable.Label, WasReplay: false);
    }
}
