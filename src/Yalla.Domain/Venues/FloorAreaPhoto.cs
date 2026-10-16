using Yalla.Domain.Common;

namespace Yalla.Domain.Venues;

/// <summary>
/// One picture of a room, in the order the venue chose. A diner swipes through them and taps a
/// table's pin on one to book it.
/// </summary>
/// <remarks>
/// <para>
/// A row per picture rather than a list of ids on the room, so the photo is a real foreign key: the
/// orphan sweep can see it is in use, and a picture a room shows cannot be deleted out from under
/// it. The same shape as <see cref="BranchGalleryPhoto"/>, one level down.
/// </para>
/// <para>
/// A photo belongs to at most one room of its branch - a table is pinned on a photo, and a photo in
/// two rooms would put the same table in both. The unique index is per room; the floor-plan save
/// refuses the cross-room case before it gets that far.
/// </para>
/// </remarks>
public sealed class FloorAreaPhoto : Entity
{
    /// <summary>
    /// How many pictures a room holds. Enough to show a large hall from every corner; more and the
    /// diner is swiping instead of booking.
    /// </summary>
    public const int MaxPhotos = 10;

    public Guid FloorAreaId { get; private set; }

    public FloorArea FloorArea { get; private set; } = null!;

    public Guid PhotoId { get; private set; }

    public Media.Photo Photo { get; private set; } = null!;

    /// <summary>Zero-based place in the room's photos, lowest first.</summary>
    public int Position { get; private set; }

    private FloorAreaPhoto()
    {
    }

    public FloorAreaPhoto(Guid floorAreaId, Guid photoId, int position)
        : base(Guid.CreateVersion7())
    {
        FloorAreaId = Guard.NotEmpty(floorAreaId, nameof(floorAreaId));
        PhotoId = Guard.NotEmpty(photoId, nameof(photoId));
        Position = Guard.NotNegative(position, nameof(position));
    }

    /// <summary>Moves the picture to another place in the room's order.</summary>
    public void MoveTo(int position) => Position = Guard.NotNegative(position, nameof(position));
}
