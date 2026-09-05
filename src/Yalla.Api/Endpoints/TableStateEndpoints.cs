using Yalla.Api.Authorization;
using Yalla.Application.Abstractions;
using Yalla.Application.Floor;
using Yalla.Application.Tables;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The table state and floor endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin: bind, validate the shape of the request, call the service, return. Every
/// rule - which transitions are legal, who may perform them, what warnings to raise - lives in
/// the domain service, because three clients call these and a rule enforced here would be a rule
/// enforced nowhere else.
/// </para>
/// <para>
/// The same goes for identity. Every route below carries <c>WaiterOrAbove</c> and
/// <c>BranchScoped</c>, applied to the group; no handler reads a claim, so no handler can forget
/// to. <c>BranchScoped</c> is the boundary that matters here: chains have several branches, staff
/// belong to one, and a session token for branch A must not be able to seat a table at branch B
/// however the request is addressed.
/// </para>
/// <para>
/// Exceptions are not caught here either. <c>UnifiedExceptionHandler</c> maps them centrally, so
/// <c>TableStateConflictException</c> becomes 409 with the current state and
/// <c>InvalidTableTransitionException</c> becomes 422 no matter which endpoint raised it. Both
/// are declared on every transition below, because the frontend has to treat "someone just took
/// that table" as a normal outcome rather than a failure.
/// </para>
/// </remarks>
public static class TableStateEndpoints
{
    private const string ConflictDescription =
        "Someone else changed this table first. The body's `context` carries the table's current "
        + "status and session, so the client can redraw it rather than showing a generic failure. "
        + "Never retry automatically: retrying would seat a walk-in at a table a booking just took.";

    private const string TransitionDescription =
        "The transition is not one the state machine allows - freeing an empty table, or marking "
        + "an occupied one out of service. The body's `context` lists the transitions that are "
        + "legal from the current status.";

    public static IEndpointRouteBuilder MapTableStateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}/tables")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        // One place enforcing the idempotency key for every POST below. GET /floor carries no
        // body, so the filter simply finds nothing to check.
        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapGet("/floor", GetFloorAsync)
            .WithName("getBranchFloor")
            .WithTags(EndpointConventions.StaffTag, EndpointConventions.DinerTag)
            .WithSummary("The derived floor state for a branch")
            .WithDescription(
                "Physical table status with the reservation overlay applied. Assembled in a "
                + "single query - this is the most-called endpoint in the product. Returns no "
                + "guest names and no money: the diner app calls it too.")
            .Produces<BranchFloorState>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        Transition(group, "/{tableId:guid}/seat-walk-in", SeatWalkInAsync, "seatWalkIn")
            .WithSummary("Seat a party with no booking (Free to Occupied)");

        Transition(group, "/{tableId:guid}/seat-reservation", SeatReservationAsync, "seatReservation")
            .WithSummary("Seat a booked party (Free to Occupied, booking to Seated)");

        Transition(group, "/{tableId:guid}/hold", HoldAsync, "holdTable")
            .WithSummary("Hold a table for a party expected imminently (Free to Held)");

        Transition(group, "/{tableId:guid}/release-hold", ReleaseHoldAsync, "releaseTableHold")
            .WithSummary("Give up on a held table (Held to Free)");

        Transition(group, "/{tableId:guid}/seat-held-party", SeatHeldPartyAsync, "seatHeldParty")
            .WithSummary("Seat the party a hold was placed for (Held to Occupied)");

        Transition(group, "/{tableId:guid}/free", FreeAsync, "freeTable")
            .WithSummary("Free a table (Occupied to Free)")
            .WithDescription(
                "Closes the occupancy. Closes the tab when nothing is owed; when a balance is "
                + "outstanding the table is still freed - the diners have left - and the response "
                + "carries the amount as a warning.");

        Transition(group, "/{tableId:guid}/out-of-service", MarkOutOfServiceAsync, "markTableOutOfService")
            .WithSummary("Withdraw a table from service (Free or Held to OutOfService)")
            .WithDescription("Refuses an occupied table with 422: free it first.");

        Transition(group, "/{tableId:guid}/return-to-service", ReturnToServiceAsync, "returnTableToService")
            .WithSummary("Put a table back into service (OutOfService to Free)");

        return app;
    }

    /// <summary>
    /// Maps one state transition with the responses every one of them can produce.
    /// </summary>
    /// <remarks>
    /// Eight endpoints with the identical 200/409/422 triple. Writing it out eight times is eight
    /// chances for one of them to be missing the 409 the frontend needs in its generated types.
    /// </remarks>
    private static RouteHandlerBuilder Transition(
        RouteGroupBuilder group,
        string pattern,
        Delegate handler,
        string operationId) =>
        group.MapPost(pattern, handler)
            .WithName(operationId)
            .Produces<TableStateChangeResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch or table.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, ConflictDescription)
            .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity, TransitionDescription);

    private static async Task<IResult> GetFloorAsync(
        Guid branchId,
        IFloorQuery floorQuery,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // The staff floor screen always means "right now", and says so rather than relying on a
        // default that a future caller would inherit by accident.
        var floor = await floorQuery.GetFloorStateAsync(branchId, clock.UtcNow, cancellationToken);

        return floor is null ? Results.NotFound() : Results.Ok(floor);
    }

    private static async Task<IResult> SeatWalkInAsync(
        Guid branchId,
        Guid tableId,
        SeatWalkInRequest request,
        ITableStateService service,
        CancellationToken cancellationToken)
    {
        var result = await service.SeatWalkInAsync(
            new SeatWalkInCommand(branchId, tableId, request.PartySize, request.ClientCommandId, request.Reason),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> SeatReservationAsync(
        Guid branchId,
        Guid tableId,
        SeatReservationRequest request,
        ITableStateService service,
        CancellationToken cancellationToken)
    {
        var result = await service.SeatReservationAsync(
            new SeatReservationCommand(
                branchId, tableId, request.ReservationId, request.ClientCommandId, request.PartySize, request.Reason),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> HoldAsync(
        Guid branchId,
        Guid tableId,
        TableStateRequest request,
        ITableStateService service,
        CancellationToken cancellationToken)
    {
        var result = await service.HoldForLatePartyAsync(
            new TableStateCommand(branchId, tableId, request.ClientCommandId, request.Reason),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ReleaseHoldAsync(
        Guid branchId,
        Guid tableId,
        TableStateRequest request,
        ITableStateService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ReleaseHoldAsync(
            new TableStateCommand(branchId, tableId, request.ClientCommandId, request.Reason),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> SeatHeldPartyAsync(
        Guid branchId,
        Guid tableId,
        SeatHeldPartyRequest request,
        ITableStateService service,
        CancellationToken cancellationToken)
    {
        var result = await service.SeatHeldPartyAsync(
            new SeatHeldPartyCommand(
                branchId, tableId, request.PartySize, request.ClientCommandId, request.ReservationId, request.Reason),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> FreeAsync(
        Guid branchId,
        Guid tableId,
        TableStateRequest request,
        ITableStateService service,
        CancellationToken cancellationToken)
    {
        var result = await service.FreeTableAsync(
            new TableStateCommand(branchId, tableId, request.ClientCommandId, request.Reason),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> MarkOutOfServiceAsync(
        Guid branchId,
        Guid tableId,
        TableStateRequest request,
        ITableStateService service,
        CancellationToken cancellationToken)
    {
        var result = await service.MarkOutOfServiceAsync(
            new TableStateCommand(branchId, tableId, request.ClientCommandId, request.Reason),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ReturnToServiceAsync(
        Guid branchId,
        Guid tableId,
        TableStateRequest request,
        ITableStateService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ReturnToServiceAsync(
            new TableStateCommand(branchId, tableId, request.ClientCommandId, request.Reason),
            cancellationToken);

        return Results.Ok(result);
    }
}
