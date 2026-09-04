using Microsoft.AspNetCore.Authorization;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Authorization;

/// <summary>
/// Requires a tab participant token whose tab matches the one in the route.
/// </summary>
/// <param name="MustBeAbleToOrder">
/// Also requires the participant's <c>CanOrder</c> flag - the difference between the
/// <c>TabParticipant</c> and <c>TabParticipantCanOrder</c> policies.
/// </param>
public sealed record TabParticipantRequirement(bool MustBeAbleToOrder) : IAuthorizationRequirement;

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
/// from the tab five minutes ago, and a tab that has since closed. A token is a statement about
/// the past; those two questions are about now.
/// </para>
/// </remarks>
internal sealed class TabParticipantHandler(
    IHttpContextAccessor accessor,
    IAuthorizationQueries queries,
    IClock clock,
    Microsoft.Extensions.Options.IOptions<JwtOptions> jwtOptions,
    ILogger<TabParticipantHandler> logger)
    : AuthorizationHandler<TabParticipantRequirement>
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

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

        var access = await queries.GetTabParticipantAccessAsync(
            routeTabId.Value, participantId.Value, accessor.HttpContext?.RequestAborted ?? default);

        if (access is null || access.ParticipantStatus != ParticipantStatus.Approved)
        {
            return;
        }

        // The token's own expiry is a ceiling, not the rule: it was minted before anyone knew when
        // the tab would close. The real lifetime is the tab, plus long enough to read the receipt.
        if (access.TabClosedAtUtc is { } closedAtUtc
            && clock.UtcNow > closedAtUtc.AddMinutes(_jwt.ParticipantReceiptGraceMinutes))
        {
            return;
        }

        if (requirement.MustBeAbleToOrder && !access.CanOrder)
        {
            return;
        }

        context.Succeed(requirement);
    }
}
