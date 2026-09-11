using Microsoft.EntityFrameworkCore;
using Yalla.Application.Media;
using Yalla.Application.Menus;
using Yalla.Domain;
using Yalla.Domain.Menus;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// One branch's menu: categories and items, with an availability toggle that is not a delete.
/// </summary>
/// <remarks>
/// <para>
/// An item an order line references is never deleted. The line snapshotted the name and price, so
/// the bill is safe either way, but the reference has to keep resolving - so the item is
/// deactivated instead, and the caller is told which happened.
/// </para>
/// <para>
/// <b>This is the console's view of the menu, and it shows incomplete items.</b> The diner-facing
/// read (<c>MenuQuery</c>) drops them; a manager has to be able to see the eleven dishes that still
/// need a photo, which is the entire point of being allowed to save them half-entered.
/// </para>
/// </remarks>
internal sealed class MenuService(YallaDbContext db) : IMenuService
{
    public async Task<IReadOnlyList<MenuCategoryView>> GetMenuAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        // Loaded as entities rather than projected into a flat row, so completeness is decided by
        // MenuItemCompleteness over the item itself. A projection would have to restate the rule in
        // a Select, and a second copy of that rule is exactly what must not exist.
        var categories = await db.MenuCategories
            .AsNoTracking()
            .Include(c => c.Items)
            .ThenInclude(i => i.Photo)
            .Where(c => c.BranchId == branchId)
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);

        return
        [
            .. categories.Select(c => new MenuCategoryView(
                c.Id,
                c.Name,
                c.DisplayOrder,
                [.. c.Items.OrderBy(i => i.DisplayOrder).ThenBy(i => i.Name).Select(ToView)])),
        ];
    }

    public async Task<MenuCategoryView> CreateCategoryAsync(
        Guid branchId,
        CreateMenuCategoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!await db.Branches.AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw new KeyNotFoundException($"Branch {branchId} was not found.");
        }

        var category = new MenuCategory(branchId, command.Name, command.DisplayOrder);
        db.MenuCategories.Add(category);
        await db.SaveChangesAsync(cancellationToken);

        return new MenuCategoryView(category.Id, category.Name, category.DisplayOrder, []);
    }

    public async Task<MenuCategoryView> UpdateCategoryAsync(
        Guid branchId,
        Guid categoryId,
        UpdateMenuCategoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = await LoadCategoryAsync(branchId, categoryId, cancellationToken);

        if (command.Name is { } name)
        {
            category.Rename(name);
        }

        if (command.DisplayOrder is { } order)
        {
            category.SetDisplayOrder(order);
        }

        await db.SaveChangesAsync(cancellationToken);

        return new MenuCategoryView(
            category.Id, category.Name, category.DisplayOrder,
            [.. category.Items.OrderBy(i => i.DisplayOrder).Select(ToView)]);
    }

    public async Task DeleteCategoryAsync(Guid branchId, Guid categoryId, CancellationToken cancellationToken = default)
    {
        var category = await LoadCategoryAsync(branchId, categoryId, cancellationToken);

        var referenced = await db.TabOrderLines
            .AnyAsync(l => l.MenuItem.MenuCategoryId == category.Id, cancellationToken);

        if (referenced)
        {
            throw new DomainStateException(
                $"Category '{category.Name}' has items that appear on orders and cannot be deleted. "
                + "Mark its items unavailable instead.");
        }

        db.MenuCategories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MenuItemView> CreateItemAsync(
        Guid branchId,
        Guid categoryId,
        CreateMenuItemCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var category = await LoadCategoryAsync(branchId, categoryId, cancellationToken);
        await RequirePhotoAtBranchAsync(branchId, command.PhotoId, cancellationToken);

        // A name and a price, and nothing else is insisted on here. The descriptive fields are what
        // make the item fit to show a diner, and that is enforced where it belongs: the item comes
        // back with isComplete false, readiness counts it, going Paid is refused while any remain,
        // and no diner is ever shown it. See docs/menu-completeness.md.
        var item = new MenuItem(
            category.Id,
            command.Name,
            command.Description,
            command.PriceAmd,
            command.PhotoId,
            command.Ingredients,
            command.Allergens,
            command.PortionSize,
            command.PrepMinutes,
            command.SpiceLevel,
            command.DisplayOrder);

        db.MenuItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        return ToView(await WithPhotoAsync(item, cancellationToken));
    }

    public async Task<MenuItemView> UpdateItemAsync(
        Guid branchId,
        Guid itemId,
        UpdateMenuItemCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var item = await LoadItemAsync(branchId, itemId, cancellationToken);
        await RequirePhotoAtBranchAsync(branchId, command.PhotoId, cancellationToken);

        var describes = command.Name is not null || command.Description is not null || command.PhotoId is not null
                        || command.Ingredients is not null || command.Allergens is not null
                        || command.PortionSize is not null || command.PrepMinutes is not null || command.SpiceLevel is not null;

        if (describes)
        {
            // Null means "not supplied", so every unspecified field keeps what it had - including
            // keeping nothing. An edit no longer insists on the fields creation no longer insists
            // on, or a half-entered item could never be saved a second time.
            item.UpdateDetails(
                command.Name ?? item.Name,
                command.Description ?? item.Description,
                command.PhotoId ?? item.PhotoId,
                command.Ingredients ?? item.Ingredients,
                command.Allergens ?? item.Allergens,
                command.PortionSize ?? item.PortionSize,
                command.PrepMinutes ?? item.PrepMinutes,
                command.SpiceLevel ?? item.SpiceLevel);
        }

        // Existing order lines snapshotted the old price and are untouched by this.
        if (command.PriceAmd is { } price)
        {
            item.SetPrice(price);
        }

        if (command.CategoryId is { } categoryId && categoryId != item.MenuCategoryId)
        {
            await MoveToCategoryAsync(branchId, item, categoryId, cancellationToken);
        }
        else if (command.DisplayOrder is { } order)
        {
            // Skipped on a move: the move already placed the item last, and applying a display
            // order from the old category in the same request would put it somewhere arbitrary.
            item.SetDisplayOrder(order);
        }

        await db.SaveChangesAsync(cancellationToken);

        return ToView(await WithPhotoAsync(item, cancellationToken));
    }

    public async Task<MenuItemView> SetItemAvailabilityAsync(
        Guid branchId,
        Guid itemId,
        bool isAvailable,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadItemAsync(branchId, itemId, cancellationToken);
        item.SetAvailable(isAvailable);
        await db.SaveChangesAsync(cancellationToken);

        return ToView(await WithPhotoAsync(item, cancellationToken));
    }

    public async Task<MenuItemDeletionResult> DeleteItemAsync(Guid branchId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await LoadItemAsync(branchId, itemId, cancellationToken);

        if (await db.TabOrderLines.AnyAsync(l => l.MenuItemId == item.Id, cancellationToken))
        {
            item.SetAvailable(false);
            await db.SaveChangesAsync(cancellationToken);

            return new MenuItemDeletionResult(
                item.Id,
                Deleted: false,
                Deactivated: true,
                $"'{item.Name}' appears on existing orders and cannot be deleted. It has been marked unavailable instead; "
                + "the orders keep the name and price they were placed at.");
        }

        db.MenuItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);

        return new MenuItemDeletionResult(item.Id, Deleted: true, Deactivated: false, $"'{item.Name}' was removed from the menu.");
    }

    /// <summary>
    /// Moves an item to another category of the same branch, placing it last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Last, not first, and not at its old position.</b> A dish moved into Desserts has no
    /// meaningful place among the desserts, and dropping it at position zero silently demotes
    /// whatever the owner deliberately put at the top. Last is the only choice that changes nothing
    /// the owner already decided, and the client can reorder immediately afterwards.
    /// </para>
    /// <para>
    /// A category on another branch is refused as a field violation rather than a 404: the item the
    /// request addresses does exist, and it is the <c>categoryId</c> in the body that is wrong -
    /// which is the difference between a form that highlights the category picker and one that
    /// shows "not found" over the whole page.
    /// </para>
    /// </remarks>
    private async Task MoveToCategoryAsync(
        Guid branchId,
        MenuItem item,
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var target = await db.MenuCategories
            .AsNoTracking()
            .Where(c => c.Id == categoryId)
            .Select(c => new { c.Id, c.BranchId, c.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (target is null || target.BranchId != branchId)
        {
            throw new FieldValidationException(new FieldViolation(
                "categoryId",
                $"Menu category {categoryId} is not a category of this branch. An item can only be moved "
                + "between categories of the branch whose menu it is on.",
                FieldBounds.Conflict,
                Value: categoryId));
        }

        var lastInTarget = await db.MenuItems
            .Where(i => i.MenuCategoryId == categoryId)
            .MaxAsync(i => (int?)i.DisplayOrder, cancellationToken) ?? -1;

        item.MoveToCategory(categoryId, lastInTarget + 1);
    }

    private async Task<MenuCategory> LoadCategoryAsync(Guid branchId, Guid categoryId, CancellationToken cancellationToken) =>
        await db.MenuCategories
            .Include(c => c.Items)
            .ThenInclude(i => i.Photo)
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.BranchId == branchId, cancellationToken)
        ?? throw new KeyNotFoundException($"Menu category {categoryId} was not found at this branch.");

    /// <summary>
    /// A photo is attached only to an item of the branch it was uploaded for.
    /// </summary>
    /// <remarks>
    /// The photo id in the command is caller-supplied, and ids are not secret - one venue's card
    /// URL names the photo. Without this a manager could put any venue's picture on their own
    /// menu. Refused the way a category from another branch is refused: the id is simply not found
    /// at this branch.
    /// </remarks>
    private async Task RequirePhotoAtBranchAsync(Guid branchId, Guid? photoId, CancellationToken cancellationToken)
    {
        // Nothing supplied is nothing to check. An empty guid is not a foreign id but a malformed
        // one, and the entity refuses it as such; left to it so that answer does not change here.
        if (photoId is not { } id || id == Guid.Empty)
        {
            return;
        }

        if (!await db.Photos.AnyAsync(p => p.Id == id && p.BranchId == branchId, cancellationToken))
        {
            throw new KeyNotFoundException($"Photo {id} was not found at this branch.");
        }
    }

    private async Task<MenuItem> LoadItemAsync(Guid branchId, Guid itemId, CancellationToken cancellationToken) =>
        await db.MenuItems
            .Include(i => i.Photo)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.MenuCategory.BranchId == branchId, cancellationToken)
        ?? throw new KeyNotFoundException($"Menu item {itemId} was not found at this branch.");

    /// <summary>
    /// Makes sure the photo behind an item is loaded before it is projected.
    /// </summary>
    /// <remarks>
    /// A freshly created item has a <c>PhotoId</c> and no <c>Photo</c> - nothing has read the row -
    /// and a view built from it would report the item as having no picture. Changing the photo has
    /// the same problem: the navigation still holds the old one until it is reloaded. An item with
    /// no photo at all needs neither, and asking EF to load a null reference is a wasted round trip.
    /// </remarks>
    private async Task<MenuItem> WithPhotoAsync(MenuItem item, CancellationToken cancellationToken)
    {
        if (item.PhotoId is null)
        {
            return item;
        }

        var reference = db.Entry(item).Reference(i => i.Photo);

        if (!reference.IsLoaded || item.Photo is null || item.Photo.Id != item.PhotoId)
        {
            reference.CurrentValue = null;
            await reference.LoadAsync(cancellationToken);
        }

        return item;
    }

    private static MenuItemView ToView(MenuItem i) => new(
        i.Id,
        i.MenuCategoryId,
        i.Name,
        i.Description,
        i.PriceAmd,
        i.Photo is { } photo ? PhotoView.From(photo) : null,
        i.Ingredients,
        i.Allergens,
        i.PortionSize,
        i.SpiceLevel,
        i.PrepMinutes,
        i.IsAvailable,
        i.DisplayOrder,
        i.IsComplete);
}
