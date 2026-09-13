using Microsoft.AspNetCore.RateLimiting;
using Yalla.Api.ApplicationExtensions;
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
/// <b>Booking reuses the existing flow</b> - request a code, verify, create a reservation - with no
/// endpoint of its own. It already works for an anonymous caller who verifies a phone. See
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
                + "rule and the tighter rate limit.")
            .Produces<BranchAvailability>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.");

        group.MapGet("/bookings/{token}", GetBookingAsync)
            .WithName("getPublicBooking")
            .WithSummary("One booking, by the manage link that was mailed with it")
            .WithDescription(
                "What the confirmation screen renders, for somebody holding nothing but the link: "
                + "venue, branch, address, table, local date and time, party size, status, the code "
                + "quoted at the door, and the instant past which cancelling counts as late.\n\n"
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
                + "reminder is cancelled in the same transaction either way.\n\n"
                + "**Cancelling an already-cancelled or finished booking is not an error**: the "
                + "booking comes back as it stands, untouched.")
            .Produces<PublicBookingView>()
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound,
                "The link is not valid. Deliberately indistinguishable from an expired one.");

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

    /// <summary>The diner app's browse reads: Explore, search, the map, details, reviews and table markers.</summary>
    private static void MapListingRoutes(RouteGroupBuilder group)
    {
        const string listingFields =
            "Each entry is a `PublicBranchListing`: ids and slugs, `venueName`, `branchName`, `venueType` "
            + "(1 Cafe, 2 Restaurant), `cuisine` and `priceLevel` (absent until the venue sets them), "
            + "`address`, `latitude`, `longitude`, `timeZoneId`, `isOpenNow`, `freeTableCount`, `rating` "
            + "(average to one decimal, **absent with no reviews**), `reviewCount`, `badges` and "
            + "`coverPhoto`.\n\n"
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
            .WithSummary("Search published branches by name, cuisine or address")
            .WithDescription(
                "`q` matches venue name, branch name, cuisine and address, contains and case-insensitive; "
                + "blank matches everything. `category` narrows to one venue type (1 Cafe, 2 Restaurant). "
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
                + "order), `tableCount`, `acceptsWebBookings`, `recentReviews` (newest three) and "
                + "`tableMarkers` - the tables placed on the cover photo with their live state.\n\n"
                + "A branch that is inactive or whose venue is suspended or deleted is **404**, read live.")
            .Produces<PublicBranchDetail>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.");

        group.MapGet("/branches/{branchId:guid}/reviews", GetReviewsAsync)
            .WithName("getPublicBranchReviews")
            .WithSummary("A page of a branch's reviews, newest first")
            .WithDescription(
                "Twenty a page from `page=1`. `rating` and `reviewCount` are read live. Each review "
                + "carries `authorName` as a first name and last initial, never the account or its id.")
            .Produces<PublicReviewPage>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`page` below 1.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.");

        group.MapGet("/branches/{branchId:guid}/table-markers", GetTableMarkersAsync)
            .WithName("getPublicTableMarkers")
            .WithSummary("Tables drawn on the cover photo, with their live state")
            .WithDescription(
                "Only tables a manager placed on the photo (`photoX`/`photoY` on the floor plan) appear. "
                + "`photoX` and `photoY` are 0-1 across and down `photo`, the branch's cover. `state` is "
                + "what the floor plan derives now: 1 Free, 2 ReservedSoon, 3 Held, 4 Occupied, "
                + "5 OutOfService. Refetch this more often than the details.")
            .Produces<PublicTableMarkers>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch, or it is not published.");
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

    private static async Task<IResult> GetTableMarkersAsync(
        Guid branchId, IPublicListingQuery listings, CancellationToken ct) =>
        Results.Ok(await listings.GetTableMarkersAsync(branchId, ct));

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
        TimeOnly? time = null)
    {
        await venues.RequirePublicBranchAsync(branchId, ct);

        var result = await availability.GetAvailabilityAsync(
            new AvailabilityRequest(branchId, partySize, date, time), ct);

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
