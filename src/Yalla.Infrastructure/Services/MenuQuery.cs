using Microsoft.EntityFrameworkCore;
using Yalla.Application.Menus;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The diner-facing menu read: one query, everything on it, sold-out items included.
/// </summary>
/// <remarks>
/// Separate from <see cref="MenuService"/>, which edits. This one adds the gate that matters for a
/// diner and not for a manager: a suspended or deleted venue serves no menu, because a phone that
/// cached the branch id must not be shown a list of things it cannot order.
/// </remarks>
internal sealed class MenuQuery(YallaDbContext db) : IMenuQuery
{
    public async Task<BranchMenuView> GetBranchMenuAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Include(b => b.Venue)
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        VenueGate.RequireOpenForBusiness(branch);

        // One round trip. The diner app opens on this screen, and a query per category would turn
        // a menu with fifteen sections into sixteen round trips over a phone connection.
        var categories = await db.MenuCategories
            .AsNoTracking()
            .Where(c => c.BranchId == branchId)
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .Select(c => new MenuCategoryView(
                c.Id,
                c.Name,
                c.DisplayOrder,
                c.Items
                    .OrderBy(i => i.DisplayOrder)
                    .ThenBy(i => i.Name)
                    .Select(i => new MenuItemView(
                        i.Id,
                        i.MenuCategoryId,
                        i.Name,
                        i.Description,
                        i.PriceAmd,
                        i.PhotoUrl,
                        i.Ingredients,
                        i.Allergens,
                        i.PortionSize,
                        i.SpiceLevel,
                        i.PrepMinutes,

                        // Not filtered. A sold-out dish is shown greyed out with its price, because
                        // "we are out of it tonight" is an answer and a missing row is not.
                        i.IsAvailable,
                        i.DisplayOrder))
                    .ToList()))
            .ToListAsync(cancellationToken);

        return new BranchMenuView(branchId, categories);
    }
}
