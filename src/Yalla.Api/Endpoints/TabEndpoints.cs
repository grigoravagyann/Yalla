using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Application.Tabs;
using Yalla.Infrastructure.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Tabs: opening one by scanning a table, inviting others, and the permission model between them.
/// </summary>
/// <remarks>
/// <para>
/// Three surfaces, kept apart by who holds what. <c>/api/tabs/open</c> and <c>/api/tabs/join</c>
/// are anonymous - the scanner has no token yet, and <b>no account is ever asked for</b>. Everything
/// under <c>/api/tabs/{tabId}</c> that a diner touches carries the <c>TabParticipant</c> policy,
/// which compares the route's tab id to the token's claim before any handler runs; the staff
/// actions carry <c>WaiterOrAbove</c> and <c>BranchScoped</c>, the latter resolving the branch from
/// the tab in the route.
/// </para>
/// <para>
/// Handlers here are thin: read the participant id off the token, call the service, return. The
/// host check - "only the host approves" - lives in the service, so it holds for every caller and
/// not only for HTTP. The visibility rules live in one projection function, so no handler here has
/// an <c>if (canSeeTotal)</c> to get wrong.
/// </para>
/// <para>
/// Menu, ordering, totals and payments are the next tasks. Nothing here accepts an order or moves
/// money; what it builds is the permission model those endpoints will enforce.
/// </para>
/// </remarks>
public static class TabEndpoints
{
    private const string NotOnTabDescription =
        "This token is for a different tab, the participant was removed, or the tab closed longer "
        + "ago than the receipt grace period. Decided by the policy, before the handler runs.";

    private const string NotHostDescription =
        "Only the host of this tab may do this. The caller is on the tab but is not its host.";

    public static IEndpointRouteBuilder MapTabEndpoints(this IEndpointRouteBuilder app)
    {
        MapOpening(app);
        MapParticipantSurface(app);
        MapStaffSurface(app);

        return app;
    }

    // ---------------------------------------------------------------------------------------
    // Anonymous: the scan and the invitation. The token comes OUT of these.
    // ---------------------------------------------------------------------------------------
    private static void MapOpening(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tabs")
            .WithTags(EndpointConventions.DinerTag)
            .AllowAnonymous();

        // The idempotency key is required on the scan. Without it a retry on flaky wifi cannot be
        // told from a second scan, and guessing means a second tab.
        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapPost("/open", OpenAsync)
            .WithName("openTab")
            .WithSummary("Scan the table's QR code: open a tab, or land on the one already open")
            .WithDescription(
                "The flow the product lives on. Anyone - booked, phoned ahead, or in off the street "
                + "- scans the code on the table and gets a token scoped to that table's tab, with "
                + "**no account behind it**.\n\n"
                + "What happens depends on the table:\n"
                + "- **Free**: a session and a tab open, the scanner is the host, the table becomes Occupied.\n"
                + "- **Seated, no tab yet** (a party a waiter sat down, or from a booking): a tab opens on "
                + "their session and the scanner is the host.\n"
                + "- **Already has a tab**: no second tab. The scanner is put on it as a *pending* "
                + "participant until the host approves them. `outcome` says which case applied.\n"
                + "- **Out of service**: 409.\n\n"
                + "Two phones scanning a free table at the same moment produce exactly one tab; the "
                + "loser lands pending on it. A double scan with the same `clientCommandId` returns the "
                + "same tab with `wasReplay` set.")
            .Produces<TabAccessResult>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "Missing device id or clientCommandId.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No table in service carries that QR code.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "The table is out of service, or the tab there is being settled and takes no new people. "
                + "Also returned when this `clientCommandId` was used by a different device.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        group.MapPost("/join", JoinAsync)
            .WithName("joinTab")
            .WithSummary("Join a tab with the host's invitation")
            .WithDescription(
                "The same token backs the QR on the host's screen and the share link they sent in "
                + "WhatsApp - two ways to hand over one thing. Joining puts this device on the tab as "
                + "a **pending** participant; the host taps approve. There is no code to say out loud, "
                + "and the next table cannot order on your bill.\n\n"
                + "Invitations last thirty minutes. A screenshot from last Tuesday gets 401.")
            .Produces<TabAccessResult>()
            .ProducesProblemDetails(
                StatusCodes.Status401Unauthorized, "The invitation is unknown, revoked or expired.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict, "The tab is being settled or is closed; nobody new can join.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);
    }

    // ---------------------------------------------------------------------------------------
    // Participant surface: everything a token holder does on their own tab.
    // ---------------------------------------------------------------------------------------
    private static void MapParticipantSurface(IEndpointRouteBuilder app)
    {
        // Reads carry the plain policy: a participant may look at a tab that is being settled.
        var group = app.MapGroup("/api/tabs/{tabId:guid}")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.TabParticipant);

        // Changes carry the stricter one. Once staff mark the tab closing the bill is being
        // settled, and somebody who has paid their share and left must not find it altered behind
        // them - reading it is still fine, which is the whole point of the split.
        var mutating = app.MapGroup("/api/tabs/{tabId:guid}")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.TabParticipantMutating);

        group.MapGet("/", GetTabAsync)
            .WithName("getTab")
            .WithSummary("The tab, as this participant is allowed to see it")
            .WithDescription(
                "Projected through the caller's own flags by one function, so every rule applies "
                + "the same way everywhere:\n"
                + "- The caller's **own items and own subtotal are always present**, whatever the flags.\n"
                + "- The **table total and other people's items appear only with `canSeeTableTotal`**, and "
                + "only once approved. When hidden they are **absent from the body** - not zero, not "
                + "null - with `tableTotalVisible: false` beside the gap, so no client can render a "
                + "hidden total as a free bill.\n"
                + "- A **pending** participant sees their own row and nothing else.\n\n"
                + "Menu prices are not part of this view and stay visible through the menu, so a "
                + "guest without the total can always work out what their own order costs.")
            .Produces<TabView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, NotOnTabDescription)
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such tab.");

        mutating.MapPost("/display-name", SetDisplayNameAsync)
            .WithName("setTabDisplayName")
            .WithSummary("Set what the host sees you called")
            .WithDescription(
                "A profile field on the participant, not an account. Someone who scanned a QR code "
                + "has no user row and never will; putting a name on the tab is optional and costs "
                + "them nothing to skip.")
            .Produces<TabParticipantView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, NotOnTabDescription);

        mutating.MapPost("/join-tokens", CreateJoinTokenAsync)
            .WithName("createTabJoinToken")
            .WithSummary("Host: create or refresh the invitation")
            .WithDescription(
                "Returns one token and the share link that carries it. Render the token as a QR on "
                + "the host's screen for the people at the table; send the link to the friend who is "
                + "fifteen minutes late. Both join the same tab.\n\n"
                + "Lasts thirty minutes. Calling again issues a fresh one and revokes any earlier "
                + "invitation still live, so a refresh also kills a screenshot that is doing the rounds.")
            .Produces<TabJoinTokenResult>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, NotHostDescription)
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict, "The tab is being settled; nobody new can join it.");

        HostAction(mutating, "/participants/{participantId:guid}/approve", ApproveAsync, "approveTabParticipant")
            .WithSummary("Host: let a pending joiner on");

        HostAction(mutating, "/participants/{participantId:guid}/reject", RejectAsync, "rejectTabParticipant")
            .WithSummary("Host: turn a pending joiner away");

        HostAction(mutating, "/participants/{participantId:guid}/remove", RemoveAsync, "removeTabParticipant")
            .WithSummary("Host: take someone off the tab")
            .WithDescription(
                "A status change, never a delete. Their items and any payment they made are "
                + "financial records and survive them leaving. The host cannot remove themself; staff "
                + "reassign the host first.");

        HostAction(mutating, "/participants/{participantId:guid}/permissions", SetPermissionsAsync, "setTabParticipantPermissions")
            .WithSummary("Host: set one person's three flags")
            .WithDescription(
                "`canOrder`, `canSeeTableTotal` and `canPay`, together. **`canPay` requires "
                + "`canSeeTableTotal`** - nobody puts money toward a total they may not see - and the "
                + "combination that breaks that is refused with 400 rather than silently corrected.")
            .ProducesProblemDetails(
                StatusCodes.Status400BadRequest, "`canPay` is true while `canSeeTableTotal` is false.");

        mutating.MapPost("/settlement-mode", SetSettlementModeAsync)
            .WithName("setTabSettlementMode")
            .WithSummary("Host: change how the bill will be split")
            .WithDescription(
                "Allowed until the first payment lands - reserved or succeeded - then locked. The "
                + "lock is stamped on the tab the first time a change is attempted after money exists, "
                + "and from then on this answers 409.")
            .Produces<TabView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, NotHostDescription)
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The settlement mode is locked.");
    }

    // ---------------------------------------------------------------------------------------
    // Staff surface: the two things only the floor can do, plus what they see.
    // ---------------------------------------------------------------------------------------
    private static void MapStaffSurface(IEndpointRouteBuilder app)
    {
        // BranchScoped resolves the branch from the tab in the route - see BranchScopedHandler -
        // so a waiter's session token for branch A cannot touch a tab at branch B.
        var group = app.MapGroup("/api/tabs/{tabId:guid}")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapGet("/participants", GetStaffViewAsync)
            .WithName("getTabForStaff")
            .WithSummary("Staff: everyone on the tab, and the money")
            .WithDescription(
                "\"Aram, Nare, +1 guest\" - every phone on the table with its role, status and flags, "
                + "so staff can see how many people are on a bill and who hosts it. Staff are not "
                + "participants; the host's visibility flags do not apply to them.")
            .Produces<TabStaffView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such tab.");

        group.MapPost("/reassign-host", ReassignHostAsync)
            .WithName("reassignTabHost")
            .WithSummary("Staff: move the host role to another participant")
            .WithDescription(
                "The host left early or their phone died, and without this the tab is stuck with "
                + "nobody able to approve joiners or change the split. The new host must be an "
                + "approved participant; the old host stays on the tab as a guest.")
            .Produces<TabStaffView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such tab or participant.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "The participant is not approved, is already the host, or the tab is closed.");

        group.MapPost("/closing", BeginClosingAsync)
            .WithName("beginClosingTab")
            .WithSummary("Staff: the bill has been asked for")
            .WithDescription(
                "After this, no new participants and no new orders - someone who already paid their "
                + "share must not get dessert added after they have left. Any live invitation is revoked.")
            .Produces<TabStaffView>()
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The tab is not open.");
    }

    /// <summary>The four host-only participant actions share one response set.</summary>
    private static RouteHandlerBuilder HostAction(
        RouteGroupBuilder group,
        string pattern,
        Delegate handler,
        string operationId) =>
        group.MapPost(pattern, handler)
            .WithName(operationId)
            .Produces<TabParticipantView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, NotHostDescription)
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such participant on this tab.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "The participant is not in a state that permits this - approving someone removed, "
                + "rejecting someone already approved, removing the host.");

    // ---------------------------------------------------------------- handlers

    private static async Task<IResult> OpenAsync(
        OpenTabRequest request,
        ITabService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.OpenAsync(
            new OpenTabCommand(
                request.QrToken,
                request.DeviceId,
                request.ClientCommandId,
                request.DisplayName,
                request.PartySize,
                request.SettlementMode,
                request.HideTotalFromGuests),
            cancellationToken));

    private static async Task<IResult> JoinAsync(
        JoinTabRequest request,
        ITabService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.JoinAsync(
            new JoinTabCommand(request.JoinToken, request.DeviceId, request.DisplayName),
            cancellationToken));

    private static async Task<IResult> GetTabAsync(
        Guid tabId,
        HttpContext http,
        ITabQuery query,
        CancellationToken cancellationToken)
    {
        // The participant comes from the token, never from the route or the body. There is no
        // request shape that lets a caller ask for somebody else's view of the tab.
        if (Participant(http) is not { } participantId)
        {
            return Results.Forbid();
        }

        var tab = await query.GetForParticipantAsync(tabId, participantId, cancellationToken);

        return tab is null ? Results.NotFound() : Results.Ok(tab);
    }

    private static async Task<IResult> SetDisplayNameAsync(
        Guid tabId,
        SetDisplayNameRequest request,
        HttpContext http,
        ITabService service,
        CancellationToken cancellationToken)
    {
        if (Participant(http) is not { } participantId)
        {
            return Results.Forbid();
        }

        return Results.Ok(await service.SetDisplayNameAsync(
            tabId, participantId, request.DisplayName, cancellationToken));
    }

    private static async Task<IResult> CreateJoinTokenAsync(
        Guid tabId,
        HttpContext http,
        ITabService service,
        CancellationToken cancellationToken)
    {
        if (Participant(http) is not { } participantId)
        {
            return Results.Forbid();
        }

        return Results.Ok(await service.CreateJoinTokenAsync(tabId, participantId, cancellationToken));
    }

    private static async Task<IResult> ApproveAsync(
        Guid tabId,
        Guid participantId,
        HttpContext http,
        ITabService service,
        CancellationToken cancellationToken)
    {
        if (Participant(http) is not { } actingId)
        {
            return Results.Forbid();
        }

        return Results.Ok(await service.ApproveParticipantAsync(tabId, actingId, participantId, cancellationToken));
    }

    private static async Task<IResult> RejectAsync(
        Guid tabId,
        Guid participantId,
        HttpContext http,
        ITabService service,
        CancellationToken cancellationToken)
    {
        if (Participant(http) is not { } actingId)
        {
            return Results.Forbid();
        }

        return Results.Ok(await service.RejectParticipantAsync(tabId, actingId, participantId, cancellationToken));
    }

    private static async Task<IResult> RemoveAsync(
        Guid tabId,
        Guid participantId,
        HttpContext http,
        ITabService service,
        CancellationToken cancellationToken)
    {
        if (Participant(http) is not { } actingId)
        {
            return Results.Forbid();
        }

        return Results.Ok(await service.RemoveParticipantAsync(tabId, actingId, participantId, cancellationToken));
    }

    private static async Task<IResult> SetPermissionsAsync(
        Guid tabId,
        Guid participantId,
        SetParticipantPermissionsRequest request,
        HttpContext http,
        ITabService service,
        CancellationToken cancellationToken)
    {
        if (Participant(http) is not { } actingId)
        {
            return Results.Forbid();
        }

        return Results.Ok(await service.SetPermissionsAsync(
            tabId,
            actingId,
            participantId,
            new SetParticipantPermissionsCommand(request.CanOrder, request.CanSeeTableTotal, request.CanPay),
            cancellationToken));
    }

    private static async Task<IResult> SetSettlementModeAsync(
        Guid tabId,
        SetSettlementModeRequest request,
        HttpContext http,
        ITabService service,
        CancellationToken cancellationToken)
    {
        if (Participant(http) is not { } actingId)
        {
            return Results.Forbid();
        }

        return Results.Ok(await service.SetSettlementModeAsync(
            tabId, actingId, request.SettlementMode, cancellationToken));
    }

    private static async Task<IResult> GetStaffViewAsync(
        Guid tabId,
        ITabQuery query,
        CancellationToken cancellationToken)
    {
        var tab = await query.GetForStaffAsync(tabId, cancellationToken);

        return tab is null ? Results.NotFound() : Results.Ok(tab);
    }

    private static async Task<IResult> ReassignHostAsync(
        Guid tabId,
        ReassignHostRequest request,
        ITabService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ReassignHostAsync(tabId, request.NewHostParticipantId, cancellationToken));

    private static async Task<IResult> BeginClosingAsync(
        Guid tabId,
        ITabService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.BeginClosingAsync(tabId, cancellationToken));

    /// <summary>The participant the token names, or null - which the policy should already have refused.</summary>
    private static Guid? Participant(HttpContext http) => http.User.Guid(YallaClaims.ParticipantId);
}
