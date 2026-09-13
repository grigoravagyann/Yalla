using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Domain;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The listing form: cuisine, about, price level, website, amenities, map pin and gallery.
/// </summary>
internal sealed class BranchListingService(
    YallaDbContext db,
    ILogger<BranchListingService> logger) : IBranchListingService
{
    public async Task<BranchListingView> GetAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        return await ViewAsync(branch, cancellationToken);
    }

    public async Task<BranchListingView> UpdateAsync(
        Guid branchId,
        BranchListingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        // Everything is checked before anything is changed, so a refusal leaves the listing as it was.
        if (command.Latitude.HasValue != command.Longitude.HasValue)
        {
            throw new FieldValidationException(new FieldViolation(
                command.Latitude.HasValue ? "longitude" : "latitude",
                "Send latitude and longitude together to move the pin, or neither.",
                FieldBounds.Required));
        }

        var gallery = command.GalleryPhotoIds;

        if (gallery is not null)
        {
            if (gallery.Count > BranchGalleryPhoto.MaxPhotos)
            {
                throw new FieldValidationException(new FieldViolation(
                    "galleryPhotoIds",
                    $"A gallery holds at most {BranchGalleryPhoto.MaxPhotos} photos.",
                    FieldBounds.Max,
                    Max: BranchGalleryPhoto.MaxPhotos,
                    Value: gallery.Count));
            }

            if (gallery.Distinct().Count() != gallery.Count)
            {
                throw new FieldValidationException(new FieldViolation(
                    "galleryPhotoIds", "A photo appears in the gallery more than once.", FieldBounds.Conflict));
            }

            // Refused the way a menu item refuses a foreign photo: simply not found at this branch.
            var ours = await db.Photos.CountAsync(p => gallery.Contains(p.Id) && p.BranchId == branchId, cancellationToken);

            if (ours != gallery.Count)
            {
                throw new KeyNotFoundException("A gallery photo was not found at this branch.");
            }
        }

        branch.UpdateListing(command.Cuisine, command.About, command.PriceLevel, command.WebsiteUrl, command.Amenities);

        if (command.Latitude is { } latitude && command.Longitude is { } longitude)
        {
            branch.Relocate(
                string.IsNullOrWhiteSpace(command.Address) ? branch.Address : command.Address,
                latitude,
                longitude);
        }

        if (gallery is not null)
        {
            var current = await db.BranchGalleryPhotos.Where(g => g.BranchId == branchId).ToListAsync(cancellationToken);
            db.BranchGalleryPhotos.RemoveRange(current);

            for (var position = 0; position < gallery.Count; position++)
            {
                db.BranchGalleryPhotos.Add(new BranchGalleryPhoto(branchId, gallery[position], position));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Branch {BranchId} listing saved; {Count} gallery photo(s).", branchId, gallery?.Count.ToString() ?? "unchanged");

        return await ViewAsync(branch, cancellationToken);
    }

    private async Task<BranchListingView> ViewAsync(Branch branch, CancellationToken cancellationToken)
    {
        var photos = await db.BranchGalleryPhotos
            .AsNoTracking()
            .Where(g => g.BranchId == branch.Id)
            .OrderBy(g => g.Position)
            .Select(g => g.Photo)
            .ToListAsync(cancellationToken);

        return new BranchListingView(
            branch.Cuisine,
            branch.About,
            branch.PriceLevel,
            branch.WebsiteUrl,
            branch.Amenities,
            branch.Address,
            branch.Latitude,
            branch.Longitude,
            [.. photos.Select(PhotoView.From)]);
    }
}
