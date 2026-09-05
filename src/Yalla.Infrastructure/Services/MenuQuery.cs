using Microsoft.EntityFrameworkCore;
using Yalla.Application.Media;
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
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.DisplayOrder,
                Items = c.Items
                    .OrderBy(i => i.DisplayOrder)
                    .ThenBy(i => i.Name)
                    .Select(i => new MenuItemRow
                    {
                        Id = i.Id,
                        CategoryId = i.MenuCategoryId,
                        Name = i.Name,
                        Description = i.Description,
                        PriceAmd = i.PriceAmd,
                        PhotoId = i.PhotoId,
                        PhotoIsExternal = i.Photo.IsExternallyHosted,
                        PhotoThumbnail = i.Photo.ThumbnailPath,
                        PhotoCard = i.Photo.CardPath,
                        PhotoFull = i.Photo.FullPath,
                        PhotoWidth = i.Photo.Width,
                        PhotoHeight = i.Photo.Height,
                        Ingredients = i.Ingredients,
                        Allergens = i.Allergens,
                        PortionSize = i.PortionSize,
                        SpiceLevel = i.SpiceLevel,
                        PrepMinutes = i.PrepMinutes,

                        // Not filtered. A sold-out dish is shown greyed out with its price, because
                        // "we are out of it tonight" is an answer and a missing row is not.
                        IsAvailable = i.IsAvailable,
                        DisplayOrder = i.DisplayOrder,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        // The photo links are built after materialising, because turning a stored key into a URL is
        // not something SQL can do. Still one round trip: the columns came back with the query.
        return new BranchMenuView(
            branchId,
            [
                .. categories.Select(c => new MenuCategoryView(
                    c.Id, c.Name, c.DisplayOrder, [.. c.Items.Select(ToView)])),
            ]);
    }

    private static MenuItemView ToView(MenuItemRow row) =>
        new(
            row.Id,
            row.CategoryId,
            row.Name,
            row.Description,
            row.PriceAmd,
            PhotoView.From(
                row.PhotoId, row.PhotoIsExternal, row.PhotoThumbnail, row.PhotoCard, row.PhotoFull,
                row.PhotoWidth, row.PhotoHeight),
            row.Ingredients,
            row.Allergens,
            row.PortionSize,
            row.SpiceLevel,
            row.PrepMinutes,
            row.IsAvailable,
            row.DisplayOrder);

    /// <summary>The columns one menu item needs, flat, so the whole menu stays one query.</summary>
    private sealed class MenuItemRow
    {
        public Guid Id { get; init; }

        public Guid CategoryId { get; init; }

        public string Name { get; init; } = null!;

        public string Description { get; init; } = null!;

        public long PriceAmd { get; init; }

        public Guid PhotoId { get; init; }

        public bool PhotoIsExternal { get; init; }

        public string PhotoThumbnail { get; init; } = null!;

        public string PhotoCard { get; init; } = null!;

        public string PhotoFull { get; init; } = null!;

        public int? PhotoWidth { get; init; }

        public int? PhotoHeight { get; init; }

        public string Ingredients { get; init; } = null!;

        public string Allergens { get; init; } = null!;

        public string PortionSize { get; init; } = null!;

        public Yalla.Domain.Enums.SpiceLevel SpiceLevel { get; init; }

        public int PrepMinutes { get; init; }

        public bool IsAvailable { get; init; }

        public int DisplayOrder { get; init; }
    }
}
