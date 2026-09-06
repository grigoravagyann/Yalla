using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.Platform;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The platform tier: creating and configuring venues through the API instead of by hand in SQL.
/// </summary>
/// <remarks>
/// <para>
/// <c>PlatformAdminOnly</c> on the whole group. The service re-checks the actor, so the rule holds
/// for any caller that is not HTTP, and writes a <c>PlatformAuditLog</c> row in the same
/// transaction as every change.
/// </para>
/// <para>
/// Nothing here bills anybody. The subscription tier is a per-branch flag that gates features.
/// </para>
/// </remarks>
public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform")
            .WithTags(EndpointConventions.PlatformTag)
            .RequireAuthorization(YallaPolicies.PlatformAdminOnly);

        group.MapPost("/venues", CreateVenueAsync)
            .WithName("createVenue")
            .WithSummary("Create a venue with its first branch")
            .WithDescription(
                "A venue with no branch is useless, so the first branch is created in the same "
                + "transaction. A failure creates neither. The branch's `subscriptionTier` defaults to Free.")
            .Produces<VenueDetail>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "A field is missing or out of range.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The venue slug is already taken.");

        group.MapGet("/venues", ListVenuesAsync)
            .WithName("listVenues")
            .WithSummary("Venues, with search and paging")
            .WithDescription(
                "Each row carries its branch count, table count, paid-branch count and the tier "
                + "rollup - Paid only when every branch is paid, because billing is per branch.")
            .Produces<PagedResult<VenueSummary>>();

        group.MapGet("/venues/{venueId:guid}", GetVenueAsync)
            .WithName("getVenue")
            .WithSummary("One venue and its branches")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.");

        group.MapPatch("/venues/{venueId:guid}", UpdateVenueAsync)
            .WithName("updateVenue")
            .WithSummary("Name, type, slug, active flag")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The slug is taken, or the venue is deleted.");

        group.MapPost("/venues/{venueId:guid}/suspend", SuspendVenueAsync)
            .WithName("suspendVenue")
            .WithSummary("Hide the venue from diners; keep everything")
            .WithDescription(
                "What happens when someone stops paying. The venue disappears from diner browsing "
                + "but keeps all its data and stays visible to its owner.")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.");

        group.MapPost("/venues/{venueId:guid}/reactivate", ReactivateVenueAsync)
            .WithName("reactivateVenue")
            .WithSummary("Undo a suspension")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.");

        group.MapDelete("/venues/{venueId:guid}", DeleteVenueAsync)
            .WithName("deleteVenue")
            .WithSummary("Soft-delete a venue")
            .WithDescription(
                "**Soft delete only.** Reservations, tabs and payments hang off the venue and are "
                + "never removed. Refused while any tab is open or any confirmed booking is still in "
                + "the future - the response names which.")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "Open tabs or future bookings block the deletion; `context` lists them.");

        group.MapPost("/venues/{venueId:guid}/branches", AddBranchAsync)
            .WithName("addBranch")
            .WithSummary("Add a branch to a venue")
            .Produces<BranchSummary>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The branch slug is taken within the venue.");

        group.MapPatch("/branches/{branchId:guid}", UpdateBranchAsync)
            .WithName("updateBranch")
            .WithSummary("Name, address, coordinates, timezone, canvas size, active flag, tier")
            .WithDescription(
                "`subscriptionTier` is set here, per branch. Moving a branch to Free switches off "
                + "tabs and ordering there; the tab endpoints answer `feature-not-enabled`. It is "
                + "refused while the branch has open tabs, because hiding a live bill from the people "
                + "who owe it strands real money on a real table.\n\n"
                + "**Moving a branch to Paid is refused while its menu has unfinished items.** This is "
                + "where the rule that a dish needs a photo, ingredients, allergens, a portion size and "
                + "a prep time is actually enforced - at the moment the branch starts taking diners, "
                + "rather than at the moment somebody types a name and a price. The refusal is "
                + "`branch-not-ready` and carries `incompleteMenuItemCount`; "
                + "`GET /api/branches/{branchId}/readiness` lists which items are left.")
            .Produces<BranchSummary>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<BranchNotReadyProblem>(
                StatusCodes.Status409Conflict,
                "Going Paid with an unfinished menu, or going Free with open tabs.");

        return app;
    }

    private static async Task<IResult> CreateVenueAsync(
        CreateVenueCommand command,
        IPlatformService service,
        CancellationToken cancellationToken)
    {
        var created = await service.CreateVenueAsync(command, cancellationToken);

        return Results.Created($"/api/platform/venues/{created.Venue.VenueId}", created);
    }

    private static async Task<IResult> ListVenuesAsync(
        IPlatformService service,
        CancellationToken cancellationToken,
        string? search = null,
        int page = 1,
        int pageSize = 20,
        bool includeDeleted = false) =>
        Results.Ok(await service.ListVenuesAsync(new VenueListQuery(search, page, pageSize, includeDeleted), cancellationToken));

    private static async Task<IResult> GetVenueAsync(Guid venueId, IPlatformService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.GetVenueAsync(venueId, cancellationToken));

    private static async Task<IResult> UpdateVenueAsync(
        Guid venueId,
        UpdateVenueCommand command,
        IPlatformService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.UpdateVenueAsync(venueId, command, cancellationToken));

    private static async Task<IResult> SuspendVenueAsync(Guid venueId, IPlatformService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.SuspendVenueAsync(venueId, cancellationToken));

    private static async Task<IResult> ReactivateVenueAsync(Guid venueId, IPlatformService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ReactivateVenueAsync(venueId, cancellationToken));

    private static async Task<IResult> DeleteVenueAsync(Guid venueId, IPlatformService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.DeleteVenueAsync(venueId, cancellationToken));

    private static async Task<IResult> AddBranchAsync(
        Guid venueId,
        CreateBranchCommand command,
        IPlatformService service,
        CancellationToken cancellationToken)
    {
        var created = await service.AddBranchAsync(venueId, command, cancellationToken);

        return Results.Created($"/api/platform/venues/{venueId}", created);
    }

    private static async Task<IResult> UpdateBranchAsync(
        Guid branchId,
        UpdateBranchCommand command,
        IPlatformService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.UpdateBranchAsync(branchId, command, cancellationToken));
}
