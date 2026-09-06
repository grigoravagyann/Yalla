using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.Menus;
using Yalla.Application.Ordering;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The menu, ordering, the live bill, the kitchen queue, calling a waiter, and cash.
/// </summary>
/// <remarks>
/// <para>
/// <b>One ordering route, two kinds of caller.</b> <c>POST /api/tabs/{tabId}/orders</c> accepts a
/// diner's phone under <c>TabParticipantCanOrder</c> and a waiter's tablet under
/// <c>WaiterOrAbove</c>, and the service works out which from the token. That is not convenience:
/// the live bill is only correct if every order goes through the system, including what is spoken
/// to a waiter, so the tablet is an order-entry point and this is the venue's till. A second route
/// for spoken orders would be a second source of truth.
/// </para>
/// <para>
/// <b>Nothing here is a fiscal receipt.</b> The cash endpoint records what the venue took so the
/// tab balances and the drawer reconciles. The venue's registered cash register still issues the
/// receipt - see <c>docs/billing.md</c>.
/// </para>
/// </remarks>
public static class OrderingEndpoints
{
    private const string NotOnTabDescription =
        "This token is for a different tab, the participant was removed, or the tab closed longer "
        + "ago than the receipt grace period. Decided by the policy, before the handler runs.";

    public static IEndpointRouteBuilder MapOrderingEndpoints(this IEndpointRouteBuilder app)
    {
        MapMenu(app);
        MapDinerOrdering(app);
        MapStaffOrdering(app);
        MapKitchen(app);
        MapServiceRequests(app);
        MapPayments(app);

        return app;
    }

    // ---------------------------------------------------------------------------------------
    // The menu. Anonymous: the scanner has not joined anything yet, and a pending participant
    // may read it with prices - that rule was set in Prompt 5 and this is where it starts to bite.
    // ---------------------------------------------------------------------------------------
    private static void MapMenu(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/branches/{branchId:guid}/menu", GetMenuAsync)
            .WithTags(EndpointConventions.DinerTag)
            .AllowAnonymous()
            .WithName("getBranchMenu")
            .WithSummary("The branch's menu")
            .WithDescription(
                "Categories in display order, each with its items, in one query.\n\n"
                + "**Sold-out items are returned with `isAvailable: false`, not hidden.** A dish that "
                + "silently vanishes looks like a broken menu and sends the diner to ask a waiter - "
                + "the exact question this feature exists to remove. Grey it out and keep the price.\n\n"
                + "**Unfinished items are excluded entirely, which is the opposite rule for the "
                + "opposite reason.** Since the photo requirement moved from create-time to "
                + "go-live-time an item can exist without a photo, a description, ingredients, "
                + "allergens, a portion size or a prep time - that is how a menu gets typed in before "
                + "it gets photographed - and none of those may reach a diner. Somebody reading an "
                + "empty allergen list reasonably concludes there are none.\n\n"
                + "So every descriptive field on this read is always present and `isComplete` is "
                + "always true. The console's `GET /api/branches/{branchId}/menu/manage` is where the "
                + "unfinished ones are visible.")
            .Produces<BranchMenuView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict, "The venue is suspended or no longer on Yalla.");
    }

    // ---------------------------------------------------------------------------------------
    // A diner ordering from their own phone, and reading their own share.
    // ---------------------------------------------------------------------------------------
    private static void MapDinerOrdering(IEndpointRouteBuilder app)
    {
        var ordering = app.MapGroup("/api/tabs/{tabId:guid}")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.TabParticipantCanOrder);

        ordering.AddEndpointFilter<ClientCommandIdFilter>();

        ordering.MapPost("/orders", PlaceOrderAsDinerAsync)
            .WithName("placeOrder")
            .WithSummary("Order from your own phone")
            .WithDescription(
                "Names and prices are **snapshotted onto every line** as they stand now, so a menu "
                + "edit next week cannot change a bill presented tonight.\n\n"
                + "A `isShared` line records **who is at the table at this moment**: a friend who "
                + "joins ten minutes later is not on the bottle, and one who is removed still is - "
                + "they were there when it was poured.\n\n"
                + "An unavailable item refuses the **whole** order, by name, rather than quietly "
                + "dropping it: a partial order is a decision made on your behalf that you discover "
                + "when the food arrives.\n\n"
                + "`estimatedReadyAtUtc` comes from the longest prep time on the order, not the sum - "
                + "a kitchen cooks an order together.")
            .Produces<OrderView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, NotOnTabDescription)
            .ProducesProblem<MenuItemUnavailableProblem>(
                StatusCodes.Status409Conflict,
                "`menu-item-unavailable`: a dish has sold out. `context.itemName` is which one - say "
                + "it, rather than failing the order generically. Nothing was placed.")
            .ProducesProblem<TabNotAcceptingOrdersProblem>(
                StatusCodes.Status409Conflict,
                "`tab-not-accepting-orders`: the bill has been asked for. Show the bill, not the menu.");

        var reads = app.MapGroup("/api/tabs/{tabId:guid}")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.TabParticipant);

        reads.MapGet("/shares", GetSharesAsDinerAsync)
            .WithName("getTabShares")
            .WithSummary("Who owes what")
            .WithDescription(
                "Every split is integer and every remainder goes to the host, so the shares sum to "
                + "the total **to the dram** - a property test asserts it over randomised tabs. "
                + "Service charge splits pro rata: order 30% of the food, pay 30% of the service.\n\n"
                + "Without `canSeeTableTotal` you get **your own share and no table aggregate** - the "
                + "members are absent from the body, not zeroed, so a hidden total cannot be rendered "
                + "as a free bill.\n\n"
                + "A removed participant's items stay on the bill and fall to the host, reported in "
                + "`absorbedFromRemovedAmd` rather than folded in silently.")
            .Produces<TabSharesView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, NotOnTabDescription);

        reads.MapGet("/events", GetEventsAsync)
            .WithName("getTabEvents")
            .WithSummary("Catch up on what happened")
            .WithDescription(
                "Everything after `afterSequence`, oldest first, for the phone that was in a lift "
                + "when the wine was ordered.\n\n"
                + "`sequence` is this event's place **on this tab**, counting from 1, and the order "
                + "is the order things happened - a payment that settles the bill is immediately "
                + "followed by the close, never the other way round. Keep the highest you have seen "
                + "and ask for everything after it.\n\n"
                + "**Ignore a `type` you do not recognise and keep your position**: new types will be "
                + "added and an old build must not break on a tab that used one.")
            .Produces<TabEventPage>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, NotOnTabDescription);
    }

    // ---------------------------------------------------------------------------------------
    // The tablet: spoken orders, voids, and a manager's discounts.
    // ---------------------------------------------------------------------------------------
    private static void MapStaffOrdering(IEndpointRouteBuilder app)
    {
        var staff = app.MapGroup("/api/tabs/{tabId:guid}")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove);

        staff.AddEndpointFilter<ClientCommandIdFilter>();

        staff.MapPost("/staff-orders", PlaceOrderAsStaffAsync)
            .WithName("placeOrderAsStaff")
            .WithSummary("Key in an order spoken at the table")
            .WithDescription(
                "The same code path as a diner's order, deliberately: the total is only right if "
                + "**every** order goes through the system.\n\n"
                + "`onBehalfOfParticipantId` is what keeps \"everyone pays their own\" working. "
                + "Without it the line is **attributed to the table** and splits across everyone "
                + "present - a real answer rather than a wrong one, and one whose cost the venue can "
                + "see in its own numbers.")
            .Produces<OrderView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff, or another branch's tab.")
            .ProducesProblem<MenuItemUnavailableProblem>(
                StatusCodes.Status409Conflict, "`menu-item-unavailable`, with the dish named.")
            .ProducesProblem<TabNotAcceptingOrdersProblem>(
                StatusCodes.Status409Conflict, "`tab-not-accepting-orders`: the bill has been asked for.");

        staff.MapPost("/lines/{lineId:guid}/void", VoidLineAsync)
            .WithName("voidTabLine")
            .WithSummary("Take a line off the bill")
            .WithDescription(
                "The line **stays on the tab and stays visible to the diner**, labelled as removed "
                + "by staff, with the reason. Nothing silently disappears from a bill somebody is "
                + "watching on their phone.\n\n"
                + "Refused once the tab has been paid against: that is a refund, which is a "
                + "different thing with its own rail.")
            .Produces<OrderView>()
            .ProducesProblem<LineAlreadyPaidProblem>(
                StatusCodes.Status409Conflict,
                "`line-already-paid`: the tab has been paid against, so this would be a refund.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such line on this tab.");

        staff.MapPost("/adjustments", AddAdjustmentAsync)
            .WithName("addTabAdjustment")
            .WithSummary("Manager: discount or comp")
            .WithDescription(
                "Exactly one of `percent` and `amountAmd`. A `tabOrderLineId` applies it to that "
                + "line; leaving it out applies it to the whole tab.\n\n"
                + "**The service charge is computed after this**, so comping a dish comps its "
                + "service charge with it - which is what a manager means by comping a dish.\n\n"
                + "Visible to the diner with its reason, for the same reason a void is.")
            .Produces<AdjustmentView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Requires the Manager role.");

        app.MapPost("/api/tab-adjustments/{adjustmentId:guid}/void", VoidAdjustmentAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .WithName("voidTabAdjustment")
            .WithSummary("Manager: reverse a discount or comp")
            .WithDescription(
                "It stops counting and stays on the record. A comp applied to the wrong table is "
                + "itself part of what happened, and reporting that cannot see it cannot explain why "
                + "the evening's takings are short.")
            .Produces<AdjustmentView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Requires the Manager role.");
    }

    // ---------------------------------------------------------------------------------------
    // The kitchen display: the staff app in another mode, so an endpoint rather than a surface.
    // ---------------------------------------------------------------------------------------
    private static void MapKitchen(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/branches/{branchId:guid}/orders", GetBranchOrdersAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove, YallaPolicies.BranchScoped)
            .WithName("getBranchOrders")
            .WithSummary("The kitchen queue")
            .WithDescription(
                "Open orders for the branch, oldest first, with the table label, the lines and their "
                + "kitchen notes. Omit `status` for everything still outstanding - New, InKitchen and "
                + "Ready - which is what a kitchen screen shows.")
            .Produces<IReadOnlyList<KitchenOrderView>>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff at this branch.");

        app.MapPost("/api/orders/{orderId:guid}/status", MoveOrderStatusAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove)
            .WithName("moveOrderStatus")
            .WithSummary("Move an order along the rail")
            .WithDescription(
                "`New -> InKitchen -> Ready -> Served`. Named transitions against an explicit table, "
                + "the same shape as the table state machine: going backwards is refused, because a "
                + "Ready order tapped back to InKitchen loses the fact that it was ever cooked.\n\n"
                + "**The Kitchen role may make exactly one move: `InKitchen -> Ready`.** Serving is a "
                + "floor action and belongs to whoever carried the plate.")
            .Produces<KitchenOrderView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "This role may not make that move.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The rail does not allow that transition.");
    }

    // ---------------------------------------------------------------------------------------
    // Calling a waiter. Presets only - see docs/tabs.md for why this is not a chat.
    // ---------------------------------------------------------------------------------------
    private static void MapServiceRequests(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tabs/{tabId:guid}/service-requests", RaiseServiceRequestAsync)
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.TabParticipant)
            .WithName("raiseServiceRequest")
            .WithSummary("Ask a waiter for something")
            .WithDescription(
                "Presets and one short optional note. **Not a chat**: a message box creates the "
                + "expectation of a reply, and during the Friday rush nobody answers it.\n\n"
                + "Rate limited **per tab**, not per person - the list a waiter is looking at is the "
                + "table's, and one bored guest must not be able to bury another table's request.")
            .Produces<ServiceRequestView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, NotOnTabDescription)
            .ProducesProblem<ServiceRequestRateLimitedProblem>(
                StatusCodes.Status429TooManyRequests,
                "`service-request-rate-limited`: too many, too fast. `context.windowMinutes` says "
                + "how long to wait.");

        app.MapGet("/api/branches/{branchId:guid}/service-requests", GetOpenServiceRequestsAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove, YallaPolicies.BranchScoped)
            .WithName("getOpenServiceRequests")
            .WithSummary("What tables are asking for")
            .WithDescription(
                "Open requests, newest first, with the table label and how long they have been "
                + "waiting. Newest first because on a busy floor the screen is glanced at, and the "
                + "thing that just came in is the one nobody has walked to yet.")
            .Produces<IReadOnlyList<ServiceRequestView>>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff at this branch.");

        app.MapPost("/api/service-requests/{id:guid}/acknowledge", AcknowledgeServiceRequestAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove)
            .WithName("acknowledgeServiceRequest")
            .WithSummary("A waiter has seen it")
            .WithDescription(
                "Acknowledging one twice is a no-op rather than an error: two waiters tapping the "
                + "same request is the normal case, and both seeing it is what the table wanted.")
            .Produces<ServiceRequestView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff.");
    }

    // ---------------------------------------------------------------------------------------
    // Cash. The only rail in this task, and the one the pilot runs on.
    // ---------------------------------------------------------------------------------------
    private static void MapPayments(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tabs/{tabId:guid}")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove);

        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapPost("/payments/cash", RecordCashAsync)
            .WithName("recordCashPayment")
            .WithSummary("Take cash at the table")
            .WithDescription(
                "The amount is **reserved against the remaining balance under the tab's row version** "
                + "before anything is recorded, then marked succeeded in the same call. The two-step "
                + "shape exists so a wallet provider can sit in `Reserved` while it is being called.\n\n"
                + "Paying more than is owed is refused **with the current balance in the body** - the "
                + "waiter is standing at the table and needs the number, and the usual cause is "
                + "somebody settling in the app while the waiter was typing.\n\n"
                + "Partial payments are normal and expected. When the balance reaches zero the tab "
                + "closes and its sitting closes with it - but **the table is not freed**. That stays "
                + "an explicit waiter action, because a party that has paid usually sits on for "
                + "another twenty minutes.\n\n"
                + "`tipAmd` is recorded and kept **entirely outside** `paidAmd` and `remainingAmd`.\n\n"
                + "**This is not a fiscal receipt.** The venue's registered cash register still issues "
                + "one.")
            .Produces<CashPaymentView>(StatusCodes.Status201Created)
            .ProducesProblem<PaymentExceedsRemainingProblem>(
                StatusCodes.Status409Conflict,
                "`payment-exceeds-remaining`: more was offered than is owed. **Show "
                + "`context.remainingAmd`** - the waiter is at the table and needs the number. Also "
                + "returned when another payment landed first, in which case the balance has moved.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff at this branch.");

        app.MapPost("/api/tabs/{tabId:guid}/abandon", AbandonAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .WithName("abandonTab")
            .WithSummary("Manager: write off an outstanding balance")
            .WithDescription(
                "The diners left without paying and the venue is accepting the loss. Kept for "
                + "reporting rather than deleted - a venue's write-offs are a number it needs.")
            .Produces<TabTotalsSnapshot>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Requires the Manager role.");
    }

    // ---------------------------------------------------------------------------------------
    // Handlers. Thin: read the caller off the token, call the service, return.
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> GetMenuAsync(
        Guid branchId,
        IMenuQuery menu,
        CancellationToken cancellationToken) =>
        Results.Ok(await menu.GetBranchMenuAsync(branchId, cancellationToken));

    private static async Task<IResult> PlaceOrderAsDinerAsync(
        Guid tabId,
        PlaceOrderRequest request,
        ITabOrderService orders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var view = await orders.PlaceOrderAsync(request.ToCommand(tabId), cancellationToken);

        return Results.Created($"/api/tabs/{tabId}/orders/{view.OrderId}", view);
    }

    private static async Task<IResult> PlaceOrderAsStaffAsync(
        Guid tabId,
        PlaceOrderRequest request,
        ITabOrderService orders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var view = await orders.PlaceOrderAsync(request.ToCommand(tabId), cancellationToken);

        return Results.Created($"/api/tabs/{tabId}/orders/{view.OrderId}", view);
    }

    private static async Task<IResult> VoidLineAsync(
        Guid tabId,
        Guid lineId,
        VoidLineRequest request,
        ITabOrderService orders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await orders.VoidLineAsync(
            new VoidLineCommand(tabId, lineId, request.Reason, request.ClientCommandId), cancellationToken));
    }

    private static async Task<IResult> AddAdjustmentAsync(
        Guid tabId,
        AddAdjustmentRequest request,
        ITabOrderService orders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var view = await orders.AddAdjustmentAsync(
            new AddAdjustmentCommand(
                tabId, request.TabOrderLineId, request.Kind, request.Percent, request.AmountAmd,
                request.Reason, request.ClientCommandId),
            cancellationToken);

        return Results.Created($"/api/tabs/{tabId}/adjustments/{view.AdjustmentId}", view);
    }

    private static async Task<IResult> VoidAdjustmentAsync(
        Guid adjustmentId,
        ITabOrderService orders,
        CancellationToken cancellationToken) =>
        Results.Ok(await orders.VoidAdjustmentAsync(adjustmentId, cancellationToken));

    private static async Task<IResult> GetSharesAsDinerAsync(
        Guid tabId,
        HttpContext http,
        ITabBillingQuery billing,
        CancellationToken cancellationToken) =>
        Results.Ok(await billing.GetSharesAsync(tabId, Participant(http), cancellationToken));

    private static async Task<IResult> GetEventsAsync(
        Guid tabId,
        ITabBillingQuery billing,
        CancellationToken cancellationToken,
        long afterSequence = 0L,
        int limit = 0) =>
        Results.Ok(await billing.GetEventsAsync(tabId, afterSequence, limit, cancellationToken));

    private static async Task<IResult> GetBranchOrdersAsync(
        Guid branchId,
        ITabOrderService orders,
        CancellationToken cancellationToken,
        TabOrderStatus? status = null) =>
        Results.Ok(await orders.GetBranchOrdersAsync(branchId, status, cancellationToken));

    private static async Task<IResult> MoveOrderStatusAsync(
        Guid orderId,
        MoveOrderStatusRequest request,
        ITabOrderService orders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await orders.MoveOrderStatusAsync(orderId, request.Status, cancellationToken));
    }

    private static async Task<IResult> RaiseServiceRequestAsync(
        Guid tabId,
        RaiseServiceRequest request,
        HttpContext http,
        IServiceRequestService requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var participantId = Participant(http)
                            ?? throw new UnauthorizedAccessException("This token carries no participant.");

        var view = await requests.RaiseAsync(
            tabId, participantId, request.Preset, request.Note, cancellationToken);

        return Results.Created($"/api/service-requests/{view.ServiceRequestId}", view);
    }

    private static async Task<IResult> GetOpenServiceRequestsAsync(
        Guid branchId,
        IServiceRequestService requests,
        CancellationToken cancellationToken) =>
        Results.Ok(await requests.GetOpenAsync(branchId, cancellationToken));

    private static async Task<IResult> AcknowledgeServiceRequestAsync(
        Guid id,
        IServiceRequestService requests,
        CancellationToken cancellationToken) =>
        Results.Ok(await requests.AcknowledgeAsync(id, cancellationToken));

    private static async Task<IResult> RecordCashAsync(
        Guid tabId,
        RecordCashRequest request,
        ITabPaymentService payments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var view = await payments.RecordCashAsync(request.ToCommand(tabId), cancellationToken);

        return Results.Created($"/api/tabs/{tabId}/payments/{view.PaymentId}", view);
    }

    private static async Task<IResult> AbandonAsync(
        Guid tabId,
        AbandonTabRequest request,
        ITabPaymentService payments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await payments.AbandonAsync(tabId, request.Reason, cancellationToken));
    }

    /// <summary>The participant this token is for. Never read from a body - see the tab endpoints.</summary>
    private static Guid? Participant(HttpContext http) => http.User.Guid(YallaClaims.ParticipantId);
}
