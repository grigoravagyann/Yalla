using Yalla.Application.Media;
using Yalla.Domain.Enums;

namespace Yalla.Application.Menus;

/// <summary>A new category.</summary>
/// <param name="Name">What the diner reads.</param>
/// <param name="DisplayOrder">Where it goes; <b>absent puts it last</b>, after every category the branch has.</param>
public sealed record CreateMenuCategoryCommand(string Name, int? DisplayOrder = null);

public sealed record UpdateMenuCategoryCommand(string? Name = null, int? DisplayOrder = null);

/// <summary>
/// A new item. Only a name and a price are required.
/// </summary>
/// <remarks>
/// <para>
/// Ingredients, allergens, portion size, prep minutes and a photo are still what make an item fit
/// to show a diner - see <c>MenuItemCompleteness</c> - but they are no longer required <i>here</i>.
/// Requiring them on create meant an eighty-dish menu could not be entered without eighty photo
/// uploads first, which is not the order the work happens in: somebody sits down with the owner and
/// types names and prices, and the photographs are taken the following week.
/// </para>
/// <para>
/// <c>DisplayOrder</c> absent puts the item last in its category, where the console's "add a dish" shows it.
/// </para>
/// <para>
/// The rule moved rather than went away. An item saved without them comes back with
/// <c>isComplete: false</c>, the branch readiness projection counts it, the branch cannot be
/// switched to Paid while any remain, and the diner-facing menu does not return it at all.
/// </para>
/// </remarks>
public sealed record CreateMenuItemCommand(
    string Name,
    long PriceAmd,
    string? Description = null,
    Guid? PhotoId = null,
    string? Ingredients = null,
    string? Allergens = null,
    string? PortionSize = null,
    int? PrepMinutes = null,
    SpiceLevel SpiceLevel = SpiceLevel.NotSpicy,
    int? DisplayOrder = null);

/// <summary>
/// Patch an item. Only supplied fields change; null means "not supplied", never "clear this".
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="CategoryId"/> moves the item between categories of the same branch.</b> There was
/// no way to do that, and an owner reorganising their menu wants to in the first month. The item
/// lands <i>last</i> in the target category's display order - anywhere else would silently demote
/// whatever the owner deliberately put at the top - and the client can reorder immediately
/// afterwards. Order lines already placed are untouched: they snapshot the name and price and
/// never reference a category at all.
/// </para>
/// <para>
/// A <see cref="DisplayOrder"/> sent alongside a move is ignored, because it describes a position
/// in the category the item is leaving.
/// </para>
/// </remarks>
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
    int? DisplayOrder = null,
    Guid? CategoryId = null,
    bool? AvailableForTakeaway = null);

/// <summary>
/// One menu item as a client reads it.
/// </summary>
/// <remarks>
/// <b><c>isComplete</c> says whether this item may be shown to a diner</b>: it has a photo, a
/// description, ingredients, allergens, a portion size and a prep time. Computed on the server,
/// from the single definition the readiness checklist and the going-live gate use. A client must
/// never decide what complete means - two definitions drift, and the way they drift is a dish with
/// no allergen list reaching somebody it is dangerous to. The diner-facing menu never returns an
/// item whose <c>isComplete</c> is false, so on that read it is always true.
/// <para>
/// <b><c>missingFields</c> says which ones</b>, from the same definition: <c>photo</c>, <c>description</c>,
/// <c>ingredients</c>, <c>allergens</c>, <c>portionSize</c>, <c>prepMinutes</c>, in that order. Empty exactly
/// when <c>isComplete</c> is true - the console's "2 fields missing" and its chips, without a copy of the rule.
/// </para>
/// <para>
/// <b><c>availableForPreOrder</c></b>: guests may put it on a booking's pre-order (the venue excludes dishes through
/// <c>PUT /api/branches/{branchId}/pre-order-settings</c>). A pre-order offers what is available and not excluded.
/// </para>
/// <para>
/// <b><c>availableForTakeaway</c></b>: guests may order it to take away - true by default, switched per dish with
/// <c>PATCH .../items/{itemId}</c>. The diner's takeaway menu greys out a dish with false: "only in the hall".
/// </para>
/// </remarks>
public sealed record MenuItemView(
    Guid Id,
    Guid CategoryId,
    string Name,
    string? Description,
    long PriceAmd,
    PhotoView? Photo,
    string? Ingredients,
    string? Allergens,
    string? PortionSize,
    SpiceLevel SpiceLevel,
    int? PrepMinutes,
    bool IsAvailable,
    int DisplayOrder,
    bool IsComplete,
    IReadOnlyList<string> MissingFields,
    bool AvailableForPreOrder = true,
    bool AvailableForTakeaway = true);

public sealed record MenuCategoryView(Guid Id, string Name, int DisplayOrder, IReadOnlyList<MenuItemView> Items);

/// <summary>A new order for every category of a branch.</summary>
/// <param name="CategoryIds">
/// Every category of the branch, <b>each exactly once</b>, first to last. A list that leaves one out, names one
/// twice or names another branch's is refused whole (422 <c>categoryIds</c> <c>conflict</c>) - the console is
/// looking at a stale menu and should reload it.
/// </param>
public sealed record ReorderMenuCategoriesCommand(IReadOnlyList<Guid> CategoryIds);

/// <summary>A new order for every item of one category.</summary>
/// <param name="ItemIds">Every item of the category, each exactly once, first to last; refused whole otherwise.</param>
public sealed record ReorderMenuItemsCommand(IReadOnlyList<Guid> ItemIds);

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

    /// <summary>
    /// Takes the photo off a dish - the one field a patch cannot clear, since null there means "not supplied". The
    /// dish is then incomplete and leaves the diner's menu until it has one again; the picture, used by nothing
    /// now, is left to the orphan sweep. Saying it of a dish with no photo changes nothing.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such dish at this branch.</exception>
    Task<MenuItemView> RemoveItemPhotoAsync(Guid branchId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts every category of the branch in the given order - display orders 0, 1, 2 - in one transaction under
    /// the branch lock, and returns the menu as the manage read does.
    /// </summary>
    /// <exception cref="Yalla.Domain.FieldValidationException">The list is not every category exactly once.</exception>
    Task<IReadOnlyList<MenuCategoryView>> ReorderCategoriesAsync(
        Guid branchId,
        ReorderMenuCategoriesCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Puts every item of one category in the given order, likewise, and returns the category.</summary>
    /// <exception cref="KeyNotFoundException">No such category at this branch.</exception>
    /// <exception cref="Yalla.Domain.FieldValidationException">The list is not every item of the category exactly once.</exception>
    Task<MenuCategoryView> ReorderItemsAsync(
        Guid branchId,
        Guid categoryId,
        ReorderMenuItemsCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a category and its items. Refused while any item is referenced by an order line.</summary>
    Task DeleteCategoryAsync(Guid branchId, Guid categoryId, CancellationToken cancellationToken = default);

    /// <summary>Adds an item. Incomplete is allowed; see <see cref="CreateMenuItemCommand"/>.</summary>
    Task<MenuItemView> CreateItemAsync(Guid branchId, Guid categoryId, CreateMenuItemCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Edits an item, including moving it to another category of the same branch.
    /// </summary>
    /// <exception cref="Yalla.Domain.FieldValidationException">
    /// <c>categoryId</c> names a category that is not on this branch.
    /// </exception>
    Task<MenuItemView> UpdateItemAsync(Guid branchId, Guid itemId, UpdateMenuItemCommand command, CancellationToken cancellationToken = default);

    /// <summary>"We're out of khachapuri tonight." Not a delete.</summary>
    Task<MenuItemView> SetItemAvailabilityAsync(Guid branchId, Guid itemId, bool isAvailable, CancellationToken cancellationToken = default);

    /// <summary>Deletes an item nothing references, or deactivates one an order line does.</summary>
    Task<MenuItemDeletionResult> DeleteItemAsync(Guid branchId, Guid itemId, CancellationToken cancellationToken = default);
}
