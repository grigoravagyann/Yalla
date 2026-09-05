namespace Yalla.Application.Menus;

/// <summary>
/// One branch's whole menu, as a diner reads it.
/// </summary>
/// <remarks>
/// <para>
/// The same <see cref="MenuCategoryView"/> and <see cref="MenuItemView"/> the admin console gets,
/// deliberately. There is no diner-shaped subset: every field on an item exists <i>because</i> a
/// diner would otherwise have to ask a waiter about it, so a trimmed read model would defeat the
/// reason those fields are required in the first place.
/// </para>
/// <para>
/// <b>Unavailable items are returned, flagged, not hidden.</b> "We are out of khachapuri tonight"
/// is information a diner can act on. A dish that silently vanishes looks like a broken menu - and
/// the diner turns round and asks a waiter about it, which is the exact question this feature was
/// built to remove.
/// </para>
/// </remarks>
/// <param name="BranchId">The branch. Menus belong to branches, not venues - see <c>SCHEMA.md</c>.</param>
/// <param name="Categories">Every category in display order, each with its items.</param>
public sealed record BranchMenuView(Guid BranchId, IReadOnlyList<MenuCategoryView> Categories);

/// <summary>Reads a branch's menu for a diner.</summary>
/// <remarks>
/// Separate from <see cref="IMenuService"/>, which is the manager's editing surface. This one is
/// read-only, is reachable without an account, and refuses a venue that is no longer open for
/// business - a diner whose app cached a branch id must not be shown a menu they cannot order from.
/// </remarks>
public interface IMenuQuery
{
    /// <summary>
    /// The whole menu in one query - categories and their items together, never a query per
    /// category. The diner app opens on this screen, so it is a hot path.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The venue is not open for business.</exception>
    Task<BranchMenuView> GetBranchMenuAsync(Guid branchId, CancellationToken cancellationToken = default);
}
