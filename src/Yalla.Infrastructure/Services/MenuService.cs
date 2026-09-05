using Microsoft.EntityFrameworkCore;
using Yalla.Application.Menus;
using Yalla.Domain;
using Yalla.Domain.Menus;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// One branch's menu: categories and items, with an availability toggle that is not a delete.
/// </summary>
/// <remarks>
/// An item an order line references is never deleted. The line snapshotted the name and price, so
/// the bill is safe either way, but the reference has to keep resolving - so the item is
/// deactivated instead, and the caller is told which happened.
/// </remarks>
internal sealed class MenuService(YallaDbContext db) : IMenuService
{
    public async Task<IReadOnlyList<MenuCategoryView>> GetMenuAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
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
                Items = c.Items.OrderBy(i => i.DisplayOrder).ThenBy(i => i.Name).Select(i => ToView(i)).ToList(),
            })
            .ToListAsync(cancellationToken);

        return categories.Select(c => new MenuCategoryView(c.Id, c.Name, c.DisplayOrder, c.Items)).ToList();
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
            category.Items.OrderBy(i => i.DisplayOrder).Select(ToView).ToList());
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

        // The constructor refuses a blank ingredients, allergens, portion size or photo, and a
        // non-positive prep time. Those are the questions a diner would otherwise ask a waiter.
        var item = new MenuItem(
            category.Id,
            command.Name,
            command.Description,
            command.PriceAmd,
            command.PhotoUrl,
            command.Ingredients,
            command.Allergens,
            command.PortionSize,
            command.PrepMinutes,
            command.SpiceLevel,
            command.DisplayOrder);

        db.MenuItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        return ToView(item);
    }

    public async Task<MenuItemView> UpdateItemAsync(
        Guid branchId,
        Guid itemId,
        UpdateMenuItemCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var item = await LoadItemAsync(branchId, itemId, cancellationToken);

        var describes = command.Name is not null || command.Description is not null || command.PhotoUrl is not null
                        || command.Ingredients is not null || command.Allergens is not null
                        || command.PortionSize is not null || command.PrepMinutes is not null || command.SpiceLevel is not null;

        if (describes)
        {
            item.UpdateDetails(
                command.Name ?? item.Name,
                command.Description ?? item.Description,
                command.PhotoUrl ?? item.PhotoUrl,
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

        if (command.DisplayOrder is { } order)
        {
            item.SetDisplayOrder(order);
        }

        await db.SaveChangesAsync(cancellationToken);

        return ToView(item);
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

        return ToView(item);
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

    private async Task<MenuCategory> LoadCategoryAsync(Guid branchId, Guid categoryId, CancellationToken cancellationToken) =>
        await db.MenuCategories
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.BranchId == branchId, cancellationToken)
        ?? throw new KeyNotFoundException($"Menu category {categoryId} was not found at this branch.");

    private async Task<MenuItem> LoadItemAsync(Guid branchId, Guid itemId, CancellationToken cancellationToken) =>
        await db.MenuItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.MenuCategory.BranchId == branchId, cancellationToken)
        ?? throw new KeyNotFoundException($"Menu item {itemId} was not found at this branch.");

    private static MenuItemView ToView(MenuItem i) => new(
        i.Id, i.MenuCategoryId, i.Name, i.Description, i.PriceAmd, i.PhotoUrl, i.Ingredients, i.Allergens,
        i.PortionSize, i.SpiceLevel, i.PrepMinutes, i.IsAvailable, i.DisplayOrder);
}
