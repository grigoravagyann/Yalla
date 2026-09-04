using System.Globalization;
using System.Security.Claims;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Authorization;

/// <summary>
/// Reading Yalla's claims off a principal, and reading route values that name the thing being
/// addressed.
/// </summary>
/// <remarks>
/// Every policy handler goes through these. The comparison that actually enforces the two
/// security boundaries in this system - claim against route value - happens in exactly one place
/// per boundary as a result.
/// </remarks>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>Which of the four identity types this token is, or null if it is not one of ours.</summary>
    public static PrincipalType? PrincipalType(this ClaimsPrincipal principal) =>
        int.TryParse(
            principal.FindFirstValue(YallaClaims.PrincipalType),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
        && Enum.IsDefined(typeof(PrincipalType), value)
            ? (PrincipalType)value
            : null;

    /// <summary>A Guid claim, or null when it is absent or malformed.</summary>
    public static Guid? Guid(this ClaimsPrincipal principal, string claimType) =>
        System.Guid.TryParse(principal.FindFirstValue(claimType), out var value) ? value : null;

    /// <summary>The staff role on the token, or null.</summary>
    public static StaffRole? StaffRole(this ClaimsPrincipal principal) =>
        Enum.TryParse<StaffRole>(principal.FindFirstValue(YallaClaims.Role), out var role) ? role : null;

    /// <summary>
    /// A Guid route value, or null when the route does not carry one.
    /// </summary>
    /// <remarks>
    /// A missing route value is treated as a failure by every caller rather than as "no
    /// restriction". A policy that quietly passes when it cannot find the thing it is supposed to
    /// be scoping to is worse than no policy, because it looks applied.
    /// </remarks>
    public static Guid? RouteGuid(this HttpContext? context, string key) =>
        context?.Request.RouteValues.TryGetValue(key, out var raw) == true
        && System.Guid.TryParse(raw?.ToString(), out var value)
            ? value
            : null;
}
