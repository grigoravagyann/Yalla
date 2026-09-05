using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
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
/// Identity comes from the policies, not from the handlers. Booking, cancelling and the diner's own
/// list carry <see cref="YallaPolicies.VerifiedDiner"/>; approving and rejecting carry
/// <see cref="YallaPolicies.ManagerOrAbove"/>. Availability is the only anonymous endpoint outside
/// the sign-in flows: somebody deciding whether to eat here must be able to see the room before
/// they are asked who they are.
/// </para>
/// <para>
/// Two boundaries the policies cannot draw are drawn in the service instead, deliberately.
/// <b>Ownership</b> - a diner may only read and cancel their own bookings - is a fact about a row,
/// not about a token. And <b>branch scope</b> on approve and reject cannot use
/// <see cref="YallaPolicies.BranchScoped"/>, because that policy compares a claim against a
/// <c>branchId</c> route value and these routes are addressed by reservation id; it fails closed
/// when it cannot find one, which is the right behaviour and the reason not to apply it here. The
/// service resolves the booking's branch and checks it against the acting staff member's own venue
/// and branch.
/// </para>
/// <para>
/// Exceptions are not caught here either. <c>UnifiedExceptionHandler</c> maps them centrally, so a
/// lost race becomes 409 with the clashing window and a fresh floor, each branch rule becomes 422
/// with its own code, and a lock timeout becomes a retryable 503 - the same way no matter which
/// endpoint raised it.
/// </para>
/// </remarks>
public static class ReservationEndpoints
{
    private const string ConflictDescription =
        "Someone else took that table between the diner seeing it free and confirming. The body's "
        + "`context` carries the clashing window and a fresh availability snapshot, so the app can "
        + "redraw the floor and show what changed rather than only saying no. Never retried "
        + "automatically: this answer will not change on a repeat.";

    private const string RejectedDescription =
        "A branch rule refused the booking. Each rule has its own `code` - "
        + "`reservation-party-exceeds-capacity`, `reservation-outside-opening-hours` and so on - "
        + "with the numbers behind it in `context`, so the app can say which table to pick instead "
        + "rather than showing a generic failure.";

    private const string LockTimeoutDescription =
        "The table's booking lock could not be had in time. `context.retryable` is true: unlike the "
        + "409, this one is worth retrying - with the same `clientCommandId`, so a retry that "
        + "crosses with a late-committing original is recognised as a replay.";

    private const string StateDescription =
        "The booking is not in a state that permits this - cancelling one that is already seated, "
        + "or approving one that was never pending.";

    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        MapAvailability(app);
        MapDinerBookings(app);
        MapStaffDecisions(app);

        return app;
    }

    private static void MapAvailability(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/branches/{branchId:guid}/availability", GetAvailabilityAsync)
            .WithTags(EndpointConventions.DinerTag)
            .WithName("getBranchAvailability")

            // Anonymous, alone among the endpoints that are not sign-in flows. Browsing needs no
            // account: somebody deciding whether to eat here has to see the room before they are
            // asked who they are.
            .AllowAnonymous()
            .WithSummary("Which tables a branch can offer for one slot")
            .WithDescription(
                "Every table in the branch with its floor-plan geometry, its derived state at the "
                + "requested time, whether it can be booked for this party - and if not, the one "
                + "specific reason - and the window the diner may have. Assembled in a single "
                + "query. Returns no guest names and no money. Omit date and time for 'now, at "
                + "the branch'.")
            .Produces<BranchAvailability>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests,
                "Too many availability queries from this address. Generous but finite - the endpoint "
                + "is anonymous and a phone will poll it.")

            // Anonymous and the hottest endpoint in the product, so it is the one that most needs
            // a ceiling. Per address, because there is no account to partition by.
            .RequireRateLimiting(RateLimitingExtensions.AvailabilityPolicy);
    }

    private static void MapDinerBookings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reservations")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner);

        // One place enforcing the idempotency key, exactly as the table group does. GET carries no
        // body, so the filter finds nothing to check.
        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapPost("/", CreateAsync)
            .WithName("createReservation")
            .WithSummary("Book a table")
            .WithDescription(
                "Idempotent on `clientCommandId`: a retry returns the original booking and creates "
                + "nothing, answering 200 rather than 201. Lands as `Confirmed`, or "
                + "`PendingApproval` when the branch approves every booking, the party is over the "
                + "branch's threshold, or the diner is over the rolling no-show threshold.")
            .Produces<ReservationView>(StatusCodes.Status201Created)
            .Produces<ReservationView>(StatusCodes.Status200OK)
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch or table.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, ConflictDescription)
            .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity, RejectedDescription)
            .ProducesProblemDetails(StatusCodes.Status503ServiceUnavailable, LockTimeoutDescription);

        group.MapGet("/mine", GetMineAsync)
            .WithName("getMyReservations")
            .WithSummary("The calling diner's own bookings, upcoming and past")
            .WithDescription(
                "Upcoming means still going to happen: not finished, and not already called off. A "
                + "booking cancelled for tomorrow belongs in the history, not at the top of the "
                + "screen.")
            .Produces<MyReservations>();

        group.MapPost("/{id:guid}/cancel", CancelAsync)
            .WithName("cancelReservation")
            .WithSummary("Cancel a booking")
            .WithDescription(
                "A diner may only cancel their own; anyone else's is 403. Free until the branch's "
                + "cancellation deadline and still allowed after it - a late cancellation is far "
                + "better than a no-show - with `cancelledAfterDeadline` recording which it was.")
            .Produces<ReservationView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "This booking belongs to somebody else.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, StateDescription);
    }

    private static void MapStaffDecisions(IEndpointRouteBuilder app)
    {
        // A separate group because the identity is different. ManagerOrAbove carries no route
        // dependency, so it works on a route addressed by reservation id; BranchScoped would not,
        // and the service does that half.
        var group = app.MapGroup("/api/reservations")
            .WithTags(EndpointConventions.StaffTag, EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove);

        group.MapPost("/{id:guid}/approve", ApproveAsync)
            .WithName("approveReservation")
            .WithSummary("Accept a booking that is waiting for approval")
            .WithDescription(
                "Scoped to the acting staff member's own branch and venue. A manager of one venue "
                + "cannot decide another's bookings by guessing an id.")
            .Produces<ReservationView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not a manager of this booking's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, StateDescription);

        group.MapPost("/{id:guid}/reject", RejectAsync)
            .WithName("rejectReservation")
            .WithSummary("Decline a booking that is waiting for approval")
            .WithDescription("Refuses a booking that was already confirmed: the diner has been told it is theirs.")
            .Produces<ReservationView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not a manager of this booking's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, StateDescription);
    }

    private static async Task<IResult> GetAvailabilityAsync(
        Guid branchId,
        IAvailabilityQuery availability,
        CancellationToken cancellationToken,
        DateOnly? date = null,
        TimeOnly? time = null,
        int partySize = DefaultPartySize)
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

    /// <summary>
    /// The party size assumed when a browser does not say. Two is the commonest booking and the
    /// least restrictive useful default: it excludes nothing a larger party would have seen.
    /// </summary>
    private const int DefaultPartySize = 2;
}
