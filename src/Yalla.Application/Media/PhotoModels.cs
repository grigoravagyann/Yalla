using Yalla.Domain.Media;

namespace Yalla.Application.Media;

/// <summary>
/// One photo as a client consumes it: three URLs and the id behind them.
/// </summary>
/// <remarks>
/// <para>
/// URLs, not storage keys. A client should never see the layout of the storage backend, because the
/// point of <c>IPhotoStorage</c> is that the layout is going to change. What it gets is three links
/// it can put in an <c>img</c> tag.
/// </para>
/// <para>
/// A photo uploaded through this system serves from <c>/api/photos/{id}/{variant}</c>, streamed out
/// of storage. One migrated from the old <c>PhotoUrl</c> column serves from wherever it always did -
/// there are no bytes here to stream, and rewriting those URLs would have broken every menu that had
/// one.
/// </para>
/// </remarks>
/// <param name="PhotoId">The photo row.</param>
/// <param name="ThumbnailUrl">Small square, for lists and the floor screen.</param>
/// <param name="CardUrl">Menu card size - what a diner actually looks at.</param>
/// <param name="FullUrl">The largest kept.</param>
/// <param name="Width">Pixel width of the full variant. Null for a migrated photo.</param>
/// <param name="Height">Pixel height of the full variant. Null for a migrated photo.</param>
public sealed record PhotoView(
    Guid PhotoId,
    string ThumbnailUrl,
    string CardUrl,
    string FullUrl,
    int? Width,
    int? Height)
{
    /// <summary>The route the streaming endpoint serves each variant from.</summary>
    public const string RouteTemplate = "/api/photos";

    /// <summary>Turns a stored photo into the three links a client uses.</summary>
    public static PhotoView From(Photo photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        return photo.IsExternallyHosted
            ? new PhotoView(
                photo.Id, photo.ThumbnailPath, photo.CardPath, photo.FullPath, photo.Width, photo.Height)
            : new PhotoView(
                photo.Id,
                $"{RouteTemplate}/{photo.Id}/thumbnail",
                $"{RouteTemplate}/{photo.Id}/card",
                $"{RouteTemplate}/{photo.Id}/full",
                photo.Width,
                photo.Height);
    }

    /// <summary>
    /// The same, from the columns a projection selected rather than from a loaded entity.
    /// </summary>
    /// <remarks>
    /// The read paths project into anonymous types to stay one query, so they cannot call
    /// <see cref="From(Photo)"/>. Kept beside it so the two cannot drift.
    /// </remarks>
    public static PhotoView From(
        Guid photoId,
        bool isExternallyHosted,
        string thumbnailPath,
        string cardPath,
        string fullPath,
        int? width,
        int? height) =>
        isExternallyHosted
            ? new PhotoView(photoId, thumbnailPath, cardPath, fullPath, width, height)
            : new PhotoView(
                photoId,
                $"{RouteTemplate}/{photoId}/thumbnail",
                $"{RouteTemplate}/{photoId}/card",
                $"{RouteTemplate}/{photoId}/full",
                width,
                height);
}

/// <summary>What an upload produced.</summary>
/// <param name="Photo">The three links, ready to attach to a menu item or a branch.</param>
/// <param name="ContentHash">SHA-256 of the processed bytes, which is also part of the storage path.</param>
/// <param name="BytesStored">Total across the three variants.</param>
/// <param name="WasDeduplicated">
/// True when these exact bytes were already stored for this branch and the existing photo was
/// returned. Reported rather than hidden - an upload that wrote nothing looks like a failure.
/// </param>
public sealed record PhotoUploadResult(
    PhotoView Photo,
    string ContentHash,
    long BytesStored,
    bool WasDeduplicated);

/// <summary>Uploading photos, serving them, and sweeping the ones nothing kept.</summary>
public interface IPhotoService
{
    /// <summary>
    /// Stores an uploaded image against a branch. <b>Manager or above.</b>
    /// </summary>
    /// <exception cref="UnsupportedImageException">
    /// Not a JPEG, PNG or WebP, or too large. Decided by sniffing the bytes.
    /// </exception>
    Task<PhotoUploadResult> UploadAsync(
        Guid branchId,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Opens one variant for streaming, with the content type to serve it as.</summary>
    /// <exception cref="KeyNotFoundException">No such photo, or it is hosted elsewhere.</exception>
    Task<(Stream Content, string ContentType)> OpenVariantAsync(
        Guid photoId,
        string variant,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes photos nothing references that are older than the grace period, files included.
    /// </summary>
    /// <returns>How many were swept.</returns>
    Task<int> SweepOrphansAsync(CancellationToken cancellationToken = default);
}
