using Yalla.Domain.Common;

namespace Yalla.Domain.Venues;

/// <summary>
/// One picture in a branch's gallery, beyond its cover, in the order the venue chose.
/// </summary>
/// <remarks>
/// A row per picture rather than a list of ids on the branch, so the photo is a real foreign key:
/// the orphan sweep can see it is in use, and a picture a gallery shows cannot be deleted out from
/// under it.
/// </remarks>
public sealed class BranchGalleryPhoto : Entity
{
    /// <summary>How many pictures a gallery holds. A phone swipes through a dozen; nobody swipes through fifty.</summary>
    public const int MaxPhotos = 12;

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public Guid PhotoId { get; private set; }

    public Media.Photo Photo { get; private set; } = null!;

    /// <summary>Zero-based place in the gallery, lowest first.</summary>
    public int Position { get; private set; }

    private BranchGalleryPhoto()
    {
    }

    public BranchGalleryPhoto(Guid branchId, Guid photoId, int position)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        PhotoId = Guard.NotEmpty(photoId, nameof(photoId));
        Position = Guard.NotNegative(position, nameof(position));
    }
}
