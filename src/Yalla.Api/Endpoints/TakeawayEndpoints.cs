using System.ComponentModel.DataAnnotations;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Application.Takeaway;
using Yalla.Domain.Takeaway;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Takeaway: the diner orders from the menu to pick up later, the floor tablet accepts and hands over, the kitchen
/// cooks, the console sets it up. Nothing is paid in the app - the guest pays at the counter.
/// </summary>
/// <remarks>Rules and their reasons: <c>docs/takeaway.md</c>.</remarks>
public static class TakeawayEndpoints
{
    private const string StatusDescription =
        "`status`: 1 Sent (waiting for the venue; the guest may cancel), 2 Accepted (\"Accepted · being prepared\" - in "
        + "the kitchen, ready at `readyAtUtc`), 3 Ready (waiting at the counter), 4 Collected (handed over and paid), "
        + "5 Declined (`declinedBecause` 1 the venue said no - `declineReason` when it said why - or 2 nobody answered "
        + "within five minutes), 6 Cancelled (by the guest, while sent), 7 NoShow (ready, and nobody came within thirty "
        + "minutes). Local times are `HH:mm` on the branch's wall clock.";

    private const string StaffRefusals =
        "`409 takeaway-status-conflict` (`context.orderId`, `status` - where it is now: redraw it). The move already made "
        + "is answered 200 `wasReplay: true` - two tablets, one answer.";

    public static IEndpointRouteBuilder MapTakeawayEndpoints(this IEndpointRouteBuilder app)
    {
        MapDiner(app);
        MapFloor(app);
        MapSettings(app);

        return app;
    }

    // ---------------------------------------------------------------- the diner

    private static void MapDiner(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diner")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner);

        group.MapGet("/branches/{branchId:guid}/takeaway", GetAvailabilityAsync)
            .WithName("getTakeawayAvailability")
            .WithSummary("Whether the place takes takeaway now, and the pickup times on offer")
            .WithDescription(
                "`enabled` false: the venue does not take takeaway - no button. `open`: orders are taken now - inside "
                + "today's opening hours and before closing minus the venue's last-order minutes. Open: `asapReadyAtUtc` / "
                + "`asapReadyAt` (now plus `prepMinutes`), `slots` (quarter hours from then to `lastPickupAt`, as local "
                + "`HH:mm`; a time that would put more orders in the kitchen than the venue allows is left out) and `busy` "
                + "(the kitchen is full: \"as soon as possible\" is refused, a slot may still work). Closed: `opensAt` / "
                + "`opensAtUtc` - the next opening an order can be taken from, within a week (\"Takeaway from opening, "
                + "10:00\"). Read it again when the checkout opens: slots move with the clock.")
            .Produces<TakeawayAvailability>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`branch-unavailable`: the venue is suspended or the branch switched off.");

        group.MapPost("/takeaway-orders", PlaceAsync)
            .AddEndpointFilter<ClientCommandIdFilter>()
            .RequireRateLimiting(RateLimitingExtensions.DinerWritePolicy)
            .WithName("placeTakeawayOrder")
            .WithSummary("Order to take away")
            .WithDescription(
                "`pickup` is `{ \"asap\": true }` or `{ \"at\": \"19:15\" }` - one of the availability's `slots`. `lines` 1 to "
                + "30, `quantity` 1 to 20, at most 60 dishes in all; `note` at most 500 characters. Names and prices are "
                + "taken as they stand now. **Idempotent on `clientCommandId`**: a retry answers the order the first try "
                + "made, `wasReplay: true`.\n\n"
                + "A proved phone is required (403 `phone-not-verified`, as for booking). Refusals, before anything is "
                + "saved: 422 `validation-failed` naming `lines`, `lines[i].quantity`, `note`, `pickup` or `pickup.at`; "
                + "409 `takeaway-not-accepted`; 409 `takeaway-closed` (`context.opensAtUtc`, `opensAt`); 409 "
                + "`takeaway-busy` (ASAP while the kitchen is full - `context.maxInProgress`); 409 "
                + "`takeaway-slot-unavailable` (`context.slots`: the ones on offer now); 422 `takeaway-items-unavailable` "
                + "(`context.items[]` = `{ menuItemId, reason }`: 1 NotOnMenu, 2 SoldOut, 3 NotForTakeaway - every one); "
                + "409 `client-command-id-in-use`.\n\n"
                + "The order is `Sent` with `acceptDeadlineUtc` five minutes on - accepted on the tablet, or declined for "
                + "nobody answering and the guest told. A branch that accepts automatically answers `Accepted` at once, "
                + "ready when asked for. The code (`T-482`) is what the guest says at the counter.\n\n"
                + StatusDescription)
            .Produces<TakeawayOrderView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`phone-not-verified`: prove the number first.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "`takeaway-not-accepted`, `takeaway-closed`, `takeaway-busy`, `takeaway-slot-unavailable`, `branch-unavailable` "
                + "or `client-command-id-in-use`.")
            .ProducesProblemDetails(
                StatusCodes.Status422UnprocessableEntity, "`validation-failed` or `takeaway-items-unavailable`.");

        group.MapGet("/takeaway-orders/{orderId:guid}", GetMineAsync)
            .WithName("getDinerTakeawayOrder")
            .WithSummary("One of the diner's takeaway orders - the status screen")
            .WithDescription(
                "The code, the status with a time per step, the lines at their ordered prices and the total, the branch's "
                + "name, address, number and position (call, directions), and `declineReason` / `declinedBecause` when "
                + "declined. Poll while it is active, in the foreground. Somebody else's order is **404**.\n\n"
                + StatusDescription)
            .Produces<TakeawayOrderView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such order of this diner's.");

        group.MapPost("/takeaway-orders/{orderId:guid}/cancel", CancelAsync)
            .WithName("cancelTakeawayOrder")
            .WithSummary("Cancel a takeaway order the venue has not answered yet")
            .WithDescription(
                "Only while `status` is 1 Sent - after that the venue is cooking it, and changing it is a phone call: 409 "
                + "`takeaway-status-conflict` (`context.status`). Cancelled already: 200 `wasReplay: true`. No body.")
            .Produces<TakeawayOrderView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such order of this diner's.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`takeaway-status-conflict`: the venue has answered it.");
    }

    // ---------------------------------------------------------------- the floor tablet and the kitchen

    private static void MapFloor(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/branches/{branchId:guid}/takeaway-orders", ListForBranchAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove, YallaPolicies.BranchScoped)
            .WithName("listBranchTakeawayOrders")
            .WithSummary("The takeaway counter: active orders and today's finished ones")
            .WithDescription(
                "`active`: Sent (with `acceptDeadlineUtc` - the answer countdown), Accepted and Ready, oldest first. "
                + "`finishedToday`: Collected, NoShow and Declined placed on the branch's service day, newest first. "
                + "`sentCount` + `readyCount` is the Takeaway tab's badge. `noShowFromUtc` says when \"No-show\" may be "
                + "offered (thirty minutes after ready). `guestPhone` is empty for a platform admin. Poll it, as the rest "
                + "of the floor is polled. Waiter or above at the branch.\n\n"
                + StatusDescription)
            .Produces<BranchTakeawayOrders>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not floor staff at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        // Not BranchScoped: the route names an order. The service loads it and refuses a caller not at its branch.
        var floor = app.MapGroup("/api/takeaway-orders/{orderId:guid}")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove);

        floor.MapPost("/accept", AcceptAsync)
            .WithName("acceptTakeawayOrder")
            .WithSummary("Accept a takeaway order, ready at a time")
            .WithDescription(
                "`readyAt` (UTC) is the ready time the waiter set - the tablet starts from `requestedPickupUtc` and steps "
                + "it by five minutes. From now (a minute of clock skew allowed) to twelve hours on; otherwise 422 "
                + "`validation-failed` naming `readyAt`. The order goes to the kitchen screen with its TAKEAWAY band, and "
                + "the guest is told (push `takeaway.accepted`, feed `takeaway-accepted`). Accepting after the five "
                + "minutes is allowed while the sweep has not declined it. " + StaffRefusals)
            .Produces<StaffTakeawayOrderView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not floor staff at this order's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such order.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`takeaway-status-conflict`.")
            .ProducesProblem<Errors.ValidationFailedProblem>(StatusCodes.Status422UnprocessableEntity, "`readyAt` out of range.");

        floor.MapPost("/decline", DeclineAsync)
            .WithName("declineTakeawayOrder")
            .WithSummary("Decline a takeaway order")
            .WithDescription(
                "Only while Sent. `reason` (optional, at most 300 characters) is shown to the guest. The guest is told "
                + "(push `takeaway.declined`, feed `takeaway-declined`). " + StaffRefusals)
            .Produces<StaffTakeawayOrderView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not floor staff at this order's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such order.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`takeaway-status-conflict`.");

        // The kitchen's one move: KitchenOrAbove on the route, and the service confines Kitchen to it.
        app.MapPost("/api/takeaway-orders/{orderId:guid}/ready", ReadyAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.KitchenOrAbove)
            .WithName("markTakeawayOrderReady")
            .WithSummary("The kitchen: the takeaway order is ready")
            .WithDescription(
                "Accepted → Ready. The Kitchen role may make this move (and only this one); the floor too. The guest is "
                + "told \"ready, come and pick it up\" (push `takeaway.ready`, feed `takeaway-ready`). No body. "
                + StaffRefusals)
            .Produces<StaffTakeawayOrderView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not kitchen or floor staff at this order's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such order.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`takeaway-status-conflict`.");

        floor.MapPost("/collect", CollectAsync)
            .WithName("collectTakeawayOrder")
            .WithSummary("Paid and handed over")
            .WithDescription(
                "Ready → Collected. `method` 1 Cash or 2 Card (the venue's own terminal); the amount recorded is the total "
                + "(`paidAmountAmd`). Nothing is a fiscal receipt: the venue's cash register issues that. Missing or "
                + "unknown `method`: 422 `validation-failed`. " + StaffRefusals)
            .Produces<StaffTakeawayOrderView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not floor staff at this order's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such order.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`takeaway-status-conflict`.")
            .ProducesProblem<Errors.ValidationFailedProblem>(StatusCodes.Status422UnprocessableEntity, "`method` missing or unknown.");

        floor.MapPost("/no-show", NoShowAsync)
            .WithName("markTakeawayOrderNoShow")
            .WithSummary("Nobody came for a ready takeaway order")
            .WithDescription(
                "Ready → NoShow, from thirty minutes after it was ready (`noShowFromUtc`: the later of the promised time and "
                + "the kitchen's \"ready\"); earlier is 409 `takeaway-no-show-too-early` (`context.noShowFromUtc`). No body. "
                + StaffRefusals)
            .Produces<StaffTakeawayOrderView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not floor staff at this order's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such order.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict, "`takeaway-status-conflict` or `takeaway-no-show-too-early`.");
    }

    // ---------------------------------------------------------------- the console

    private static void MapSettings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}/takeaway-settings")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapGet("/", GetSettingsAsync)
            .WithName("getTakeawaySettings")
            .WithSummary("The branch's takeaway settings, with their bounds")
            .Produces<TakeawaySettingsView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/", PutSettingsAsync)
            .WithName("putTakeawaySettings")
            .WithSummary("Save the branch's takeaway settings")
            .WithDescription(
                "All five are required: `enabled` (off by default), `autoAccept` (off: staff accept on the tablet, and an "
                + "order nobody accepts within five minutes is declined and the guest told), `prepMinutes` (5 to 90 in "
                + "fives, default 20), `lastOrderMinutes` (0 to 120 in fifteens, default 30) and `maxInProgress` (1 to 30, "
                + "default 5). One left out, out of bounds or off its step is 422 `validation-failed` naming it. Dishes "
                + "served in the hall only are switched per dish: `availableForTakeaway` on `PATCH .../menu/items/{itemId}`.")
            .Produces<TakeawaySettingsView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<Errors.ValidationFailedProblem>(StatusCodes.Status422UnprocessableEntity, "`context.fields` names each.");
    }

    // ---------------------------------------------------------------- handlers

    private static async Task<IResult> GetAvailabilityAsync(
        Guid branchId, ITakeawayService takeaway, CancellationToken cancellationToken) =>
        Results.Ok(await takeaway.GetAvailabilityAsync(branchId, cancellationToken));

    private static async Task<IResult> PlaceAsync(
        PlaceTakeawayOrderRequest request, ITakeawayService takeaway, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await takeaway.PlaceAsync(
            new PlaceTakeawayOrderCommand(
                request.BranchId!.Value,
                request.Pickup!.Asap == true,
                request.Pickup.At,
                request.Note,
                [.. (request.Lines ?? []).Select(l => new TakeawayLineCommand(l.MenuItemId, l.Quantity))],
                request.ClientCommandId),
            cancellationToken));
    }

    private static async Task<IResult> GetMineAsync(Guid orderId, ITakeawayService takeaway, CancellationToken cancellationToken) =>
        Results.Ok(await takeaway.GetMineAsync(orderId, cancellationToken));

    private static async Task<IResult> CancelAsync(Guid orderId, ITakeawayService takeaway, CancellationToken cancellationToken) =>
        Results.Ok(await takeaway.CancelAsync(orderId, cancellationToken));

    private static async Task<IResult> ListForBranchAsync(
        Guid branchId, ITakeawayService takeaway, CancellationToken cancellationToken) =>
        Results.Ok(await takeaway.ListForBranchAsync(branchId, cancellationToken));

    private static async Task<IResult> AcceptAsync(
        Guid orderId, AcceptTakeawayOrderRequest request, ITakeawayService takeaway, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await takeaway.AcceptAsync(orderId, request.ReadyAt!.Value, cancellationToken));
    }

    private static async Task<IResult> DeclineAsync(
        Guid orderId, DeclineTakeawayOrderRequest? request, ITakeawayService takeaway, CancellationToken cancellationToken) =>
        Results.Ok(await takeaway.DeclineAsync(orderId, request?.Reason, cancellationToken));

    private static async Task<IResult> ReadyAsync(Guid orderId, ITakeawayService takeaway, CancellationToken cancellationToken) =>
        Results.Ok(await takeaway.MarkReadyAsync(orderId, cancellationToken));

    private static async Task<IResult> CollectAsync(
        Guid orderId, CollectTakeawayOrderRequest request, ITakeawayService takeaway, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.IsDefined(request.Method!.Value))
        {
            throw new Domain.FieldValidationException(
            [
                new Domain.FieldViolation(
                    TakeawayRules.Fields.Method, "The method is 1 Cash or 2 Card.", Domain.FieldBounds.Range, Min: 1, Max: 2,
                    Value: (int)request.Method.Value),
            ]);
        }

        return Results.Ok(await takeaway.CollectAsync(orderId, request.Method.Value, cancellationToken));
    }

    private static async Task<IResult> NoShowAsync(Guid orderId, ITakeawayService takeaway, CancellationToken cancellationToken) =>
        Results.Ok(await takeaway.MarkNoShowAsync(orderId, cancellationToken));

    private static async Task<IResult> GetSettingsAsync(Guid branchId, ITakeawayService takeaway, CancellationToken cancellationToken) =>
        Results.Ok(await takeaway.GetSettingsAsync(branchId, cancellationToken));

    private static async Task<IResult> PutSettingsAsync(
        Guid branchId, TakeawaySettingsRequest request, ITakeawayService takeaway, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await takeaway.SaveSettingsAsync(
            branchId,
            new TakeawaySettings(
                request.Enabled!.Value,
                request.AutoAccept!.Value,
                request.PrepMinutes!.Value,
                request.LastOrderMinutes!.Value,
                request.MaxInProgress!.Value),
            cancellationToken));
    }
}

/// <summary>Body of <c>POST /api/diner/takeaway-orders</c>.</summary>
/// <param name="BranchId">Where to order from.</param>
/// <param name="Pickup">When: <c>{ "asap": true }</c> or <c>{ "at": "19:15" }</c>.</param>
/// <param name="Note">For the venue, at most 500 characters.</param>
/// <param name="Lines">The dishes, in display order.</param>
/// <param name="ClientCommandId">Idempotency key; reuse it only to retry this same order.</param>
public sealed record PlaceTakeawayOrderRequest(
    [Required] Guid? BranchId,
    [Required] TakeawayPickupRequest? Pickup,
    string? Note,
    IReadOnlyList<TakeawayLineRequest>? Lines,
    [Required] Guid ClientCommandId) : IClientCommandRequest;

/// <summary>When to pick up: exactly one of the two.</summary>
/// <param name="Asap">As soon as possible: ready at now plus the venue's preparation time.</param>
/// <param name="At">A slot from the availability's <c>slots</c>, local <c>HH:mm</c>.</param>
public sealed record TakeawayPickupRequest(bool? Asap = null, string? At = null);

/// <summary>One dish on a takeaway order.</summary>
/// <param name="MenuItemId">The dish, from the branch's menu.</param>
/// <param name="Quantity">1 to 20.</param>
public sealed record TakeawayLineRequest(Guid MenuItemId, int Quantity);

/// <summary>Body of <c>POST /api/takeaway-orders/{orderId}/accept</c>.</summary>
/// <param name="ReadyAt">When it will be ready, UTC.</param>
public sealed record AcceptTakeawayOrderRequest([Required] DateTime? ReadyAt);

/// <summary>Body of <c>POST /api/takeaway-orders/{orderId}/decline</c>. May be empty.</summary>
/// <param name="Reason">Shown to the guest. At most 300 characters.</param>
public sealed record DeclineTakeawayOrderRequest(string? Reason = null);

/// <summary>Body of <c>POST /api/takeaway-orders/{orderId}/collect</c>.</summary>
/// <param name="Method">1 Cash, 2 Card.</param>
public sealed record CollectTakeawayOrderRequest([Required] TakeawayPaymentMethod? Method);

/// <summary>Body of <c>PUT /api/branches/{branchId}/takeaway-settings</c>. All five are required.</summary>
/// <param name="Enabled">Guests may order to take away.</param>
/// <param name="AutoAccept">Accepted without a tap.</param>
/// <param name="PrepMinutes">5 to 90, in fives.</param>
/// <param name="LastOrderMinutes">0 to 120, in fifteens.</param>
/// <param name="MaxInProgress">1 to 30.</param>
public sealed record TakeawaySettingsRequest(
    // Nullable so [Required] can fire: a missing bool binds false and would silently switch takeaway off.
    [Required] bool? Enabled,
    [Required] bool? AutoAccept,
    [Required] int? PrepMinutes,
    [Required] int? LastOrderMinutes,
    [Required] int? MaxInProgress);
