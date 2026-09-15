using Yalla.Domain.Common;

namespace Yalla.Domain.Identity;

/// <summary>
/// A place a diner hearted, kept on the account so it follows them to a new phone (K11).
/// </summary>
/// <remarks>
/// <para>
/// <b>One row per diner per branch</b>, by a unique index on <c>(DinerUserId, BranchId)</c>, so hearting
/// a place twice is the same place once. Adding is idempotent at the route as well as at the index.
/// </para>
/// <para>
/// <b>The diner's, not the venue's.</b> Nothing about a favourite is shown to the venue, and deleting the
/// account deletes every one of them. A branch that closes keeps its rows: the list leaves it out while
/// it is not published, and it comes back if the branch does.
/// </para>
/// <para>
/// Any diner account may keep favourites - a proved number is not needed to remember a place.
/// </para>
/// </remarks>
public sealed class DinerFavorite : Entity
{
    /// <summary>The most places one account keeps. Beyond it an add is refused rather than trimmed.</summary>
    public const int MaxPerDiner = 500;

    public Guid DinerUserId { get; private set; }

    public Guid BranchId { get; private set; }

    private DinerFavorite()
    {
    }

    public DinerFavorite(Guid dinerUserId, Guid branchId, DateTime atUtc)
        : base(Guid.CreateVersion7())
    {
        DinerUserId = Guard.NotEmpty(dinerUserId, nameof(dinerUserId));
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        StampCreatedAt(atUtc);
    }
}
