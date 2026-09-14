using Yalla.Application.Media;

namespace Yalla.Application.BranchSettings;

/// <summary>What a branch says about itself on the diner app's browse screens.</summary>
/// <param name="Cuisine">The cuisine line, or absent.</param>
/// <param name="About">A paragraph, or absent.</param>
/// <param name="PriceLevel">1-4, or absent.</param>
/// <param name="WebsiteUrl">http(s), or absent.</param>
/// <param name="Amenities">Amenity keys.</param>
/// <param name="Address">Street address.</param>
/// <param name="Latitude">WGS84.</param>
/// <param name="Longitude">WGS84.</param>
/// <param name="Gallery">Pictures beyond the cover, in order.</param>
public sealed record BranchListingView(
    string? Cuisine,
    string? About,
    int? PriceLevel,
    string? WebsiteUrl,
    IReadOnlyList<string> Amenities,
    string Address,
    double Latitude,
    double Longitude,
    IReadOnlyList<PhotoView> Gallery);

/// <summary>
/// The listing form, as written.
/// </summary>
/// <param name="Cuisine">Up to 120 characters. Null or blank clears it.</param>
/// <param name="About">Up to 2000 characters. Null or blank clears it.</param>
/// <param name="PriceLevel">1-4, or null to clear.</param>
/// <param name="WebsiteUrl">An absolute http(s) URL, or null to clear.</param>
/// <param name="Amenities">
/// Keys from <c>outdoorSeating</c>, <c>wifi</c>, <c>parking</c>, <c>cardPayment</c>, <c>vegan</c>.
/// Null or empty clears them.
/// </param>
/// <param name="GalleryPhotoIds">
/// Photos uploaded for <b>this</b> branch, in display order, at most 12. <b>Null leaves the gallery
/// as it is</b>; an empty list clears it.
/// </param>
/// <param name="Address">A new street address; only applied together with coordinates.</param>
/// <param name="Latitude">Sent with <paramref name="Longitude"/> to move the pin, or neither.</param>
/// <param name="Longitude">Sent with <paramref name="Latitude"/>.</param>
public sealed record BranchListingCommand(
    string? Cuisine = null,
    string? About = null,
    int? PriceLevel = null,
    string? WebsiteUrl = null,
    IReadOnlyList<string>? Amenities = null,
    IReadOnlyList<Guid>? GalleryPhotoIds = null,
    string? Address = null,
    double? Latitude = null,
    double? Longitude = null);

/// <summary>Reads and writes a branch's listing.</summary>
public interface IBranchListingService
{
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    Task<BranchListingView> GetAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <param name="branchId">The branch.</param>
    /// <param name="command">The form.</param>
    /// <param name="signedInToAdminPanel">
    /// Whether the caller holds an admin-panel sign-in rather than a PIN session on a tablet. Moving
    /// the branch needs one: only an owner or a platform admin signed in to the panel may relocate.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="KeyNotFoundException">No such branch, or a gallery photo not uploaded for it.</exception>
    /// <exception cref="Yalla.Domain.FieldValidationException">Every field that broke a rule.</exception>
    /// <exception cref="RelocationNotAllowedException">The form moves the branch and the caller may not.</exception>
    Task<BranchListingView> UpdateAsync(
        Guid branchId,
        BranchListingCommand command,
        bool signedInToAdminPanel = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The listing form moves the branch - a different address or map pin - and the caller is not an
/// owner or platform admin signed in to the admin panel. Answers 403 <c>relocation-not-allowed</c>;
/// nothing on the form is saved.
/// </summary>
/// <remarks>
/// Where a branch is, is what every diner walks to. A manager correcting a typo in the cuisine line
/// should not be one mis-drag of the pin away from sending the city to the wrong street, so moving
/// it is the owner's call, and it is audited.
/// </remarks>
public sealed class RelocationNotAllowedException(Guid branchId)
    : Exception("Only the owner can move the branch's address or map pin. Nothing was saved.")
{
    /// <summary>The branch the form was for.</summary>
    public Guid BranchId { get; } = branchId;
}
