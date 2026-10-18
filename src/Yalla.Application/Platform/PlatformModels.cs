using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Domain;
using Yalla.Domain.Enums;

namespace Yalla.Application.Platform;

/// <summary>Create a venue together with its first branch. A venue with no branch is useless.</summary>
public sealed record CreateVenueCommand(
    string Name,
    VenueType Type,
    string Slug,
    CreateBranchCommand FirstBranch)
{
    /// <summary>
    /// Names every required field the payload left out, rather than the first one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty body is missing a name, a slug and a whole branch, and reporting one of them at a
    /// time makes somebody submit three times to find that out. The same argument
    /// <c>ReservationPolicyLimits</c> was written for, applied to the other form an owner meets
    /// during onboarding - and this one is about to grow the branch's address and coordinates in
    /// the console, so it will be submitting a dozen inputs at once.
    /// </para>
    /// <para>
    /// <b>Presence only, deliberately - not bounds.</b> "Is this null or blank?" needs no knowledge
    /// of any rule, so it can be asked here without a second copy of anything. A <i>bound</i> -
    /// a latitude of 200, a name of 500 characters - is enforced by the <c>Venue</c> and
    /// <c>Branch</c> constructors through <c>Guard</c>, and checking those here would mean either
    /// duplicating every limit or extracting them into a <c>VenueLimits</c> class the way the
    /// reservation policy has one. Both are real changes to how venue creation validates, which is
    /// more than this is. So a request with three missing fields reports three, and a request with
    /// three out-of-range fields still reports the first. See <c>docs/platform-admin.md</c>.
    /// </para>
    /// <para>
    /// Field names are the wire ones, dotted for the nested object - <c>firstBranch.name</c> - in
    /// the same shape the opening-hours refusal already uses for <c>[1].opensAt</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="FieldValidationException">One or more required fields are missing.</exception>
    public void Validate()
    {
        var violations = new List<FieldViolation>();

        Require(violations, "name", Name);
        Require(violations, "slug", Slug);

        if (!Enum.IsDefined(Type))
        {
            violations.Add(new FieldViolation(
                "type",
                "Not a defined venue type. 1 Cafe, 2 Restaurant, 3 Bar.",
                FieldBounds.Required,
                Value: (int)Type));
        }

        if (FirstBranch is null)
        {
            // A venue with no branch is useless, so the first branch is created with it - which
            // makes a missing firstBranch a missing required field like any other, and not the
            // internal null it used to be reported as.
            violations.Add(new FieldViolation(
                "firstBranch",
                "A venue needs its first branch.",
                FieldBounds.Required));
        }
        else
        {
            Require(violations, "firstBranch.name", FirstBranch.Name);
            Require(violations, "firstBranch.slug", FirstBranch.Slug);
            Require(violations, "firstBranch.address", FirstBranch.Address);
            Require(violations, "firstBranch.timeZoneId", FirstBranch.TimeZoneId);
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }
    }

    private static void Require(List<FieldViolation> violations, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(new FieldViolation(field, "This field is required.", FieldBounds.Required));
        }
    }
}

/// <summary>Add a branch. The tier is per branch - a chain with four locations pays four times.</summary>
public sealed record CreateBranchCommand(
    string Name,
    string Slug,
    string Address,
    double Latitude,
    double Longitude,
    string TimeZoneId,
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
    bool? IsActive = null,
    SubscriptionTier? SubscriptionTier = null);

/// <summary>Search, filters, sorting and paging for the venue list.</summary>
/// <param name="Search">
/// Matched against the venue's name and slug and each branch's name and district.
/// </param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">1 to 200.</param>
/// <param name="IncludeDeleted">
/// The older switch, kept for callers that send it: true is <c>status=all</c>. A <paramref name="Status"/>
/// wins when both are sent.
/// </param>
/// <param name="Status">One of <see cref="VenueListFilters.Statuses"/>; <c>live</c> (every venue not deleted) by default.</param>
/// <param name="Tier">One of <see cref="VenueListFilters.Tiers"/>, matched against <see cref="VenueSummary.TierMix"/>; every tier by default.</param>
/// <param name="Sort">One of <see cref="VenueListFilters.Sorts"/>; <c>name</c> by default.</param>
/// <param name="Dir"><c>asc</c> (default) or <c>desc</c>. A venue with no rating sorts below every rated one.</param>
public sealed record VenueListQuery(
    string? Search = null,
    int Page = 1,
    int PageSize = 20,
    bool IncludeDeleted = false,
    string? Status = null,
    string? Tier = null,
    string? Sort = null,
    string? Dir = null);

/// <summary>The values the venue list's query parameters accept. Stable; the console sends them.</summary>
public static class VenueListFilters
{
    /// <summary>Every venue that is not deleted - active, suspended and switched off alike. The default.</summary>
    public const string Live = "live";

    /// <summary>Everything, deleted venues included.</summary>
    public const string All = "all";

    /// <summary>Not deleted, not suspended, switched on.</summary>
    public const string Active = "active";

    /// <summary>Suspended and not deleted.</summary>
    public const string Suspended = "suspended";

    /// <summary>Switched off (<c>isActive</c> false), neither suspended nor deleted.</summary>
    public const string Inactive = "inactive";

    /// <summary>Soft-deleted.</summary>
    public const string Deleted = "deleted";

    /// <summary>Every branch on the Free tier (and a venue with no branch).</summary>
    public const string Free = "free";

    /// <summary>Some branches paid, some not.</summary>
    public const string Mixed = "mixed";

    /// <summary>Every branch on the Paid tier.</summary>
    public const string Paid = "paid";

    public const string SortName = "name";

    /// <summary>The average of the published reviews across every branch.</summary>
    public const string SortRating = "rating";

    /// <summary>Active tables across every branch.</summary>
    public const string SortTables = "tables";

    /// <summary>When the venue was created.</summary>
    public const string SortCreated = "created";

    public const string Ascending = "asc";

    public const string Descending = "desc";

    /// <summary>The accepted <c>status</c> values.</summary>
    public static readonly IReadOnlyList<string> Statuses = [Live, All, Active, Suspended, Inactive, Deleted];

    /// <summary>The accepted <c>tier</c> values.</summary>
    public static readonly IReadOnlyList<string> Tiers = [Free, Mixed, Paid];

    /// <summary>The accepted <c>sort</c> values.</summary>
    public static readonly IReadOnlyList<string> Sorts = [SortName, SortRating, SortTables, SortCreated];

    /// <summary>The accepted <c>dir</c> values.</summary>
    public static readonly IReadOnlyList<string> Directions = [Ascending, Descending];
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// One venue as the platform sees it, with the per-branch tier rolled up.
/// </summary>
/// <param name="VenueId">The venue.</param>
/// <param name="Name">Its name.</param>
/// <param name="Type">1 Cafe, 2 Restaurant, 3 Bar. Selects the shipped policy defaults for a new branch.</param>
/// <param name="Slug">Unique URL segment for the brand.</param>
/// <param name="IsActive">Whether the venue is switched on at all.</param>
/// <param name="IsSuspended">Suspended venues keep every row but disappear from diner browsing.</param>
/// <param name="IsDeleted">Soft-deleted. Never a hard delete; the venue refuses every change.</param>
/// <param name="SuspendedAtUtc">When it was suspended, if it is.</param>
/// <param name="DeletedAtUtc">When it was deleted, if it was. Never cleared.</param>
/// <param name="BranchCount">How many branches it has.</param>
/// <param name="TableCount">
/// How many <b>active</b> tables across all of them. A table kept only for its history is not one the
/// room has, the same rule the readiness checklist counts by.
/// </param>
/// <param name="PaidBranchCount">How many of those branches are on the paid tier.</param>
/// <param name="SubscriptionTier">
/// The older rollup: <c>Paid</c> when every branch is paid, otherwise <c>Free</c> - a mixed venue reads
/// Free here. <paramref name="TierMix"/> says which it is; the real flag is per branch, in
/// <paramref name="Branches"/>.
/// </param>
/// <param name="Status">
/// The four booleans above as one value: 4 Deleted, else 2 Suspended, else 3 Inactive (switched off),
/// else 1 Active.
/// </param>
/// <param name="TierMix">
/// 1 Free (no branch paid, or no branch at all), 2 Mixed (some), 3 Paid (every one) - what the list's
/// tier column shows, with <paramref name="PaidBranchCount"/> of <paramref name="BranchCount"/>.
/// </param>
/// <param name="Branches">Every branch with its own tier, active ones first, then by name.</param>
/// <param name="AverageRating">
/// The average of the published reviews across every branch, to one decimal; absent with none. The
/// same rule the public pages use, over the whole venue.
/// </param>
/// <param name="ReviewCount">Published reviews across every branch.</param>
/// <param name="OpenTabCount">Tabs open or closing right now, across every branch.</param>
/// <param name="ReadinessIssue">
/// The first thing a guest would find missing at the first branch that has one, in the order of
/// <paramref name="Branches"/> - with that branch's name - or absent when nothing is. Codes:
/// <c>no-tables</c>, <c>no-menu</c>, <c>incomplete-dishes</c> (with <c>count</c>), <c>no-hours</c>,
/// <c>no-photo</c>. Never set for a deleted venue.
/// </param>
/// <param name="NotReadyBranchCount">How many branches have at least one such issue.</param>
/// <param name="CoverThumbnailUrl">
/// The thumbnail of the first branch's cover photo, in the order of <paramref name="Branches"/>:
/// root-relative (<c>/api/photos/{id}/thumbnail</c>) for an uploaded photo, absolute for a migrated
/// one. Absent when no branch has a cover.
/// </param>
/// <param name="District">The first branch's district, as the listing form saved it.</param>
/// <param name="Cuisine">The first branch's cuisine, as the listing form saved it.</param>
/// <param name="CreatedAtUtc">When the venue was created.</param>
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
    SubscriptionTier SubscriptionTier,
    VenueStatus Status,
    TierMix TierMix,
    IReadOnlyList<BranchTierView> Branches,
    double? AverageRating,
    int ReviewCount,
    int OpenTabCount,
    VenueReadinessIssue? ReadinessIssue,
    int NotReadyBranchCount,
    string? CoverThumbnailUrl,
    string? District,
    string? Cuisine,
    DateTime CreatedAtUtc);

/// <summary>One branch's tier and what it still lacks, as the venue list shows it.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="Name">Its name.</param>
/// <param name="SubscriptionTier">1 Free, 2 Paid. Set per branch with <c>PATCH /api/platform/branches/{id}</c>.</param>
/// <param name="IsActive">Whether the branch is switched on.</param>
/// <param name="Issues">
/// What a guest would find missing at the branch, in checklist order: <c>no-tables</c>, <c>no-menu</c>,
/// <c>incomplete-dishes</c> (with <c>count</c>), <c>no-hours</c>, then <c>no-photo</c>. Empty when there
/// is nothing to do. Always empty for a deleted venue, which is not worked out. The full checklist is
/// <c>GET /api/branches/{id}/readiness</c>.
/// </param>
/// <param name="District">The branch's district as the listing form saved it; absent when it has none.</param>
public sealed record BranchTierView(
    Guid BranchId,
    string Name,
    SubscriptionTier SubscriptionTier,
    bool IsActive,
    IReadOnlyList<ReadinessIssue> Issues,
    string? District);

/// <summary>The one issue the venue table shows, and the branch it is at.</summary>
/// <param name="Code"><c>no-tables</c>, <c>no-menu</c>, <c>incomplete-dishes</c>, <c>no-hours</c> or <c>no-photo</c>.</param>
/// <param name="Count">How many, for <c>incomplete-dishes</c>; absent otherwise.</param>
/// <param name="BranchName">The branch it is at.</param>
public sealed record VenueReadinessIssue(string Code, int? Count, string BranchName);

/// <summary>One branch as the platform's venue page shows it.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="VenueId">Its venue.</param>
/// <param name="Name">Its name.</param>
/// <param name="Slug">URL segment, unique within the venue.</param>
/// <param name="Address">Street address.</param>
/// <param name="Latitude">The map pin.</param>
/// <param name="Longitude">The map pin.</param>
/// <param name="TimeZoneId">IANA zone; every local time at the branch is in it.</param>
/// <param name="IsActive">Whether the branch is switched on.</param>
/// <param name="SubscriptionTier">1 Free, 2 Paid.</param>
/// <param name="TableCount">Active tables. A table kept only for its history is not counted.</param>
/// <param name="OpenTabCount">Tabs open or closing at the branch right now.</param>
/// <param name="District">As the listing form saved it.</param>
public sealed record BranchSummary(
    Guid BranchId,
    Guid VenueId,
    string Name,
    string Slug,
    string Address,
    double Latitude,
    double Longitude,
    string TimeZoneId,
    bool IsActive,
    SubscriptionTier SubscriptionTier,
    int TableCount,
    int OpenTabCount,
    string? District);

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
    /// Undoes a soft delete: the venue comes back as it was before - switched on or off, suspended or
    /// not. A venue that is not deleted is answered as it is and nothing is written.
    /// </summary>
    /// <exception cref="DomainStateException">The venue's web address is now another venue's.</exception>
    Task<VenueDetail> RestoreVenueAsync(Guid venueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft delete. Refused, naming the blockers, while any tab is open or any confirmed booking
    /// is still in the future.
    /// </summary>
    /// <exception cref="Domain.Venues.VenueDeletionBlockedException">Open tabs or future bookings exist.</exception>
    Task<VenueDetail> DeleteVenueAsync(Guid venueId, CancellationToken cancellationToken = default);

    Task<BranchSummary> AddBranchAsync(Guid venueId, CreateBranchCommand command, CancellationToken cancellationToken = default);

    Task<BranchSummary> UpdateBranchAsync(Guid branchId, UpdateBranchCommand command, CancellationToken cancellationToken = default);
}
