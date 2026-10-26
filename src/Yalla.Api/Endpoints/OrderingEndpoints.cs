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
                + "a kitchen cooks an order together.\n\n"
                + "`totals` is the tab's, so it follows `tableTotalVisible` on your tab: **absent**, "
                + "with `tableTotalVisible: false` beside it, when the host hid the total from you. "
                + "Read it as optional.\n\n"
                + "`onBehalfOfParticipantId` and a line's `participantId` are the waiter's: a phone "
                + "sending either is refused with 403 `forbidden`, its own id included.")
            .Produces<OrderView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                NotOnTabDescription
                + " Or the body named a guest: `onBehalfOfParticipantId` and `items[i].participantId` are staff only.")
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
                + "added and an old build must not break on a tab that used one.\n\n"
                + "**You read the stream you may see.** Every event is here - the sequence never has a "
                + "hole - but without `tableTotalVisible` on your tab the payloads carry only your own "
                + "money: `orderPlaced.lines` holds just your lines (your own, a waiter's on your "
                + "behalf, and shared items you were at the table for - never a waiter's line for the "
                + "table, which can be the whole bill), a void "
                + "names only your own dish, an adjustment says what, how much and why only when it is "
                + "off your line, a payment says how much only when it was yours, "
                + "and `remainingAmd`, `totalAmd` and `writtenOffAmd` are never there. While you wait "
                + "for the host, other people's `displayName` and `note` are left out too. Redacted "
                + "members are **absent**, never null or zero. See `docs/tabs.md`, section 7.4.")
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
            .RequireAuthorization(YallaPolicies.WaiterOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        staff.AddEndpointFilter<ClientCommandIdFilter>();

        staff.MapPost("/staff-orders", PlaceOrderAsStaffAsync)
            .WithName("placeOrderAsStaff")
            .WithSummary("Key in an order spoken at the table")
            .WithDescription(
                "The same code path as a diner's order, deliberately: the total is only right if "
                + "**every** order goes through the system.\n\n"
                + "**Straight to the kitchen.** A waiter's order is answered with `status` `InKitchen`: "
                + "the waiter who took it at the table is the check a phone's order waits at `New` "
                + "for, so there is no second send. The tab's stream carries `OrderPlaced` and then "
                + "`OrderStatusChanged` (New -> InKitchen) in the same commit.\n\n"
                + "**On a tab waiting for admission too** - staff may always order - and it **never admits the "
                + "tab**: the phones on it still wait for a waiter's Admit (`POST /api/tabs/{tabId}/admit`).\n\n"
                + "`onBehalfOfParticipantId` is what keeps \"everyone pays their own\" working. "
                + "Without it the line is **attributed to the table** and splits across everyone "
                + "present - a real answer rather than a wrong one, and one whose cost the venue can "
                + "see in its own numbers.\n\n"
                + "**Who, per line.** `items[i].participantId` names the guest one line is for and "
                + "overrides `onBehalfOfParticipantId` for that line alone, so a round with the "
                + "table's latte, Narine's steak and Aram's coffee is **one order and one kitchen "
                + "ticket**, each dish on the person who ate it. Every line in the response carries "
                + "`onBehalfOfParticipantId`: its own guest, else the order's, and absent on a shared "
                + "line. `onBehalfOfParticipantId` on the order is a default for the lines that name "
                + "nobody, not a claim on the order. Both fields are checked "
                + "by one rule - an approved participant on this tab.\n\n"
                + "**A shared line has no owner.** `isShared` splits the line across everyone present "
                + "whatever `onBehalfOfParticipantId` says, as it always did; a line that is `isShared` "
                + "**and** names a `participantId` contradicts itself and is refused. Every refusal "
                + "refuses the whole send.")
            .Produces<OrderView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(
                StatusCodes.Status400BadRequest,
                "`invalid-request`: a shared line named a guest. `context.field` is `items[i].participantId`.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff, or another branch's tab.")
            .ProducesProblem<NamedGuestRefusedProblem>(
                StatusCodes.Status404NotFound,
                "`not-found`. When `onBehalfOfParticipantId` or a line's `participantId` is not on this tab - "
                + "made up, or a guest at another table - `context` names it: `field` "
                + "(`onBehalfOfParticipantId` or `items[i].participantId`) and `participantId`. Any other "
                + "404 here (a dish on no menu, an unknown tab) has no `context`.")
            .ProducesProblem<NamedGuestRefusedProblem>(
                StatusCodes.Status409Conflict,
                "`conflicting-state` about a named guest: `context` carries `field`, `participantId` and "
                + "`participantStatus` (1 not let on yet, 3 taken off). A `conflicting-state` without a "
                + "`participantId` is something else, e.g. another branch's dish.")
            .ProducesProblem<MenuItemUnavailableProblem>(
                StatusCodes.Status409Conflict, "`menu-item-unavailable`, with the dish named.")
            .ProducesProblem<TabNotAcceptingOrdersProblem>(
                StatusCodes.Status409Conflict,
                "`tab-not-accepting-orders`: the tab takes no more orders. `context.status` says why - 2 "
                + "Closing (the bill has been asked for), 3 Closed (paid and settled), 4 Abandoned "
                + "(written off). Also `menu-item-unavailable`, with the dish named, and "
                + "`conflicting-state`: with `context.participantId` when a guest the order named is not "
                + "orderable for (not let on yet, or taken off), without it for anything else.");

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
                + "Visible to the diner with its reason, for the same reason a void is.\n\n"
                + "`reductionAmd` is what this adjustment itself took off, in the bill's order - the amount the "
                + "History and the reports give it - not the subtotal's drop: a comp under a standing tab-wide "
                + "discount takes its line's whole price, while the subtotal drops by less.")
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
            .RequireAuthorization(YallaPolicies.KitchenOrAbove, YallaPolicies.BranchScoped)
            .WithName("getBranchOrders")
            .WithSummary("The kitchen queue")
            .WithDescription(
                "Open orders for the branch, oldest first, with the table label, the lines and their "
                + "kitchen notes. Omit `status` for everything still outstanding - New, InKitchen and "
                + "Ready - which is what a kitchen screen shows.\n\n"
                + "`placedByName` is who sent each ticket: the waiter's name as the PIN roster shows it, "
                + "read as they are now (renamed or no longer active, it is still theirs). **Absent** when "
                + "a diner ordered from their own phone - say it came from the app. Nothing else of the "
                + "staff member is on a ticket.")
            .Produces<IReadOnlyList<KitchenOrderView>>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not kitchen or floor staff at this branch.");

        // Not BranchScoped: the route names an order, not a branch. The service loads the order and
        // refuses a token whose branch is not the order's, before it looks at the move.
        app.MapPost("/api/orders/{orderId:guid}/status", MoveOrderStatusAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.KitchenOrAbove)
            .WithName("moveOrderStatus")
            .WithSummary("Move an order along the rail")
            .WithDescription(
                "`New -> InKitchen -> Ready -> Served`. Named transitions against an explicit table, "
                + "the same shape as the table state machine: going backwards is refused, because a "
                + "Ready order tapped back to InKitchen loses the fact that it was ever cooked.\n\n"
                + "**The Kitchen role may make exactly one move: `InKitchen -> Ready`.** Serving is a "
                + "floor action and belongs to whoever carried the plate.\n\n"
                + "**`Voided` (from `New` or `InKitchen`) takes the order off the bill**: every line of it still "
                + "on the bill is voided with it - when, by whom, and the reason \"The whole order was voided.\" - "
                + "and stays on the tab marked, at zero, exactly as a line voided on its own. Refused once the tab "
                + "has been paid against, as a line's void is: that is a refund.\n\n"
                + "Answers with the ticket as the queue shows it, `placedByName` included.")
            .Produces<KitchenOrderView>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not kitchen or floor staff at the order's branch, or this role may not make that move.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "The rail does not allow that transition (`conflicting-state`), or `line-already-paid`: voiding an "
                + "order on a tab that has been paid against would be a refund.");
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
                + "table's, and one bored guest must not be able to bury another table's request.\n\n"
                + "**Asking for the bill from the pay sheet.** With `preset` 3 a request may carry "
                + "`billScope` (1 WholeBill, 2 MyShare) and `tipPercent` (0 to 25). The server prices "
                + "it from the tab's own arithmetic - the same numbers `/shares` reports - and the "
                + "response and the waiter's list carry `billAmountAmd` and `tipAmd`:\n"
                + "- WholeBill: the tab's remaining balance. Only for somebody who can see the table "
                + "total - otherwise 403.\n"
                + "- MyShare: what the asker still owes of their own share (`shareAmd` less `paidAmd`); "
                + "for somebody who can see the table total, never more than the tab owes.\n\n"
                + "The tip is on the **unpaid part of the items**: the tab subtotal (WholeBill) or the "
                + "asker's `personalAmd` (MyShare), in the proportion `billAmountAmd` bears to the "
                + "total or the share - all of the items when nothing has been paid. The service "
                + "charge is inside `billAmountAmd`; the tip is **outside** it, as a cash payment's "
                + "`tipAmd` is outside the balance. Either field on another preset, a tip with no "
                + "scope, or a tip outside 0 to 25 is a 422 naming the field; a scope with nothing "
                + "left to pay is a 422 on `billScope`.\n\n"
                + "A request with a `billScope` **replaces** the asker's own earlier open request for "
                + "the bill on this tab: that one leaves the waiter's list. The `ServiceRequested` "
                + "event carries `billScope` and `tipPercent` but never the amounts - every phone at "
                + "the table reads the stream.")
            .Produces<ServiceRequestView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                NotOnTabDescription + " Or `billScope` 1 WholeBill from somebody the table total is hidden from.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`billScope` or `tipPercent` refused - on a preset other than 3, a tip with no scope, "
                + "a tip outside 0 to 25, or a scope that leaves nothing to pay. `context.fields` names each.")
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
                + "thing that just came in is the one nobody has walked to yet.\n\n"
                + "A request for the bill from the diner's pay sheet carries `billScope`, "
                + "`billAmountAmd`, `tipPercent` and `tipAmd`: the amount to bring, priced when the "
                + "diner asked, and the tip to expect on top of it - what the cash keypad opens with. "
                + "One the same diner has since replaced with a newer request for the bill is not "
                + "listed: the newer one is what to bring.\n\n"
                + "A claimed request (`claimServiceRequest`, \"I'm on it\") stays listed until acknowledged, with "
                + "`claimedAtUtc`, `claimedByStaffMemberId` and `claimedByName` - so every tablet shows who has it.")
            .Produces<IReadOnlyList<ServiceRequestView>>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff at this branch.");

        app.MapPost("/api/branches/{branchId:guid}/calls/{callId:guid}/claim", ClaimServiceRequestAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove, YallaPolicies.BranchScoped)
            .WithName("claimServiceRequest")
            .WithSummary("\"I'm on it\": claim a call or a bill request")
            .WithDescription(
                "No body. Records `claimedByStaffMemberId` and `claimedAtUtc`; **the first claim wins** and a repeat - "
                + "anybody's - answers with the request as it stands, nothing written. The guest is **not** told, and "
                + "the request does **not** leave the open list: it stays, showing `claimedByName`, until somebody "
                + "acknowledges it (`acknowledgeServiceRequest`). One already acknowledged or replaced is answered as "
                + "it stands. The seconds from the call to the claim are the waiter's response time in Staff → Results, "
                + "and the claim is logged as `table.call-claimed` (`docs/activity.md`). Acknowledging a request nobody "
                + "claimed claims it in the same moment.\n\n"
                + "A waiter or above at the branch; a call at another branch is not found.")
            .Produces<ServiceRequestView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such call at this branch.");

        app.MapPost("/api/service-requests/{id:guid}/acknowledge", AcknowledgeServiceRequestAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove)
            .WithName("acknowledgeServiceRequest")
            .WithSummary("A waiter has seen it")
            .WithDescription(
                "Acknowledging one twice is a no-op rather than an error: two waiters tapping the "
                + "same request is the normal case, and both seeing it is what the table wanted.\n\n"
                + "The first acknowledgement stands (`acknowledgedByStaffMemberId`, `acknowledgedByName`). One nobody "
                + "claimed (`claimServiceRequest`) is claimed by the same tap: that is then the response time "
                + "(`docs/activity.md`).")
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
            .RequireAuthorization(YallaPolicies.WaiterOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

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
            .RequireAuthorization(YallaPolicies.BranchScoped)
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
        HttpContext http,
        ITabBillingQuery billing,
        CancellationToken cancellationToken,
        long afterSequence = 0L,
        int limit = 0)
    {
        // Whose stream it is comes from the token, as on the tab read: the payloads are redacted to
        // what this participant may see, so there is no reading it as anybody else.
        if (Participant(http) is not { } participantId)
        {
            return Results.Forbid();
        }

        return Results.Ok(await billing.GetEventsAsync(
            tabId, participantId, afterSequence, limit, cancellationToken));
    }

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
            tabId, participantId, request.Preset, request.Note, request.BillScope, request.TipPercent,
            cancellationToken);

        return Results.Created($"/api/service-requests/{view.ServiceRequestId}", view);
    }

    private static async Task<IResult> GetOpenServiceRequestsAsync(
        Guid branchId,
        IServiceRequestService requests,
        CancellationToken cancellationToken) =>
        Results.Ok(await requests.GetOpenAsync(branchId, cancellationToken));

    private static async Task<IResult> ClaimServiceRequestAsync(
        Guid branchId,
        Guid callId,
        IServiceRequestService requests,
        CancellationToken cancellationToken) =>
        Results.Ok(await requests.ClaimAsync(branchId, callId, cancellationToken));

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
