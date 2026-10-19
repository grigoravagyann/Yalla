using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.BranchSettings;
using Yalla.Application.Menus;
using Yalla.Application.Overview;
using Yalla.Application.Staff;
using Yalla.Application.Venues;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Configuring a venue: reservation policy, opening hours, the floor plan, the menu, the staff.
/// </summary>
/// <remarks>
/// <para>
/// <c>ManagerOrAbove</c> within scope, or a platform admin - the scope handlers pass the platform
/// tier for every branch and venue, so no route here needs a role check of its own. Branch routes
/// carry <c>BranchScoped</c>; the venue read and the staff routes are addressed by venue and carry
/// <c>VenueScoped</c>; the one table route resolves its branch from the table.
/// </para>
/// <para>
/// The venue read is the one a signed-in owner or manager opens the console with. It is the only
/// read of the venue itself a venue user can make (the staff list below is addressed by venue
/// too, but lists people): everything under <c>/api/platform</c> is the platform tier's, and a
/// console that tried to read the venue from there got a 403 and drew "no branch".
/// </para>
/// <para>
/// This is the tooling the team uses to draw the first twenty venues' floor plans during
/// onboarding, so the floor-plan replace is the endpoint that matters most: the editor sends the
/// finished layout, once, and it lands whole or not at all.
/// </para>
/// </remarks>
public static class VenueAdminEndpoints
{
    public static IEndpointRouteBuilder MapVenueAdminEndpoints(this IEndpointRouteBuilder app)
    {
        MapVenue(app);
        MapBranchSettings(app);
        MapMenu(app);
        MapStaff(app);

        return app;
    }

    // ------------------------------------------------------------ the venue itself

    private static void MapVenue(IEndpointRouteBuilder app)
    {
        // Addressed by venue, like the staff routes: the venue is what an owner signs in to, and
        // VenueScoped compares the token's venue claim. The query then checks the caller's stored
        // row as well, so a neighbour is refused twice and a deactivated account once.
        var group = app.MapGroup("/api/venues/{venueId:guid}")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.VenueScoped);

        // Deliberately not "/" - that is the diner-facing GET /api/venues/{venueId}, awaiting its
        // route, and two operations cannot share a path in OpenAPI 3.0. Same convention as the
        // admin menu read under /menu/manage.
        group.MapGet("/manage", GetManagedVenueAsync)
            .WithName("getManagedVenue")
            .WithSummary("The venue you work in, with the branches your account covers")
            .WithDescription(
                "The read the console opens a venue with. **Which branches come back is decided "
                + "from the caller's own staff record, not from the token**: an owner covers every "
                + "branch of the venue, whether or not their record names a home branch; a general "
                + "manager covers every branch; a branch manager is given their own branch only - the "
                + "only one the server lets them act on; a platform admin is given everything.\n\n"
                + "Branches are listed active first, then by name. An inactive branch is listed and "
                + "flagged rather than hidden.\n\n"
                + "This is not `GET /api/platform/venues/{id}`, which is the platform tier's and "
                + "refuses every venue user. Nothing here is platform-only: no tier rollup, no "
                + "paid-branch count, no suspension timestamps.")
            .Produces<ManagedVenueView>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Another venue's staff, a waiter or kitchen hand, or an account that has been deactivated.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such venue. Only a platform admin can reach this.");
    }

    private static async Task<IResult> GetManagedVenueAsync(Guid venueId, IManagedVenueQuery query, CancellationToken ct) =>
        Results.Ok(await query.GetAsync(venueId, ct));

    // ------------------------------------------------------------ branch settings

    private static void MapBranchSettings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapGet("/public-profile", GetPublicProfileAsync)
            .WithName("getBranchPublicProfile")
            .WithSummary("What this branch publishes on its public page")
            .Produces<PublicProfileView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/public-profile", PutPublicProfileAsync)
            .WithName("putBranchPublicProfile")
            .WithSummary("Set the published phone number, whether the page takes bookings, and the cover photo")
            .WithDescription(
                "`acceptsWebBookings` is **false until somebody switches it on**, and stays false "
                + "for every branch that has never been asked. A venue has not agreed to take "
                + "bookings from strangers on the internet by never having been consulted, so this "
                + "is a decision made during onboarding rather than a default inherited - which is "
                + "also why it appears on the branch readiness checklist.\n\n"
                + "While it is off the public page still shows the room, the menu and the hours and "
                + "simply offers no booking.\n\n"
                + "`phoneE164` must be E.164 (`+37411223344`); spaces, dashes and brackets are "
                + "stripped first. Null or blank clears it. A branch with no number published is "
                + "a branch a diner on the public page has no way to ask about a high chair.\n\n"
                + "`coverPhotoId` is the venue card's picture: a photo uploaded for **this** branch "
                + "through `POST /api/branches/{branchId}/photos`, or null to clear it. A photo "
                + "uploaded for another branch is not found here, and the whole form is refused "
                + "before anything is written. The cover is the card's picture only - tables are pinned "
                + "on room photos through the floor plan, so changing it moves no pin.")
            .Produces<PublicProfileView>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`phoneE164` is not a valid E.164 number.")
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound,
                "No such branch, or `coverPhotoId` names a photo that was not uploaded for it.");

        group.MapGet("/listing", GetListingAsync)
            .WithName("getBranchListing")
            .WithSummary("What the diner app's browse screens say about this branch")
            .Produces<BranchListingView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/listing", PutListingAsync)
            .WithName("putBranchListing")
            .WithSummary("Set cuisine, about, price level, website, amenities, district, map pin and gallery")
            .WithDescription(
                "Every listing field is replaced; null or blank clears it. `priceLevel` is 1-4; "
                + "`amenities` are keys from `outdoorSeating`, `wifi`, `parking`, `cardPayment`, `vegan`; "
                + "`websiteUrl` is an absolute http(s) address; `district` is the part of town, e.g. "
                + "`Kentron`, trimmed, at most 60 characters, and a manager may set it like any other "
                + "listing field - it is not the address and moves nothing. Every broken field is reported "
                + "at once as `422 validation-failed`.\n\n"
                + "**Moving the branch.** `address`, `latitude` and `longitude` travel together: coordinates "
                + "without an address, or an address without coordinates, is `422` naming the missing field "
                + "(bound `required`); the address is at most 400 characters. Sending all three as they are "
                + "stored is not a move. A move - any of them present and different from the stored value - "
                + "is allowed only to an **owner of this venue or a platform admin signed in to the admin "
                + "panel**; anyone else (a manager, or any PIN session) gets `403 relocation-not-allowed` and "
                + "**nothing on the form is saved**. A move is audited as `branch.relocate` with the old and "
                + "new address and coordinates.\n\n"
                + "`galleryPhotoIds` are photos uploaded for **this** branch through "
                + "`POST /api/branches/{branchId}/photos`, in display order, at most 12. **Null leaves the "
                + "gallery alone**; `[]` clears it. A photo from another branch is 404 and nothing is written.")
            .Produces<BranchListingView>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "Coordinates out of range.")
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "`relocation-not-allowed`: the form moves the branch and the caller is not its owner or a "
                + "platform admin. Or `forbidden`: not a manager of this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or a gallery photo not uploaded for it.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "Fields out of bounds; `context.fields` names each.");

        group.MapGet("/reservation-policy", GetPolicyAsync)
            .WithName("getReservationPolicy")
            .WithSummary("Every field of the branch's reservation policy")
            .Produces<ReservationPolicyView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/reservation-policy", PutPolicyAsync)
            .WithName("putReservationPolicy")
            .WithSummary("Replace the reservation policy")
            .WithDescription(
                "Every field, as one form. Out-of-range values are **refused, never clamped** - a "
                + "turn time of 5 minutes or 12 hours gets a 422 that says so.\n\n"
                + "**Every refusal names its field.** `context.field` is the first offending property "
                + "and `context.fields` is all of them, each with the `bound` it broke, the `min` and "
                + "`max` allowed and the `value` sent - in the same casing this schema uses, so a form "
                + "can put each message against its own input without mapping English prose back to a "
                + "field. A request that breaks six bounds reports six.\n\n"
                + "**Existing bookings are never touched.** If the new window or turn time would not "
                + "have allowed some of them, `affectedExistingReservations` says how many and "
                + "`affectedReservationIds` which; they stay exactly as booked. The new rules apply "
                + "to future bookings only.\n\n"
                + "Saving this form is what marks the policy as reviewed on the branch readiness "
                + "checklist.")
            .Produces<ReservationPolicyChangeResult>()
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "One or more fields are outside their bounds; `context.fields` names every one.");

        group.MapGet("/opening-hours", GetHoursAsync)
            .WithName("getOpeningHours")
            .WithSummary("The weekly opening hours")
            .Produces<IReadOnlyList<OpeningHoursView>>();

        group.MapPut("/opening-hours", PutHoursAsync)
            .WithName("putOpeningHours")
            .WithSummary("Replace the whole week atomically")
            .WithDescription(
                "Send every block for every day. `closesNextDay` is derived - a closing time at or "
                + "before the opening time means after midnight - and is not accepted from the client. "
                + "Blocks on one day may touch but not overlap.\n\n"
                + "The body is an array, so a refusal names the offending block by index: "
                + "`context.field` reads `[2].closesAt`. Every bad block is reported, not just the "
                + "first.")
            .Produces<IReadOnlyList<OpeningHoursView>>()
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "Blocks overlap, or one opens and closes at the same minute; `context.fields` names each.");

        group.MapGet("/opening-hours/exceptions", GetSpecialDaysAsync)
            .WithName("getOpeningHoursExceptions")
            .WithSummary("The special opening days from today on")
            .WithDescription(
                "Holidays, closures and different hours on one date, earliest first, from today in the branch's "
                + "own zone. A special day **replaces** that date's weekly blocks; `closed` (no `blocks`) is shut all "
                + "day. A date's blocks are the ones that open on it, so 31 December 18:00-03:00 runs into "
                + "1 January whatever 1 January says. Availability, the day's slots, booking, the Overview and "
                + "every \"open now\" read them.")
            .Produces<IReadOnlyList<SpecialOpeningDayView>>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/opening-hours/exceptions", PutSpecialDaysAsync)
            .WithName("putOpeningHoursExceptions")
            .WithSummary("Replace the special opening days from today on")
            .WithDescription(
                "Send the whole list from today at the branch on (`[]` clears it); days before today are kept as "
                + "they were and never sent. Each day is a `date`, an optional `note` a diner is shown (at most 60 "
                + "characters) and its `blocks` - `[]` for closed all day, at most 4. `closesNextDay` is derived as "
                + "in the weekly hours. Saved atomically under the branch lock, so two saves never interleave.\n\n"
                + "**Every refusal names its field**, indexed into the array: `[1].date` is `min` (before today), "
                + "`max` (more than 366 days ahead) or `conflict` (the same date twice); `[0].note` `max`; "
                + "`[0].blocks` `required` or `max`; `[0].blocks[1].opensAt` `conflict` (overlaps an earlier "
                + "block); `[0].blocks[0].closesAt` `conflict` (no length); `[100]` `max` (more than 100 days).\n\n"
                + "**Existing bookings are never touched.** `affectedReservationIds` are the live ones on a changed "
                + "date that no longer fit its hours.")
            .Produces<SpecialOpeningDaysChangeResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict, "`concurrent-update`: another save held the branch; try again.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "A day or a block breaks a rule; `context.fields` names each.");

        group.MapGet("/readiness", GetReadinessAsync)
            .WithName("getBranchReadiness")
            .WithSummary("What this branch still needs before it can take diners")
            .WithDescription(
                "The onboarding checklist, answered by the server: floor plan drawn, tables labelled, "
                + "at least one menu category, how many menu items are still incomplete, hours set, "
                + "reservation policy reviewed, staff enrolled, at least one tablet.\n\n"
                + "The console used to render this from a client-side guess, which meant the console "
                + "and the server had two different ideas of ready - and only the server's decides "
                + "whether the branch may be switched to `Paid`. `incompleteMenuItemCount` is the line "
                + "that gate enforces, and `incompleteMenuItemIds` says which items to finish.\n\n"
                + "`blockers` says what is left in sentences; `issues` says the same lines, one for one "
                + "and in the same order, as stable codes - `no-tables`, `tables-unlabelled`, `no-menu`, "
                + "`menu-empty`, `incomplete-dishes` (with `count`), `no-hours`, "
                + "`policy-not-reviewed`, `no-staff`, `no-device`.")
            .Produces<BranchReadinessView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapGet("/overview", GetOverviewAsync)
            .WithName("getBranchOverview")
            .WithSummary("The console's Overview: today at this branch")
            .WithDescription(
                "One read for the Overview screen's numbers. No parameters. Poll it with the approvals list and read "
                + "it again after any booking action.\n\n"
                + "**Today is the branch's service day** (`serviceDate`), in its own time zone at `asOfUtc`: the "
                + "calendar day at the branch (`localDate`, `localTime`), except while last night's opening block is "
                + "still open past midnight - at 01:00 on Sunday a bar open 20:00-02:00 is still in Saturday's "
                + "service. A booking belongs to the service day it starts in: 00:30 on Sunday inside Saturday's block "
                + "is Saturday's, and 00:30 on Saturday was Friday night's. A block closing at 00:00 does not run past "
                + "midnight.\n\n"
                + "- `graceMinutes`: how long past its start a late party's table is held (the policy's grace).\n"
                + "- `hours`: `isOpen`; while open `closesAt` and `closesNextDay` (the close falls after midnight "
                + "tonight - \"(next day)\"; false for a 00:00 close, which reads \"until 00:00\", and for last "
                + "night's block, which closes today); while shut `opensAt`, the next opening later today, absent when "
                + "there is none.\n"
                + "- `bookings`: the service day's bookings - `total` (pending, confirmed, seated, completed and "
                + "no-shows), `pendingApproval` (requests with no confirmed slot today to fall back to), `confirmed` "
                + "(the late ones and the confirmed bookings whose change is waiting included), `late` (of those, "
                + "start passed), `seated`, `completed`, `noShow`, `cancelled` (by the diner or the venue, declined "
                + "requests included), `people` (the party sizes of the pending, confirmed, seated and completed) and "
                + "`pendingChanges` (confirmed bookings with a change waiting).\n"
                + "- `inRoom`: `people` at the sittings still open, `occupiedTables`, `tables` (active, any state) and "
                + "`outOfServiceTables`.\n"
                + "- `openTabs`: `count` open or settling and `unpaidAmd`, what they still owe.\n"
                + "- `revenue`: `todayAmd`, the closed tabs' totals from `sinceUtc` - the service date's first "
                + "opening, or its local midnight when it has no hours - until now, against `lastWeekAmd` from the "
                + "same opening on `lastWeekDate` (seven days before) for as long on the clock; `changePercent` to one "
                + "decimal, absent when last week took nothing. A tab counts at the moment it closed.\n"
                + "- `load`: `capacity` (seats at the active tables in service - the chart's full-room line) and "
                + "`hours`, one entry per hour **numbered from the service date's midnight**: 0-23 on that date, then "
                + "24 for 00:00-01:00 of the next, 25 for 01:00 - label one `hour % 24`. They run from the first "
                + "opening (or booking) to the last open (or booked) hour. `people` are the confirmed, seated and "
                + "completed parties whose sitting touches the hour, `pendingPeople` the unanswered requests and the "
                + "slots changes ask for. Empty when shut all day with nothing booked.\n"
                + "- `todayBookings`: the service day's bookings by start, **leaving out the cancelled and declined**, "
                + "at most 200 (`todayBookingsTruncated` when there were more; the counts and the chart read them all) "
                + "- `id`, `code`, `status`, `startUtc`, `endUtc`, `localDate`, `localStartTime`, `partySize`, "
                + "`guestName`, `tableId`, `tableLabel`, `tableStatus` (the table now), `roomName`, `channel`, "
                + "`changeRequested`, `holdExpiresAtUtc` and `lateNudgeAtUtc`/`lateNudgeSent`. A confirmed booking "
                + "whose change is waiting is listed at the slot still promised (its time, table and party) with "
                + "`changeRequested` true and `status` 1 PendingApproval: the change is in the approvals list, and the "
                + "floor's actions on it wait for that answer. `holdExpiresAtUtc` and the nudge are given only for a "
                + "`Confirmed` booking; the nudge is the one the outbox holds - its send time once `lateNudgeSent`, "
                + "else when it is due; there is no manual nudge. No guest phone numbers.\n\n"
                + "The row menu's actions are the floor's own routes, which the console's sign-in may call: "
                + "`seat-reservation` and `free` under `/api/branches/{branchId}/tables/{tableId}`, and `no-show`, "
                + "`release` (`outcome` 2 to cancel as the venue), `approve` and `reject` under "
                + "`/api/reservations/{id}`.\n\n"
                + "At most seven statements however many bookings. An owner or general manager at any branch of their "
                + "venue, a branch manager at their own branch only, a platform admin anywhere.")
            .Produces<BranchOverview>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "A waiter's or cook's tablet session, a diner, another venue's staff, or a manager of another branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapGet("/floor-plan", GetFloorPlanAsync)
            .WithName("getFloorPlan")
            .WithSummary("The rooms, their photos, and every table with its pin")
            .WithDescription(
                "The rooms editor's read. Each area is a room (Hall, Terrace, VIP) with its `photos` in the "
                + "order a diner swipes them. Each table carries `photoId`/`photoX`/`photoY`: the room photo it "
                + "is pinned on and where, as fractions of the picture (0-1 from the left and the top), or all "
                + "three null when it has no pin. Inactive tables are included, flagged by `isActive`.\n\n"
                + "`version` is the plan's revision, opaque: send it back as `expectedVersion` when saving.")
            .Produces<FloorPlanView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/floor-plan", PutFloorPlanAsync)
            .WithName("putFloorPlan")
            .WithSummary("Replace the rooms, their photos and every table's pin in one atomic call")
            .WithDescription(
                "The editor sends the finished layout: rooms with their `photoIds`, and tables with their "
                + "pins. It lands whole or not at all.\n\n"
                + "- Tables match by `id`, then by `label`, so an editor that lost the ids still edits "
                + "the same tables and their **QR codes survive**. A QR token is never regenerated by an edit.\n"
                + "- Labels must be unique and present; room names too. Violations are `422 floor-plan-invalid` "
                + "naming them in `context.errors`.\n"
                + "- A room has at most 10 photos (`areas[i].photoIds`, bound `max`), no photo twice, and a "
                + "photo is in at most one room (`areas[i].photoIds[j]`, bound `conflict`). A photo not "
                + "uploaded for this branch is `404`, the gallery's answer.\n"
                + "- A pin is `photoId`, `photoX` and `photoY` together or none of them (`tables[i].photoX`, "
                + "bound `required`), each coordinate 0-1 (bound `range`), on one of **the table's own room's** "
                + "photos (`tables[i].photoId`, bound `conflict`).\n"
                + "- **A photo dropped from every room takes its tables off it**: a table still sent pinned on "
                + "such a photo is saved with no pin rather than refused.\n"
                + "- A bookable table with no pin is saved, with a warning: diners are not offered it.\n"
                + "- A table omitted from the plan is deleted only if it has never been used. One with any "
                + "reservation, session or tab is **deactivated** instead, and `deactivatedTables` says so. "
                + "One with a booking still to come (Confirmed or PendingApproval, not yet ended) is refused: "
                + "`409 table-has-future-bookings` naming `context.tables` and `context.bookings`, nothing written.\n"
                + "- **`expectedVersion` is required**: the `version` the editor loaded. A plan saved by "
                + "somebody else since then is `409 floor-plan-changed` with `context.currentVersion`, and "
                + "nothing is written. Missing, it is `422` naming `expectedVersion`. A successful save "
                + "returns the new `version`; nothing else moves it.")
            .Produces<FloorPlanReplaceResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or a room photo that is not this branch's.")
            .ProducesProblem<FloorPlanChangedProblem>(
                StatusCodes.Status409Conflict,
                "`floor-plan-changed`: saved by somebody else since `expectedVersion`. `concurrent-update` "
                + "(no context) when another save on this branch held on for too long - reload and retry.")
            .ProducesProblem<TableHasFutureBookingsProblem>(
                StatusCodes.Status409Conflict,
                "`table-has-future-bookings`: a table left out of the plan has a booking still to come. "
                + "Move or cancel the bookings in `context.bookings`, or keep the tables.")
            .ProducesProblem<FloorPlanRejectedProblem>(StatusCodes.Status422UnprocessableEntity, FloorPlan422)
            .ProducesProblem<ValidationFailedProblem>(StatusCodes.Status422UnprocessableEntity, FloorPlan422);

        group.MapPost("/floor-areas", CreateAreaAsync)
            .WithName("createFloorArea")
            .WithSummary("Add a floor area")
            .Produces<FloorAreaView>(StatusCodes.Status201Created);

        group.MapPatch("/floor-areas/{areaId:guid}", UpdateAreaAsync)
            .WithName("updateFloorArea")
            .WithSummary("Rename or reorder a floor area")
            .Produces<FloorAreaView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such area at this branch.");

        group.MapDelete("/floor-areas/{areaId:guid}", DeleteAreaAsync)
            .WithName("deleteFloorArea")
            .WithSummary("Remove a room and its photos; its tables stay, with no room and no pin")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such area at this branch.");

        group.MapDelete("/tables/{tableId:guid}", DeleteTableAsync)
            .WithName("deleteTable")
            .WithSummary("Delete a never-used table, or deactivate one with history")
            .WithDescription(
                "A table with any reservation, session or tab is **deactivated**, not deleted - "
                + "deleting it would orphan financial and occupancy records. The response says which "
                + "happened. Only a table that has never been used is removed outright. A table with a "
                + "booking still to come (Confirmed or PendingApproval, not yet ended) is neither: it is "
                + "refused `409 table-has-future-bookings` with `context.tables` and `context.bookings`.\n\n"
                + "Either outcome changes the floor plan and moves its version: `floorPlanVersion` is the "
                + "new one. A floor-plan save still carrying the version loaded before the delete is "
                + "refused 409 `floor-plan-changed`, rather than saving the table back as a new one.")
            .Produces<TableDeletionResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such table at this branch.")
            .ProducesProblem<TableHasFutureBookingsProblem>(
                StatusCodes.Status409Conflict,
                "`table-has-future-bookings`: the table has a booking still to come - move or cancel the "
                + "bookings in `context.bookings` first. Also 409 (no context): a party is seated at the "
                + "table, or another floor-plan save held the plan too long.");

        // Addressed by table, so BranchScoped resolves the branch from the table.
        app.MapPost("/api/tables/{tableId:guid}/regenerate-qr", RegenerateQrAsync)
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped)
            .WithName("regenerateTableQr")
            .WithSummary("Replace a compromised QR code")
            .WithDescription(
                "The one way a table's QR token changes. Editing the table never touches it - the "
                + "printed code must keep working - so this is explicit, and audited to the platform log.")
            .Produces<FloorTableView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such table.");
    }

    private static async Task<IResult> GetPublicProfileAsync(
        Guid branchId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetPublicProfileAsync(branchId, ct));

    private static async Task<IResult> PutPublicProfileAsync(
        Guid branchId, PublicProfileCommand command, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.UpdatePublicProfileAsync(branchId, command, ct));

    private static async Task<IResult> GetPolicyAsync(Guid branchId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetReservationPolicyAsync(branchId, ct));

    private static async Task<IResult> PutPolicyAsync(
        Guid branchId, ReservationPolicyCommand command, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateReservationPolicyAsync(branchId, command, ct));

    private static async Task<IResult> GetHoursAsync(Guid branchId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetOpeningHoursAsync(branchId, ct));

    private static async Task<IResult> GetSpecialDaysAsync(
        Guid branchId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetSpecialOpeningDaysAsync(branchId, ct));

    private static async Task<IResult> PutSpecialDaysAsync(
        Guid branchId, IReadOnlyList<SpecialOpeningDayInput> days, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.ReplaceSpecialOpeningDaysAsync(branchId, days, ct));

    private static async Task<IResult> GetReadinessAsync(
        Guid branchId, IBranchReadinessQuery readiness, CancellationToken ct) =>
        Results.Ok(await readiness.GetAsync(branchId, ct));

    private static async Task<IResult> GetOverviewAsync(
        Guid branchId, IBranchOverviewQuery overview, CancellationToken ct) =>
        Results.Ok(await overview.GetAsync(branchId, ct));

    private static async Task<IResult> PutHoursAsync(
        Guid branchId, IReadOnlyList<OpeningHoursBlock> blocks, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.ReplaceOpeningHoursAsync(branchId, blocks, ct));

    private static async Task<IResult> GetFloorPlanAsync(Guid branchId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.GetFloorPlanAsync(branchId, ct));

    private static async Task<IResult> GetListingAsync(
        Guid branchId, IBranchListingService service, CancellationToken ct) =>
        Results.Ok(await service.GetAsync(branchId, ct));

    private static async Task<IResult> PutListingAsync(
        Guid branchId,
        BranchListingCommand command,
        ClaimsPrincipal user,
        IBranchListingService service,
        CancellationToken ct) =>
        Results.Ok(await service.UpdateAsync(
            branchId,
            command,
            // The panel sign-in, not a PIN session on a tablet: only the first may move a branch. A
            // platform admin's token is a venue-user token too.
            signedInToAdminPanel: user.PrincipalType() == PrincipalType.VenueUser,
            ct));

    /// <summary>The floor-plan save's two 422 shapes share one description.</summary>
    private const string FloorPlan422 =
        "Missing or repeated labels or room names, or a table matched twice (`floor-plan-invalid`, "
        + "`context.errors`); or a photo or pin rule, or `expectedVersion` missing (`validation-failed`, "
        + "`context.fields` names each).";

    private static async Task<IResult> PutFloorPlanAsync(
        Guid branchId, ReplaceFloorPlanCommand command, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.ReplaceFloorPlanAsync(branchId, command, ct));

    private static async Task<IResult> CreateAreaAsync(
        Guid branchId, FloorAreaCommand command, IBranchSettingsService service, CancellationToken ct)
    {
        var area = await service.CreateFloorAreaAsync(branchId, command, ct);

        return Results.Created($"/api/branches/{branchId}/floor-plan", area);
    }

    private static async Task<IResult> UpdateAreaAsync(
        Guid branchId, Guid areaId, FloorAreaCommand command, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateFloorAreaAsync(branchId, areaId, command, ct));

    private static async Task<IResult> DeleteAreaAsync(Guid branchId, Guid areaId, IBranchSettingsService service, CancellationToken ct)
    {
        await service.DeleteFloorAreaAsync(branchId, areaId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteTableAsync(Guid branchId, Guid tableId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.DeleteTableAsync(branchId, tableId, ct));

    private static async Task<IResult> RegenerateQrAsync(Guid tableId, IBranchSettingsService service, CancellationToken ct) =>
        Results.Ok(await service.RegenerateQrTokenAsync(tableId, ct));

    // ------------------------------------------------------------ menu

    private static void MapMenu(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}/menu")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        // Deliberately not "/" - that is the diner-facing GET /api/branches/{id}/menu, and two
        // operations cannot share a path in OpenAPI 3.0. The two reads return the same shape; this
        // one exists because it is reachable for a venue that is suspended, which the diner route
        // refuses on purpose. A manager fixing their menu during a suspension needs to see it.
        group.MapGet("/manage", GetMenuAsync)
            .WithName("getMenuForAdmin")
            .WithSummary("The full menu, including unavailable and unfinished items")
            .WithDescription(
                "The same shape as the public `GET /api/branches/{branchId}/menu`, without its "
                + "open-for-business gate, so a suspended venue's manager can still edit.\n\n"
                + "**This read includes items with `isComplete: false`; the diner-facing one does "
                + "not.** A manager has to be able to see the eleven dishes that still need a photo - "
                + "that is the entire point of being allowed to save them half-entered - and a diner "
                + "must never be shown a dish with no allergen list.")
            .Produces<IReadOnlyList<MenuCategoryView>>();

        group.MapPost("/categories", CreateCategoryAsync)
            .WithName("createMenuCategory")
            .WithSummary("Add a category")
            .Produces<MenuCategoryView>(StatusCodes.Status201Created);

        group.MapPatch("/categories/{categoryId:guid}", UpdateCategoryAsync)
            .WithName("updateMenuCategory")
            .WithSummary("Rename or reorder a category")
            .Produces<MenuCategoryView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such category at this branch.");

        group.MapPut("/categories/order", ReorderCategoriesAsync)
            .WithName("reorderMenuCategories")
            .WithSummary("Put every category in a new order, atomically")
            .WithDescription(
                "`categoryIds` is **every category of the branch, each exactly once**, first to last; they get "
                + "display orders 0, 1, 2... in one transaction under the branch lock, so a move up or down is "
                + "one request and two saves at once never leave two categories on one position. Returns the "
                + "menu as `GET .../menu/manage` does.\n\n"
                + "A list that misses one, repeats one or names another branch's is refused whole with "
                + "`context.field` `categoryIds` (`conflict`): the console is showing a stale menu - reload it.")
            .Produces<IReadOnlyList<MenuCategoryView>>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict, "`concurrent-update`: another save held the branch; try again.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "`categoryIds` is not every category exactly once.");

        group.MapPut("/categories/{categoryId:guid}/items/order", ReorderItemsAsync)
            .WithName("reorderMenuItems")
            .WithSummary("Put every item of a category in a new order, atomically")
            .WithDescription(
                "`itemIds` is **every item of the category, each exactly once**, first to last - display orders "
                + "0, 1, 2... in one transaction under the branch lock. Returns the category. A list that is not "
                + "every item exactly once is refused whole with `context.field` `itemIds` (`conflict`).")
            .Produces<MenuCategoryView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such category at this branch.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict, "`concurrent-update`: another save held the branch; try again.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "`itemIds` is not every item of the category exactly once.");

        group.MapDelete("/categories/{categoryId:guid}", DeleteCategoryAsync)
            .WithName("deleteMenuCategory")
            .WithSummary("Remove a category and its items")
            .WithDescription("Refused while any of its items appears on an order; mark them unavailable instead.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "Items in this category appear on orders.");

        group.MapPost("/categories/{categoryId:guid}/items", CreateItemAsync)
            .WithName("createMenuItem")
            .WithSummary("Add an item; only a name and a price are required")
            .WithDescription(
                "**A photo and the descriptive fields are optional here and required to go live.** "
                + "Ingredients, allergens, portion size, prep minutes and a photo are what a diner "
                + "would otherwise ask a waiter, so an item without them is not fit to show one - but "
                + "requiring them at this point meant an eighty-dish menu could not be entered without "
                + "eighty photo uploads first, in order, before a single name or price could be typed. "
                + "That is not the order the work happens in.\n\n"
                + "So the rule moved rather than went away. An item saved without them comes back with "
                + "`isComplete: false`; `GET /api/branches/{branchId}/readiness` counts it; the branch "
                + "cannot be switched to `Paid` while any remain; and the diner-facing menu does not "
                + "return it at all.")
            .Produces<MenuItemView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "The name is blank, or the price is negative.");

        group.MapPatch("/items/{itemId:guid}", UpdateItemAsync)
            .WithName("updateMenuItem")
            .WithSummary("Edit an item, including its price and its category")
            .WithDescription(
                "A price change never affects existing order lines, which snapshotted the price they "
                + "were placed at.\n\n"
                + "`categoryId` **moves the item to another category of the same branch**, where it "
                + "lands last in the display order - reorder afterwards if that is not where it "
                + "belongs. Existing order lines are unaffected: they never reference a category. A "
                + "category belonging to another branch is refused with `context.field` of "
                + "`categoryId`.\n\n"
                + "A field left out is left alone, including one that was never filled in, so a "
                + "half-entered item can be saved again without the edit insisting on the half that "
                + "is missing.")
            .Produces<MenuItemView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such item at this branch.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`categoryId` names a category that is not on this branch.");

        group.MapPost("/items/{itemId:guid}/availability", SetAvailabilityAsync)
            .WithName("setMenuItemAvailability")
            .WithSummary("\"We're out of khachapuri tonight\"")
            .WithDescription("Separate from delete. The item stays on the menu record; diners cannot order it while unavailable.")
            .Produces<MenuItemView>();

        group.MapDelete("/items/{itemId:guid}", DeleteItemAsync)
            .WithName("deleteMenuItem")
            .WithSummary("Remove an item, or deactivate one that appears on orders")
            .WithDescription(
                "An item referenced by any order line cannot be deleted - the reference must survive - so it "
                + "is marked unavailable instead, and the response says so.")
            .Produces<MenuItemDeletionResult>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such item at this branch.");
    }

    private static async Task<IResult> GetMenuAsync(Guid branchId, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.GetMenuAsync(branchId, ct));

    private static async Task<IResult> ReorderCategoriesAsync(
        Guid branchId, ReorderMenuCategoriesCommand command, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.ReorderCategoriesAsync(branchId, command, ct));

    private static async Task<IResult> ReorderItemsAsync(
        Guid branchId, Guid categoryId, ReorderMenuItemsCommand command, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.ReorderItemsAsync(branchId, categoryId, command, ct));

    private static async Task<IResult> CreateCategoryAsync(
        Guid branchId, CreateMenuCategoryCommand command, IMenuService service, CancellationToken ct)
    {
        var category = await service.CreateCategoryAsync(branchId, command, ct);

        return Results.Created($"/api/branches/{branchId}/menu", category);
    }

    private static async Task<IResult> UpdateCategoryAsync(
        Guid branchId, Guid categoryId, UpdateMenuCategoryCommand command, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateCategoryAsync(branchId, categoryId, command, ct));

    private static async Task<IResult> DeleteCategoryAsync(Guid branchId, Guid categoryId, IMenuService service, CancellationToken ct)
    {
        await service.DeleteCategoryAsync(branchId, categoryId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> CreateItemAsync(
        Guid branchId, Guid categoryId, CreateMenuItemCommand command, IMenuService service, CancellationToken ct)
    {
        var item = await service.CreateItemAsync(branchId, categoryId, command, ct);

        return Results.Created($"/api/branches/{branchId}/menu", item);
    }

    private static async Task<IResult> UpdateItemAsync(
        Guid branchId, Guid itemId, UpdateMenuItemCommand command, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateItemAsync(branchId, itemId, command, ct));

    /// <summary>Body of the availability toggle.</summary>
    public sealed record SetAvailabilityRequest(bool IsAvailable);

    private static async Task<IResult> SetAvailabilityAsync(
        Guid branchId, Guid itemId, SetAvailabilityRequest request, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.SetItemAvailabilityAsync(branchId, itemId, request.IsAvailable, ct));

    private static async Task<IResult> DeleteItemAsync(Guid branchId, Guid itemId, IMenuService service, CancellationToken ct) =>
        Results.Ok(await service.DeleteItemAsync(branchId, itemId, ct));

    // ------------------------------------------------------------ staff

    private static void MapStaff(IEndpointRouteBuilder app)
    {
        // Staff belong to a venue, not a branch - an owner works everywhere - so the routes are
        // addressed by venue and VenueScoped compares the token's venue claim.
        var group = app.MapGroup("/api/venues/{venueId:guid}/staff")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.VenueScoped);

        group.MapGet("/", ListStaffAsync)
            .WithName("listStaff")
            .WithSummary("Everyone who works for the venue")
            .WithDescription(
                "Most senior first, then by name. An owner, general manager or platform admin sees the "
                + "whole venue; a branch manager sees the people at their own branch.")
            .Produces<IReadOnlyList<StaffMemberView>>();

        group.MapPost("/", CreateStaffAsync)
            .WithName("createStaff")
            .WithSummary("Add a staff member, with a PIN")
            .WithDescription(
                "Roles: 1 Owner, 6 GeneralManager, 2 Manager (a branch manager), 3 Waiter, 4 Kitchen. "
                + "A branch manager may create waiters and kitchen staff at their own branch; a general "
                + "manager may create branch managers, waiters and kitchen at any branch; an owner or "
                + "platform admin may also create general managers and owners. Nobody creates a role "
                + "above their own.\n\n"
                + "**The branch suits the role**: a branch manager needs `branchId`, a general manager "
                + "must not have one - otherwise `422 validation-failed` with `context.field` = "
                + "`branchId`.\n\n"
                + "A manager or owner gets their admin-panel sign-in through `issueStaffSignIn` after "
                + "they exist: it stores the address and returns a link they choose their own password "
                + "by. Sending `email` and `password` here still works, and is the one path where the "
                + "caller types a password for somebody else.")
            .Produces<StaffMemberView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "The role asked for is above what the caller may assign, or a branch manager is putting "
                + "somebody at another branch or at none.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "A branch manager with no `branchId`, or a general manager with one. `context.field` is `branchId`.");

        group.MapPatch("/{staffMemberId:guid}", UpdateStaffAsync)
            .WithName("updateStaff")
            .WithSummary("Edit a staff member")
            .WithDescription(
                "Nobody may change their own role or deactivate their own account. Set `setBranch` to "
                + "apply `branchId`, including null for venue-wide.\n\n"
                + "The role and the branch are checked as a pair: promoting a branch manager to general "
                + "manager is `role: 6, setBranch: true, branchId: null` in one request, and the reverse "
                + "names the branch. A branch manager edits only their own branch's people and cannot "
                + "move anybody elsewhere.")
            .Produces<StaffMemberView>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Changing your own role, editing someone you could not have created, or a branch manager "
                + "reaching past their branch.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "The resulting role and branch do not fit: a branch manager with none, or a general manager with one.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such staff member in this venue.");

        group.MapPost("/{staffMemberId:guid}/pin", SetPinAsync)
            .WithName("setStaffPin")
            .WithSummary("Set or reset a PIN")
            .WithDescription("4 to 8 digits. Clears any lockout.")
            .Produces<StaffMemberView>();

        // Mints a credential, so it carries the sign-in budget rather than the global one: ten a
        // minute per caller is plenty for a person and not much for a script.
        group.MapPost("/{staffMemberId:guid}/sign-in", IssueSignInAsync)
            .WithName("issueStaffSignIn")
            .WithSummary("Give an owner, general manager or branch manager their admin-panel sign-in")
            .WithDescription(
                "Stores the address and returns a link the person opens to choose their own password. "
                + "Nobody types a password for somebody else: the reset endpoint that consumes the link is "
                + "the only thing that ever sets one.\n\n"
                + "The link is returned **once** and this is the only copy - the server keeps the token's "
                + "hash and never logs it, so a lost link is replaced by issuing another. It is good for "
                + "24 hours and exactly one use. Issuing a new link retires any earlier unused one; two "
                + "links issued at the same instant can both be live until one is used.\n\n"
                + "Only somebody the caller strictly outranks: a general manager issues for branch "
                + "managers, an owner for general and branch managers, a platform admin for owners as "
                + "well, and nobody for a peer or for themselves. A person who "
                + "already has a password keeps it - and their open sessions - until the link is used, "
                + "when both are replaced. Their sign-in address changes to the one given as soon as this "
                + "answers.\n\n"
                + "Ten a minute per caller: this mints a credential, so it spends the sign-in budget "
                + "rather than the global one.")
            .Produces<StaffSignInLink>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not allowed to issue a sign-in for this person: yourself, an equal, or someone above you.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such staff member in this venue.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "A waiter or kitchen hand signs in with a PIN; the person is deactivated; or that address already has an account.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`email` is missing, not an address, or longer than 320 characters; `context.field` names it.")
            .ProducesProblemDetails(
                StatusCodes.Status429TooManyRequests,
                "Too many sign-ins issued in the last minute. Wait, then retry.")
            .RequireRateLimiting(RateLimitingExtensions.AuthPolicy);

        // Device enrolment codes and revocation live under /api/branches/{branchId}/devices from
        // the authentication task, with the same policies; nothing is duplicated here.
    }

    private static async Task<IResult> ListStaffAsync(Guid venueId, IStaffManagementService service, CancellationToken ct) =>
        Results.Ok(await service.ListAsync(venueId, ct));

    private static async Task<IResult> CreateStaffAsync(
        Guid venueId, CreateStaffCommand command, IStaffManagementService service, CancellationToken ct)
    {
        var created = await service.CreateAsync(venueId, command, ct);

        return Results.Created($"/api/venues/{venueId}/staff/{created.Id}", created);
    }

    private static async Task<IResult> UpdateStaffAsync(
        Guid venueId, Guid staffMemberId, UpdateStaffCommand command, IStaffManagementService service, CancellationToken ct) =>
        Results.Ok(await service.UpdateAsync(venueId, staffMemberId, command, ct));

    /// <summary>Body of the PIN reset.</summary>
    public sealed record SetPinRequest(string Pin);

    private static async Task<IResult> SetPinAsync(
        Guid venueId, Guid staffMemberId, SetPinRequest request, IStaffManagementService service, CancellationToken ct) =>
        Results.Ok(await service.SetPinAsync(venueId, staffMemberId, request.Pin, ct));

    /// <summary>Body of the sign-in issue.</summary>
    /// <param name="Email">The address this person will sign in with. Stored lowercased.</param>
    public sealed record IssueSignInRequest(
        [Required][EmailAddress][StringLength(FieldLengths.Email)] string Email);

    private static async Task<IResult> IssueSignInAsync(
        Guid venueId, Guid staffMemberId, IssueSignInRequest request, IStaffManagementService service, CancellationToken ct) =>
        Results.Ok(await service.IssueSignInAsync(venueId, staffMemberId, new IssueSignInCommand(request.Email), ct));
}
