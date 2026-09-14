using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Domain.Enums;

namespace Yalla.Application.Public;

/// <summary>
/// One branch on the diner app's Explore list, search results and map.
/// </summary>
/// <remarks>
/// Named <c>PublicBranchListing</c> because <c>PublicBranchCard</c> is already the branch row nested
/// under a venue on <c>GET /api/public/venues</c>, and changing that shape would break the web chooser.
/// </remarks>
/// <param name="BranchId">The branch id every other route takes. The app's <c>Place.id</c>.</param>
/// <param name="VenueId">The venue's id.</param>
/// <param name="VenueSlug">The venue half of the public link.</param>
/// <param name="BranchSlug">The branch half.</param>
/// <param name="VenueName">The brand name, what the card is titled with.</param>
/// <param name="BranchName">Which location, e.g. "Cascade".</param>
/// <param name="VenueType">1 Cafe, 2 Restaurant.</param>
/// <param name="Cuisine">Free text from the venue, or absent when not set.</param>
/// <param name="PriceLevel">1-4, or absent when not set.</param>
/// <param name="Address">Street address.</param>
/// <param name="Latitude">WGS84.</param>
/// <param name="Longitude">WGS84.</param>
/// <param name="DistanceKm">From the <c>lat</c>/<c>lng</c> the caller sent, to 0.1 km. Absent when none was sent.</param>
/// <param name="TimeZoneId">The branch's IANA zone.</param>
/// <param name="IsOpenNow">Inside an opening block now, in the branch's zone.</param>
/// <param name="FreeTableCount">Active tables with nobody at them.</param>
/// <param name="Rating">Average stars to one decimal; absent when nobody has reviewed it.</param>
/// <param name="ReviewCount">How many reviews.</param>
/// <param name="Badges"><c>"popular"</c> and/or <c>"new"</c>, derived - see <c>BranchBadgeRules</c>.</param>
/// <param name="CoverPhoto">The hero picture, or absent.</param>
public sealed record PublicBranchListing(
    Guid BranchId,
    Guid VenueId,
    string VenueSlug,
    string BranchSlug,
    string VenueName,
    string BranchName,
    VenueType VenueType,
    string? Cuisine,
    int? PriceLevel,
    string Address,
    double Latitude,
    double Longitude,
    double? DistanceKm,
    string TimeZoneId,
    bool IsOpenNow,
    int FreeTableCount,
    double? Rating,
    int ReviewCount,
    IReadOnlyList<string> Badges,
    PhotoView? CoverPhoto);

/// <summary>
/// One branch's details screen: everything on the card, and the rest.
/// </summary>
/// <param name="Listing">The card, exactly as the list serves it.</param>
/// <param name="About">A paragraph from the venue, or absent.</param>
/// <param name="WebsiteUrl">http(s), or absent.</param>
/// <param name="PhoneE164">Published contact number, or absent.</param>
/// <param name="Amenities">Keys: <c>outdoorSeating</c>, <c>wifi</c>, <c>parking</c>, <c>cardPayment</c>, <c>vegan</c>.</param>
/// <param name="OpeningHours">Weekly wall-clock hours; <c>day</c> 0 = Sunday.</param>
/// <param name="Gallery">Pictures beyond the cover, in the venue's order.</param>
/// <param name="TableCount">Active tables on the plan.</param>
/// <param name="AcceptsWebBookings">Whether the public page offers booking.</param>
/// <param name="AcceptsAppBookings">
/// Whether the diner app may book here: online bookings switched on <b>and</b> the reservation policy
/// saved by somebody at the venue (K9). False means hide the booking button.
/// </param>
/// <param name="RecentReviews">The newest three published reviews, by when they were first written. The rest are paged on the reviews route.</param>
/// <param name="TableMarkers">Tables placed on the cover photo, with their live state.</param>
/// <param name="AsOfUtc">When the live half was read.</param>
public sealed record PublicBranchDetail(
    PublicBranchListing Listing,
    string? About,
    string? WebsiteUrl,
    string? PhoneE164,
    IReadOnlyList<string> Amenities,
    IReadOnlyList<OpeningHoursView> OpeningHours,
    IReadOnlyList<PhotoView> Gallery,
    int TableCount,
    bool AcceptsWebBookings,
    bool AcceptsAppBookings,
    IReadOnlyList<PublicReviewView> RecentReviews,
    IReadOnlyList<PublicTableMarker> TableMarkers,
    DateTime AsOfUtc);

/// <summary>One published review.</summary>
/// <param name="ReviewId">The review's id.</param>
/// <param name="AuthorName">
/// "Anahit S." - a first name and an initial, "Yalla diner" when the name cannot be shown. Never the
/// full name, a number, an email or any id. See <c>BranchReview.PublicAuthorName</c>.
/// </param>
/// <param name="Rating">1-5.</param>
/// <param name="Text">Absent when the diner left stars only.</param>
/// <param name="CreatedAtUtc">First written. The list is ordered by this, newest first.</param>
/// <param name="UpdatedAtUtc">When the rating or text last changed.</param>
/// <param name="Edited">Whether it was changed after it was first written.</param>
public sealed record PublicReviewView(
    Guid ReviewId,
    string AuthorName,
    int Rating,
    string? Text,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool Edited);

/// <summary>A page of a branch's published reviews, newest first by when each was written.</summary>
public sealed record PublicReviewPage(
    Guid BranchId,
    double? Rating,
    int ReviewCount,
    int Page,
    int PageSize,
    IReadOnlyList<PublicReviewView> Reviews);

/// <summary>
/// One table drawn on the branch's cover photo.
/// </summary>
/// <param name="TableId">The floor-plan table id the booking flow takes.</param>
/// <param name="Label">What is printed on it.</param>
/// <param name="Seats">How many it seats. There is no minimum party size in the model.</param>
/// <param name="IsBookable">False for walk-in-only seats.</param>
/// <param name="State">1 Free, 2 ReservedSoon, 3 Held, 4 Occupied, 5 OutOfService - as the floor plan derives it now.</param>
/// <param name="PhotoX">0-1 across the photo.</param>
/// <param name="PhotoY">0-1 down the photo.</param>
public sealed record PublicTableMarker(
    Guid TableId,
    string Label,
    int Seats,
    bool IsBookable,
    DerivedTableState State,
    double PhotoX,
    double PhotoY);

/// <summary>The live markers, and the photo they are drawn on.</summary>
public sealed record PublicTableMarkers(
    Guid BranchId,
    PhotoView? Photo,
    DateTime AsOfUtc,
    IReadOnlyList<PublicTableMarker> Tables);

/// <summary>What Explore, search and the map ask for.</summary>
/// <param name="Query">Contains-match on venue name, branch name, cuisine and address, case-insensitive. Blank means everything.</param>
/// <param name="Category">Only this venue type.</param>
/// <param name="Latitude">The diner's position, to compute <c>distanceKm</c> and sort nearest first.</param>
/// <param name="Longitude">Sent together with <paramref name="Latitude"/> or not at all.</param>
public sealed record BranchSearchRequest(
    string? Query = null,
    VenueType? Category = null,
    double? Latitude = null,
    double? Longitude = null);

/// <summary>
/// The diner app's browse reads: list, search, details, reviews, table markers.
/// </summary>
/// <remarks>
/// The same published-branch rule as <see cref="IPublicVenueQuery"/>: a branch that is inactive, or
/// whose venue is suspended or deleted, is absent from lists and a 404 from the id routes.
/// </remarks>
public interface IPublicListingQuery
{
    /// <summary>Published branches matching the request. Nearest first with a position, else best rated.</summary>
    /// <exception cref="ArgumentException">A position half sent, out of range, or a query over 100 characters.</exception>
    Task<IReadOnlyList<PublicBranchListing>> SearchAsync(
        BranchSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <exception cref="KeyNotFoundException">No such published branch.</exception>
    Task<PublicBranchDetail> GetDetailAsync(
        Guid branchId,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default);

    /// <exception cref="KeyNotFoundException">No such published branch.</exception>
    Task<PublicReviewPage> GetReviewsAsync(Guid branchId, int page, CancellationToken cancellationToken = default);

    /// <exception cref="KeyNotFoundException">No such published branch.</exception>
    Task<PublicTableMarkers> GetTableMarkersAsync(Guid branchId, CancellationToken cancellationToken = default);
}
