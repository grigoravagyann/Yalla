using Microsoft.Data.SqlClient;
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
    IStaffBranchGuard branchGuard,
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
        var staffId = await RequireActorAtBranchAsync("Upload a photo", branchId, cancellationToken);

        // Validation, EXIF stripping and the three variants all happen in here. There is no path
        // that stores the bytes as they arrived.
        var stored = await storage.SaveAsync(
            PhotoRules.OwnerKeyForBranch(branchId), content, contentType, cancellationToken);

        // Identical bytes for this branch reuse the row. The files are already on disk under the
        // same hash, so writing a second row would give two ids pointing at one set of files - and
        // deleting either would break the other.
        var existing = await db.Photos
            .FirstOrDefaultAsync(
                p => p.BranchId == branchId && p.ContentHash == stored.ContentHash, cancellationToken);

        if (existing is not null)
        {
            // Uploading it again is using it again: the grace period starts over, or a picture abandoned
            // yesterday and re-uploaded now could be swept before the editor saves it.
            existing.MarkUploaded(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);

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
        try
        {
            return (await storage.OpenAsync(path, cancellationToken), "image/webp");
        }
        catch (FileNotFoundException)
        {
            // The row outlived its bytes - a disk restored from an older backup, or a seed row that
            // never had any. To the client that is "no such picture", exactly what a missing row is;
            // a 500 here painted a broken image where a placeholder belongs.
            throw new KeyNotFoundException($"Photo {photoId} has no stored '{variant}' variant.");
        }
    }

    public async Task<int> SweepOrphansAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.UtcNow - OrphanGrace;

        // A list of candidates only: see Orphans for what makes one, and the loop for why each is
        // asked again at the moment it is deleted.
        var candidates = await Orphans(cutoff)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var swept = 0;

        foreach (var photo in candidates)
        {
            // The row first, one at a time, and only if it is still an orphan: the delete asks the
            // question again in the same statement. An editor can attach a photo - or upload it again,
            // which refreshes its age - between the list above and here, and that row is skipped with
            // its files untouched. Deleting every candidate's files first and the rows in one save at
            // the end meant one attach committing in between failed the save, kept every row, and left
            // all of them - the one now in use included - pointing at files that were gone.
            //
            // The price is the other order's failure: a crash between this delete and the files below
            // leaves files no row points at. A leak on disk, where the old order left a broken picture
            // on a live page.
            if (!await DeleteIfStillOrphanedAsync(photo.Id, cutoff, cancellationToken))
            {
                continue;
            }

            swept++;

            if (photo.IsExternallyHosted)
            {
                continue;
            }

            foreach (var path in new[] { photo.ThumbnailPath, photo.CardPath, photo.FullPath })
            {
                try
                {
                    await storage.DeleteAsync(path, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep going. One stuck file must not stop the sweep, and the row is already gone.
                    logger.LogWarning(
                        ex, "Could not delete {Path} while sweeping photo {PhotoId}.", path, photo.Id);
                }
            }
        }

        if (swept > 0)
        {
            logger.LogInformation(
                "Swept {Count} photo(s) that nothing referenced and were older than {Hours} hours.",
                swept, OrphanGrace.TotalHours);
        }

        return swept;
    }

    /// <summary>
    /// Photos nothing references that have been sitting there longer than the grace period.
    /// </summary>
    /// <remarks>
    /// Both halves matter: a photo attached the moment it was uploaded is old and in use, and one
    /// uploaded a minute ago is unattached and still being worked on. Four things can reference a
    /// photo - a dish, a venue's cover, its gallery, a diner's profile - and a fifth added without a
    /// line here would be swept out from under whatever it was attached to.
    /// </remarks>
    private IQueryable<Photo> Orphans(DateTime cutoff) =>
        db.Photos
            .Where(p => p.UploadedAtUtc < cutoff)
            .Where(p => !db.MenuItems.Any(i => i.PhotoId == p.Id))
            .Where(p => !db.Branches.Any(b => b.CoverPhotoId == p.Id))
            .Where(p => !db.BranchGalleryPhotos.Any(g => g.PhotoId == p.Id))
            .Where(p => !db.DinerUsers.Any(d => d.PhotoId == p.Id));

    /// <summary>
    /// Deletes one photo row in a single statement that re-checks it is still old and unreferenced.
    /// False when it no longer is, or was attached in the instant between that check and the delete
    /// (which the foreign key refuses).
    /// </summary>
    private async Task<bool> DeleteIfStillOrphanedAsync(Guid photoId, DateTime cutoff, CancellationToken cancellationToken)
    {
        try
        {
            return await Orphans(cutoff)
                .Where(p => p.Id == photoId)
                .ExecuteDeleteAsync(cancellationToken) > 0;
        }
        catch (Exception ex) when (IsReferenceViolation(ex))
        {
            logger.LogInformation("Photo {PhotoId} was attached while the sweep was deleting it; kept.", photoId);

            return false;
        }
    }

    /// <summary>SQL Server 547: the statement conflicted with a foreign key.</summary>
    private static bool IsReferenceViolation(Exception exception) =>
        (exception as SqlException ?? exception.InnerException as SqlException) is { Number: 547 };

    public async Task DeleteAsync(Guid photoId, CancellationToken cancellationToken = default)
    {
        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == photoId, cancellationToken);

        if (photo is null)
        {
            return;
        }

        // Files first, for the sweep's reason: if the row delete then fails, what is left is a row
        // whose variants answer 404 - "no such picture", which is true - rather than files nothing
        // will ever find again.
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
                    logger.LogWarning(ex, "Could not delete {Path} while deleting photo {PhotoId}.", path, photo.Id);
                }
            }
        }

        db.Photos.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted photo {PhotoId} and its files.", photo.Id);
    }

    /// <summary>
    /// The caller must be an active owner or manager who covers the branch (a manager with a home
    /// branch covers that one only), or a platform admin.
    /// </summary>
    /// <remarks>
    /// The route carries <c>BranchScoped</c>, which decides the same thing from the token. This is
    /// decided again from the stored row through <see cref="IStaffBranchGuard"/>, so that the lock
    /// holds if a route ever forgets the policy, and so that a deactivated or reassigned account's
    /// still-valid token is refused here rather than honoured until it expires.
    /// </remarks>
    private async Task<Guid> RequireActorAtBranchAsync(string operation, Guid branchId, CancellationToken cancellationToken)
    {
        var staffId = await branchGuard.RequireAtBranchAsync(branchId, operation, cancellationToken);

        // Only a platform admin can pass the guard for a branch that does not exist, and the photo
        // row's foreign key would refuse it with a 500. Say so instead.
        if (!await db.Branches.AsNoTracking().AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw new KeyNotFoundException($"Branch {branchId} was not found.");
        }

        return staffId;
    }
}
