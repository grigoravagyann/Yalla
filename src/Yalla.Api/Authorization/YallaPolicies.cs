namespace Yalla.Api.Authorization;

/// <summary>
/// The names of the six authorisation policies, so an endpoint cannot misspell one.
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

    /// <summary>A live staff session whose role is Waiter, Manager or Owner.</summary>
    public const string WaiterOrAbove = "WaiterOrAbove";

    /// <summary>A live staff or admin-panel session whose role is Manager or Owner.</summary>
    public const string ManagerOrAbove = "ManagerOrAbove";

    /// <summary>The token's <c>branchId</c> claim matches the branch in the route.</summary>
    public const string BranchScoped = "BranchScoped";

    /// <summary>An owner or manager acting inside their own venue.</summary>
    public const string VenueScoped = "VenueScoped";
}
