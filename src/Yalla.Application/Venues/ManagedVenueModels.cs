using Yalla.Domain.Enums;

namespace Yalla.Application.Venues;

/// <summary>
/// The venue an owner or manager signs in to, with the branches their own staff row covers.
/// </summary>
/// <remarks>
/// <para>
/// The one read the console opens a venue with. It exists because the console had no venue read
/// a venue user could make: the platform's <c>GET /api/platform/venues/{id}</c> is
/// <c>PlatformAdminOnly</c>, and every owner and manager who tried it got a 403 and a console that
/// said "no branch". This is deliberately not that read. It carries no tier rollup, no paid-branch
/// count and no suspension timestamps - nothing the platform tier keeps to itself - so a field
/// added to the platform view later cannot reach a manager by accident.
/// </para>
/// <para>
/// <b>Coverage is decided from the caller's stored row, never from the token.</b> A token names at
/// most one branch, and an owner's names none - which is the exact shape the console mistook for
/// "no access". An owner covers every branch; so does a manager whose row names none; a manager
/// whose row names a branch is shown that branch only. The server's own scope enforcement is
/// wider than that last case, on purpose, and this view does not change it: it says what the
/// console shows, and the console never shows a branch the server would refuse.
/// </para>
/// </remarks>
/// <param name="VenueId">The venue.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Type">Cafe, restaurant or bar.</param>
/// <param name="Slug">The URL-safe handle its public page lives under.</param>
/// <param name="IsSuspended">The platform has suspended it for non-payment; diners cannot book, staff can still configure.</param>
/// <param name="IsDeleted">Soft-deleted by the platform; nothing can be changed.</param>
/// <param name="Branches">
/// Every branch the caller covers, active ones first, then by name. Inactive branches are listed
/// and flagged rather than hidden, because an owner may still open one.
/// </param>
public sealed record ManagedVenueView(
    Guid VenueId,
    string Name,
    VenueType Type,
    string Slug,
    bool IsSuspended,
    bool IsDeleted,
    IReadOnlyList<ManagedBranchView> Branches);

/// <summary>One branch of the venue as the console lists it: enough to pick it and address it.</summary>
/// <param name="BranchId">The branch; what every <c>/api/branches/{branchId}</c> route is addressed by.</param>
/// <param name="VenueId">Its venue. Always the venue this view is of.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Slug">Its URL-safe handle.</param>
/// <param name="TimeZoneId">IANA time zone, so the console can show the branch's own clock.</param>
/// <param name="IsActive">False for a branch the platform has switched off. It is listed so the switcher can say so.</param>
/// <param name="SubscriptionTier">Free or Paid; tabs and payments are a Paid feature.</param>
/// <param name="TableCount">How many tables it has, active or not.</param>
public sealed record ManagedBranchView(
    Guid BranchId,
    Guid VenueId,
    string Name,
    string Slug,
    string TimeZoneId,
    bool IsActive,
    SubscriptionTier SubscriptionTier,
    int TableCount);

/// <summary>
/// The managed-venue read. A projection; nothing here writes.
/// </summary>
/// <remarks>
/// The route carries <c>VenueScoped</c>, and this checks the acting staff member's stored row as
/// well - two locks, the way the staff service does it - so a route that lost its policy would
/// still refuse a neighbour, and a caller whose row was deactivated after their token was minted
/// is refused before it expires.
/// </remarks>
public interface IManagedVenueQuery
{
    /// <exception cref="Domain.Staff.StaffPermissionException">
    /// The caller is not an active owner or manager of this venue, and not a platform admin.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No such venue.</exception>
    Task<ManagedVenueView> GetAsync(Guid venueId, CancellationToken cancellationToken = default);
}
