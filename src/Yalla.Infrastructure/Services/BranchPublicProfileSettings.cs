using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The public page's settings: the phone, the web-bookings switch and the card's cover photo.
/// </summary>
internal sealed class BranchPublicProfileSettings(
    YallaDbContext db,
    IStaffBranchGuard branchGuard,
    ILogger logger,
    BranchSettingsLookups lookups)
{
    public async Task<PublicProfileView> GetPublicProfileAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the public profile", cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        return await PublicProfileOfAsync(branch, cancellationToken);
    }

    public async Task<PublicProfileView> UpdatePublicProfileAsync(
        Guid branchId,
        PublicProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Save the public profile", cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        // The photo id is caller-supplied and not secret - one venue's card URL names it. Checked
        // before anything is written, so a foreign id refuses the whole form and leaves the old
        // phone and switch as they were. Refused the way a menu item refuses a foreign photo: the
        // id is simply not found at this branch.
        if (command.CoverPhotoId is { } photoId
            && !await db.Photos.AnyAsync(p => p.Id == photoId && p.BranchId == branchId, cancellationToken))
        {
            throw new KeyNotFoundException($"Photo {photoId} was not found at this branch.");
        }

        // The number first: a bad one must refuse the whole form rather than leave the branch
        // switched on for bookings with its old phone still published.
        branch.SetPhoneE164(command.PhoneE164);
        branch.SetAcceptsWebBookings(command.AcceptsWebBookings);

        // The card's picture only. Tables are pinned on room photos, not on the cover, so changing it
        // moves nothing else.
        branch.SetCoverPhoto(command.CoverPhotoId);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Branch {BranchId} public profile saved; web bookings {State}; cover photo {Cover}.",
            branch.Id, command.AcceptsWebBookings ? "on" : "off", command.CoverPhotoId?.ToString() ?? "none");

        return await PublicProfileOfAsync(branch, cancellationToken);
    }

    /// <summary>
    /// The branch's public settings with its cover photo loaded - the branch row alone carries only
    /// the id, and a view built from that would report the card as having no picture.
    /// </summary>
    private async Task<PublicProfileView> PublicProfileOfAsync(Branch branch, CancellationToken cancellationToken)
    {
        var cover = branch.CoverPhotoId is { } photoId
            ? await db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == photoId, cancellationToken)
            : null;

        return new PublicProfileView(
            branch.PhoneE164,
            branch.AcceptsWebBookings,
            cover is null ? null : PhotoView.From(cover));
    }
}
