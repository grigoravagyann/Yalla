using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Media;

/// <summary>
/// One uploaded image, stored as three variants and never as the bytes that arrived.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row, not a string.</b> Menu items carried a <c>PhotoUrl</c> until Prompt 9, which meant a
/// venue could not be onboarded at all: the field was required and nothing could produce a value for
/// it. It also meant nothing knew where an image lived, how big it was, who uploaded it, or whether
/// anything still referenced it - so a photo could never be swept and a storage move would have been
/// a string-rewriting exercise across the database.
/// </para>
/// <para>
/// <b>The original bytes are never stored.</b> They are decoded, stripped of metadata and re-encoded
/// into the three variants. A menu photo comes off the owner's phone carrying GPS coordinates and a
/// device serial, and a restaurant should not publish its owner's home address because they
/// photographed a khachapuri.
/// </para>
/// <para>
/// <see cref="ContentHash"/> sits in the storage path, which buys two things for nothing: replacing
/// an image never needs a cache bust, because the new one lives at a different path; and uploading
/// the same bytes twice writes no second copy.
/// </para>
/// </remarks>
public sealed class Photo : Entity
{
    /// <summary>
    /// The branch that owns it. Also the first path segment.
    /// </summary>
    /// <remarks>
    /// A prefix rather than a flat namespace, so an object store maps onto the same keys later with
    /// nothing to rethink - and so one venue's images can be enumerated, counted or deleted without
    /// a database round trip.
    /// </remarks>
    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    /// <summary>SHA-256 of the <i>processed</i> bytes, lowercase hex. Part of every variant's path.</summary>
    public string ContentHash { get; private set; } = null!;

    /// <summary>Small square, for lists and the floor screen.</summary>
    public string ThumbnailPath { get; private set; } = null!;

    /// <summary>The menu card size, which is what a diner actually looks at.</summary>
    public string CardPath { get; private set; } = null!;

    /// <summary>The largest kept. Not the original - see the type's remarks.</summary>
    public string FullPath { get; private set; } = null!;

    /// <summary>
    /// Pixel width of the full variant. Null for a photo migrated from the old string column, whose
    /// bytes this system has never seen.
    /// </summary>
    public int? Width { get; private set; }

    /// <summary>Pixel height of the full variant. Null for a migrated one - see <see cref="Width"/>.</summary>
    public int? Height { get; private set; }

    /// <summary>Total bytes across all three variants. Zero for a migrated one.</summary>
    public long BytesStored { get; private set; }

    /// <summary>
    /// True when the variants are absolute URLs somebody else hosts rather than keys in our storage.
    /// </summary>
    /// <remarks>
    /// Only ever true for a row migrated from the old <c>PhotoUrl</c> column. Those images were never
    /// uploaded here, so there are no bytes to stream, no EXIF that was stripped and no variants that
    /// were generated - the read model hands the URL to the client unchanged and the streaming
    /// endpoint refuses. Every photo uploaded through this system is false, and the flag exists so
    /// the difference is visible rather than inferred from whether a path starts with "http".
    /// </remarks>
    public bool IsExternallyHosted { get; private set; }

    /// <summary>Who uploaded it. Null for one migrated from the old string column.</summary>
    public Guid? UploadedByStaffId { get; private set; }

    public DateTime UploadedAtUtc { get; private set; }

    private Photo()
    {
    }

    public Photo(
        Guid branchId,
        string contentHash,
        string thumbnailPath,
        string cardPath,
        string fullPath,
        int width,
        int height,
        long bytesStored,
        DateTime uploadedAtUtc,
        Guid? uploadedByStaffId = null)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        ContentHash = Guard.NotBlank(contentHash, nameof(contentHash), FieldLengths.ContentHash);
        ThumbnailPath = Guard.NotBlank(thumbnailPath, nameof(thumbnailPath), FieldLengths.Url);
        CardPath = Guard.NotBlank(cardPath, nameof(cardPath), FieldLengths.Url);
        FullPath = Guard.NotBlank(fullPath, nameof(fullPath), FieldLengths.Url);
        Width = Guard.Positive(width, nameof(width));
        Height = Guard.Positive(height, nameof(height));
        IsExternallyHosted = false;
        BytesStored = Guard.NotNegativeAmd(bytesStored, nameof(bytesStored));
        UploadedByStaffId = uploadedByStaffId;
        UploadedAtUtc = Guard.NotLocalTime(uploadedAtUtc, nameof(uploadedAtUtc));
        StampCreatedAt(uploadedAtUtc);
    }

    /// <summary>
    /// A row standing in for an image that was only ever a URL in the old schema.
    /// </summary>
    /// <remarks>
    /// The alternative was dropping those URLs on migration, which would have blanked the photo off
    /// every menu item that had one. The hash is of the URL rather than of any bytes, so two items
    /// pointing at the same picture share a row exactly as two identical uploads would.
    /// </remarks>
    public static Photo ExternallyHosted(Guid branchId, string url, string contentHash, DateTime atUtc) =>
        new(branchId, url, contentHash, atUtc);

    /// <summary>
    /// The migrated-row constructor. Private, and reached only through
    /// <see cref="ExternallyHosted"/>, so nothing can create one of these by accident.
    /// </summary>
    /// <remarks>
    /// An object initializer would have been shorter and would have left <c>Id</c> empty, because the
    /// parameterless constructor EF materialises through does not assign one. Going through the base
    /// constructor is what gives the row a key.
    /// </remarks>
    private Photo(Guid branchId, string url, string contentHash, DateTime atUtc)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        ContentHash = Guard.NotBlank(contentHash, nameof(contentHash), FieldLengths.ContentHash);
        ThumbnailPath = Guard.NotBlank(url, nameof(url), FieldLengths.Url);
        CardPath = ThumbnailPath;
        FullPath = ThumbnailPath;
        Width = null;
        Height = null;
        BytesStored = 0L;
        IsExternallyHosted = true;
        UploadedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        StampCreatedAt(atUtc);
    }

    /// <summary>Whether this has been sitting unattached long enough for the sweep to take it.</summary>
    /// <remarks>
    /// Age alone is never enough - the sweep also checks that nothing references it. A photo attached
    /// to a menu item on the day it was uploaded is old and in use, and those are different questions.
    /// </remarks>
    public bool IsOlderThan(DateTime nowUtc, TimeSpan age) => nowUtc - UploadedAtUtc > age;
}
