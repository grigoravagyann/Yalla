using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reads one tab, whole, and hands it to <see cref="TabProjection"/> to decide what the viewer gets.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here filters by viewer. The query loads every participant, every line and the money;
/// the projection applies the flags. That split is deliberate - if the query also filtered, there
/// would be two places deciding what a guest may see, and the day they disagreed would be a leak.
/// </para>
/// <para>
/// It also does not check that the caller belongs to the tab: the <c>TabParticipant</c> policy
/// has already refused anyone who does not, and the projection returns null for a participant id
/// that is not on the tab, so the handler answers 404 rather than inventing a view.
/// </para>
/// </remarks>
internal sealed class TabQuery(YallaDbContext db) : ITabQuery
{
    public async Task<TabView?> GetForParticipantAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(tabId, cancellationToken);

        return snapshot is null ? null : TabProjection.Project(snapshot, participantId);
    }

    public async Task<TabStaffView?> GetForStaffAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(tabId, cancellationToken);

        return snapshot is null ? null : TabProjection.ProjectForStaff(snapshot);
    }

    /// <summary>
    /// Three reads on one connection: the tab and its table, the participants, the lines with
    /// their shares. Untracked throughout - this is a read model, and the service that may have
    /// just written these rows reads them back through here after its own SaveChanges.
    /// </summary>
    private async Task<TabSnapshot?> LoadSnapshotAsync(Guid tabId, CancellationToken cancellationToken)
    {
        var tab = await db.Tabs
            .AsNoTracking()
            .Where(t => t.Id == tabId)
            .Select(t => new
            {
                t.Id,
                t.BranchId,
                t.DiningTableId,
                TableLabel = t.DiningTable.Label,
                t.Status,
                t.SettlementMode,
                t.SettlementModeLockedAtUtc,
                t.HideTotalFromGuests,
                t.HostParticipantId,
                t.OpenedAtUtc,
                t.ClosedAtUtc,
                t.SubtotalAmd,
                t.ServiceChargeAmd,
                t.TotalAmd,
                t.PaidAmd,
                t.RemainingAmd,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (tab is null)
        {
            return null;
        }

        // Removed participants included: a line they placed still names them, and the projection
        // is what keeps them out of the roster.
        var participants = await db.TabParticipants
            .AsNoTracking()
            .Where(p => p.TabId == tabId)
            .OrderBy(p => p.JoinedAtUtc)
            .Select(p => new TabParticipantSnapshot(
                p.Id, p.DisplayName, p.Role, p.Status, p.CanOrder, p.CanSeeTableTotal, p.CanPay, p.JoinedAtUtc))
            .ToListAsync(cancellationToken);

        var lines = await db.TabOrderLines
            .AsNoTracking()
            .Where(l => l.TabOrder.TabId == tabId)
            .OrderBy(l => l.TabOrder.PlacedAtUtc)
            .ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.TabOrder.PlacedByParticipantId,
                l.NameSnapshot,
                l.UnitPriceAmdSnapshot,
                l.Quantity,
                l.IsShared,
                IsVoided = l.VoidedAtUtc != null,
                Shares = l.Shares.Select(s => s.TabParticipantId).ToList(),
            })
            .ToListAsync(cancellationToken);

        return new TabSnapshot(
            tab.Id,
            tab.BranchId,
            tab.DiningTableId,
            tab.TableLabel,
            tab.Status,
            tab.SettlementMode,
            tab.SettlementModeLockedAtUtc,
            tab.HideTotalFromGuests,
            tab.HostParticipantId,
            tab.OpenedAtUtc,
            tab.ClosedAtUtc,
            tab.SubtotalAmd,
            tab.ServiceChargeAmd,
            tab.TotalAmd,
            tab.PaidAmd,
            tab.RemainingAmd,
            participants,
            lines.Select(l => new TabLineSnapshot(
                    l.Id,
                    l.PlacedByParticipantId,
                    l.NameSnapshot,
                    l.UnitPriceAmdSnapshot,
                    l.Quantity,
                    l.IsShared,
                    l.IsVoided,
                    l.Shares))
                .ToList());
    }
}
