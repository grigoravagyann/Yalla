using System.Globalization;
using System.Text.Json;
using Yalla.Api.Authorization;
using Yalla.Application.Activity;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The History (who did what, from which device), the staff results, and handing a table to another waiter. See
/// <c>docs/activity.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who reads what.</b> The History and Staff → Results are manager or above at the branch - a branch manager their
/// own branch, the owner and the general manager every branch of their venue - checked against the stored row as well
/// as the token. A waiter reads their own numbers only (<c>/api/staff/me/performance</c>): there is no ranking a waiter
/// can open.
/// </para>
/// <para>
/// <b>Every query value is a string</b>, so a value that does not parse is named in the one 422 <c>validation-failed</c>
/// with the rest rather than refused alone by the binder.
/// </para>
/// </remarks>
public static class ActivityEndpoints
{
    private const string CsvMediaType = "text/csv; charset=utf-8";

    private const string ManagerScope =
        "Manager or above, scoped like the Reviews page: a branch manager reaches their own branch only, the owner and the "
        + "general manager every branch of their venue, a platform admin any. Waiters and the kitchen: 403.";

    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var branch = app.MapGroup("/api/branches/{branchId:guid}")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove, YallaPolicies.BranchScoped);

        branch.MapGet("/activity", ListAsync)
            .WithName("listBranchActivity")
            .WithSummary("The History: who did what at the branch, newest first")
            .WithDescription(
                "Every recorded action - bookings, tables, orders, money, takeaway, guest messages, settings, the team and "
                + "devices - with who did it (staff, the guest, or the system), from which device (`deviceLabel`: the "
                + "tablet's or phone's name, `Console` for the console), and what it was about.\n\n"
                + "Query, all optional: `date` (one branch-local day, `yyyy-MM-dd`) **or** `from`/`to` (inclusive; both "
                + "default to today) - not both; at most 366 days, not before today - 1095 (money is kept three years, "
                + "everything else one) and not after today + 366. `staffMemberId`: only what this person did. `actorKind`: `staff`, `guest`, `system` (what the system did by itself) or `platform`. `category`: `booking`, `table`, "
                + "`order`, `payment`, `takeaway`, `message`, `settings`, `staff` or `device`. `q` (at most 60): the "
                + "table (exact), the guest (contains), a code (`T-482` or `482`, or a booking's), or a dish (contains). "
                + "`limit` 1 to 100, default 50. `cursor`: the page before's `nextCursor`. A bad value is **422 "
                + "`validation-failed`** naming each in `context.fields`.\n\n"
                + "Answers `{ items, nextCursor, from, to }`, newest first then by id, so equal instants neither repeat "
                + "nor skip across pages. Each item: `id`, `occurredAtUtc`, `local` (`yyyy-MM-dd HH:mm` on the branch's "
                + "clock), `actorKind` (`staff`, `guest`, `system`, `platform`), `staffMemberId`, `actorName`, `deviceId`, "
                + "`deviceLabel`, `category`, `action` (a stable code - the list is in `docs/activity.md`), `subjectType` "
                + "and `subjectId` (the detail link: `reservation`, `tab`, `takeaway`, `menu-item`, `staff`, `branch`, "
                + "`device`, `service-request`, `table`, `table-session`), `subjectCode`, `tableSessionId`, `tableLabel`, "
                + "`guestName`, `itemNames`, `amountAmd`, `isMoney`, `before`/`after` (what changed, shape per action), "
                + "`reason` (as typed), `summary` (the parameters to render the line from - never prose) and `seconds` "
                + "(a call's claim, a reply, a booking decision).\n\n"
                + "Entries that name no branch (an owner's account) are on every branch's feed for the owner, the general "
                + "manager and the platform, and on none for a branch manager.\n\n" + ManagerScope)
            .Produces<ActivityPage>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`forbidden`: not a manager or above at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<Errors.ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "A bad query value; `context.fields` names each.");

        branch.MapGet("/activity.csv", ExportAsync)
            .WithName("exportBranchActivity")
            .WithSummary("The History as CSV, with the same filters")
            .WithDescription(
                "The feed's filters (`date` or `from`/`to`, `staffMemberId`, `actorKind`, `category`, `q`) - every entry in the window, "
                + "newest first; `cursor` and `limit` are ignored. At most 20,000 rows: more is **422 "
                + "`activity-export-too-large`** with `context.rows` and `context.maxRows` - narrow the dates or filter.\n\n"
                + "The reports' CSV rules: UTF-8 with a byte-order mark (Excel reads Armenian names), RFC 4180 quoting, "
                + "plain digits, whole dram, branch-local times; and a leading `'` on a text cell that starts with `=`, "
                + "`+`, `-`, `@`, a tab or a carriage return, because reasons and dish names are typed by staff. Columns: "
                + "`local`, `actorKind`, `actorName`, `deviceLabel`, `category`, `action`, `subjectType`, `subjectId`, "
                + "`subjectCode`, `tableLabel`, `guestName`, `items`, `amountAmd`, `isMoney`, `seconds`, `reason`, "
                + "`before`, `after`, `summary` (the last three JSON).\n\n" + ManagerScope)
            .Produces<string>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`forbidden`: not a manager or above at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<Errors.ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "A bad query value (`validation-failed`, `context.fields`), or `activity-export-too-large`.");

        branch.MapGet("/activity/subjects/{subjectType}/{subjectId:guid}", TimelineAsync)
            .WithName("getActivityTimeline")
            .WithSummary("One bill's, booking's or takeaway order's whole story, oldest first")
            .WithDescription(
                "The Check board. `subjectType` `tab`: every entry of the bill's sitting - the seat, orders, the kitchen, "
                + "voids, discounts, calls claimed, the transfer, payments - with `tab` (the header: table, party, "
                + "opened/closed, totals, tips, and `servers`, the table's waiters in turn with when each took it) and "
                + "`lines`, each with who added it (`addedByStaffMemberId`/`addedByName`, or `addedByGuest` and the "
                + "guest's name), whose sale it was (`serverStaffMemberId`) and who voided it and why. `reservation`: the "
                + "booking's entries and `booking`. `takeawayOrder` (or `takeaway`): the order's entries, `takeaway` and its `lines`. Any other "
                + "type (`menu-item`, `staff`, ...): its entries alone. `people` lists who appears, for the filter chips.\n\n"
                + "404 when the subject is not this branch's, or nothing at the branch is recorded about it.\n\n" + ManagerScope)
            .Produces<ActivityTimeline>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`forbidden`: not a manager or above at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such subject at this branch.");

        branch.MapGet("/staff-performance", TeamAsync)
            .WithName("getStaffPerformance")
            .WithSummary("Staff → Results: everybody's numbers over the dates, and the branch's")
            .WithDescription(
                "Query: `from`, `to` - branch-local dates, inclusive; `from` defaults to `to`, `to` to today; at most 366 "
                + "days, not before today - 1095 nor after today + 366. Bad values: 422 `validation-failed`.\n\n"
                + "**Whose a sale is.** A bill counts when it closes, as the revenue report counts it, and is split over "
                + "the table's waiters in the proportion of the lines ordered while the table was theirs: a transfer moves "
                + "what comes after it and leaves what came before. Tips are the waiter's whose table it was when paid; "
                + "cash is whoever took it (the takeaway counter's included). A table and its guests count for each waiter "
                + "with a share of the bill or holding it at the close.\n\n"
                + "Each row: `staffMemberId`, `name`, `role`, `isActive`, `tables`, `guests`, `salesAmd`, "
                + "`averageCheckAmd`, `tipsAmd`, `callsClaimed` and `callClaimMedianSeconds` (call to \"I'm on it\"), "
                + "`messagesAnswered` and `messageReplyMedianSeconds` (guest text to the first venue reply), "
                + "`approvals`, `declines`, `cashAmd`, `cashPayments`, `discounts`, `comps`, `adjustmentsAmd`, `voids`. "
                + "Medians are of whole seconds - the mean of the middle two for an even count, rounded half up; absent "
                + "with none. Rows: everybody with anything in the window, and every active waiter and manager of the "
                + "branch with nothing.\n\n"
                + "`totals` counts the branch once (a transferred table is one table; the medians are over every claim and "
                + "reply). **Its money is the revenue report's for the same dates**: `salesAmd` = `hallAmd`, `tables` = "
                + "`tabs`, `averageCheckAmd` = `averageTabAmd`, `cashAmd` = `cashAmd`, `takeawayAmd` = `takeawayAmd`, "
                + "`adjustmentsAmd` = `adjustmentsTotalAmd`; `unattributedSalesAmd` is the part no waiter held.\n\n" + ManagerScope)
            .Produces<StaffPerformanceReport>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`forbidden`: not a manager or above at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<Errors.ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "A bad query value; `context.fields` names each.");

        branch.MapGet("/staff-performance.csv", TeamCsvAsync)
            .WithName("exportStaffPerformance")
            .WithSummary("Staff → Results as CSV")
            .WithDescription(
                "The same `from`/`to` and the same numbers: one row per person, then a `totals` row. The reports' CSV "
                + "rules (byte-order mark, RFC 4180, whole dram, plain digits; an empty cell for a median with nothing to "
                + "take one of). Columns: `staffMemberId`, `name`, `role`, `tables`, `guests`, `salesAmd`, "
                + "`averageCheckAmd`, `tipsAmd`, `callsClaimed`, `callClaimMedianSeconds`, `messagesAnswered`, "
                + "`messageReplyMedianSeconds`, `approvals`, `declines`, `cashAmd`, `cashPayments`, `discounts`, `comps`, "
                + "`adjustmentsAmd`, `voids`.\n\n" + ManagerScope)
            .Produces<string>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`forbidden`: not a manager or above at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<Errors.ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "A bad query value; `context.fields` names each.");

        branch.MapGet("/staff/{staffMemberId:guid}/performance", PersonAsync)
            .WithName("getStaffMemberPerformance")
            .WithSummary("One person's page: their numbers, sales by day, and their recent work")
            .WithDescription(
                "Query: `from`, `to` as Staff → Results; `list` - `orders` (default: orders sent, pre-orders sent, "
                + "voids, discounts, comps, bills closed in cash), `messages` (replies and \"are you coming?\"), "
                + "`bookings` (approved, declined, seated, no-show), `cash` (cash at tables and the takeaway counter) or "
                + "`calls` (calls claimed); `limit` 1 to 100, default 50; `cursor` the page before's `recent.nextCursor`.\n\n"
                + "Answers `{ branchId, from, to, kpis, dailySales, recent }`: `kpis` is their row as on Staff → Results "
                + "(zeros when they did nothing); `dailySales` one `{ date, salesAmd, tables }` per day from six days "
                + "before `to` (or `from`, when earlier) to `to`; `recent` `{ list, items, nextCursor }` - History "
                + "entries, newest first, with `seconds` where the action measures it.\n\n"
                + "404 when the person does not work at this venue.\n\n" + ManagerScope)
            .Produces<PersonPerformance>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`forbidden`: not a manager or above at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or nobody by that id at this venue.")
            .ProducesProblem<Errors.ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "A bad query value; `context.fields` names each.");

        app.MapGet("/api/staff/me/performance", MineAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove)
            .WithName("getMyPerformance")
            .WithSummary("My results: the caller's own numbers")
            .WithDescription(
                "The same answer as `getStaffMemberPerformance`, for the person signed in - a waiter from the tablet or "
                + "their phone, a manager from the console. At their home branch; an owner or general manager, who has "
                + "none, says which with `branchId` (one of their venue's - another venue's or another branch than their "
                + "own is 403). Nobody else's numbers are reachable from here.\n\n"
                + "A waiter or above, by the stored row: the kitchen and the platform are 403.")
            .Produces<PersonPerformance>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`forbidden`: the kitchen, the platform, or a branch not theirs.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<Errors.ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "A bad query value, or `branchId` missing for somebody venue-wide.");

        app.MapPost("/api/tabs/{tabId:guid}/transfer", TransferAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove, YallaPolicies.BranchScoped)
            .WithName("transferTable")
            .WithSummary("Hand the table to another waiter, from now on")
            .WithDescription(
                "Body `{ toStaffMemberId, clientCommandId? }`. Answers `{ tabId, serverStaffMemberId, serverName, "
                + "previousServerStaffMemberId, previousServerName, transferredAtUtc, tableSessionId, tableLabel, "
                + "wasReplay }`. The bill, its calls and messages are the new waiter's from this moment; what "
                + "was done before stays with whoever did it - each order and payment keeps the waiter it was taken under, "
                + "so the sales and tips before the hand-over stay the earlier waiter's.\n\n"
                + "Allowed for the table's waiter, or a manager or above at the branch (stored roles) - anybody else 403. "
                + "The target: active staff of this venue at this branch (or venue-wide), who serves - a waiter, manager, "
                + "general manager or owner, never the kitchen; otherwise **422 `transfer-target-ineligible`** with "
                + "`context.staffMemberId` and `context.reason` (`not-found`, `inactive`, `other-branch`, `does-not-serve`). "
                + "A bill closed or written off, or a party gone: **409 `tab-not-transferable`** (`context.tabId`, "
                + "`context.status`). Already theirs: 200 with `wasReplay` true, nothing written.\n\n"
                + "Logged as `table.transferred`, and a `TableTransferred` (7) signal on the branch's change stream so "
                + "every tablet re-reads the floor - whose `serverStaffMemberId`/`serverName` moved.")
            .Produces<TableTransferResult>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`forbidden`: not the table's waiter nor a manager here.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such tab.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "`tab-not-transferable`: the sitting is over.")
            .ProducesProblem<Errors.ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`transfer-target-ineligible` (`context.reason`), or `toStaffMemberId` missing (`validation-failed`).");

        return app;
    }

    // ---------------------------------------------------------------- handlers

    private static async Task<IResult> ListAsync(
        Guid branchId,
        IActivityQuery activity,
        CancellationToken cancellationToken,
        string? date = null,
        string? from = null,
        string? to = null,
        string? staffMemberId = null,
        string? category = null,
        string? q = null,
        string? cursor = null,
        string? limit = null,
        string? actorKind = null) =>
        Results.Ok(await activity.ListAsync(
            branchId, new ActivityFeedQuery(date, from, to, staffMemberId, category, q, cursor, limit, actorKind), cancellationToken));

    private static async Task<IResult> ExportAsync(
        Guid branchId,
        IActivityQuery activity,
        CancellationToken cancellationToken,
        string? date = null,
        string? from = null,
        string? to = null,
        string? staffMemberId = null,
        string? category = null,
        string? q = null,
        string? actorKind = null)
    {
        var export = await activity.ExportAsync(
            branchId, new ActivityFeedQuery(date, from, to, staffMemberId, category, q, ActorKind: actorKind), cancellationToken);

        return CsvFile.Result(
            $"activity-{export.From:yyyy-MM-dd}-to-{export.To:yyyy-MM-dd}",
            [
                "local", "actorKind", "actorName", "deviceLabel", "category", "action", "subjectType", "subjectId",
                "subjectCode", "tableLabel", "guestName", "items", "amountAmd", "isMoney", "seconds", "reason", "before",
                "after", "summary",
            ],
            export.Items.Select(i => new[]
            {
                i.Local,
                i.ActorKind,
                i.ActorName ?? string.Empty,
                i.DeviceLabel ?? string.Empty,
                i.Category,
                i.Action,
                i.SubjectType,
                i.SubjectId.ToString(),
                i.SubjectCode ?? string.Empty,
                i.TableLabel ?? string.Empty,
                i.GuestName ?? string.Empty,
                i.ItemNames ?? string.Empty,
                Number(i.AmountAmd),
                i.IsMoney ? "true" : "false",
                Number(i.Seconds),
                i.Reason ?? string.Empty,
                Json(i.Before),
                Json(i.After),
                Json(i.Summary),
            }),
            guardFormulas: true);
    }

    private static async Task<IResult> TimelineAsync(
        Guid branchId, string subjectType, Guid subjectId, IActivityQuery activity, CancellationToken cancellationToken) =>
        Results.Ok(await activity.TimelineAsync(branchId, subjectType, subjectId, cancellationToken));

    private static async Task<IResult> TeamAsync(
        Guid branchId,
        IStaffPerformanceQuery performance,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null) =>
        Results.Ok(await performance.GetBranchAsync(branchId, new StaffPerformanceQuery(from, to), cancellationToken));

    private static async Task<IResult> TeamCsvAsync(
        Guid branchId,
        IStaffPerformanceQuery performance,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null)
    {
        var report = await performance.GetBranchAsync(branchId, new StaffPerformanceQuery(from, to), cancellationToken);
        var t = report.Totals;

        return CsvFile.Result(
            $"staff-results-{report.From:yyyy-MM-dd}-to-{report.To:yyyy-MM-dd}",
            [
                "staffMemberId", "name", "role", "tables", "guests", "salesAmd", "averageCheckAmd", "tipsAmd", "callsClaimed",
                "callClaimMedianSeconds", "messagesAnswered", "messageReplyMedianSeconds", "approvals", "declines", "cashAmd",
                "cashPayments", "discounts", "comps", "adjustmentsAmd", "voids",
            ],
            report.Staff
                .Select(r => new[]
                {
                    r.StaffMemberId.ToString(), r.Name, ((int)r.Role).ToString(CultureInfo.InvariantCulture),
                    Number(r.Tables), Number(r.Guests), Number(r.SalesAmd), Number(r.AverageCheckAmd), Number(r.TipsAmd),
                    Number(r.CallsClaimed), Number(r.CallClaimMedianSeconds), Number(r.MessagesAnswered),
                    Number(r.MessageReplyMedianSeconds), Number(r.Approvals), Number(r.Declines), Number(r.CashAmd),
                    Number(r.CashPayments), Number(r.Discounts), Number(r.Comps), Number(r.AdjustmentsAmd), Number(r.Voids),
                })
                .Append(
                [
                    "totals", string.Empty, string.Empty, Number(t.Tables), Number(t.Guests), Number(t.SalesAmd),
                    Number(t.AverageCheckAmd), Number(t.TipsAmd), Number(t.CallsClaimed), Number(t.CallClaimMedianSeconds),
                    Number(t.MessagesAnswered), Number(t.MessageReplyMedianSeconds), Number(t.Approvals), Number(t.Declines),
                    Number(t.CashAmd), Number(t.CashPayments), Number(t.Discounts), Number(t.Comps), Number(t.AdjustmentsAmd),
                    Number(t.Voids),
                ]),
            guardFormulas: true);
    }

    private static async Task<IResult> PersonAsync(
        Guid branchId,
        Guid staffMemberId,
        IStaffPerformanceQuery performance,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null,
        string? list = null,
        string? cursor = null,
        string? limit = null) =>
        Results.Ok(await performance.GetPersonAsync(
            branchId, staffMemberId, new StaffPerformanceQuery(from, to, list, cursor, limit), cancellationToken));

    private static async Task<IResult> MineAsync(
        IStaffPerformanceQuery performance,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null,
        string? list = null,
        string? cursor = null,
        string? limit = null,
        string? branchId = null) =>
        Results.Ok(await performance.GetMineAsync(
            new StaffPerformanceQuery(from, to, list, cursor, limit, branchId), cancellationToken));

    private static async Task<IResult> TransferAsync(
        Guid tabId, TransferTableRequest request, ITableTransferService transfers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await transfers.TransferAsync(
            new TransferTableCommand(tabId, request.ToStaffMemberId!.Value), cancellationToken));
    }

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Json(JsonElement? value) => value is { } element ? element.GetRawText() : string.Empty;
}

/// <summary>Body of <c>POST /api/tabs/{tabId}/transfer</c>.</summary>
/// <param name="ToStaffMemberId">The waiter taking the table. Required.</param>
/// <param name="ClientCommandId">
/// Optional, accepted for the tablet's offline queue. A transfer is idempotent by what it asks - a second one to the same
/// waiter answers as done - so the id is not needed to make a retry safe.
/// </param>
public sealed record TransferTableRequest(
    [System.ComponentModel.DataAnnotations.Required] Guid? ToStaffMemberId,
    Guid? ClientCommandId = null);
