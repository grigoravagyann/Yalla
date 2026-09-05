using Yalla.Domain.Enums;

namespace Yalla.Application.Platform;

/// <summary>Create a venue together with its first branch. A venue with no branch is useless.</summary>
public sealed record CreateVenueCommand(
    string Name,
    VenueType Type,
    string Slug,
    CreateBranchCommand FirstBranch);

/// <summary>Add a branch. The tier is per branch - a chain with four locations pays four times.</summary>
public sealed record CreateBranchCommand(
    string Name,
    string Slug,
    string Address,
    double Latitude,
    double Longitude,
    string TimeZoneId,
    int FloorWidth,
    int FloorHeight,
    SubscriptionTier SubscriptionTier = SubscriptionTier.Free);

/// <summary>Patch a venue. Every field optional; only the ones supplied change.</summary>
public sealed record UpdateVenueCommand(
    string? Name = null,
    VenueType? Type = null,
    string? Slug = null,
    bool? IsActive = null);

/// <summary>Patch a branch. Every field optional; only the ones supplied change. Coordinates and address travel together.</summary>
public sealed record UpdateBranchCommand(
    string? Name = null,
    string? Address = null,
    double? Latitude = null,
    double? Longitude = null,
    string? TimeZoneId = null,
    int? FloorWidth = null,
    int? FloorHeight = null,
    bool? IsActive = null,
    SubscriptionTier? SubscriptionTier = null);

/// <summary>Search and paging for the venue list.</summary>
public sealed record VenueListQuery(string? Search = null, int Page = 1, int PageSize = 20, bool IncludeDeleted = false);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// One venue as the platform sees it, with the per-branch tier rolled up.
/// </summary>
/// <param name="VenueId">The venue.</param>
/// <param name="Name">Its name.</param>
/// <param name="Type">1 Cafe, 2 Restaurant. Selects the shipped policy defaults for a new branch.</param>
/// <param name="Slug">Unique URL segment for the brand.</param>
/// <param name="IsActive">Whether the venue is switched on at all.</param>
/// <param name="IsSuspended">Suspended venues keep every row but disappear from diner browsing.</param>
/// <param name="IsDeleted">Soft-deleted. Never a hard delete; the venue refuses every change.</param>
/// <param name="SuspendedAtUtc">When it was suspended, if it is.</param>
/// <param name="DeletedAtUtc">When it was deleted, if it was. Never cleared.</param>
/// <param name="BranchCount">How many branches it has.</param>
/// <param name="TableCount">How many tables across all of them.</param>
/// <param name="PaidBranchCount">How many of those branches are on the paid tier.</param>
/// <param name="SubscriptionTier">
/// The rollup: <c>Paid</c> when every branch is paid, otherwise <c>Free</c>. The real flag is per
/// branch - see <paramref name="PaidBranchCount"/>.
/// </param>
public sealed record VenueSummary(
    Guid VenueId,
    string Name,
    VenueType Type,
    string Slug,
    bool IsActive,
    bool IsSuspended,
    bool IsDeleted,
    DateTime? SuspendedAtUtc,
    DateTime? DeletedAtUtc,
    int BranchCount,
    int TableCount,
    int PaidBranchCount,
    SubscriptionTier SubscriptionTier);

public sealed record BranchSummary(
    Guid BranchId,
    Guid VenueId,
    string Name,
    string Slug,
    string Address,
    double Latitude,
    double Longitude,
    string TimeZoneId,
    int FloorWidth,
    int FloorHeight,
    bool IsActive,
    SubscriptionTier SubscriptionTier,
    int TableCount);

public sealed record VenueDetail(VenueSummary Venue, IReadOnlyList<BranchSummary> Branches);

/// <summary>
/// What the platform tier does: onboard venues, configure them, suspend the ones that stop paying.
/// Every mutation here writes a <c>PlatformAuditLog</c> row in the same transaction.
/// </summary>
/// <remarks>
/// The caller must be a platform admin. That is checked <i>inside</i> the service, from the
/// current actor, so it holds for every caller and not only for the HTTP pipeline.
/// </remarks>
public interface IPlatformService
{
    /// <summary>Creates a venue and its first branch atomically. A failure creates neither.</summary>
    Task<VenueDetail> CreateVenueAsync(CreateVenueCommand command, CancellationToken cancellationToken = default);

    Task<PagedResult<VenueSummary>> ListVenuesAsync(VenueListQuery query, CancellationToken cancellationToken = default);

    Task<VenueDetail> GetVenueAsync(Guid venueId, CancellationToken cancellationToken = default);

    Task<VenueDetail> UpdateVenueAsync(Guid venueId, UpdateVenueCommand command, CancellationToken cancellationToken = default);

    /// <summary>Hides the venue from diners, keeps everything, stays visible to the owner.</summary>
    Task<VenueDetail> SuspendVenueAsync(Guid venueId, CancellationToken cancellationToken = default);

    Task<VenueDetail> ReactivateVenueAsync(Guid venueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft delete. Refused, naming the blockers, while any tab is open or any confirmed booking
    /// is still in the future.
    /// </summary>
    /// <exception cref="Domain.Venues.VenueDeletionBlockedException">Open tabs or future bookings exist.</exception>
    Task<VenueDetail> DeleteVenueAsync(Guid venueId, CancellationToken cancellationToken = default);

    Task<BranchSummary> AddBranchAsync(Guid venueId, CreateBranchCommand command, CancellationToken cancellationToken = default);

    Task<BranchSummary> UpdateBranchAsync(Guid branchId, UpdateBranchCommand command, CancellationToken cancellationToken = default);
}
