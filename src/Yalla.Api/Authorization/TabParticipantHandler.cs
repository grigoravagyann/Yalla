using Microsoft.AspNetCore.Authorization;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Authorization;

/// <summary>
/// Requires a tab participant token whose tab matches the one in the route.
/// </summary>
/// <param name="MustBeAbleToOrder">
/// Also requires that the participant may order <i>right now</i> - approved, allowed to order,
/// and the tab still open. The difference between the <c>TabParticipant</c> and
/// <c>TabParticipantCanOrder</c> policies.
/// </param>
/// <param name="MustBeAbleToMutate">
/// Also requires the tab to be open to change - false once staff mark it closing. The difference
/// between reading a bill that is being settled and altering it.
/// </param>
public sealed record TabParticipantRequirement(bool MustBeAbleToOrder, bool MustBeAbleToMutate = false)
    : IAuthorizationRequirement;

/// <summary>
/// Enforces the boundary that two adjacent tables cannot order on each other's bill.
/// </summary>
/// <remarks>
/// <para>
/// This is one of the two boundaries that are the real security of this system, and it is
/// enforced here rather than in each handler on purpose: a check inside a handler is a check the
/// next handler can forget, and the failure mode is silent.
/// </para>
/// <para>
/// The claim-to-route comparison alone would be enough to stop a participant addressing another
/// tab. The database read on top of it covers what a claim cannot express: a participant removed
/// from the tab five minutes ago, a tab that has since closed, a tab that staff have marked
/// closing so nobody may order. A token is a statement about the past; those are questions about
/// now.
/// </para>
/// <para>
/// A <b>pending</b> participant passes the plain policy. They may read their own state - and,
/// through the menu endpoints, the menu - and the projection gives them nothing else. What they
/// may not do is order, which the <c>CanOrder</c> variant refuses through the same
/// <see cref="TabPermissions"/> rule the projection uses to tell the client so.
/// </para>
/// </remarks>
internal sealed class TabParticipantHandler(
    IHttpContextAccessor accessor,
    ITokenAuthorityCheck authority,
    ILogger<TabParticipantHandler> logger)
    : AuthorizationHandler<TabParticipantRequirement>
{

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TabParticipantRequirement requirement)
    {
        if (context.User.PrincipalType() != PrincipalType.TabParticipant)
        {
            return;
        }


        var claimedTabId = context.User.Guid(YallaClaims.TabId);
        var participantId = context.User.Guid(YallaClaims.ParticipantId);
        var routeTabId = accessor.HttpContext.RouteGuid("tabId");

        // The whole boundary, in one comparison. A token minted for tab A cannot address tab B,
        // whatever the request says, because the claim does not name tab B.
        if (claimedTabId is null || participantId is null || routeTabId is null || claimedTabId != routeTabId)
        {
            logger.LogWarning(
                "Tab participant token for tab {ClaimedTabId} refused on tab {RouteTabId}.",
                claimedTabId, routeTabId);

            return;
        }

        // Everything a stateless token cannot say about itself - is the tab still live, is this
        // participant still on it - comes from one place, cached for seconds. A removed participant
        // fails here, which is what the approval flow exists to guarantee: the stranger from the
        // next table who was taken off cannot carry on ordering with the token they already hold.
        var standing = await authority.GetParticipantAuthorityAsync(
            routeTabId.Value, participantId.Value, accessor.HttpContext?.RequestAborted ?? default);

        if (standing is null || !standing.MayRead)
        {
            logger.LogInformation(
                "Participant {ParticipantId} is no longer entitled to tab {TabId}.", participantId, routeTabId);

            return;
        }

        if (requirement.MustBeAbleToMutate && !standing.MayMutate)
        {
            return;
        }

        if (requirement.MustBeAbleToOrder
            && !TabPermissions.MayOrder(standing.ParticipantStatus, standing.CanOrder, standing.TabStatus))
        {
            return;
        }

        context.Succeed(requirement);
    }
}
