using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tabs;
using Yalla.Domain.Tabs;
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
/// <para>
/// <b>Voided lines and adjustments are read, not filtered.</b> Whether a diner sees them is a
/// question for the projection, and the answer is that they do - a bill that quietly loses a line
/// is a bill somebody stops trusting.
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
    /// Five reads on one connection: the tab and its table, the participants, the lines with
    /// their shares, the adjustments, and the stream's high-water mark. Untracked throughout -
    /// this is a read model, and the service that may have just written these rows reads them back
    /// through here after its own SaveChanges.
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
                Tier = t.Branch.SubscriptionTier,

                // Named on the tab so the phone need not make a second, public read to put the venue
                // and branch at the top of the bill.
                VenueName = t.Branch.Venue.Name,
                BranchName = t.Branch.Name,

                // The branch's wall clock. Every instant below is UTC and the client renders it in
                // this zone; without it on the response the client had to fetch the branch as well.
                t.Branch.TimeZoneId,
                t.DiningTableId,
                TableLabel = t.DiningTable.Label,
                t.Status,
                t.SettlementMode,
                t.SettlementModeLockedAtUtc,
                t.HideTotalFromGuests,
                t.HostParticipantId,
                t.ServiceChargePercentSnapshot,
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

        // A free branch has no tabs to read. Same answer the service gives on write.
        if (tab.Tier != Yalla.Domain.Enums.SubscriptionTier.Paid)
        {
            throw new Yalla.Domain.Venues.FeatureNotEnabledException("Tabs and ordering", tab.BranchId, tab.Tier);
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
                l.TabOrderId,
                l.MenuItemId,
                l.TabOrder.PlacedByParticipantId,
                OrderStatus = l.TabOrder.Status,
                l.NameSnapshot,
                l.UnitPriceAmdSnapshot,
                l.Quantity,
                l.Note,
                l.IsShared,
                l.VoidedAtUtc,
                l.VoidReason,
                Shares = l.Shares.Select(s => s.TabParticipantId).ToList(),
            })
            .ToListAsync(cancellationToken);

        // Voided adjustments are read too. One that was reversed stays on the record, marked - the
        // diner saw the discount arrive and has to be able to see it go.
        var adjustments = await db.TabAdjustments
            .AsNoTracking()
            .Where(a => a.TabId == tabId)
            .OrderBy(a => a.CreatedAtUtc)
            .Select(a => new
            {
                a.Id,
                a.TabOrderLineId,
                a.Kind,
                a.Percent,
                a.AmountAmd,
                a.Reason,
                a.CreatedAtUtc,
                a.VoidedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var maxSequence = await db.TabEvents
            .AsNoTracking()
            .Where(e => e.TabId == tabId)
            .MaxAsync(e => (long?)e.Sequence, cancellationToken) ?? 0L;

        // What each adjustment actually took off. Computed here rather than stored, from the same
        // base the bill uses: a percentage against the line it names, or against the tab subtotal
        // when it names none.
        var lineTotals = lines.ToDictionary(
            l => l.Id,
            l => l.VoidedAtUtc is null ? l.UnitPriceAmdSnapshot * l.Quantity : 0L);

        var liveSubtotal = lineTotals.Values.Sum();

        return new TabSnapshot(
            tab.Id,
            tab.BranchId,
            tab.VenueName,
            tab.BranchName,
            tab.DiningTableId,
            tab.TableLabel,
            tab.TimeZoneId,
            tab.Status,
            tab.SettlementMode,
            tab.SettlementModeLockedAtUtc,
            tab.HideTotalFromGuests,
            tab.HostParticipantId,
            tab.ServiceChargePercentSnapshot,
            tab.OpenedAtUtc,
            tab.ClosedAtUtc,
            tab.SubtotalAmd,
            tab.ServiceChargeAmd,
            tab.TotalAmd,
            tab.PaidAmd,
            tab.RemainingAmd,
            maxSequence,
            participants,
            [
                .. lines.Select(l => new TabLineSnapshot(
                    l.Id,
                    l.TabOrderId,
                    l.MenuItemId,
                    l.PlacedByParticipantId,
                    l.NameSnapshot,
                    l.UnitPriceAmdSnapshot,
                    l.Quantity,
                    l.Note,
                    l.OrderStatus,
                    l.IsShared,
                    IsVoided: l.VoidedAtUtc is not null,
                    l.VoidedAtUtc,
                    l.VoidReason,
                    l.Shares)),
            ],
            [
                .. adjustments.Select(a => new TabAdjustmentSnapshot(
                    a.Id,
                    a.TabOrderLineId,
                    a.Kind,
                    a.Percent,
                    a.AmountAmd,
                    ReductionAmd: a.VoidedAtUtc is not null
                        ? 0L
                        : Reduction(a.Percent, a.AmountAmd, a.TabOrderLineId, lineTotals, liveSubtotal),
                    a.Reason,
                    a.CreatedAtUtc,
                    IsVoided: a.VoidedAtUtc is not null)),
            ]);
    }

    /// <summary>
    /// What one adjustment takes off, against the line it names or the tab as a whole.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="TabAdjustment.ReductionOn"/> so the number a diner reads on the bill comes
    /// out of the same rounding rule the bill itself was computed with. A second implementation
    /// here would show a diner a discount that does not add up to the total beneath it.
    /// </remarks>
    private static long Reduction(
        decimal? percent,
        long? amountAmd,
        Guid? lineId,
        IReadOnlyDictionary<Guid, long> lineTotals,
        long liveSubtotal)
    {
        var baseAmd = lineId is { } id
            ? lineTotals.GetValueOrDefault(id)
            : liveSubtotal;

        return TabAdjustment.ReductionFor(percent, amountAmd, baseAmd);
    }
}
