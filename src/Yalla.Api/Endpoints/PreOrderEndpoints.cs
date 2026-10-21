using System.ComponentModel.DataAnnotations;
using Yalla.Api.Authorization;
using Yalla.Api.ApplicationExtensions;
using Yalla.Application.Reservations;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Pre-orders on a booking: the guest's (app and manage link), the floor tablet's rail, the console's settings.
/// </summary>
/// <remarks>
/// Rules and their reasons: <c>docs/reservations.md</c>, "Pre-orders". A pre-order is the dishes a guest chose ahead of a
/// booking, editable until two hours before it; the waiter sees it from the branch's minutes before and sends it to the
/// kitchen - before the party sits, it is a held kitchen ticket; when they sit, the first order on their tab.
/// </remarks>
public static class PreOrderEndpoints
{
    private const string StateDescription =
        "`preOrder.state`: 1 Editable (until `editableUntilUtc`, two hours before the start), 2 Locked, 3 Sent (the "
        + "kitchen has it - whatever the booking does next), 4 NotComing (a waiter marked the party not coming; still "
        + "sendable), 5 Cancelled (the booking was cancelled, declined or blocked, or ended without a send - undoing a "
        + "no-show brings it back). Prices are **locked**: a dish keeps the price it had when it first entered the "
        + "pre-order. A line whose dish has since sold out or been excluded reads `availableNow: false`.";

    private const string GuestRefusals =
        "`409 reservation-not-changeable` (not pending/confirmed, or started); `409 pre-order-already-sent`; "
        + "`409 pre-order-locked` (`context.lockedAtUtc`: two hours before the start); `409 pre-orders-not-accepted` "
        + "(`context.branchId`; saving only - clearing is always allowed); `409 client-command-id-in-use`; "
        + "`409 concurrent-update` (two saves at once - read and retry).";

    public static IEndpointRouteBuilder MapPreOrderEndpoints(this IEndpointRouteBuilder app)
    {
        MapGuest(app);
        MapManageLink(app);
        MapFloor(app);
        MapSettings(app);

        return app;
    }

    // ---------------------------------------------------------------- the guest, in the app

    private static void MapGuest(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reservations")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner);

        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapPut("/{reservationId:guid}/pre-order", ReplaceAsync)
            .WithName("replacePreOrder")
            .WithSummary("Save the booking's pre-order: the whole list of dishes, and a note")
            .WithDescription(
                "Replaces the pre-order with `lines` (1 to 30, in display order; `quantity` 1 to 20, at most 60 dishes "
                + "in all; a line's `note` at most 200 characters) and `note` (at most 500). The guest's own booking "
                + "only - anybody else's is 404, as for an id that is no booking. Until **two hours before the start**. "
                + "Idempotent on `clientCommandId`: a retry answers the pre-order as it stands, `wasReplay: true`.\n\n"
                + "Dishes: on this branch's menu as a diner sees it, not sold out, not excluded from pre-orders "
                + "(`availableForPreOrder` on the menu). Anything else is **422 `pre-order-items-unavailable`** with "
                + "`context.items[]` = `{ menuItemId, reason }` for every one (1 NotOnMenu, 2 SoldOut, 3 NotForPreOrder), "
                + "and nothing is saved. Bounds are 422 `validation-failed` naming `lines`, `lines[i].quantity`, "
                + "`lines[i].note` or `note`.\n\n"
                + "The first save schedules **\"Are you on your way?\"** 45 minutes before the start (push "
                + "`reservation.on-your-way` and feed `booking-on-your-way`); clearing withdraws it.\n\n"
                + StateDescription)
            .Produces<PreOrderResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking of yours.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, GuestRefusals)
            .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity, "A bound broken, or dishes that cannot be pre-ordered.");

        group.MapPost("/{reservationId:guid}/pre-order/clear", ClearAsync)
            .WithName("clearPreOrder")
            .WithSummary("Remove the booking's pre-order")
            .WithDescription(
                "Under the same window as saving, and allowed whether or not the branch still takes pre-orders. "
                + "Clearing a booking with none answers 200 with no `preOrder`. Idempotent on `clientCommandId`.")
            .Produces<PreOrderResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking of yours.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, GuestRefusals);

        group.MapPost("/{reservationId:guid}/on-my-way", OnMyWayAsync)
            .WithName("confirmOnMyWay")
            .WithSummary("\"Yes, on my way\"")
            .WithDescription(
                "The answer to \"are you on your way?\", asked of every booking 30 minutes before (45 while a pre-order "
                + "waits) - taken from two hours before the start "
                + "until the booking ends. Recorded once (`guestConfirmedOnWayAtUtc` on the booking; the waiter's card "
                + "says \"Guest confirmed\"); a second tap answers the first time, `wasReplay: true`. Moving the booking "
                + "to another start forgets the answer. No body. \"Can't come\" is the booking's ordinary cancel. A "
                + "booking not confirmed or seated, more than two hours away, or over: **409 `on-my-way-not-open`** "
                + "(`context`: `reservationId`, `status`, `opensAtUtc`).")
            .Produces<OnMyWayResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking of yours.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`on-my-way-not-open`: the booking is not expecting anybody, or not yet (`context.opensAtUtc`).");
    }

    // ---------------------------------------------------------------- the guest, from the manage link

    private static void MapManageLink(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public")
            .WithTags(EndpointConventions.DinerTag)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingExtensions.PublicPolicy);

        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapPut("/bookings/{token}/pre-order", ReplaceByLinkAsync)
            .WithName("replacePublicBookingPreOrder")
            .WithSummary("Save a booking's pre-order from its manage link")
            .WithDescription(
                "The app's `replacePreOrder` for somebody holding nothing but the link - the same rules, refusals and "
                + "answer. The menu is the branch's (`GET /api/public/branches/{branchId}/menu`), its id from the "
                + "branch page the link's `venueSlug`/`branchSlug` name.\n\n"
                + StateDescription)
            .Produces<PreOrderResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "The link is not valid.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, GuestRefusals)
            .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity, "A bound broken, or dishes that cannot be pre-ordered.");

        group.MapPost("/bookings/{token}/pre-order/clear", ClearByLinkAsync)
            .WithName("clearPublicBookingPreOrder")
            .WithSummary("Remove a booking's pre-order from its manage link")
            .Produces<PreOrderResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "The link is not valid.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, GuestRefusals);

        group.MapPost("/bookings/{token}/on-my-way", OnMyWayByLinkAsync)
            .WithName("confirmPublicBookingOnMyWay")
            .WithSummary("\"Yes, on my way\" from the manage link")
            .WithDescription(
                "As `confirmOnMyWay`. The link's read says when to ask: `onYourWayAsked`. \"Can't come\" is the link's "
                + "cancel.")
            .Produces<OnMyWayResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "The link is not valid.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`on-my-way-not-open`: the booking is not expecting anybody, or not yet (`context.opensAtUtc`).");
    }

    // ---------------------------------------------------------------- the floor tablet

    private static void MapFloor(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/branches/{branchId:guid}/pre-orders/due", ListDueAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove, YallaPolicies.BranchScoped)
            .WithName("listDuePreOrders")
            .WithSummary("The pre-order rail: the cards due now")
            .WithDescription(
                "Bookings of this branch with a pre-order: **confirmed** from `dueAtUtc` (the start minus the branch's "
                + "`minutesBefore`) until the booking ends, or **seated** while the pre-order is not yet on their tab. "
                + "Soonest first. Each card: table, time, party, guest name and `guestPhone` to call (empty for a "
                + "platform admin), `guestConfirmedOnWayAtUtc` (\"Guest confirmed\" - no call needed), the guest record "
                + "(as on the awaiting cards), the pre-order, who sent it or marked the party not coming, and the tab "
                + "once it is on one.\n\n"
                + "`nextDueAtUtc`: when the next card appears - read again then. Read again on change-stream signal 6 "
                + "(`PreOrderChanged`) and 5. Waiter or above at the branch, from the stored staff row.")
            .Produces<DuePreOrders>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not floor staff at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        var group = app.MapGroup("/api/reservations")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove);

        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapPost("/{reservationId:guid}/pre-order/send", SendAsync)
            .WithName("sendPreOrder")
            .WithSummary("\"Coming - send to kitchen\"")
            .WithDescription(
                "The pre-order becomes a kitchen ticket at once (`kitchenStatus` 2 InKitchen, like a waiter's order). "
                + "**Party not seated yet:** a held ticket on the kitchen screen with the table and a `preOrder` marker "
                + "and no `tabId`; when the party is seated on the booking it becomes the first order on their tab - "
                + "same id, locked prices, its kitchen progress kept (a tab is opened for the sitting if it has none). "
                + "**Already seated:** straight onto their tab.\n\n"
                + "From two hours before the start until the booking ends (confirmed), or while the party's sitting is "
                + "open (seated); otherwise **409 `pre-order-not-sendable`** (`context`: `reservationId`, `status`, "
                + "`sendableFromUtc`, `endUtc`). Already sent: 200 `wasReplay: true` - two tablets make one ticket. "
                + "The party's tab closing: 409 `tab-not-accepting-orders`. Takes the table's lock, as seating does: "
                + "503 `table-lock-timeout` is retryable. Waiter or above at the booking's branch. Online only.")
            .Produces<DuePreOrder>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not floor staff at this booking's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking, or it has no pre-order.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`pre-order-not-sendable` or `tab-not-accepting-orders`.")
            .ProducesProblem<Errors.LockTimeoutProblem>(StatusCodes.Status503ServiceUnavailable, "The table is busy; retry.");

        group.MapPost("/{reservationId:guid}/pre-order/not-coming", NotComingAsync)
            .WithName("markPreOrderNotComing")
            .WithSummary("\"Not coming / no answer\"")
            .WithDescription(
                "Nothing is sent; the card stays (`preOrder.state` 4) and **send stays possible** until the booking "
                + "ends. A confirmed booking from two hours before its start until it ends; otherwise 409 "
                + "`pre-order-not-sendable`. Sent already: 409 `pre-order-already-sent`. Marked already: 200 "
                + "`wasReplay: true`. A party that was sent a pre-order and never comes is marked a no-show as always.")
            .Produces<DuePreOrder>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not floor staff at this booking's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking, or it has no pre-order.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`pre-order-not-sendable` or `pre-order-already-sent`.");
    }

    // ---------------------------------------------------------------- the console

    private static void MapSettings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}/pre-order-settings")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapGet("/", GetSettingsAsync)
            .WithName("getPreOrderSettings")
            .WithSummary("Whether the branch takes pre-orders, when the waiter sees them, and which dishes are excluded")
            .Produces<PreOrderSettingsView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/", PutSettingsAsync)
            .WithName("putPreOrderSettings")
            .WithSummary("Save the branch's pre-order settings")
            .WithDescription(
                "`acceptsPreOrders` (off by default), `minutesBefore` (when the waiter's card appears; 5 to 120, "
                + "default 30 - never earlier than the guest's two-hour edit lock) and `excludedMenuItemIds`, the "
                + "**whole** list of this branch's dishes guests may not pre-order (every other dish may be). Out of "
                + "bounds, or a dish not on this branch's menu: 422 `validation-failed` naming `minutesBefore` or "
                + "`excludedMenuItemIds`. All three are required: one left out is 422 `validation-failed` naming it, "
                + "never a silent reset. Switching off keeps existing pre-orders working; guests may still clear them. "
                + "A save puts signal 6 on the change stream for the bookings whose rail card it reaches, so tablets "
                + "re-read `pre-orders/due`.")
            .Produces<PreOrderSettingsView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<Errors.ValidationFailedProblem>(StatusCodes.Status422UnprocessableEntity, "`context.fields` names each.");
    }

    // ---------------------------------------------------------------- handlers

    private static async Task<IResult> ReplaceAsync(
        Guid reservationId, ReplacePreOrderRequest request, IPreOrderService preOrders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await preOrders.ReplaceAsync(
            new ReplacePreOrderCommand(reservationId, Lines(request), request.Note, request.ClientCommandId),
            cancellationToken));
    }

    private static async Task<IResult> ClearAsync(
        Guid reservationId, PreOrderCommandRequest request, IPreOrderService preOrders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await preOrders.ClearAsync(reservationId, request.ClientCommandId, cancellationToken));
    }

    private static async Task<IResult> OnMyWayAsync(
        Guid reservationId, IPreOrderService preOrders, CancellationToken cancellationToken) =>
        Results.Ok(await preOrders.ConfirmOnMyWayAsync(reservationId, cancellationToken));

    private static async Task<IResult> ReplaceByLinkAsync(
        string token, ReplacePreOrderRequest request, IPreOrderService preOrders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await preOrders.ReplaceByManageTokenAsync(
            token,
            new ReplacePreOrderByManageTokenCommand(Lines(request), request.Note, request.ClientCommandId),
            cancellationToken));
    }

    private static async Task<IResult> ClearByLinkAsync(
        string token, PreOrderCommandRequest request, IPreOrderService preOrders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await preOrders.ClearByManageTokenAsync(token, request.ClientCommandId, cancellationToken));
    }

    private static async Task<IResult> OnMyWayByLinkAsync(
        string token, IPreOrderService preOrders, CancellationToken cancellationToken) =>
        Results.Ok(await preOrders.ConfirmOnMyWayByManageTokenAsync(token, cancellationToken));

    private static async Task<IResult> ListDueAsync(
        Guid branchId, IPreOrderService preOrders, CancellationToken cancellationToken) =>
        Results.Ok(await preOrders.ListDueAsync(branchId, cancellationToken));

    private static async Task<IResult> SendAsync(
        Guid reservationId, PreOrderCommandRequest request, IPreOrderService preOrders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await preOrders.SendAsync(
            new PreOrderStaffCommand(reservationId, request.ClientCommandId), cancellationToken));
    }

    private static async Task<IResult> NotComingAsync(
        Guid reservationId, PreOrderCommandRequest request, IPreOrderService preOrders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await preOrders.MarkNotComingAsync(
            new PreOrderStaffCommand(reservationId, request.ClientCommandId), cancellationToken));
    }

    private static async Task<IResult> GetSettingsAsync(
        Guid branchId, IPreOrderService preOrders, CancellationToken cancellationToken) =>
        Results.Ok(await preOrders.GetSettingsAsync(branchId, cancellationToken));

    private static async Task<IResult> PutSettingsAsync(
        Guid branchId, PreOrderSettingsRequest request, IPreOrderService preOrders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await preOrders.SaveSettingsAsync(
            branchId,
            new PreOrderSettings(request.AcceptsPreOrders!.Value, request.MinutesBefore!.Value, request.ExcludedMenuItemIds!),
            cancellationToken));
    }

    private static IReadOnlyList<PreOrderLineCommand> Lines(ReplacePreOrderRequest request) =>
        [.. (request.Lines ?? []).Select(l => new PreOrderLineCommand(l.MenuItemId, l.Quantity, l.Note))];
}

/// <summary>Body of <c>PUT .../pre-order</c>: the whole pre-order.</summary>
/// <param name="Lines">The dishes, in display order. Bounds are the service's, named per field.</param>
/// <param name="Note">A booking-level note for the kitchen, at most 500 characters.</param>
/// <param name="ClientCommandId">Idempotency key; reuse it only to retry this same save.</param>
public sealed record ReplacePreOrderRequest(
    IReadOnlyList<PreOrderLineRequest>? Lines,
    string? Note,
    [Required] Guid ClientCommandId) : IClientCommandRequest;

/// <summary>One dish on a pre-order.</summary>
/// <param name="MenuItemId">The dish, from the branch's menu.</param>
/// <param name="Quantity">1 to 20.</param>
/// <param name="Note">For the kitchen, at most 200 characters.</param>
public sealed record PreOrderLineRequest(Guid MenuItemId, int Quantity, string? Note = null);

/// <summary>Body of the pre-order's other commands: clear, send, not coming.</summary>
public sealed record PreOrderCommandRequest([Required] Guid ClientCommandId) : IClientCommandRequest;

/// <summary>Body of <c>PUT /api/branches/{branchId}/pre-order-settings</c>. All three are required.</summary>
/// <param name="AcceptsPreOrders">Whether guests may pre-order.</param>
/// <param name="MinutesBefore">When the waiter's card appears, 5 to 120 minutes before the start.</param>
/// <param name="ExcludedMenuItemIds">The whole list of dishes guests may not pre-order; empty for none.</param>
public sealed record PreOrderSettingsRequest(
    // Nullable so [Required] can fire: a missing bool binds false and a missing list binds empty, and either would
    // silently switch pre-orders off or put every dish back on - a 422 naming the field instead.
    [Required] bool? AcceptsPreOrders,
    [Required] int? MinutesBefore,
    [Required] IReadOnlyList<Guid>? ExcludedMenuItemIds);
