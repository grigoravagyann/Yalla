using Microsoft.EntityFrameworkCore;
using Yalla.Application.Media;
using Yalla.Application.Menus;
using Yalla.Domain.Menus;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The diner-facing menu read: one query, sold-out items included, unfinished ones excluded.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="MenuService"/>, which edits. This one adds the two gates that matter
/// for a diner and not for a manager.
/// </para>
/// <para>
/// The first is the venue gate: a suspended or deleted venue serves no menu, because a phone that
/// cached the branch id must not be shown a list of things it cannot order.
/// </para>
/// <para>
/// The second is completeness. An item with no photo and no allergen list must never reach a
/// diner - a diner reading an empty allergen list reasonably concludes there are none - so it is
/// dropped here rather than shown greyed out. That is deliberately the opposite treatment from a
/// sold-out dish, which <i>is</i> shown: "we are out of khachapuri tonight" is an answer, and "we
/// have not finished typing this in" is not something to put in front of a customer at all.
/// </para>
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
        // a menu with fifteen sections into sixteen round trips over a phone connection. Include
        // rather than a projection so completeness is decided by MenuItemCompleteness over the item
        // itself: restating the rule in a Select would be a second definition of "complete", and
        // the day the two disagreed is the day a dish with no allergens reached a phone.
        var categories = await db.MenuCategories
            .AsNoTracking()
            .Include(c => c.Items)
            .ThenInclude(i => i.Photo)
            .Where(c => c.BranchId == branchId)
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);

        return new BranchMenuView(
            branchId,
            [
                .. categories.Select(c => new MenuCategoryView(
                    c.Id,
                    c.Name,
                    c.DisplayOrder,
                    [
                        .. c.Items
                            .Where(MenuItemCompleteness.IsComplete)
                            .OrderBy(i => i.DisplayOrder)
                            .ThenBy(i => i.Name)
                            .Select(ToView),
                    ])),
            ]);
    }

    /// <summary>
    /// Projects an item a diner is allowed to see.
    /// </summary>
    /// <remarks>
    /// Every optional field is dereferenced without a null check on purpose: this only ever runs on
    /// items that passed <see cref="MenuItemCompleteness"/>, which is precisely the statement that
    /// none of them is null. If that ever stops being true the null-forgiving operators are where
    /// it will show up, which is better than silently serving a dish with no allergens.
    /// </remarks>
    private static MenuItemView ToView(MenuItem item) =>
        new(
            item.Id,
            item.MenuCategoryId,
            item.Name,
            item.Description,
            item.PriceAmd,
            PhotoView.From(item.Photo!),
            item.Ingredients,
            item.Allergens,
            item.PortionSize,
            item.SpiceLevel,
            item.PrepMinutes,

            // Not filtered. A sold-out dish is shown greyed out with its price, because "we are out
            // of it tonight" is an answer and a missing row is not.
            item.IsAvailable,
            item.DisplayOrder,
            IsComplete: true);
}
