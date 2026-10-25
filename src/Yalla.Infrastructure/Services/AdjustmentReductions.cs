using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Billing;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// What discounts and comps actually took off their bills, for the reads that add them up - the revenue report's
/// adjustments and Staff → Results - by the bill's own arithmetic (<see cref="TabBilling.Reductions"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not off the stored subtotal.</b> <c>Tab.SubtotalAmd</c> is what is left <i>after</i> the adjustments, so a 10 %
/// discount read back as 10 % of it says 882 where the bill took 980 off. A line's own adjustments come off that line in
/// turn and the tab-wide ones off what is left of the sum, each in turn, so two on one bill cannot be read one at a time
/// either: each bill's lines and every adjustment standing on it are read, and its reductions worked out as the bill
/// worked them out - in the order they were given, as the bill (<see cref="TabLedger"/>) applies them.
/// </para>
/// <para>
/// <b>Two statements</b> whatever the window: the lines and the standing adjustments of every bill the asked-for
/// adjustments are on, each a subquery on that set rather than a list of ids.
/// </para>
/// </remarks>
internal static class AdjustmentReductions
{
    /// <summary>Each of <paramref name="wanted"/>'s adjustments by id, and what it took off its bill.</summary>
    public static async Task<Dictionary<Guid, long>> ForAsync(
        YallaDbContext db, IQueryable<TabAdjustment> wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(wanted);

        var tabIds = wanted.Select(a => a.TabId).Distinct();

        var lines = await db.TabOrderLines
            .AsNoTracking()
            .Where(l => tabIds.Contains(l.TabOrder.TabId))
            .Select(l => new { l.TabOrder.TabId, l.Id, l.UnitPriceAmdSnapshot, l.Quantity, Voided = l.VoidedAtUtc != null })
            .ToListAsync(cancellationToken);

        var standing = await db.TabAdjustments
            .AsNoTracking()
            .Where(a => tabIds.Contains(a.TabId) && a.VoidedAtUtc == null)
            .Select(a => new { a.Id, a.TabId, a.TabOrderLineId, a.Percent, a.AmountAmd, a.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        var linesByTab = lines.ToLookup(l => l.TabId);
        var reductions = new Dictionary<Guid, long>(standing.Count);

        foreach (var tab in standing.GroupBy(a => a.TabId))
        {
            // The bill's order (TabLedger): as given, ties by id as .NET compares it - in memory, not as SQL Server
            // orders a uniqueidentifier.
            var adjustments = tab.OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).ToList();

            var off = TabBilling.Reductions(
                [.. linesByTab[tab.Key].Select(l => new BillingLine(l.Id, null, l.UnitPriceAmdSnapshot, l.Quantity, l.Voided, false, []))],
                [.. adjustments.Select(a => new BillingAdjustment(a.TabOrderLineId, a.Percent, a.AmountAmd))]);

            for (var i = 0; i < adjustments.Count; i++)
            {
                reductions[adjustments[i].Id] = off[i];
            }
        }

        return reductions;
    }
}
