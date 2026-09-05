namespace Yalla.Application.Abstractions;

/// <summary>Where the three variants of one photo ended up.</summary>
/// <param name="ContentHash">SHA-256 of the processed bytes, lowercase hex. Part of every path.</param>
/// <param name="ThumbnailPath">Small square, for lists.</param>
/// <param name="CardPath">Menu card size - what a diner actually looks at.</param>
/// <param name="FullPath">The largest kept. Never the bytes that arrived.</param>
/// <param name="Width">Pixel width of the full variant.</param>
/// <param name="Height">Pixel height of the full variant.</param>
/// <param name="BytesStored">Total across all three, so a venue's usage is answerable.</param>
/// <param name="WasDeduplicated">
/// True when these exact bytes were already stored and nothing new was written. Reported rather than
/// hidden: an upload that quietly wrote nothing is indistinguishable from one that failed.
/// </param>
public sealed record StoredPhoto(
    string ContentHash,
    string ThumbnailPath,
    string CardPath,
    string FullPath,
    int Width,
    int Height,
    long BytesStored,
    bool WasDeduplicated);

/// <summary>
/// Where photos live. One implementation today; the seam exists so there can be another.
/// </summary>
/// <remarks>
/// <para>
/// Local disk is the only backend in this task, and the point of the interface is that moving to an
/// object store later touches this file's implementations and nothing else. The paths it returns are
/// opaque keys, not URLs and not file-system paths as far as any caller is concerned - which is why
/// photos are served through an endpoint that streams from here rather than by a static-file mapping
/// pointed at a folder. The endpoint keeps working when the folder stops existing.
/// </para>
/// <para>
/// <b>Validation and processing happen behind <see cref="SaveAsync"/>, not in front of it.</b> A
/// caller hands over bytes and gets back a stored photo; it never sees the original, cannot choose
/// to keep it, and cannot skip the EXIF strip by calling a lower-level method, because there is not
/// one.
/// </para>
/// </remarks>
public interface IPhotoStorage
{
    /// <summary>
    /// Validates, strips metadata, produces the three variants and stores them.
    /// </summary>
    /// <exception cref="Domain.Media.UnsupportedImageException">
    /// The bytes are not a JPEG, PNG or WebP, or the image is too large. Decided by <b>sniffing the
    /// bytes</b> - the declared content type and the file name are both attacker-controlled.
    /// </exception>
    Task<StoredPhoto> SaveAsync(
        Guid branchId,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Opens one stored variant for streaming to a client.</summary>
    /// <exception cref="FileNotFoundException">No such path.</exception>
    Task<Stream> OpenAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Removes one stored variant. Missing is not an error - the end state is the same.</summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}
