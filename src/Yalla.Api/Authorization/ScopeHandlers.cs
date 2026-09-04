using Microsoft.AspNetCore.Authorization;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Authorization;

/// <summary>Requires the caller's branch claim to match the branch named in the route.</summary>
public sealed record BranchScopedRequirement : IAuthorizationRequirement;

/// <summary>Requires an owner or manager acting inside their own venue.</summary>
public sealed record VenueScopedRequirement : IAuthorizationRequirement;

/// <summary>Requires a staff identity holding one of the listed roles.</summary>
/// <param name="AllowedRoles">The roles that satisfy this requirement.</param>
public sealed record StaffRoleRequirement(IReadOnlySet<StaffRole> AllowedRoles) : IAuthorizationRequirement;

/// <summary>
/// Enforces the boundary that a staff token for branch A cannot act on branch B.
/// </summary>
/// <remarks>
/// <para>
/// Chains have several branches and staff belong to one. This is the other boundary that is the
/// real security of this system, and like the tab boundary it is a comparison between a claim and
/// a route value, made once, outside every handler.
/// </para>
/// <para>
/// The branch claim on a staff session is copied from the enrolled device, so a waiter cannot
/// obtain one for a branch their tablet is not at. There is nothing they can send that changes it.
/// </para>
/// <para>
/// An owner or manager signed in to the admin panel is venue-scoped rather than branch-scoped -
/// managing every branch is the point of that account - so when their token carries no matching
/// branch claim, the handler asks whether the branch belongs to their venue. That is a widening
/// for venue users only; a staff session with a branch claim is still confined to it, and a
/// venue user is still confined to their own venue.
/// </para>
/// </remarks>
internal sealed class BranchScopedHandler(
    IHttpContextAccessor accessor,
    IAuthorizationQueries queries,
    ILogger<BranchScopedHandler> logger)
    : AuthorizationHandler<BranchScopedRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BranchScopedRequirement requirement)
    {
        var routeBranchId = accessor.HttpContext.RouteGuid("branchId");

        // No branch in the route means this policy has been applied to an endpoint it cannot
        // scope. Failing is the only safe reading: succeeding would leave the endpoint looking
        // protected while protecting nothing.
        if (routeBranchId is null)
        {
            logger.LogError(
                "BranchScoped was applied to {Path}, which has no branchId route value.",
                accessor.HttpContext?.Request.Path);

            return;
        }

        if (context.User.Guid(YallaClaims.BranchId) == routeBranchId)
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.PrincipalType() != PrincipalType.VenueUser)
        {
            logger.LogWarning(
                "Staff token scoped to branch {ClaimedBranchId} refused on branch {RouteBranchId}.",
                context.User.Guid(YallaClaims.BranchId), routeBranchId);

            return;
        }

        var venueId = context.User.Guid(YallaClaims.VenueId);

        if (venueId is not null
            && await queries.BranchBelongsToVenueAsync(
                routeBranchId.Value, venueId.Value, accessor.HttpContext?.RequestAborted ?? default))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Enforces that an owner or manager only acts inside their own venue.</summary>
internal sealed class VenueScopedHandler(
    IHttpContextAccessor accessor,
    IAuthorizationQueries queries)
    : AuthorizationHandler<VenueScopedRequirement>
{
    private static readonly HashSet<StaffRole> Administrators = [StaffRole.Owner, StaffRole.Manager];

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        VenueScopedRequirement requirement)
    {
        var role = context.User.StaffRole();

        if (role is null || !Administrators.Contains(role.Value))
        {
            return;
        }

        var venueId = context.User.Guid(YallaClaims.VenueId);

        if (venueId is null)
        {
            return;
        }

        // The venue can be named directly, or implied by the branch being addressed. Both are
        // checked against the same claim, so there is no route shape that gets past this.
        var routeVenueId = accessor.HttpContext.RouteGuid("venueId");

        if (routeVenueId is not null)
        {
            if (routeVenueId == venueId)
            {
                context.Succeed(requirement);
            }

            return;
        }

        var routeBranchId = accessor.HttpContext.RouteGuid("branchId");

        if (routeBranchId is not null
            && await queries.BranchBelongsToVenueAsync(
                routeBranchId.Value, venueId.Value, accessor.HttpContext?.RequestAborted ?? default))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Enforces the role requirements, and only for identities that can hold a role at all.
/// </summary>
/// <remarks>
/// A device token carries a branch but no person, so it can never satisfy this - which is the
/// point of separating the two. An enrolled tablet with nobody signed in must not be able to seat
/// a table.
/// </remarks>
internal sealed class StaffRoleHandler : AuthorizationHandler<StaffRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StaffRoleRequirement requirement)
    {
        var principalType = context.User.PrincipalType();

        if (principalType is not (PrincipalType.StaffSession or PrincipalType.VenueUser))
        {
            return Task.CompletedTask;
        }

        if (context.User.StaffRole() is { } role && requirement.AllowedRoles.Contains(role))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
