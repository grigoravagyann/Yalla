using Yalla.Application.Media;
using Yalla.Domain.Enums;

namespace Yalla.Application.Menus;

public sealed record CreateMenuCategoryCommand(string Name, int DisplayOrder = 0);

public sealed record UpdateMenuCategoryCommand(string? Name = null, int? DisplayOrder = null);

/// <summary>
/// A new item. Ingredients, allergens, portion size, prep minutes and a photo are <b>required</b>:
/// they are what a diner would otherwise ask a waiter, and optional fields stay blank.
/// </summary>
public sealed record CreateMenuItemCommand(
    string Name,
    string Description,
    long PriceAmd,
    Guid PhotoId,
    string Ingredients,
    string Allergens,
    string PortionSize,
    int PrepMinutes,
    SpiceLevel SpiceLevel = SpiceLevel.NotSpicy,
    int DisplayOrder = 0);

/// <summary>Patch an item. Only supplied fields change; a supplied field must still be non-blank.</summary>
public sealed record UpdateMenuItemCommand(
    string? Name = null,
    string? Description = null,
    long? PriceAmd = null,
    Guid? PhotoId = null,
    string? Ingredients = null,
    string? Allergens = null,
    string? PortionSize = null,
    int? PrepMinutes = null,
    SpiceLevel? SpiceLevel = null,
    int? DisplayOrder = null);

public sealed record MenuItemView(
    Guid Id,
    Guid CategoryId,
    string Name,
    string Description,
    long PriceAmd,
    PhotoView Photo,
    string Ingredients,
    string Allergens,
    string PortionSize,
    SpiceLevel SpiceLevel,
    int PrepMinutes,
    bool IsAvailable,
    int DisplayOrder);

public sealed record MenuCategoryView(Guid Id, string Name, int DisplayOrder, IReadOnlyList<MenuItemView> Items);

/// <summary>What deleting an item did: removed, or deactivated because order lines reference it.</summary>
public sealed record MenuItemDeletionResult(Guid ItemId, bool Deleted, bool Deactivated, string Message);

/// <summary>
/// Managing one branch's menu. Manager or owner within scope, or a platform admin.
/// </summary>
public interface IMenuService
{
    Task<IReadOnlyList<MenuCategoryView>> GetMenuAsync(Guid branchId, CancellationToken cancellationToken = default);

    Task<MenuCategoryView> CreateCategoryAsync(Guid branchId, CreateMenuCategoryCommand command, CancellationToken cancellationToken = default);

    Task<MenuCategoryView> UpdateCategoryAsync(Guid branchId, Guid categoryId, UpdateMenuCategoryCommand command, CancellationToken cancellationToken = default);

    /// <summary>Removes a category and its items. Refused while any item is referenced by an order line.</summary>
    Task DeleteCategoryAsync(Guid branchId, Guid categoryId, CancellationToken cancellationToken = default);

    Task<MenuItemView> CreateItemAsync(Guid branchId, Guid categoryId, CreateMenuItemCommand command, CancellationToken cancellationToken = default);

    Task<MenuItemView> UpdateItemAsync(Guid branchId, Guid itemId, UpdateMenuItemCommand command, CancellationToken cancellationToken = default);

    /// <summary>"We're out of khachapuri tonight." Not a delete.</summary>
    Task<MenuItemView> SetItemAvailabilityAsync(Guid branchId, Guid itemId, bool isAvailable, CancellationToken cancellationToken = default);

    /// <summary>Deletes an item nothing references, or deactivates one an order line does.</summary>
    Task<MenuItemDeletionResult> DeleteItemAsync(Guid branchId, Guid itemId, CancellationToken cancellationToken = default);
}
