using Yalla.Api.Authorization;
using Yalla.Application.Auth;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The tab-scoped surface a participant token can reach.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small. Orders, line items and payments belong to later work; what is here is the
/// tab a person is on and the display name they may set on it, which is the whole extent of a
/// participant's "profile" and is not an account.
/// </para>
/// <para>
/// Every route is under <c>/api/tabs/{tabId}</c> and carries the <c>TabParticipant</c> policy.
/// That is what makes the boundary structural: the tab id is in the route, the policy compares it
/// to the token's claim, and no handler below sees an unauthorised tab to forget to check.
/// </para>
/// </remarks>
public static class TabEndpoints
{
    public static IEndpointRouteBuilder MapTabEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tabs/{tabId:guid}")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.TabParticipant);

        group.MapGet("/", GetTabAsync)
            .WithName("getTab")
            .WithSummary("The tab this token is for")
            .WithDescription(
                "Money is omitted entirely - not zeroed - when the host has hidden the total from "
                + "guests, because that is a per-tab decision recorded on the tab rather than "
                + "something each client decides whether to render.\n\n"
                + "A token for another tab gets **403**, from the policy, before this handler runs.")
            .Produces<TabView>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "This token is for a different tab, the participant was removed, or the tab closed "
                + "longer ago than the receipt grace period.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such tab.");

        group.MapPut("/me/display-name", SetDisplayNameAsync)
            .WithName("setTabDisplayName")
            .WithSummary("Set what the host sees you called")
            .WithDescription(
                "A profile field on the participant, not an account. Someone who scanned a QR code "
                + "has no user row and never will; putting a name on the tab is optional and costs "
                + "them nothing to skip.\n\n"
                + "Returns a refreshed tab token, because the name is carried in the participant "
                + "view the client renders from.")
            .Produces<TabParticipantTokenResult>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden, "This token is for a different tab.");

        return app;
    }

    private static async Task<IResult> GetTabAsync(
        Guid tabId,
        HttpContext http,
        ITabQuery query,
        CancellationToken cancellationToken)
    {
        // The participant comes from the token, never from the route or the body. There is no
        // request shape that lets a caller ask for somebody else's view of the tab.
        var participantId = http.User.Guid(YallaClaims.ParticipantId);

        if (participantId is null)
        {
            return Results.Forbid();
        }

        var tab = await query.GetForParticipantAsync(tabId, participantId.Value, cancellationToken);

        return tab is null ? Results.NotFound() : Results.Ok(tab);
    }

    private static async Task<IResult> SetDisplayNameAsync(
        Guid tabId,
        SetDisplayNameRequest request,
        HttpContext http,
        ITabParticipantAuthService service,
        CancellationToken cancellationToken)
    {
        var participantId = http.User.Guid(YallaClaims.ParticipantId);

        if (participantId is null)
        {
            return Results.Forbid();
        }

        var result = await service.SetDisplayNameAsync(
            tabId, participantId.Value, request.DisplayName, cancellationToken);

        return Results.Ok(result);
    }
}
