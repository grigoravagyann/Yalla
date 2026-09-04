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
/// Exceptions are not caught here either. <c>UnifiedExceptionHandler</c> maps them centrally, so
/// <c>TableStateConflictException</c> becomes 409 with the current state and
/// <c>InvalidTableTransitionException</c> becomes 422 no matter which endpoint raised it.
/// </para>
/// </remarks>
public static class TableStateEndpoints
{
    public static IEndpointRouteBuilder MapTableStateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}/tables")
            .WithTags("Tables");

        // One place enforcing the idempotency key for every POST below. GET /floor carries no
        // body, so the filter simply finds nothing to check.
        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapGet("/floor", GetFloorAsync)
            .WithName("GetBranchFloor")
            .WithSummary("The derived floor state for a branch")
            .WithDescription(
                "Physical table status with the reservation overlay applied. Assembled in a "
                + "single query - this is the most-called endpoint in the product. Returns no "
                + "guest names and no money: the diner app calls it too.")
            .Produces<BranchFloorState>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{tableId:guid}/seat-walk-in", SeatWalkInAsync)
            .WithName("SeatWalkIn")
            .WithSummary("Seat a party with no booking (Free to Occupied)")
            .Produces<TableStateChangeResult>();

        group.MapPost("/{tableId:guid}/seat-reservation", SeatReservationAsync)
            .WithName("SeatReservation")
            .WithSummary("Seat a booked party (Free to Occupied, booking to Seated)")
            .Produces<TableStateChangeResult>();

        group.MapPost("/{tableId:guid}/hold", HoldAsync)
            .WithName("HoldTable")
            .WithSummary("Hold a table for a party expected imminently (Free to Held)")
            .Produces<TableStateChangeResult>();

        group.MapPost("/{tableId:guid}/release-hold", ReleaseHoldAsync)
            .WithName("ReleaseHold")
            .WithSummary("Give up on a held table (Held to Free)")
            .Produces<TableStateChangeResult>();

        group.MapPost("/{tableId:guid}/seat-held-party", SeatHeldPartyAsync)
            .WithName("SeatHeldParty")
            .WithSummary("Seat the party a hold was placed for (Held to Occupied)")
            .Produces<TableStateChangeResult>();

        group.MapPost("/{tableId:guid}/free", FreeAsync)
            .WithName("FreeTable")
            .WithSummary("Free a table (Occupied to Free)")
            .WithDescription(
                "Closes the occupancy. Closes the tab when nothing is owed; when a balance is "
                + "outstanding the table is still freed - the diners have left - and the response "
                + "carries the amount as a warning.")
            .Produces<TableStateChangeResult>();

        group.MapPost("/{tableId:guid}/out-of-service", MarkOutOfServiceAsync)
            .WithName("MarkTableOutOfService")
            .WithSummary("Withdraw a table from service (Free or Held to OutOfService)")
            .WithDescription("Refuses an occupied table with 422: free it first.")
            .Produces<TableStateChangeResult>();

        group.MapPost("/{tableId:guid}/return-to-service", ReturnToServiceAsync)
            .WithName("ReturnTableToService")
            .WithSummary("Put a table back into service (OutOfService to Free)")
            .Produces<TableStateChangeResult>();

        return app;
    }

    private static async Task<IResult> GetFloorAsync(
        Guid branchId,
        IFloorQuery floorQuery,
        CancellationToken cancellationToken)
    {
        var floor = await floorQuery.GetFloorAsync(branchId, cancellationToken);

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
