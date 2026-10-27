using Microsoft.AspNetCore.Authorization;
using Yalla.Domain.Venues;

namespace Yalla.Api.Middleware;

/// <summary>
/// Refuses every call from a staff surface the platform has blocked at its branch - a paired phone without
/// <c>StaffPhone</c>, a kitchen session without <c>KitchenDisplay</c> - with 409 <c>feature-not-enabled</c>
/// (<c>docs/branch-features.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The decision is made while the token is validated (<c>AuthenticationExtensions.RejectRevokedDevicesAsync</c>, from the
/// same cached read that answers whether the device is revoked) and left in <see cref="HttpContext.Items"/>; it is
/// thrown here, after routing, because a bearer handler can only fail a token into a 401 and the clients need the
/// feature's own answer to show the surface locked rather than signed out.
/// </para>
/// <para>
/// Anonymous routes run as they would for anybody: signing out still works from a blocked phone.
/// </para>
/// </remarks>
internal sealed class FeatureGateMiddleware(RequestDelegate next)
{
    /// <summary>Where token validation leaves the refusal.</summary>
    public const string BlockedItem = "yalla:feature-blocked";

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Items[BlockedItem] is FeatureNotEnabledException blocked
            && context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is null)
        {
            throw blocked;
        }

        return next(context);
    }
}
