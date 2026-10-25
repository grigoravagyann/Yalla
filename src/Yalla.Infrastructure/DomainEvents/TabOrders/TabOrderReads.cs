using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.TabOrders;

/// <summary>
/// What the tab-order handlers read about an order's tab, from the tracker first: the code that raised the event has
/// almost always loaded it already, and a handler that queries again for what is in memory costs every order a round
/// trip. Each read falls back to the database for an event raised on a path that loaded less.
/// </summary>
internal static class TabOrderReads
{
    /// <summary>The order's tab, with its participants.</summary>
    public static async Task<Tab> TabAsync(YallaDbContext db, TabOrder order, CancellationToken cancellationToken)
    {
        var tab = db.Tabs.Local.FirstOrDefault(t => t.Id == order.TabId)
                  ?? await db.Tabs.FirstAsync(t => t.Id == order.TabId, cancellationToken);

        var participants = db.Entry(tab).Collection(t => t.Participants);

        if (!participants.IsLoaded)
        {
            await participants.LoadAsync(cancellationToken);
        }

        return tab;
    }

    /// <summary>The tab's table label, from the tracker when it is there.</summary>
    public static async Task<string?> TableLabelAsync(YallaDbContext db, Tab tab, CancellationToken cancellationToken) =>
        db.DiningTables.Local.FirstOrDefault(t => t.Id == tab.DiningTableId)?.Label
        ?? await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == tab.DiningTableId)
            .Select(t => t.Label)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The tab's branch, with its venue.</summary>
    public static async Task<Branch> BranchAsync(YallaDbContext db, Tab tab, CancellationToken cancellationToken)
    {
        var branchReference = db.Entry(tab).Reference(t => t.Branch);

        if (!branchReference.IsLoaded)
        {
            await branchReference.LoadAsync(cancellationToken);
        }

        var venueReference = db.Entry(tab.Branch).Reference(b => b.Venue);

        if (!venueReference.IsLoaded)
        {
            await venueReference.LoadAsync(cancellationToken);
        }

        return tab.Branch;
    }
}
