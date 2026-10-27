using Microsoft.AspNetCore.RateLimiting;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Errors;
using Yalla.Application.Abstractions;
using Yalla.Application.Menus;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The anonymous surface: a link anybody can open, with no app.
/// </summary>
/// <remarks>
/// <para>
/// Every diner today has to install an app before they can see anything, which is the wrong shape
/// for a product whose main growth channel is a venue putting a link in its Instagram bio and a
/// diner sending it to four friends on WhatsApp. It is also the wrong shape for a tourist - a named
/// primary user group, and somebody who will not install a Yerevan-only app to find out whether a
/// table is free.
/// </para>
/// <para>
/// <b>Strictly read-only about the venue, and blind to everything else.</b> No staff data, no tab
/// data, no other diners, and no floor state beyond what a person standing in the doorway could see
/// - which specifically excludes a table's QR token, the credential that opens a tab. The response
/// shapes in <c>PublicModels</c> physically cannot carry those fields, which holds better than
/// remembering not to select them.
/// </para>
/// <para>
/// <b>Rate limited harder than the app routes</b>, per address and again per branch. Everything else
/// anonymous in this API is reached by somebody who has at least scanned a code at a table; this is
/// reached by anybody with a URL that is meant to be public.
/// </para>
/// <para>
/// <b>Booking reuses the existing flow</b> - request a code, verify, create a reservation - and since 2026-10-27 books
/// through a route of its own, <c>POST /api/public/branches/{branchId}/reservations</c> (mapped with the reservations,
/// since it needs the diner's session this anonymous group cannot require), so the platform can allow web and app
/// bookings apart (<c>docs/branch-features.md</c>). See
/// <c>docs/reports.md</c> for the consequence that needs a commercial decision: somebody who books
/// here has no app, so no reminder reaches them.
/// </para>
/// </remarks>
public static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public")
            .WithTags(EndpointConventions.DinerTag)
            .AllowAnonymous()

            // One policy, because an endpoint only ever has one - the per-branch ceiling is chained
            // into the global limiter instead. See RateLimitingExtensions.
            .RequireRateLimiting(RateLimitingExtensions.PublicPolicy);

        group.MapGet("/venues", GetVenuesAsync)
            .WithName("getPublicVenues")
            .WithSummary("Every venue on Yalla, for the browse case")
            .WithDescription(
                "Active, non-suspended venues with their active branches: name, type, the slug pair "
                + "that addresses each branch, its IANA `timeZoneId`, whether it is open now, and a "
                + "**live free-table count**: every table nobody is sitting at, walk-in-only stools "
                + "included, the same tables the branch page's `tableCount` counts.\n\n"
                + "The estate is cached for minutes; the table counts, open-now and which branches "
                + "are still published for fifteen seconds, because a stale menu is fine and a stale "
                + "table count is the one thing here that can waste somebody's evening. A suspended "
                + "venue leaves the list within that window.\n\n"
                + "**Rate limited on budgets of its own**, per caller and city-wide, not the page "
                + "budget: this is the diner app's Explore screen, and a phone network puts thousands "
                + "of phones behind one address.")
            .Produces<IReadOnlyList<PublicVenueCard>>()

            // Replaces the group's page policy on this one route: an endpoint carries one policy,
            // and its own wins. See RateLimitingExtensions.PublicBrowsePolicy.
            .RequireRateLimiting(RateLimitingExtensions.PublicBrowsePolicy);

        group.MapGet("/branches/{venueSlug}/{branchSlug}", GetBranchAsync)
            .WithName("getPublicBranch")
            .WithSummary("One branch's public page, by its slug pair")
            .WithDescription(
                "Address, coordinates, opening hours, whether it is open **now** in its own time "
                + "zone, the live free-table count, and the floor plan in diner shape.\n\n"
                + "`venueBranchCount`: how many of the venue's branches `GET /api/public/venues` lists - the "
                + "ones switched on, this one included; a branch switched off is neither listed nor counted "
                + "- read live with the booking switch, for the page's \"All N branches\" link.\n\n"
                + "The floor plan here is not the editor's: it carries no QR tokens and no table "
                + "status beyond whether somebody is sitting there. A QR token is the credential "
                + "that opens a tab.\n\n"
                + "A suspended venue, an inactive branch and a wrong slug pairing all answer **404**, "
                + "identically and on purpose - a public page that distinguished them would be "
                + "publishing a customer's billing status to anybody who guessed a slug.")
            .Produces<PublicBranchPage>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No branch is published at that pairing.");

        group.MapGet("/branches/{branchId:guid}/menu", GetMenuAsync)
            .WithName("getPublicMenu")
            .WithSummary("The branch's menu, complete items only")
            .WithDescription(
                "Exactly what the diner app receives, from the same read model - not a second "
                + "implementation.\n\n"
                + "**An item without a photo or without allergens never appears here**, for the same "
                + "reason it never reaches the app: somebody reading an empty allergen list "
                + "reasonably concludes there are none. Sold-out items *are* shown, flagged.")
            .Produces<BranchMenuView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.");

        group.MapGet("/branches/{branchId:guid}/availability", GetAvailabilityAsync)
            .WithName("getPublicAvailability")
            .WithSummary("Which tables are free for a slot")
            .WithDescription(
                "The existing availability read model, unchanged and reused rather than "
                + "reimplemented. It was already anonymous; what this route adds is the public 404 "
                + "rule and the tighter rate limit.\n\n"
                + ReservationEndpoints.ExcludeReservationDescription)
            .Produces<BranchAvailability>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.");

        group.MapGet("/branches/{branchId:guid}/availability/slots", GetDaySlotsAsync)
            .WithName("getPublicDaySlots")
            .WithSummary("Which start times on one day have a table for this party")
            .WithDescription(
                "The app's day-slots read, reused rather than reimplemented, behind the public 404 rule "
                + "and the tighter rate limit. `partySize` is required here, as on the public "
                + "single-slot read.\n\n"
                + ReservationEndpoints.DaySlotsDescription)
            .Produces<BranchDaySlots>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`date` or `partySize` is missing or malformed.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.");

        group.MapGet("/bookings/{token}", GetBookingAsync)
            .WithName("getPublicBooking")
            .WithSummary("One booking, by the manage link that was mailed with it")
            .WithDescription(
                "What the confirmation screen renders, for somebody holding nothing but the link: "
                + "venue, branch, address, table and the room it is in (`roomName`, absent for a table "
                + "in no room), local date and time, party size, status, the code quoted at the door, "
                + "and the instant past which cancelling counts as late. A waiting booking carries "
                + "`approvalExpiresAtUtc` to count down to and `awaitsEarlierGuestAnswer`: true while the "
                + "guest with the table before it is asked in the app whether they will have left in time "
                + "(only they answer), false when the venue decides it.\n\n"
                + "For the page around it: `venueSlug` and `branchSlug`, the branch page's public address "
                + "(`/{venueSlug}/{branchSlug}`) - the link a reminder carries is the web's token-only "
                + "`/booking/{token}`, which names no branch - and, for changing the booking, "
                + "`reservationId` (send it as `excludeReservationId` on the public availability and "
                + "day-slot reads, so the table already held reads as free) and `tableId`, the booked "
                + "table.\n\n"
                + "**Nothing else.** No diner id, no phone number, no other bookings, no floor "
                + "state. The token is the whole credential and it will be pasted into WhatsApp, "
                + "left in browser history and read by whoever picks the phone up, so this response "
                + "is a hand-picked subset rather than a trimmed reservation.\n\n"
                + "**A cancelled, missed or finished booking answers 200 with its state**, not 404. "
                + "Somebody opening a three-week-old link should learn what happened to their table.\n\n"
                + "**An unknown token, an expired one and a booking that no longer exists answer "
                + "identically** - same status, same code, same sentence. The link must not be "
                + "usable to find out which tokens are real. The link stops working "
                + "`ManageTokenGraceDays` after the booking ends.")
            .Produces<PublicBookingView>()
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound,
                "The link is not valid. Deliberately indistinguishable from an expired one.");

        group.MapPost("/bookings/{token}/cancel", CancelBookingAsync)
            .WithName("cancelPublicBooking")
            .WithSummary("Cancel a booking from its manage link")
            .WithDescription(
                "The reason a web booking is not a no-show generator. A diner who booked from this "
                + "page has no app, so no push reminder and no one-tap cancel reach them - this "
                + "link is the only way cancelling is ever easier for them than simply not turning "
                + "up, which is the premise the whole reservation product rests on.\n\n"
                + "**Free before the branch's deadline, allowed after it and recorded as late, "
                + "never blocked.** A late cancellation is far better than a no-show: the venue at "
                + "least knows. `cancellationDeadlineUtc` on the read above is that deadline.\n\n"
                + "The same cancellation the app performs, not a second one - the booking's "
                + "reminder is cancelled in the same transaction either way. One addition on the link: the "
                + "booking's account is told it was cancelled from there - a push and a feed entry "
                + "(`reservation.cancelled-by-link`, kind `booking-cancelled-by-link`) - because whoever "
                + "holds the link may not be the diner.\n\n"
                + "**Cancelling an already-cancelled or finished booking is not an error**: the "
                + "booking comes back as it stands, untouched, and nobody is told again.")
            .Produces<PublicBookingView>()
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound,
                "The link is not valid. Deliberately indistinguishable from an expired one.");

        group.MapPost("/bookings/{token}/reschedule", RescheduleBookingAsync)
            .WithName("reschedulePublicBooking")
            .WithSummary("Change a booking's date, time, party size or table from its manage link")
            .WithDescription(
                "The app's change (`rescheduleReservation`) for somebody holding nothing but the link - "
                + "the **same move, by the same code**: only how the caller proved they may differs. Send "
                + "the whole new slot - `date`, `time`, `partySize`, `diningTableId` (required: the "
                + "current table, `tableId` on the read above, or another at the branch) and a "
                + "`clientCommandId` - exactly as the app does. Answers `200` with the booking as the read "
                + "above shows it: the **same `code`** and link, at the new `localDate`/`localStartTime`, "
                + "`partySize`, `tableLabel`/`roomName` and `status`.\n\n"
                + "**The status is decided again, as for the app.** A `Confirmed` booking that keeps its "
                + "table and start and does not grow stays `Confirmed`. A confirmed party that does not "
                + "grow keeps its confirmation against the party-size threshold. Anything else is judged as "
                + "a new booking: a move into the short-notice window (today, or under six hours away) "
                + "waits for the floor with a fifteen-minute `approvalExpiresAtUtc` - and if that change "
                + "is declined or unanswered the booking goes back to the slot it had, `Confirmed`, when "
                + "it is still free; a party that grows past the threshold, a branch that approves every "
                + "booking, or a no-show history waits for the venue (`PendingApproval`); a table whose "
                + "guest before must agree first waits on them. A booking already waiting keeps its "
                + "deadline through a move.\n\n"
                + "**Only a booking still to come**: `PendingApproval` or `Confirmed` and not yet started; "
                + "anything else - cancelled, declined, seated, finished, a no-show, past its start - is "
                + "`409 reservation-not-changeable`. Judged as a new booking would be, with the same "
                + "`reservation-*` 422s: lead time, booking window, opening hours and special days, the "
                + "table pinned on its room and fitting the party. The booking being moved never stands "
                + "in its own way; read the floor with `excludeReservationId` = `reservationId` on the "
                + "public availability and day-slot reads to see it that way.\n\n"
                + "**The phone gate is the booking's account's**: its reminders and no-show record land on "
                + "that account, so it must still be live with a proved number - `403 phone-not-verified` "
                + "otherwise (deactivated, deleted, or found not live by the move's own lock), with a message "
                + "a link holder can act on: call the venue, or cancel and book again. Reading and cancelling "
                + "stay open.\n\n"
                + "**One diner, one table at a time** at the branch: a move onto a sitting that overlaps "
                + "another of the booking's diner's live bookings there is `409 reservation-overlaps-own` - "
                + "with **no `context`** and a message that names no time, table or code. It does not hide "
                + "that such a booking exists: this check runs before the table's own clash check, so the "
                + "code itself says the diner holds another table around then.\n\n"
                + "**The booking's account is told** - a push and a feed entry, in the same save "
                + "(`reservation.changed-by-link`, kind `booking-changed-by-link`): the link goes round group "
                + "chats, and whoever moved it may not be the diner. A retry tells nobody twice.\n\n"
                + "The reminder and late nudge are replaced with ones for the new time, carrying the same "
                + "manage link. Idempotent on `clientCommandId`, shared with the app's change: a retry of "
                + "the same change answers `200` with the booking **as it stands now**, moving nothing; the "
                + "same id on another command, another booking or a different change (another table, "
                + "start or party size) is `409 client-command-id-in-use` - mint a fresh id per change.\n\n"
                + "**An unknown, expired or gone link answers exactly as the read does** - the same 404, "
                + "code and sentence. Rate limited per link with the read and the cancel.")
            .Produces<PublicBookingView>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`diningTableId` names a table at another branch.")
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "`phone-not-verified`: the booking's account cannot take a move - deactivated, deleted, "
                + "without a proved number, or found not live mid-move. The message is for the link holder "
                + "(\"This booking can no longer be changed online. Call the venue, or cancel it and book "
                + "again.\"); never a 401, since there is no session. The booking can still be read and "
                + "cancelled from the link.")
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound,
                "The link is not valid - deliberately indistinguishable from an expired one, as on the read. "
                + "Also `not-found` for a `diningTableId` that is no active table: read the link again to "
                + "tell the two apart.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "`reservation-not-changeable`: the booking is finished with or has started. "
                + "`reservation-approval-expired`: a short-notice request nobody answered in time. "
                + "`table-already-booked` / `table-currently-occupied`: the new slot is taken - "
                + ReservationEndpoints.ConflictDescription
                + " `reservation-overlaps-own`: another of the diner's bookings at the branch runs then "
                + "(no context). `concurrent-update`: the booking was cancelled, approved or seated while "
                + "this was in flight; read it again. `bookings-not-accepted`, `web-bookings-not-accepted`, "
                + "`branch-unavailable`: the branch stopped taking bookings. `client-command-id-in-use`: "
                + "the id belongs to another command or to a different change.")
            .ProducesProblemDetails(
                StatusCodes.Status422UnprocessableEntity,
                ReservationEndpoints.RejectedDescription
                + " `validation-failed` names a missing or malformed field; `reservation-free-by-required` "
                + "asks for `acceptsFreeByUtc`.")
            .ProducesProblem<LockTimeoutProblem>(
                StatusCodes.Status503ServiceUnavailable, ReservationEndpoints.LockTimeoutDescription)

            // The one write here that carries an idempotency key, refused without one exactly as on
            // the app's routes.
            .AddEndpointFilter<ClientCommandIdFilter>();

        group.MapGet("/branches/{branchId:guid}/meta", GetMetaAsync)
            .WithName("getPublicBranchMeta")
            .WithSummary("Open Graph and Twitter card fields for this branch")
            .WithDescription(
                "What a link should look like when it is pasted into WhatsApp or Telegram. Served "
                + "here so the web page and any future renderer do not each invent their own.\n\n"
                + "**The free-table count is deliberately absent.** A card is fetched once by "
                + "whichever chat app saw the link and cached for hours, so a live number would be "
                + "frozen at whatever it was when somebody first pasted it - and \"4 tables free\" "
                + "three hours stale is worse than no number. The venue description does not move.")
            .Produces<PublicBranchMeta>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.");

        MapListingRoutes(group);

        return app;
    }

    /// <summary>The diner app's browse reads: Explore, search, the map, details, reviews and rooms.</summary>
    private static void MapListingRoutes(RouteGroupBuilder group)
    {
        const string listingFields =
            "Each entry is a `PublicBranchListing`: ids and slugs, `venueName`, `branchName`, `venueType` "
            + "(1 Cafe, 2 Restaurant, 3 Bar), `cuisine` and `priceLevel` (absent until the venue sets them), "
            + "`address`, `latitude`, `longitude`, `timeZoneId`, `isOpenNow`, `freeTableCount`, `rating` "
            + "(average to one decimal, **absent with no reviews**), `reviewCount`, `badges` and "
            + "`coverPhoto`, and `district` - the part of town, e.g. `Kentron`, absent until the venue "
            + "sets it; search's `q` matches it as well as the names, cuisine and address.\n\n"
            + "`badges` is derived, never stored: `new` for a branch created in the last 30 days; "
            + "`popular` for one that seated 20 or more parties in the last 30 days, or has 5 or more "
            + "reviews averaging 4.5 or better.\n\n"
            + "Send `lat` and `lng` together to get `distanceKm` on every entry and nearest-first order; "
            + "without them the order is best rated first. Cached for fifteen seconds.";

        group.MapGet("/branches", SearchBranchesAsync)
            .WithName("getPublicBranches")
            .WithSummary("Every published branch, for Explore and the map")
            .WithDescription(listingFields)
            .Produces<IReadOnlyList<PublicBranchListing>>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`lat` without `lng`, or either out of range.")
            .RequireRateLimiting(RateLimitingExtensions.PublicBrowsePolicy);

        group.MapGet("/branches/search", SearchBranchesAsync)
            .WithName("searchPublicBranches")
            .WithSummary("Search published branches by name, cuisine, address or district")
            .WithDescription(
                "`q` matches venue name, branch name, cuisine and address, contains and case-insensitive; "
                + "blank matches everything. `category` narrows to one venue type (1 Cafe, 2 Restaurant, 3 Bar). "
                + "`q` is at most 100 characters.\n\n" + listingFields)
            .Produces<IReadOnlyList<PublicBranchListing>>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`q` too long, or a half or out-of-range position.")
            .RequireRateLimiting(RateLimitingExtensions.PublicBrowsePolicy);

        group.MapGet("/branches/{branchId:guid}", GetBranchDetailAsync)
            .WithName("getPublicBranchDetail")
            .WithSummary("One branch's details screen, by id")
            .WithDescription(
                "`listing` is the card exactly as the list serves it. Beside it: `about`, `websiteUrl`, "
                + "`phoneE164`, `amenities`, `openingHours` (`day` 0 = Sunday, wall-clock `opensAt`/"
                + "`closesAt` as `HH:mm:ss`, `closesNextDay`), `gallery` (pictures beyond the cover, in "
                + "order), `tableCount`, `acceptsWebBookings`, `recentReviews` (newest three), "
                + "`ratingCounts` (shown reviews per star, **one star first**: always five numbers, adding up "
                + "to `listing.reviewCount`) and "
                + "`rooms` - the rooms with their photos and the live pins on them, exactly as "
                + "`GET /api/public/branches/{branchId}/rooms` serves them. `takeaway` (`enabled`, `prepMinutes`) says "
                + "whether to show \"Order to take away\"; whether it is open for takeaway now is "
                + "`GET /api/diner/branches/{branchId}/takeaway`.\n\n"
                + "A branch that is inactive or whose venue is suspended or deleted is **404**, read live. "
                + "`listing.rating` and `listing.reviewCount` are read live too; the other live numbers "
                + "share the list's fifteen seconds.")
            .Produces<PublicBranchDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.")

            // The app's per-tap routes: their own per-caller budget, not the page budget. See
            // RateLimitingExtensions.PublicPlacePolicy.
            .RequireRateLimiting(RateLimitingExtensions.PublicPlacePolicy);

        group.MapGet("/branches/{branchId:guid}/reviews", GetReviewsAsync)
            .WithName("getPublicBranchReviews")
            .WithSummary("A page of a branch's reviews, newest first")
            .WithDescription(
                "Twenty a page from `page=1`. `rating`, `reviewCount` and `ratingCounts` are read live, "
                + "over the shown reviews only (one taken down by moderation counts nowhere). "
                + "`ratingCounts` is the whole branch's star distribution, the same on every page: five "
                + "numbers, **one star first** (`[1★, 2★, 3★, 4★, 5★]`), zeros included, adding up to "
                + "`reviewCount`. Each review carries `authorName` as a first name and last initial, never "
                + "the account or its id.")
            .Produces<PublicReviewPage>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`page` below 1, or past the last page that can exist.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.")
            .RequireRateLimiting(RateLimitingExtensions.PublicPlacePolicy);

        group.MapGet("/branches/{branchId:guid}/rooms", GetRoomsAsync)
            .WithName("getPublicBranchRooms")
            .WithSummary("The rooms a diner picks from: photos, and the live pins on them")
            .WithDescription(
                "`rooms` lowest `displayOrder` first; each room's `photos` in the order the venue chose; on "
                + "each photo the active tables pinned there, by label. A room with no photo is not listed, "
                + "and a table with no pin is not on any photo - a diner books by tapping a pin, so an "
                + "unpinned table is not offered.\n\n"
                + "`photoX` and `photoY` are 0-1 across and down that photo. `state` is what the floor derives "
                + "now: 1 Free, 2 ReservedSoon, 3 Held, 4 Occupied, 5 OutOfService. `isBookable` false is a "
                + "walk-in-only table, shown but not booked. Refetch this more often than the details.")
            .Produces<PublicBranchRooms>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.")
            .RequireRateLimiting(RateLimitingExtensions.PublicPlacePolicy);
    }

    private static async Task<IResult> SearchBranchesAsync(
        IPublicListingQuery listings,
        CancellationToken ct,
        string? q = null,
        VenueType? category = null,
        double? lat = null,
        double? lng = null) =>
        Results.Ok(await listings.SearchAsync(new BranchSearchRequest(q, category, lat, lng), ct));

    private static async Task<IResult> GetBranchDetailAsync(
        Guid branchId,
        IPublicListingQuery listings,
        CancellationToken ct,
        double? lat = null,
        double? lng = null) =>
        Results.Ok(await listings.GetDetailAsync(branchId, lat, lng, ct));

    private static async Task<IResult> GetReviewsAsync(
        Guid branchId, IPublicListingQuery listings, CancellationToken ct, int page = 1) =>
        Results.Ok(await listings.GetReviewsAsync(branchId, page, ct));

    private static async Task<IResult> GetRoomsAsync(
        Guid branchId, IPublicListingQuery listings, CancellationToken ct) =>
        Results.Ok(await listings.GetRoomsAsync(branchId, ct));

    private static async Task<IResult> GetVenuesAsync(IPublicVenueQuery venues, CancellationToken ct) =>
        Results.Ok(await venues.GetVenuesAsync(ct));

    private static async Task<IResult> GetBranchAsync(
        string venueSlug, string branchSlug, IPublicVenueQuery venues, CancellationToken ct) =>
        Results.Ok(await venues.GetBranchAsync(venueSlug, branchSlug, ct));

    private static async Task<IResult> GetMenuAsync(
        Guid branchId, IPublicVenueQuery venues, CancellationToken ct) =>
        Results.Ok(await venues.GetMenuAsync(branchId, ct));

    private static async Task<IResult> GetBookingAsync(
        string token, IPublicBookingService bookings, CancellationToken ct) =>
        Results.Ok(await bookings.GetAsync(token, ct));

    /// <summary>
    /// Cancels a booking from its manage link.
    /// </summary>
    /// <remarks>
    /// The reason is optional and comes from the body, which may be absent entirely - a diner
    /// tapping Cancel on a page has nothing to say and should not have to send an empty object.
    /// </remarks>
    private static async Task<IResult> CancelBookingAsync(
        string token,
        IPublicBookingService bookings,
        CancellationToken ct,
        CancelBookingRequest? request = null) =>
        Results.Ok(await bookings.CancelAsync(token, request?.Reason, ct));

    /// <summary>
    /// Moves a booking from its manage link.
    /// </summary>
    /// <remarks>
    /// The app's body, unchanged, so the page and the app send one shape. The validation filter has
    /// already refused a null date, time or table by the time this runs.
    /// </remarks>
    private static async Task<IResult> RescheduleBookingAsync(
        string token,
        RescheduleReservationRequest request,
        IPublicBookingService bookings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await bookings.RescheduleAsync(
            token,
            new RescheduleByManageTokenCommand(
                request.DiningTableId!.Value,
                request.Date!.Value,
                request.Time!.Value,
                request.PartySize,
                request.ClientCommandId,
                request.AcceptsFreeByUtc),
            ct));
    }

    private static async Task<IResult> GetMetaAsync(
        Guid branchId, IPublicVenueQuery venues, CancellationToken ct) =>
        Results.Ok(await venues.GetMetaAsync(branchId, ct));

    /// <summary>
    /// Availability, through the existing read model.
    /// </summary>
    /// <remarks>
    /// The public gate is applied first and the query is then the same one the app calls. Growing a
    /// second availability implementation here would be the beginning of two answers to "is table 7
    /// free", and the two would eventually disagree in front of somebody standing at the door.
    /// </remarks>
    private static async Task<IResult> GetAvailabilityAsync(
        Guid branchId,
        int partySize,
        IPublicVenueQuery venues,
        IAvailabilityQuery availability,
        CancellationToken ct,
        DateOnly? date = null,
        TimeOnly? time = null,
        Guid? excludeReservationId = null)
    {
        await venues.RequirePublicBranchAsync(branchId, ct);

        var result = await availability.GetAvailabilityAsync(
            new AvailabilityRequest(branchId, partySize, date, time, excludeReservationId), ct);

        return result is null
            ? Results.NotFound()
            : Results.Ok(result);
    }

    /// <summary>
    /// The day's slots, through the same read model as the app's - the public gate first, exactly as
    /// for one slot.
    /// </summary>
    private static async Task<IResult> GetDaySlotsAsync(
        Guid branchId,
        DateOnly date,
        int partySize,
        IPublicVenueQuery venues,
        IAvailabilityQuery availability,
        CancellationToken ct,
        Guid? excludeReservationId = null)
    {
        await venues.RequirePublicBranchAsync(branchId, ct);

        var result = await availability.GetDaySlotsAsync(
            new DaySlotsRequest(branchId, date, partySize, excludeReservationId), ct);

        return result is null
            ? Results.NotFound()
            : Results.Ok(result);
    }
}

/// <summary>Why a booking was cancelled, if the diner said. Optional in every sense.</summary>
/// <param name="Reason">
/// Free text, stored on the booking. Null when the body is absent or the field is omitted.
/// </param>
public sealed record CancelBookingRequest(string? Reason = null);
