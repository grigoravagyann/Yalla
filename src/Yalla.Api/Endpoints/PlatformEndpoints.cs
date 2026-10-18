using System.ComponentModel.DataAnnotations;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.Auth;
using Yalla.Application.Platform;
using Yalla.Application.Reviews;

namespace Yalla.Api.Endpoints;

/// <summary>Body of <c>PATCH /api/platform/me</c>.</summary>
/// <param name="FullName">The name the console shows and the activity feed records. 1-200 characters.</param>
public sealed record UpdatePlatformAdminProfileRequest([Required] string? FullName);

/// <summary>Body of <c>POST /api/platform/me/password</c>.</summary>
/// <param name="CurrentPassword">The password the admin signs in with now.</param>
/// <param name="NewPassword">At least 12 characters. Length is the only rule.</param>
public sealed record ChangePlatformAdminPasswordRequest(
    [Required] string? CurrentPassword,
    [Required] string? NewPassword);

/// <summary>
/// The platform tier: creating and configuring venues through the API instead of by hand in SQL.
/// </summary>
/// <remarks>
/// <para>
/// <c>PlatformAdminOnly</c> on the whole group. The service re-checks the actor, so the rule holds
/// for any caller that is not HTTP, and writes a <c>PlatformAuditLog</c> row in the same
/// transaction as every change.
/// </para>
/// <para>
/// Nothing here bills anybody. The subscription tier is a per-branch flag that gates features.
/// </para>
/// </remarks>
public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform")
            .WithTags(EndpointConventions.PlatformTag)
            .RequireAuthorization(YallaPolicies.PlatformAdminOnly);

        group.MapPost("/venues", CreateVenueAsync)
            .WithName("createVenue")
            .WithSummary("Create a venue with its first branch")
            .WithDescription(
                "A venue with no branch is useless, so the first branch is created in the same "
                + "transaction. A failure creates neither. The branch's `subscriptionTier` defaults to Free."
                + "\n\n"
                + "**Missing fields are reported together.** An empty body names `name`, `slug` and "
                + "`firstBranch` in one 422, each in `context.fields`, rather than making somebody "
                + "submit three times to discover three problems. Nested fields are dotted - "
                + "`firstBranch.address`."
                + "\n\n"
                + "**Out-of-range values are still reported one at a time**, as a 400 - a latitude "
                + "of 200 refuses before the next field is looked at. The bounds live in the `Venue` "
                + "and `Branch` constructors rather than in a limits class of their own, so "
                + "collecting them would mean a second copy of every rule. See `docs/platform-admin.md`.")
            .Produces<VenueDetail>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "A field is out of range, or the body is malformed.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "Required fields are missing; `context.fields` names every one.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The venue slug is already taken.");

        group.MapGet("/venues", ListVenuesAsync)
            .WithName("listVenues")
            .WithSummary("Venues, with search, filters, sorting and paging")
            .WithDescription(
                "Each row is what the console's venue table shows: the branch, active-table and "
                + "paid-branch counts; `tierMix` (1 Free, 2 Mixed, 3 Paid - billing is per branch, so a "
                + "venue is Mixed when only some branches pay) and `branches`, every branch with its own "
                + "tier and `issues`; `status` (1 Active, 2 Suspended, 3 Inactive, 4 Deleted); "
                + "`averageRating` and `reviewCount` over the published reviews of every branch; `openTabCount`; "
                + "`readinessIssue` (`code`, `count`, `branchName`), the first thing a guest would find missing at an "
                + "active branch - setup progress, so suspended venues count and switched-off branches never do - "
                + "`no-tables`, `no-menu`, `incomplete-dishes`, `no-hours` or `no-photo`; `coverThumbnailUrl`, the "
                + "first branch's cover thumbnail; `district`, `cuisine` and `createdAtUtc`.\n\n"
                + "`search` matches the venue's name and slug and each branch's name and district. "
                + "`status`: `live` (default: every venue not deleted), `all`, `active`, `suspended`, "
                + "`inactive` or `deleted`; the older `includeDeleted=true` is `all` when no `status` is "
                + "sent. `tier`: `free`, `mixed` or `paid`; blank is every tier. `sort`: `name` (default), `rating`, `tables` or "
                + "`created`; `dir`: `asc` (default) or `desc` - a venue with no rating sorts below every "
                + "rated one. `page` from 1, `pageSize` 1-200 (default 20).")
            .Produces<PagedResult<VenueSummary>>()
            .ProducesProblemDetails(
                StatusCodes.Status400BadRequest, "`status`, `tier`, `sort` or `dir` is not a value it takes; `context.field` names it.");

        group.MapGet("/overview", GetOverviewAsync)
            .WithName("getPlatformOverview")
            .WithSummary("Home: the platform's numbers in one read")
            .WithDescription(
                "**Aggregates only** - no guest's name, phone or booking code at any depth.\n\n"
                + "`venues`: venues not deleted, split by `tierMix` (`paid` every branch paid, `mixed` some, "
                + "`free` none) and by state, with `deleted` counted apart and the branch totals. "
                + "`bookings`: bookings by the day of the visit, on each branch's own calendar, cancelled "
                + "ones left out - `perDay` is thirty entries, oldest first and today last (the 14-day chart "
                + "is the last fourteen), `last7Days` against `previous7Days` with `changePercent` (absent "
                + "when the previous week had none). `pendingApprovals`: booking requests still waiting "
                + "for a venue whose visit is ahead; `openTabs`: tabs open or closing now - each a total "
                + "and the venues it comes from, most first. `notReady`: branches diners can reach that a "
                + "guest would find unfinished - `no-tables`, `no-menu`, `incomplete-dishes`, `no-hours`, "
                + "`no-photo` (the first twenty, newest venues first, and the `total`). `recentVenues`: the five newest venues not deleted. "
                + "`outbox`: the counts `GET /api/platform/outbox` serves, with `deadLetteredLastDay` - the "
                + "messages given up on in the last 24 hours, what Home's alert counts.")
            .Produces<PlatformOverview>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Platform admin only.");

        group.MapGet("/activity", GetActivityAsync)
            .WithName("listPlatformActivity")
            .WithSummary("The activity feed: the newest audit rows, newest first")
            .WithDescription(
                "Every change the audit log records that a member of staff made - the platform admin's own "
                + "and the venues' teams' (adding staff, taking down a review, replacing a QR code). Each "
                + "carries the `action` slug, the `actor` (`role` 5 is the platform admin; `displayName` as "
                + "they were called then), the `target` (`type`, `id`, `displayName` then - for a review, "
                + "the branch it is about, never the reviewer), the `venue` it concerns by its current "
                + "name, and `detail`, the row's JSON as written (shapes in `docs/platform-admin.md`). Rows "
                + "the log keeps that are not a change are left out: a diner deleting their own account (no "
                + "staff member behind it), and `admin.password-change-failed`, the password lockout's "
                + "counter.\n\n"
                + "`limit` 1-100 (default 20). `venueId` narrows it to one venue.")
            .Produces<ActivityFeed>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`limit` out of range; `context.field` names it.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Platform admin only.");

        group.MapGet("/venues/{venueId:guid}/bookings-summary", GetVenueBookingsSummaryAsync)
            .WithName("getVenueBookingsSummary")
            .WithSummary("One venue's bookings as counts, for the last 7 or 30 days")
            .WithDescription(
                "**Aggregates only**: the restaurant sees its guests; the platform sees numbers.\n\n"
                + "`days` is `7` or `30` (default 30). The window is each branch's own today and the days "
                + "before it, by the branch's local date of the visit, in **every** status: `byStatus` "
                + "splits the `total` into `completed` (seated or completed), `upcoming` (confirmed, not yet "
                + "seated), `pendingApproval`, `cancelled` (either side, rejections included) and `noShow`. "
                + "`byWeekday` is all seven days by the weekday of the visit, `dayOfWeek` 0 Sunday to 6 "
                + "Saturday. `averagePartySize` is over the bookings not cancelled, to one decimal, and absent "
                + "with none. `byBranch` is every branch of the venue, by name, with the same split flat and "
                + "`todayCount` - its bookings for today, cancelled ones left out.")
            .Produces<VenueBookingsSummary>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`days` is not 7 or 30; `context.field` names it.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.");

        group.MapGet("/venues/{venueId:guid}", GetVenueAsync)
            .WithName("getVenue")
            .WithSummary("One venue and its branches")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.");

        group.MapPatch("/venues/{venueId:guid}", UpdateVenueAsync)
            .WithName("updateVenue")
            .WithSummary("Name, type, slug, active flag")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The slug is taken, or the venue is deleted.");

        group.MapPost("/venues/{venueId:guid}/suspend", SuspendVenueAsync)
            .WithName("suspendVenue")
            .WithSummary("Hide the venue from diners; keep everything")
            .WithDescription(
                "What happens when someone stops paying. The venue disappears from diner browsing "
                + "but keeps all its data and stays visible to its owner.")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.");

        group.MapPost("/venues/{venueId:guid}/reactivate", ReactivateVenueAsync)
            .WithName("reactivateVenue")
            .WithSummary("Undo a suspension")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.");

        group.MapPost("/venues/{venueId:guid}/restore", RestoreVenueAsync)
            .WithName("restoreVenue")
            .WithSummary("Undo a soft delete")
            .WithDescription(
                "The venue comes back exactly as it was before it was deleted: switched on or off as it "
                + "was then, and **still suspended if it was suspended** - deleting never touched the "
                + "suspension. Nothing under it was removed, so nothing is rebuilt; its branches, tables, "
                + "staff and history are all where they were, and it accepts changes again. A venue "
                + "deleted before this was recorded comes back switched off, for somebody to switch on on purpose.\n\n"
                + "A venue that is not deleted is answered as it is and nothing is written, so an undo "
                + "that arrives twice is harmless. Refused with **409** when its web address now belongs "
                + "to another venue - which the unique slug index, covering deleted venues, keeps from "
                + "happening today. **Audited** as `venue.restore`.")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The venue's web address is now another venue's.");

        group.MapDelete("/venues/{venueId:guid}", DeleteVenueAsync)
            .WithName("deleteVenue")
            .WithSummary("Soft-delete a venue")
            .WithDescription(
                "**Soft delete only.** Reservations, tabs and payments hang off the venue and are "
                + "never removed. Refused while any tab is open or any confirmed booking is still in "
                + "the future - the response names which. `POST .../restore` undoes it.")
            .Produces<VenueDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "Open tabs or future bookings block the deletion; `context` lists them.");

        group.MapPost("/venues/{venueId:guid}/branches", AddBranchAsync)
            .WithName("addBranch")
            .WithSummary("Add a branch to a venue")
            .Produces<BranchSummary>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The branch slug is taken within the venue.");

        group.MapPatch("/branches/{branchId:guid}", UpdateBranchAsync)
            .WithName("updateBranch")
            .WithSummary("Name, address, coordinates, timezone, active flag, tier")
            .WithDescription(
                "`subscriptionTier` is set here, per branch. Moving a branch to Free switches off "
                + "tabs and ordering there; the tab endpoints answer `feature-not-enabled`. It is "
                + "refused while the branch has open tabs, because hiding a live bill from the people "
                + "who owe it strands real money on a real table.\n\n"
                + "**Moving a branch to Paid is refused while its menu has unfinished items.** This is "
                + "where the rule that a dish needs a photo, ingredients, allergens, a portion size and "
                + "a prep time is actually enforced - at the moment the branch starts taking diners, "
                + "rather than at the moment somebody types a name and a price. The refusal is "
                + "`branch-not-ready` and carries `incompleteMenuItemCount`; "
                + "`GET /api/branches/{branchId}/readiness` lists which items are left.")
            .Produces<BranchSummary>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<BranchNotReadyProblem>(
                StatusCodes.Status409Conflict,
                "Going Paid with an unfinished menu, or going Free with open tabs.");

        group.MapGet("/branches/{branchId:guid}/reviews", ListBranchReviewsAsync)
            .WithName("listPlatformBranchReviews")
            .WithSummary("A branch's reviews for moderation, hidden ones included")
            .WithDescription(
                "Every review of the branch - published or hidden, at any branch whether or not it is "
                + "published - with `hidden`, `hiddenReason`, `hiddenAtUtc`, `hiddenByPlatform`, "
                + "`reportCount` and `lastReportedAtUtc`. `authorName` is the public name, not the account's.\n\n"
                + "`filter`: `all` (default, newest written first), `reported` (at least one report, most "
                + "recently reported first) or `hidden`. `page` from 1, `pageSize` 1-100 (default 20); "
                + "`total` counts the rows for the filter.")
            .Produces<ModeratedReviewPage>()
            .ProducesProblemDetails(
                StatusCodes.Status400BadRequest, "`page`, `pageSize` or `filter` out of range; `context.field` names it.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/reviews/{reviewId:guid}/visibility", SetReviewVisibilityAsync)
            .WithName("setPlatformReviewVisibility")
            .WithSummary("Take a review down, or put one back")
            .WithDescription(
                "`{ \"hidden\": true, \"reason\": \"…\" }` takes it down: out of the public list, the rating, "
                + "the count and the badges at once. `reason` is required when hiding, at most 500 characters. "
                + "`{ \"hidden\": false }` puts it back; the reason is ignored.\n\n"
                + "The platform can put back any review, including one the venue hid, and a review it hides "
                + "is the platform's: the venue cannot put it back.\n\n"
                + "**Audited** as `review.hide` or `review.unhide` with `actorType: platform`. A request that "
                + "changes nothing writes nothing. Answers the review as the list shows it.")
            .Produces<ModeratedReviewView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such review.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`hidden` missing, or `reason` missing (bound `required`) or over 500 characters (bound `max`) when hiding.");

        group.MapPost("/reviews/{reviewId:guid}/dismiss-reports", DismissReviewReportsAsync)
            .WithName("dismissPlatformReviewReports")
            .WithSummary("Leave a reported review up, and set its reports aside")
            .WithDescription(
                "The platform looked and found nothing wrong: the review stays exactly as it is - shown "
                + "or hidden - and stops counting as reported, for the platform's list and the venue's. "
                + "The reports are kept, marked dismissed; `reportCount` counts only the ones nobody has "
                + "dismissed, so a diner reporting it afterwards brings it back to the reported filter.\n\n"
                + "**Idempotent**: a review with no open report is answered as it is and nothing is "
                + "written. **Audited** as `review.dismiss-reports`, with how many were dismissed. Answers "
                + "the review as the moderation list shows it.")
            .Produces<ModeratedReviewView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such review.");

        group.MapPost("/reviews/{reviewId:guid}/restore-reports", RestoreReviewReportsAsync)
            .WithName("restorePlatformReviewReports")
            .WithSummary("Undo the last dismissal of a review's reports")
            .WithDescription(
                "The console's undo for `dismiss-reports`: the reports the **most recent** dismissal of this "
                + "review set aside count again - exactly those, recognised by the dismissal's instant, which "
                + "every report it dismissed shares. Reports dismissed by an earlier dismissal stay dismissed; "
                + "reports filed since are untouched. The review itself is not changed.\n\n"
                + "**One dismissal, once**: when the review was already restored after its latest dismissal - "
                + "or nothing was ever dismissed - it is answered as it is and nothing is written; a second "
                + "undo never reaches back to an older dismissal. **Audited** as `review.restore-reports`, "
                + "with how many came back. Answers the review as the moderation list shows it.")
            .Produces<ModeratedReviewView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such review.");

        group.MapGet("/me", GetMeAsync)
            .WithName("getPlatformAdminProfile")
            .WithSummary("The signed-in platform admin's own account")
            .WithDescription(
                "`fullName` is theirs to change. `email` is the sign-in address and is fixed: it is "
                + "`PlatformAdmin:Email` in configuration, and nothing here changes it.")
            .Produces<PlatformAdminProfile>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Platform admin only.");

        group.MapPatch("/me", UpdateMeAsync)
            .WithName("updatePlatformAdminProfile")
            .WithSummary("Change the signed-in platform admin's name")
            .WithDescription(
                "`fullName`, 1-200 characters after trimming. The configured `PlatformAdmin:FullName` only "
                + "names the account when the seed first creates it; the seed never overwrites a name "
                + "changed here. **Audited** as `admin.rename`; a name that does not change writes nothing.")
            .Produces<PlatformAdminProfile>()
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "`fullName` missing, blank or over 200 characters.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Platform admin only.");

        group.MapPost("/me/password", ChangeMyPasswordAsync)
            .WithName("changePlatformAdminPassword")
            .WithSummary("Change the signed-in platform admin's password")
            .WithDescription(
                "`currentPassword` must be right and `newPassword` at least 12 characters - the one rule "
                + "every console password follows. Everything wrong is reported in one 422: a wrong current "
                + "password is `context.fields` naming `currentPassword`, not a 401, so the console stays "
                + "signed in and shows it under the field.\n\n"
                + "**Answers a fresh token pair - store it.** The change ends every session the account has, "
                + "at once: the session generation moves, so every admin-panel access token it holds - the one "
                + "this request was sent with included - is refused from now on with `401 session-revoked`, "
                + "every refresh token is revoked and every unused reset link is spent. The body "
                + "(`accessToken`, `refreshToken`, `expiresInSeconds`, the refresh endpoint's shape) is how "
                + "the sign-in that made the change carries on.\n\n"
                + "**Wrong current passwords are counted per account**: each is audited as "
                + "`admin.password-change-failed`, and after 10 inside 15 minutes the route answers "
                + "`429 too-many-attempts` with `Retry-After` (and `context.retryAfterSeconds`) until the oldest "
                + "leaves the window - right password or not. A successful change starts the count again. The "
                + "route is also under the `auth` rate limit. The seed never resets a password changed here. "
                + "**Audited** as `admin.password-change`, with nothing about either password.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy)
            .Produces<RefreshResult>()
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`currentPassword` missing or wrong, or `newPassword` missing or too short - every one at once.")
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests,
                "Too many wrong current passwords, or the `auth` rate limit; `Retry-After` says how long to wait.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Platform admin only.");

        return app;
    }

    private static async Task<IResult> GetOverviewAsync(IPlatformOverviewQuery overview, CancellationToken cancellationToken) =>
        Results.Ok(await overview.GetOverviewAsync(cancellationToken));

    private static async Task<IResult> GetActivityAsync(
        IPlatformOverviewQuery overview,
        CancellationToken cancellationToken,
        int limit = 20,
        Guid? venueId = null) =>
        Results.Ok(await overview.GetActivityAsync(new ActivityQuery(limit, venueId), cancellationToken));

    private static async Task<IResult> GetVenueBookingsSummaryAsync(
        Guid venueId,
        IPlatformOverviewQuery overview,
        CancellationToken cancellationToken,
        int days = VenueBookingsSummaryWindows.Default) =>
        Results.Ok(await overview.GetVenueBookingsSummaryAsync(venueId, days, cancellationToken));

    private static async Task<IResult> DismissReviewReportsAsync(
        Guid reviewId,
        IReviewModerationService moderation,
        CancellationToken cancellationToken) =>
        Results.Ok(await moderation.DismissReportsForPlatformAsync(reviewId, cancellationToken));

    private static async Task<IResult> GetMeAsync(IPlatformAdminAccountService account, CancellationToken cancellationToken) =>
        Results.Ok(await account.GetAsync(cancellationToken));

    private static async Task<IResult> UpdateMeAsync(
        UpdatePlatformAdminProfileRequest request,
        IPlatformAdminAccountService account,
        CancellationToken cancellationToken) =>
        Results.Ok(await account.UpdateAsync(new UpdatePlatformAdminProfileCommand(request.FullName), cancellationToken));

    private static async Task<IResult> ChangeMyPasswordAsync(
        ChangePlatformAdminPasswordRequest request,
        IPlatformAdminAccountService account,
        CancellationToken cancellationToken) =>
        Results.Ok(await account.ChangePasswordAsync(
            new ChangePlatformAdminPasswordCommand(request.CurrentPassword, request.NewPassword),
            cancellationToken));

    private static async Task<IResult> RestoreReviewReportsAsync(
        Guid reviewId,
        IReviewModerationService moderation,
        CancellationToken cancellationToken) =>
        Results.Ok(await moderation.RestoreReportsForPlatformAsync(reviewId, cancellationToken));

    private static async Task<IResult> ListBranchReviewsAsync(
        Guid branchId,
        Application.Reviews.IReviewModerationService moderation,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 20,
        string? filter = null) =>
        Results.Ok(await moderation.ListForPlatformAsync(
            branchId, new Application.Reviews.ReviewModerationQuery(page, pageSize, filter), cancellationToken));

    private static async Task<IResult> SetReviewVisibilityAsync(
        Guid reviewId,
        SetReviewVisibilityRequest request,
        Application.Reviews.IReviewModerationService moderation,
        CancellationToken cancellationToken) =>
        Results.Ok(await moderation.SetVisibilityForPlatformAsync(
            reviewId,

            // The validation filter has refused a missing `hidden` before this runs.
            new Application.Reviews.SetReviewVisibilityCommand(request.Hidden!.Value, request.Reason),
            cancellationToken));

    private static async Task<IResult> CreateVenueAsync(
        CreateVenueCommand command,
        IPlatformService service,
        CancellationToken cancellationToken)
    {
        var created = await service.CreateVenueAsync(command, cancellationToken);

        return Results.Created($"/api/platform/venues/{created.Venue.VenueId}", created);
    }

    private static async Task<IResult> ListVenuesAsync(
        IPlatformService service,
        CancellationToken cancellationToken,
        string? search = null,
        int page = 1,
        int pageSize = 20,
        bool includeDeleted = false,
        string? status = null,
        string? tier = null,
        string? sort = null,
        string? dir = null) =>
        Results.Ok(await service.ListVenuesAsync(
            new VenueListQuery(search, page, pageSize, includeDeleted, status, tier, sort, dir),
            cancellationToken));

    private static async Task<IResult> GetVenueAsync(Guid venueId, IPlatformService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.GetVenueAsync(venueId, cancellationToken));

    private static async Task<IResult> UpdateVenueAsync(
        Guid venueId,
        UpdateVenueCommand command,
        IPlatformService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.UpdateVenueAsync(venueId, command, cancellationToken));

    private static async Task<IResult> SuspendVenueAsync(Guid venueId, IPlatformService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.SuspendVenueAsync(venueId, cancellationToken));

    private static async Task<IResult> ReactivateVenueAsync(Guid venueId, IPlatformService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ReactivateVenueAsync(venueId, cancellationToken));

    private static async Task<IResult> RestoreVenueAsync(Guid venueId, IPlatformService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.RestoreVenueAsync(venueId, cancellationToken));

    private static async Task<IResult> DeleteVenueAsync(Guid venueId, IPlatformService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.DeleteVenueAsync(venueId, cancellationToken));

    private static async Task<IResult> AddBranchAsync(
        Guid venueId,
        CreateBranchCommand command,
        IPlatformService service,
        CancellationToken cancellationToken)
    {
        var created = await service.AddBranchAsync(venueId, command, cancellationToken);

        return Results.Created($"/api/platform/venues/{venueId}", created);
    }

    private static async Task<IResult> UpdateBranchAsync(
        Guid branchId,
        UpdateBranchCommand command,
        IPlatformService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.UpdateBranchAsync(branchId, command, cancellationToken));
}
