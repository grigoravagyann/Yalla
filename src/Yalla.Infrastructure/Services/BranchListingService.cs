using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The listing form: cuisine, about, price level, website, amenities, map pin and gallery.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may do what (K4, K5).</b> Reading and saving need an owner or manager who covers the branch
/// - checked again from the stored row through <see cref="IStaffBranchGuard"/>. <b>Moving</b> the
/// branch - an address, latitude or longitude sent and different from what is stored - needs more:
/// an active owner of this venue or a platform admin, signed in to the admin panel. Anyone else gets
/// <see cref="RelocationNotAllowedException"/> and nothing on the form is written. Sending the stored
/// location back unchanged, which the console does on every save, is not a move.
/// </para>
/// <para>
/// A move is audited as <c>branch.relocate</c>, with the old and new address and coordinates.
/// </para>
/// </remarks>
internal sealed class BranchListingService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IStaffBranchGuard branchGuard,
    ILogger<BranchListingService> logger) : IBranchListingService
{
    /// <summary>The audit action a relocation writes.</summary>
    public const string RelocateAuditAction = "branch.relocate";

    public async Task<BranchListingView> GetAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the listing", cancellationToken);

        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        return await ViewAsync(branch, cancellationToken);
    }

    public async Task<BranchListingView> UpdateAsync(
        Guid branchId,
        BranchListingCommand command,
        bool signedInToAdminPanel = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var staffId = await branchGuard.RequireAtBranchAsync(branchId, "Save the listing", cancellationToken);

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        // Everything is checked before anything is changed, so a refusal leaves the listing as it was.
        var address = string.IsNullOrWhiteSpace(command.Address) ? null : command.Address.Trim();

        var moves = (command.Latitude is { } latitude && !latitude.Equals(branch.Latitude))
                    || (command.Longitude is { } longitude && !longitude.Equals(branch.Longitude))
                    || (address is not null && !string.Equals(address, branch.Address, StringComparison.Ordinal));

        // Before the field rules: a manager who is not allowed to move the branch is told that, rather
        // than being asked to complete a move they will then be refused.
        if (moves)
        {
            await RequireMayRelocateAsync(branch, signedInToAdminPanel, cancellationToken);
        }

        ValidateLocation(command, address);

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

        if (moves)
        {
            // ValidateLocation has already refused a move without both coordinates and an address.
            var before = new { address = branch.Address, latitude = branch.Latitude, longitude = branch.Longitude };

            branch.Relocate(address!, command.Latitude!.Value, command.Longitude!.Value);

            // Added to this unit of work, so the move and its record commit together or not at all.
            PlatformAudit.Record(db, actor, clock, RelocateAuditAction, "Branch", branch.Id, new
            {
                old = before,
                @new = new { address = branch.Address, latitude = branch.Latitude, longitude = branch.Longitude },
            });
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

        if (moves)
        {
            logger.LogWarning("Branch {BranchId} relocated by staff member {StaffMemberId}.", branchId, staffId);
        }

        logger.LogInformation(
            "Branch {BranchId} listing saved by staff member {StaffMemberId}; {Count} gallery photo(s).",
            branchId, staffId, gallery?.Count.ToString() ?? "unchanged");

        return await ViewAsync(branch, cancellationToken);
    }

    /// <summary>
    /// Coordinates and an address travel together: a pin with no street, or a street with no pin, is
    /// a diner walking to the wrong place.
    /// </summary>
    /// <exception cref="FieldValidationException">Every location field that broke a rule.</exception>
    private static void ValidateLocation(BranchListingCommand command, string? address)
    {
        var violations = new List<FieldViolation>();

        if (command.Latitude.HasValue != command.Longitude.HasValue)
        {
            violations.Add(new FieldViolation(
                command.Latitude.HasValue ? "longitude" : "latitude",
                "Send latitude and longitude together to move the pin, or neither.",
                FieldBounds.Required));
        }

        var coordinates = command.Latitude.HasValue && command.Longitude.HasValue;

        if (coordinates && address is null)
        {
            violations.Add(new FieldViolation(
                "address", "Send the street address together with the map pin.", FieldBounds.Required));
        }

        if (address is not null && !command.Latitude.HasValue && !command.Longitude.HasValue)
        {
            foreach (var field in new[] { "latitude", "longitude" })
            {
                violations.Add(new FieldViolation(
                    field, "Send the map pin together with the street address.", FieldBounds.Required));
            }
        }

        if (address is { Length: > FieldLengths.Address })
        {
            violations.Add(new FieldViolation(
                "address",
                $"At most {FieldLengths.Address} characters.",
                FieldBounds.Max,
                Max: FieldLengths.Address,
                Value: address.Length));
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }
    }

    /// <summary>
    /// An active owner of this branch's venue, or a platform admin, signed in to the admin panel.
    /// </summary>
    /// <remarks>
    /// Read from the stored row, not the token: an owner demoted or deactivated this morning still
    /// holds a token that says Owner.
    /// </remarks>
    /// <exception cref="RelocationNotAllowedException">Anyone else.</exception>
    private async Task RequireMayRelocateAsync(Branch branch, bool signedInToAdminPanel, CancellationToken cancellationToken)
    {
        if (signedInToAdminPanel && actor.StaffMemberId is { } staffId)
        {
            var staff = await db.StaffMembers
                .AsNoTracking()
                .Where(s => s.Id == staffId)
                .Select(s => new { s.Role, s.VenueId, s.IsActive })
                .FirstOrDefaultAsync(cancellationToken);

            if (staff is { IsActive: true }
                && (staff.Role == StaffRole.PlatformAdmin
                    || (staff.Role == StaffRole.Owner && staff.VenueId == branch.VenueId)))
            {
                return;
            }
        }

        logger.LogWarning(
            "Relocating branch {BranchId} refused for staff member {StaffMemberId}.", branch.Id, actor.StaffMemberId);

        throw new RelocationNotAllowedException(branch.Id);
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
