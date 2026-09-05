namespace Yalla.Api.Authorization;

/// <summary>
/// The names of the authorisation policies, so an endpoint cannot misspell one.
/// </summary>
/// <remarks>
/// A misspelled policy name is not a silent failure - ASP.NET Core throws when it cannot resolve
/// one - but it throws on the first request, not at build time. These constants move that to the
/// compiler.
/// </remarks>
public static class YallaPolicies
{
    /// <summary>The token's <c>tabId</c> claim matches the tab in the route.</summary>
    public const string TabParticipant = "TabParticipant";

    /// <summary>As <see cref="TabParticipant"/>, and the participant may add items to the tab.</summary>
    public const string TabParticipantCanOrder = "TabParticipantCanOrder";

    /// <summary>A live staff session whose role is Waiter, Manager, Owner or PlatformAdmin.</summary>
    public const string WaiterOrAbove = "WaiterOrAbove";

    /// <summary>A live staff or admin-panel session whose role is Manager, Owner or PlatformAdmin.</summary>
    public const string ManagerOrAbove = "ManagerOrAbove";

    /// <summary>The people who run Yalla. Nobody at any venue passes this.</summary>
    public const string PlatformAdminOnly = "PlatformAdminOnly";

    /// <summary>The token's <c>branchId</c> claim matches the branch in the route.</summary>
    public const string BranchScoped = "BranchScoped";

    /// <summary>An owner or manager acting inside their own venue.</summary>
    public const string VenueScoped = "VenueScoped";

    /// <summary>
    /// A diner who verified a phone number, and therefore has an account.
    /// </summary>
    /// <remarks>
    /// Deliberately not "any diner". A tab participant is a diner too, but has no account by
    /// design - so there is nothing to list their bookings under and nothing to count a no-show
    /// against. Booking needs the account, not just the person.
    /// </remarks>
    public const string VerifiedDiner = "VerifiedDiner";
}
