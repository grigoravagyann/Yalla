using Yalla.Api.Errors;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The availability and booking endpoints.
/// </summary>
/// <remarks>
/// <para>
/// As thin as the table endpoints, and for the same reason: bind, call the service, return. Every
/// rule - the overlap check, the branch policy, who may cancel what - lives in the service and the
/// domain, because three clients call these and a rule enforced here is a rule enforced nowhere
/// else.
/// </para>
/// <para>
/// Nothing is caught here either. <c>UnifiedExceptionHandler</c> maps centrally, so a lost race
/// becomes 409 with the clashing window and a fresh floor, each branch rule becomes 422 with its
/// own code, and a lock timeout becomes a retryable 503 - the same way no matter which endpoint
/// raised it.
/// </para>
/// </remarks>
public static class ReservationEndpoints
{
    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        MapAvailability(app);
        MapBookings(app);

        return app;
    }

    private static void MapAvailability(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/branches/{branchId:guid}/availability", GetAvailabilityAsync)
            .WithTags("Availability")
            .WithName("GetBranchAvailability")

            // Browsing needs no account. Somebody deciding whether to eat here must be able to see
            // the room before they are asked who they are.
            .AllowAnonymous()
            .WithSummary("Which tables a branch can offer for one slot")
            .WithDescription(
                "Every table in the branch with its floor-plan geometry, its derived state at the "
                + "requested time, whether it can be booked for this party - and if not, the one "
                + "specific reason - and the window the diner may have. Assembled in a single "
                + "query. Returns no guest names and no money. Omit date and time for 'now, at "
                + "the branch'.")
            .Produces<BranchAvailability>()
            .Produces(StatusCodes.Status404NotFound);
    }

    private static void MapBookings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reservations")
            .WithTags("Reservations");

        // One place enforcing the idempotency key, exactly as the table group does. GET carries no
        // body, so the filter finds nothing to check.
        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapPost("/", CreateAsync)
            .WithName("CreateReservation")
            .WithSummary("Book a table")
            .WithDescription(
                "Requires a verified diner. Idempotent on clientCommandId: a retry returns the "
                + "original booking and creates nothing. Answers 409 with the clashing window and "
                + "a fresh availability snapshot when the table went first, 422 with a specific "
                + "code when a branch rule refuses, and a retryable 503 when the table's lock "
                + "could not be had in time.")
            .Produces<ReservationView>()
            .Produces<UnifiedErrorEnvelope>(StatusCodes.Status409Conflict)
            .Produces<UnifiedErrorEnvelope>(StatusCodes.Status422UnprocessableEntity)
            .Produces<UnifiedErrorEnvelope>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/mine", GetMineAsync)
            .WithName("GetMyReservations")
            .WithSummary("The calling diner's own bookings, upcoming and past")
            .Produces<MyReservations>();

        group.MapPost("/{id:guid}/cancel", CancelAsync)
            .WithName("CancelReservation")
            .WithSummary("Cancel a booking")
            .WithDescription(
                "A diner may only cancel their own. Free until the branch's cancellation deadline "
                + "and still allowed after it - a late cancellation is far better than a no-show - "
                + "with the lateness recorded on the booking.")
            .Produces<ReservationView>();

        group.MapPost("/{id:guid}/approve", ApproveAsync)
            .WithName("ApproveReservation")
            .WithSummary("Accept a booking that is waiting for approval")
            .WithDescription($"Requires {ReservationPolicies.ManagerOrAbove}, scoped to the branch.")
            .Produces<ReservationView>();

        group.MapPost("/{id:guid}/reject", RejectAsync)
            .WithName("RejectReservation")
            .WithSummary("Decline a booking that is waiting for approval")
            .WithDescription($"Requires {ReservationPolicies.ManagerOrAbove}, scoped to the branch.")
            .Produces<ReservationView>();
    }

    private static async Task<IResult> GetAvailabilityAsync(
        Guid branchId,
        IAvailabilityQuery availability,
        CancellationToken cancellationToken,
        DateOnly? date = null,
        TimeOnly? time = null,
        int partySize = ReservationPolicies.DefaultPartySize)
    {
        var result = await availability.GetAvailabilityAsync(
            new AvailabilityRequest(branchId, partySize, date, time), cancellationToken);

        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        CreateReservationRequest request,
        IReservationService reservations,
        CancellationToken cancellationToken)
    {
        var view = await reservations.CreateAsync(
            new CreateReservationCommand(
                request.BranchId,
                request.TableId,
                request.Date,
                request.Time,
                request.PartySize,
                request.GuestName,
                request.GuestPhone,
                request.ClientCommandId,
                request.StayHint),
            cancellationToken);

        // A replay is answered 200, not 201: the second request created nothing, and telling a
        // client it did is how a retry ends up looking like a second booking in its own logs.
        return view.WasReplay
            ? Results.Ok(view)
            : Results.Created($"/api/reservations/{view.Id}", view);
    }

    private static async Task<IResult> GetMineAsync(
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.GetMineAsync(cancellationToken));

    private static async Task<IResult> CancelAsync(
        Guid id,
        CancelReservationRequest? request,
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.CancelAsync(
            new CancelReservationCommand(id, request?.Reason), cancellationToken));

    private static async Task<IResult> ApproveAsync(
        Guid id,
        DecideReservationRequest? request,
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.ApproveAsync(
            new DecideReservationCommand(id, request?.Reason), cancellationToken));

    private static async Task<IResult> RejectAsync(
        Guid id,
        DecideReservationRequest? request,
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.RejectAsync(
            new DecideReservationCommand(id, request?.Reason), cancellationToken));
}

/// <summary>
/// The authorization policies these endpoints are meant to carry, named in one place.
/// </summary>
/// <remarks>
/// <para>
/// Authentication is a parallel task and its policies are not registered in this build yet.
/// Calling <c>RequireAuthorization</c> with a policy name nothing has registered fails at startup,
/// so the endpoints enforce their rules through <c>ICurrentActor</c> in the service for now -
/// a verified diner may only read and cancel their own bookings, and approve or reject is a
/// manager or owner scoped to the branch. That check is the real one and does not move.
/// </para>
/// <para>
/// When the policies land, attaching them is one <c>RequireAuthorization</c> per route using these
/// constants. They are declared here so the intended surface is written down rather than
/// remembered.
/// </para>
/// </remarks>
public static class ReservationPolicies
{
    /// <summary>A diner whose account is verified. Booking, cancelling, and reading their own list.</summary>
    public const string VerifiedDiner = "VerifiedDiner";

    /// <summary>Manager or owner, scoped to the branch. Approving and rejecting.</summary>
    public const string ManagerOrAbove = "ManagerOrAbove";

    /// <summary>
    /// The party size assumed when a browser does not say. Two is the commonest booking and the
    /// least restrictive useful default: it excludes nothing a larger party would have seen.
    /// </summary>
    public const int DefaultPartySize = 2;
}
