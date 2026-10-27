using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Reservations;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// What staff do to a tab from the tablet: open one on a seated table, move the host, and present the bill.
/// </summary>
internal sealed class TabStaffActions(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ITokenAuthorityCheck authority,
    TabLedger ledger,
    ILogger<TabService> logger,
    TabLookups lookups)
{
    // ---------------------------------------------------------------- staff

    public async Task<TabStaffView> OpenForStaffAsync(
        OpenTabForStaffCommand command,
        CancellationToken cancellationToken = default)
    {
        lookups.RequireStaff("Open tab");
        TabLookups.RequireOpening(command.ClientCommandId, partySize: null);

        if (await FindStaffReplayAsync(command, cancellationToken) is { } replay)
        {
            return replay;
        }

        for (var attempt = 1; attempt <= TabOpener.MaxOpenAttempts; attempt++)
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
            TabLookups.RequireTableService(table.Branch);

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
            if (await lookups.LoadTabBySessionAsync(session.Id, cancellationToken) is { } existing)
            {
                return await lookups.StaffViewAsync(existing.Id, cancellationToken);
            }

            // Raises TabOpenedByStaff, which writes the History's line.
            var tab = Tab.OpenByStaff(
                table.BranchId,
                table.Id,
                session.Id,
                clock.UtcNow,
                table.Branch.ReservationPolicy.ServiceChargePercent,
                command.ClientCommandId,
                actor.StaffMemberId);

            session.AttachTab(tab.Id);
            db.Tabs.Add(tab);

            // Whoever opens the tab on a table nobody serves yet serves it (docs/activity.md).
            if (actor.StaffMemberId is { } openerId)
            {
                session.ClaimServerIfNone(openerId, tab.OpenedAtUtc);
            }

            try
            {
                await ledger.SaveAppendedAsync(cancellationToken);
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

            return await lookups.StaffViewAsync(tab.Id, cancellationToken);
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

        return await lookups.StaffViewAsync(tab.Id, cancellationToken);
    }

    public async Task<TabStaffView> ReassignHostAsync(
        Guid tabId,
        Guid newHostParticipantId,
        CancellationToken cancellationToken = default)
    {
        lookups.RequireStaff("Reassign host");

        var tab = await lookups.LoadTabAsync(tabId, cancellationToken);

        var newHost = tab.Participants.FirstOrDefault(p => p.Id == newHostParticipantId)
                      ?? throw new KeyNotFoundException("No such participant on that tab.");

        var oldHost = tab.Participants.FirstOrDefault(p => p.Id == tab.HostParticipantId);

        // Refuses a closed tab, a no-op and a new host who was never let on before either row is
        // touched. Raises TabHostMoved: the stream and the History's line.
        tab.MoveHostTo(newHost, clock.UtcNow, hostLeft: false);

        await ledger.SaveAppendedAsync(cancellationToken);

        logger.LogInformation(
            "Staff {StaffId} moved the host of tab {TabId} from {OldHost} to {NewHost}.",
            actor.StaffMemberId, tab.Id, oldHost?.Id, newHost.Id);

        return await lookups.StaffViewAsync(tab.Id, cancellationToken);
    }

    public async Task<TabStaffView> BeginClosingAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        lookups.RequireStaff("Close tab");

        var tab = await lookups.LoadTabAsync(tabId, cancellationToken, includeJoinTokens: true);
        var nowUtc = clock.UtcNow;

        // Raises TabBillPresented: the stream's TabClosing and the History's line.
        tab.BeginClosing(nowUtc);

        // Every participant on this tab may now read it but not change it, so the cached answers
        // about all of them are wrong. Dropped together rather than one at a time.
        authority.InvalidateTab(tab.Id);

        // No new participants means no live invitation either.
        foreach (var live in tab.JoinTokens.Where(t => t.IsUsableAt(nowUtc)))
        {
            live.Revoke(nowUtc);
        }

        await ledger.SaveAppendedAsync(cancellationToken);

        return await lookups.StaffViewAsync(tab.Id, cancellationToken);
    }
}
