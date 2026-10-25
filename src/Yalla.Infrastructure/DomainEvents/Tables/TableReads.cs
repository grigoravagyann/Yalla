using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Tables;

/// <summary>
/// What the table handlers read, from the tracker first: the state machine loaded the table with its branch, and the
/// transfer loaded the tab, its table and the two people - a handler that queried again would cost every change a round
/// trip the hand-written code did not. Each read falls back to the database for an event raised on a path that loaded
/// less.
/// </summary>
internal static class TableReads
{
    /// <summary>The table's branch (for its venue).</summary>
    public static async Task<Branch> BranchAsync(YallaDbContext db, DiningTable table, CancellationToken cancellationToken)
    {
        var reference = db.Entry(table).Reference(t => t.Branch);

        if (!reference.IsLoaded)
        {
            await reference.LoadAsync(cancellationToken);
        }

        return table.Branch;
    }

    /// <summary>The tab a hand-over was asked through, with its table.</summary>
    public static async Task<Tab> TabAsync(YallaDbContext db, Guid tabId, CancellationToken cancellationToken)
    {
        var tab = db.Tabs.Local.FirstOrDefault(t => t.Id == tabId)
                  ?? await db.Tabs.FirstAsync(t => t.Id == tabId, cancellationToken);

        var table = db.Entry(tab).Reference(t => t.DiningTable);

        if (!table.IsLoaded)
        {
            await table.LoadAsync(cancellationToken);
        }

        return tab;
    }

    /// <summary>A staff member's name as stored, or null when there is no such person.</summary>
    public static async Task<string?> StaffNameAsync(YallaDbContext db, Guid staffMemberId, CancellationToken cancellationToken) =>
        db.StaffMembers.Local.FirstOrDefault(s => s.Id == staffMemberId)?.FullName
        ?? await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffMemberId)
            .Select(s => s.FullName)
            .FirstOrDefaultAsync(cancellationToken);
}
