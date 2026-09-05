using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Media;
using Yalla.Domain.Enums;
using Yalla.Domain.Media;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Uploading photos, serving them back, and deleting the ones nothing ever used.
/// </summary>
/// <remarks>
/// The storage backend is behind <see cref="IPhotoStorage"/>; this is the part that decides who may
/// upload, which branch owns the result, and when a photo has been abandoned. None of that changes
/// when the bytes move off local disk.
/// </remarks>
internal sealed class PhotoService(
    YallaDbContext db,
    IPhotoStorage storage,
    IClock clock,
    ICurrentActor actor,
    ILogger<PhotoService> logger) : IPhotoService
{
    /// <summary>
    /// How long a photo may sit unattached before the sweep takes it.
    /// </summary>
    /// <remarks>
    /// Long enough that somebody uploading a picture, going to lunch and coming back to finish the
    /// menu item still has it. Short enough that a venue's failed attempts do not accumulate for a
    /// year.
    /// </remarks>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(24);

    public async Task<PhotoUploadResult> UploadAsync(
        Guid branchId,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireManager("Upload a photo");

        if (!await db.Branches.AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw new KeyNotFoundException($"Branch {branchId} was not found.");
        }

        // Validation, EXIF stripping and the three variants all happen in here. There is no path
        // that stores the bytes as they arrived.
        var stored = await storage.SaveAsync(branchId, content, contentType, cancellationToken);

        // Identical bytes for this branch reuse the row. The files are already on disk under the
        // same hash, so writing a second row would give two ids pointing at one set of files - and
        // deleting either would break the other.
        var existing = await db.Photos
            .FirstOrDefaultAsync(
                p => p.BranchId == branchId && p.ContentHash == stored.ContentHash, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Photo {ContentHash} already existed for branch {BranchId}; returning {PhotoId}.",
                stored.ContentHash, branchId, existing.Id);

            return new PhotoUploadResult(
                PhotoView.From(existing), stored.ContentHash, existing.BytesStored, WasDeduplicated: true);
        }

        var photo = new Photo(
            branchId,
            stored.ContentHash,
            stored.ThumbnailPath,
            stored.CardPath,
            stored.FullPath,
            stored.Width,
            stored.Height,
            stored.BytesStored,
            clock.UtcNow,
            staffId);

        db.Photos.Add(photo);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Photo {PhotoId} ({Width}x{Height}, {Bytes} bytes) uploaded for branch {BranchId} by staff {StaffId}.",
            photo.Id, photo.Width, photo.Height, photo.BytesStored, branchId, staffId);

        return new PhotoUploadResult(
            PhotoView.From(photo), stored.ContentHash, stored.BytesStored, stored.WasDeduplicated);
    }

    public async Task<(Stream Content, string ContentType)> OpenVariantAsync(
        Guid photoId,
        string variant,
        CancellationToken cancellationToken = default)
    {
        var photo = await db.Photos
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == photoId, cancellationToken)
            ?? throw new KeyNotFoundException($"Photo {photoId} was not found.");

        if (photo.IsExternallyHosted)
        {
            // Nothing to stream: these are somebody else's URLs, migrated from the old column. The
            // read model gives the client the URL directly and never points it here.
            throw new KeyNotFoundException(
                $"Photo {photoId} is hosted elsewhere and is not served from this API.");
        }

        var path = variant.ToLowerInvariant() switch
        {
            "thumbnail" => photo.ThumbnailPath,
            "card" => photo.CardPath,
            "full" => photo.FullPath,
            _ => throw new KeyNotFoundException(
                $"'{variant}' is not a photo variant. Ask for thumbnail, card or full."),
        };

        // Every stored variant is WebP - one format, one decoder path in every client.
        return (await storage.OpenAsync(path, cancellationToken), "image/webp");
    }

    public async Task<int> SweepOrphansAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.UtcNow - OrphanGrace;

        // Nothing references it, and it has been sitting there long enough. Both halves matter: a
        // photo attached the moment it was uploaded is old and in use, and one uploaded a minute ago
        // is unattached and still being worked on.
        var orphans = await db.Photos
            .Where(p => p.UploadedAtUtc < cutoff)
            .Where(p => !db.MenuItems.Any(i => i.PhotoId == p.Id))
            .Where(p => !db.Branches.Any(b => b.CoverPhotoId == p.Id))
            .ToListAsync(cancellationToken);

        if (orphans.Count == 0)
        {
            return 0;
        }

        foreach (var photo in orphans)
        {
            // Files first. A row deleted with its files left behind is a leak nothing will ever find
            // again; files deleted with the row left behind is a broken image somebody reports.
            if (!photo.IsExternallyHosted)
            {
                foreach (var path in new[] { photo.ThumbnailPath, photo.CardPath, photo.FullPath })
                {
                    try
                    {
                        await storage.DeleteAsync(path, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Keep going. One stuck file must not stop the sweep, and the row is worth
                        // removing either way - it references nothing anybody can reach.
                        logger.LogWarning(
                            ex, "Could not delete {Path} while sweeping photo {PhotoId}.", path, photo.Id);
                    }
                }
            }

            db.Photos.Remove(photo);
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Swept {Count} photo(s) that nothing referenced and were older than {Hours} hours.",
            orphans.Count, OrphanGrace.TotalHours);

        return orphans.Count;
    }

    private Guid RequireManager(string operation)
    {
        if (actor.Type != ActorType.Staff || actor.StaffMemberId is not { } staffId)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        if (actor.Role is not (StaffRole.Manager or StaffRole.Owner or StaffRole.PlatformAdmin))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        return staffId;
    }
}
