using Yalla.Api.Errors;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The availability and booking endpoints.
/// </summary>
/// <remarks>
/// <para>
/// As thin as the table endpoints, and for the same reason: bind, call the service, return. Every
/// rule - the overlap check, the branch policy, who may cancel what - lives in the service and the
/// domain, because three clients call these and a rule enforced here is a rule enforced nowhere
/// else.
/// </para>
/// <para>
/// Identity comes from the policies, not from the handlers. Booking, cancelling and the diner's own
/// list carry <see cref="YallaPolicies.VerifiedDiner"/>; approving and rejecting carry
/// <see cref="YallaPolicies.WaiterOrAbove"/>, because short-notice bookings are the floor's to decide.
/// Availability is the only anonymous endpoint outside the sign-in flows: somebody deciding whether to
/// eat here must be able to see the room before they are asked who they are.
/// </para>
/// <para>
/// Two boundaries the policies cannot draw are drawn in the service instead, deliberately.
/// <b>Ownership</b> - a diner may only read and cancel their own bookings - is a fact about a row,
/// not about a token. And <b>branch scope</b> on approve and reject cannot use
/// <see cref="YallaPolicies.BranchScoped"/>, because that policy compares a claim against a
/// <c>branchId</c> route value and these routes are addressed by reservation id; it fails closed
/// when it cannot find one, which is the right behaviour and the reason not to apply it here. The
/// service resolves the booking's branch and checks it against the acting staff member's own venue
/// and branch.
/// </para>
/// <para>
/// Exceptions are not caught here either. <c>UnifiedExceptionHandler</c> maps them centrally, so a
/// lost race becomes 409 with the clashing window and a fresh floor, each branch rule becomes 422
/// with its own code, and a lock timeout becomes a retryable 503 - the same way no matter which
/// endpoint raised it.
/// </para>
/// </remarks>
public static class ReservationEndpoints
{
    private const string ConflictDescription =
        "Someone else took that table between the diner seeing it free and confirming. The body's "
        + "`context` carries the clashing window and a fresh availability snapshot, so the app can "
        + "redraw the floor and show what changed rather than only saying no. Never retried "
        + "automatically: this answer will not change on a repeat.";

    private const string RejectedDescription =
        "A branch rule refused the booking. Each rule has its own `code` - "
        + "`reservation-party-exceeds-capacity`, `reservation-outside-opening-hours` and so on - "
        + "with the numbers behind it in `context`, so the app can say which table to pick instead "
        + "rather than showing a generic failure.";

    private const string LockTimeoutDescription =
        "The table's booking lock could not be had in time. `context.retryable` is true: unlike the "
        + "409, this one is worth retrying - with the same `clientCommandId`, so a retry that "
        + "crosses with a late-committing original is recognised as a replay.";

    private const string OverlapsOwnDescription =
        "**One table at a time per diner at a branch**: a sitting that overlaps another of the diner's "
        + "own live bookings there (pending, confirmed or seated) is `409 reservation-overlaps-own`, with "
        + "`context` naming the booking already held - `reservationId`, `code` (its door code), "
        + "`localDate` (`\"2026-10-22\"`, the branch's calendar - say the day when it differs), "
        + "`localStartTime` (`\"19:30:00\"`) and `tableLabel` - so the app can say \"You already have a "
        + "booking at 19:30 (table 2)\". Sittings that only touch do not overlap; another branch is not "
        + "refused. A retry with the same `clientCommandId` still answers as a replay.";

    private const string StateDescription =
        "The booking is not in a state that permits this - cancelling one that is already seated, "
        + "or approving one that was never pending.";

    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        MapAvailability(app);
        MapDinerBookings(app);
        MapStaffDecisions(app);
        MapBranchBookings(app);

        return app;
    }

    private static void MapBranchBookings(IEndpointRouteBuilder app)
    {
        // Addressed by branch, so BranchScoped applies - in the house style of the branch settings
        // group. It widens a home-branch manager to the whole venue; the service narrows them back
        // with the stored-row branch check, so a manager lists only bookings they can decide.
        var group = app.MapGroup("/api/branches/{branchId:guid}")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapGet("/reservations", ListBranchReservationsAsync)
            .WithName("listBranchReservations")
            .WithSummary("A branch's bookings in one status - the approval queue")
            .WithDescription(
                "What the console's *awaiting approval* panel reads before calling `approve` or "
                + "`reject`. `status` is required (`1` PendingApproval).\n\n"
                + "Sorted by the branch's local date, then local start time. **No paging: at most "
                + "200 rows**, the earliest first, so what a cap would cut is what is furthest away.\n\n"
                + "Manager or above: a branch manager lists their own branch only; an owner or general "
                + "manager, any branch of their venue; a platform admin, any branch. (Approving and "
                + "rejecting are open to the branch's waiters too; the floor's own list is "
                + "`/reservations/awaiting`.) `awaitingApprovalBecause` is the reason stored when the "
                + "booking went to approval - worked out against the branch's current policy only for "
                + "a booking from before that was stored - and `approvalExpiresAtUtc` the deadline to count "
                + "down to: fifteen minutes at short notice, two hours for a booking waiting on the guest "
                + "before it (`awaitingApprovalBecause` 5). `awaitsEarlierGuestAnswer` true means that guest "
                + "answers in the app and only they can - approve and reject are `409 "
                + "reservation-awaiting-earlier-guest` - so show the countdown, not the buttons; false, the "
                + "floor decides it after calling them.")
            .Produces<IReadOnlyList<ReservationView>>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Another venue's staff, a waiter, or a manager of a different branch of this venue.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(
                StatusCodes.Status422UnprocessableEntity, "`status` is missing or is not a booking status.");

        // The floor's read: waiter or above, because the people deciding short-notice bookings are
        // the ones on the floor. BranchScoped first, from the token; the service checks the stored
        // staff row again, as it does for approve and reject.
        var floor = app.MapGroup("/api/branches/{branchId:guid}")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        floor.MapGet("/reservations/awaiting", ListAwaitingApprovalAsync)
            .WithName("listAwaitingApproval")
            .WithSummary("The bookings waiting for the floor's yes or no, with each table's evening")
            .WithDescription(
                "What the staff tablet's *awaiting approval* panel polls (every ten seconds or so) before "
                + "calling `approve` or `reject`. The branch's bookings in `PendingApproval` that can still "
                + "be answered - not past `approvalExpiresAtUtc`, not started - **soonest deadline first**, "
                + "then those with no deadline by start. At most 200; no paging.\n\n"
                + "Each item: `reservation` (the `ReservationView` - code, start, party, table, room, guest "
                + "name, and `guestPhone` for calling), `approvalExpiresAtUtc` (count down to it - fifteen "
                + "minutes at short notice, two hours for a booking waiting on the guest before it, whom the "
                + "floor calls; null for "
                + "a booking that waits as long as it takes), `trigger` (the `ApprovalTrigger`, 4 "
                + "ShortNotice), and the table's context that day: `previousBooking` (`reservationId`, "
                + "`startUtc`, `localStartTime`, `partySize`, `guestName`, `guestPhone`, `status` - the "
                + "booking before it, cancelled ones skipped), `nextBooking` (`reservationId`, `startUtc`, "
                + "`localStartTime`, `partySize`) and `currentSitting` (`seatedAtUtc`, `partySize` - absent "
                + "when a diner's scan opened the table and only guessed). Each is absent when there is "
                + "none, like every null in this API.\n\n"
                + "One query however many are waiting. A waiter or above at this branch; an owner or "
                + "general manager at any branch of their venue. A platform admin reads the list with "
                + "every guest number blank (`guestPhone` empty, `previousBooking.guestPhone` absent).")
            .Produces<IReadOnlyList<AwaitingApproval>>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not a waiter or above at this branch - a cook, a diner, another branch's staff.");
    }

    private static async Task<IResult> ListAwaitingApprovalAsync(
        Guid branchId,
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.ListAwaitingApprovalAsync(branchId, cancellationToken));

    private static async Task<IResult> ListBranchReservationsAsync(
        Guid branchId,
        [AsParameters] ListBranchReservationsQuery query,
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.ListForBranchAsync(

            // The validation filter has refused a null before this runs.
            branchId, query.Status!.Value, cancellationToken));

    private static void MapAvailability(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/branches/{branchId:guid}/availability", GetAvailabilityAsync)
            .WithTags(EndpointConventions.DinerTag)
            .WithName("getBranchAvailability")

            // Anonymous, alone among the endpoints that are not sign-in flows. Browsing needs no
            // account: somebody deciding whether to eat here has to see the room before they are
            // asked who they are.
            .AllowAnonymous()
            .WithSummary("Which tables a branch can offer for one slot")
            .WithDescription(
                "Every table in the branch pinned on a room photo - `photoId`, `photoX`, `photoY` - with its "
                + "derived state at the "
                + "requested time, whether it can be booked for this party - and if not, the one "
                + "specific reason - and the window the diner may have. Assembled in a single "
                + "query. Returns no guest names and no money. Omit date and time for 'now, at "
                + "the branch'.\n\n"
                + "Each table also says what booking it takes: `isRequestable` when the guest with it before "
                + "has to agree to leave in time first (the booking waits - `requiresApproval`), and "
                + "`mustFreeByUtc`/`mustFreeByLocal` when a full sitting would run into its next booking - "
                + "send that instant back as `acceptsFreeByUtc` once the diner agrees. The rules are the "
                + "day slots' - see `getBranchDaySlots`.\n\n"
                + ExcludeReservationDescription)
            .Produces<BranchAvailability>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(StatusCodes.Status429TooManyRequests, AvailabilityRateLimitedDescription)

            // Anonymous and the hottest endpoint in the product, so it is the one that most needs
            // a ceiling. Per address, because there is no account to partition by.
            .RequireRateLimiting(RateLimitingExtensions.AvailabilityPolicy);

        app.MapGet("/api/branches/{branchId:guid}/availability/slots", GetDaySlotsAsync)
            .WithTags(EndpointConventions.DinerTag)
            .WithName("getBranchDaySlots")

            // Anonymous for the same reason as the single slot: the time picker comes before the
            // diner is asked who they are.
            .AllowAnonymous()
            .WithSummary("Which start times on one day have a table for this party")
            .WithDescription(DaySlotsDescription)
            .Produces<BranchDaySlots>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`date` is missing or is not a date.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblemDetails(StatusCodes.Status429TooManyRequests, AvailabilityRateLimitedDescription)

            // The same ceiling as the single slot, which it sits beside on the same screen.
            .RequireRateLimiting(RateLimitingExtensions.AvailabilityPolicy);
    }

    /// <summary>The day-slots read, word for word on the app route and its public twin.</summary>
    internal const string DaySlotsDescription =
        "Every quarter of an hour from 00:00 on `date` (`stepMinutes` is 15) whose whole sitting - the "
        + "branch's turn time - fits one opening block, earliest first. A time the branch is shut for is "
        + "**left out**, never struck. Each slot carries `time` (`HH:mm`, branch wall clock), `startUtc`, "
        + "`isAvailable`, `isRequestable`, `requiresApproval`, `freeTableCount`, `requestableTableCount`, "
        + "`mustFreeByUtc`/`mustFreeByLocal` and, when not available, `unavailableReason`.\n\n"
        + "**`isAvailable`** is true when at least one bookable table pinned on a photo of its room "
        + "seats the party within the seat-overhang limit and the table's evening allows it - free "
        + "outright (`freeTableCount`) or once the guest before agrees to leave in time "
        + "(`requestableTableCount`; `isRequestable` when that is all there is, and `requiresApproval` with "
        + "it). These are the single-slot read's own rules over the same rows, so a time listed here is "
        + "one `availability?time=` offers exactly those tables at, and a struck time one it offers none at.\n\n"
        + "**The table's evening** - fixed rules, the ones a booking is judged by under the table's lock: "
        + "refused from the previous booking's start until ninety minutes after it (`TableJustBooked` 11) "
        + "and within forty-five minutes before the next one (`NextBookingTooSoon` 12); from ninety minutes "
        + "to three hours after the previous start, or while that sitting plus clearing time still runs, "
        + "the table can only be asked for - its guest is asked to have left by the start less the clearing "
        + "time - and while one such question to a guest is open nobody else can ask "
        + "(`EarlierGuestBeingAsked` 13). A sitting that would run into the next booking ends at that "
        + "booking's start less the clearing time; `mustFreeByUtc` is the latest such time when no table "
        + "on offer gives a full sitting.\n\n"
        + "**Why a time is struck**: `LeadTimeTooShort` (1) or `OutsideBookingWindow` (2) when the "
        + "time itself is refused; otherwise, when tables that fit the party are taken, the commonest of "
        + "`TableAlreadyBooked` (8), `TableCurrentlyOccupied` (10), `TableJustBooked` (11), "
        + "`NextBookingTooSoon` (12) and `EarlierGuestBeingAsked` (13); else the commonest table reason, "
        + "e.g. `PartyExceedsCapacity` (4) when nothing seats them.\n\n"
        + "The top-level `unavailableReason` refuses the whole day: `OutsideOpeningHours` (3) with no "
        + "slots when the branch is shut all day, or the timing reason every listed time shares - a "
        + "past day, the rest of today inside the lead time, a date beyond the booking window. Null "
        + "when any time was judged on its tables.\n\n"
        + "One query, like the single slot. Returns no guest names and no money. `partySize` "
        + "defaults to 2.\n\n"
        + ExcludeReservationDescription;

    /// <summary>The <c>excludeReservationId</c> parameter, as both availability reads take it.</summary>
    internal const string ExcludeReservationDescription =
        "`excludeReservationId` leaves one booking out of the answer as though it had never been "
        + "made - the diner's own, while they change it, so the table they already hold reads as free "
        + "to them. Every other booking still counts, and an id that is not a booking at this branch "
        + "changes nothing. Anonymous like the rest of the read: booking ids are unguessable, and the "
        + "answer says no more about the one left out than that its table is free.";

    private const string AvailabilityRateLimitedDescription =
        "Too many availability queries from this address. Generous but finite - the endpoint "
        + "is anonymous and a phone will poll it.";

    /// <summary>
    /// The 403 on every diner route that lands on the account's phone number. Public so the tab
    /// route that opens from a booking says it in the same words.
    /// </summary>
    internal const string PhoneNotVerifiedDescription =
        "`phone-not-verified`: the account registered with a number it has never proved. Send the "
        + "diner through the code flow (`request-code`, `verify-code`) first; reading and cancelling "
        + "bookings stay open meanwhile.";

    private static void MapDinerBookings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reservations")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner);

        // One place enforcing the idempotency key, exactly as the table group does. GET carries no
        // body, so the filter finds nothing to check.
        group.AddEndpointFilter<ClientCommandIdFilter>();

        group.MapPost("/", CreateAsync)
            .WithName("createReservation")
            .WithSummary("Book a table")
            .WithDescription(
                "Idempotent on `clientCommandId`: a retry returns the original booking and creates "
                + "nothing, answering 200 rather than 201. Lands as `Confirmed`, or "
                + "`PendingApproval` (`awaitingApprovalBecause` says why) when the booking is at **short "
                + "notice** - for the branch's today, or starting under six hours from now - the branch "
                + "approves every booking, the party is over the branch's threshold, or the diner is over "
                + "the rolling no-show threshold.\n\n"
                + "A short-notice booking (`awaitingApprovalBecause` 4) is the floor's to decide, by "
                + "`approvalExpiresAtUtc` - fifteen minutes from now, or the start if sooner. Unanswered "
                + "by then it is declined (`CancelledByVenue`, `declinedBecause` 2 NoAnswer) and the diner "
                + "gets a `reservation-decided` push with `expired` true. Count down to "
                + "`approvalExpiresAtUtc`; read the booking again to see the answer.\n\n"
                + "A booking for a table whose previous booking started under three hours before "
                + "(`awaitingApprovalBecause` 5 EarlierGuestConsent) waits on that guest's yes, for **two "
                + "hours** at most (`approvalExpiresAtUtc`, never past their start nor this one's). When "
                + "`awaitsEarlierGuestAnswer` is true they are asked in the app and only they answer - "
                + "unanswered it is declined with `declinedBecause` 4 EarlierGuestNoAnswer. When it is false "
                + "the floor calls them and decides, by the same deadline - unanswered it is declined with "
                + "`declinedBecause` 2 NoAnswer.\n\n"
                + OverlapsOwnDescription)
            .Produces<ReservationView>(StatusCodes.Status201Created)
            .Produces<ReservationView>(StatusCodes.Status200OK)
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, PhoneNotVerifiedDescription)
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch or table.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "`table-already-booked` / `table-currently-occupied`: " + ConflictDescription
                + " `reservation-overlaps-own`: the diner already has a booking at this branch then - "
                + "`context.reservationId`, `code`, `localDate`, `localStartTime`, `tableLabel` name it.")
            .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity, RejectedDescription)
            .ProducesProblem<LockTimeoutProblem>(
                StatusCodes.Status503ServiceUnavailable, LockTimeoutDescription);

        group.MapPost("/{reservationId:guid}/reschedule", RescheduleAsync)
            .WithName("rescheduleReservation")
            .WithSummary("Change a booking's date, time, party size or table")
            .WithDescription(
                "Moves the diner's **own** booking and answers with it: the **same `id` and `code`** and "
                + "manage link, at the new `startUtc`/`endUtc`, `partySize`, `tableId`, `roomId`/`roomName` "
                + "and `status`. Send the whole new slot - `date`, `time`, `partySize` and `diningTableId` "
                + "(required, the current table or another at the branch) - as a new booking would.\n\n"
                + "**Only a booking still to come**: `PendingApproval` or `Confirmed` and not yet started. "
                + "Anything else - seated, finished, cancelled, a no-show, or past its start - is `409 "
                + "reservation-not-changeable`, before the new slot is looked at.\n\n"
                + "**Judged as a new booking would be**, with the same `reservation-*` 422s: the branch "
                + "open for business and still taking bookings from the channel the booking came from, "
                + "lead time and booking window, opening hours, the table pinned on its room and fitting "
                + "the party. The clash check runs under the target table's lock **excluding this "
                + "booking**, so moving dinner by half an hour on the same table is never refused "
                + "because of itself - and the 409's fresh floor shows the diner's own table free.\n\n"
                + "**The status is decided again** by the create rules: the move can keep it `Confirmed`, "
                + "confirm a pending one, or send it back to `PendingApproval` (`awaitingApprovalBecause` "
                + "says why). A `Confirmed` booking that keeps its **table and start** and does not grow "
                + "stays `Confirmed`, today included - nothing about it got harder to keep. Otherwise a "
                + "move into the short-notice window (today, or under six hours away) goes to "
                + "`PendingApproval` with a fifteen-minute `approvalExpiresAtUtc` - and keeps the slot it "
                + "had: if the change is declined, or nobody answers it, the booking goes back to that "
                + "slot, `Confirmed` as before, when it is still free. A booking **already waiting** on a "
                + "short-notice answer keeps its deadline through a move (never past the new start); past "
                + "the deadline it cannot be moved at all - `409 reservation-approval-expired`. A `Confirmed` "
                + "booking whose party does not grow keeps its confirmation against the party-size "
                + "threshold - the venue already said yes to that many people - but not against a branch "
                + "that approves every booking or the diner's no-show history. A booking waiting on the "
                + "guest before it (`awaitingApprovalBecause` 5) keeps its two-hour deadline through a move "
                + "in the same way, whoever answers - the floor or that guest in the app.\n\n"
                + OverlapsOwnDescription + " The booking being moved is left out of that check, and a "
                + "change that keeps its table, start and end (a party size) is not judged by it.\n\n"
                + "The reminder and late nudge are replaced with ones for the new time. Idempotent on "
                + "`clientCommandId`: a retry of the same change answers `200` with the booking **as it "
                + "stands now** and `wasReplay: true`, moving nothing; the same id on another command, "
                + "or on a different change (another table, start or party size), is `409 "
                + "client-command-id-in-use` - mint a fresh id per change.")
            .Produces<ReservationView>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`diningTableId` names a table at another branch.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, PhoneNotVerifiedDescription)
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound,
                "No such booking, or it is somebody else's - one answer for both; or no such table.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "`reservation-not-changeable`: the booking is finished with or has started. "
                + "`table-already-booked` / `table-currently-occupied`: the new slot is taken - " + ConflictDescription
                + " `reservation-overlaps-own`: another of the diner's bookings at this branch runs then - "
                + "`context.reservationId`, `code`, `localDate`, `localStartTime`, `tableLabel` name it."
                + " `concurrent-update`: the booking was cancelled, approved or seated while this was in "
                + "flight; read it again. `bookings-not-accepted`, `web-bookings-not-accepted`, "
                + "`branch-unavailable`: the branch stopped taking bookings. `client-command-id-in-use`: "
                + "the id belongs to another command or to a different change.")
            .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity, RejectedDescription)
            .ProducesProblem<LockTimeoutProblem>(
                StatusCodes.Status503ServiceUnavailable, LockTimeoutDescription);

        group.MapPost("/{reservationId:guid}/table-requests/{requestId:guid}/answer", AnswerTableRequestAsync)
            .WithName("answerTableRequest")
            .WithSummary("Answer a later guest's request for your table: will you have left in time?")
            .WithDescription(
                "Somebody would like the table after the diner's **own** booking - its `incomingTableRequest` "
                + "says when (`requestedStartUtc`) and by when the diner is asked to have left (`freeByUtc`, "
                + "the requested start less the branch's clearing time); the push is kind `table-request` "
                + "with `reservationId` and `requestId`. The other guest stays anonymous.\n\n"
                + "`accept: true`: the later booking is confirmed, and this booking promises to free the table "
                + "by `freeByUtc` - its `endUtc` is cut to it and `promisedFreeByUtc` set. `accept: false`: the "
                + "later booking is declined (`declinedBecause` 3). Both diners are told. Answers with this "
                + "booking as it now stands.\n\n"
                + "Before `expiresAtUtc` only - two hours from the request, never past this booking's start; "
                + "unanswered by then the later booking is declined for it (`declinedBecause` 4) and this one "
                + "is unchanged. Idempotent on `clientCommandId`, and the same answer twice is answered "
                + "with the booking; a request answered the other way, expired, or withdrawn because either "
                + "booking moved or ended is `409 table-request-not-pending`.")
            .Produces<ReservationView>()
            .ProducesProblemDetails(
                StatusCodes.Status404NotFound,
                "No such booking of the caller's, or no such request on it put to them - one answer for all.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "`table-request-not-pending`: the request no longer waits - drop the card. "
                + "`client-command-id-in-use`: the id belongs to another command or the other answer.")
            .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity, "`accept` is missing.");

        group.MapGet("/mine", GetMineAsync)
            .WithName("getMyReservations")
            .WithSummary("The calling diner's own bookings, upcoming and past")
            .WithDescription(
                "Upcoming means still going to happen: not finished, and not already called off. A "
                + "booking cancelled for tomorrow belongs in the history, not at the top of the "
                + "screen.")
            .Produces<MyReservations>();

        group.MapPost("/{id:guid}/extend-hold", ExtendHoldAsync)
            .WithName("extendReservationHold")
            .WithSummary("Keep the table a little longer - the late nudge's action")
            .WithDescription(
                "What the **late nudge** notification's action button calls. The diner taps it from "
                + "the lock screen; they never open a screen and hunt for it.\n\n"
                + "**Once.** `graceExtensionsUsed` enforces it, and a second attempt is refused with "
                + "a reason a diner can read. Repeated requests for five more minutes are how a table "
                + "stays held all evening for somebody who is not coming.\n\n"
                + "The extension goes onto the branch change sequence, so the waiter watching that "
                + "table sees it immediately rather than in a column nothing reads.\n\n"
                + "**Not before the booking starts.** Nothing is held until then, so there is nothing "
                + "to keep: offer the button from the late nudge, or once `startUtc` has passed. A tap "
                + "days early used to spend the one extension and ping the floor about a table nobody "
                + "was holding.")
            .Produces<ExtendHoldResult>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Not the diner who made the booking (`forbidden`), or " + PhoneNotVerifiedDescription)
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking.")
            .ProducesProblem<HoldExtensionRefusedProblem>(
                StatusCodes.Status409Conflict,
                "Refused, with one of three codes - branch on it. `hold-not-active`: the booking has "
                + "not started, so nothing is held yet, or it is not confirmed. `hold-already-extended`: "
                + "the one extension is used. `extensions-not-offered`: the branch offers none - not "
                + "the diner's doing, so never tell them they already let the venue know.");

        group.MapPost("/{id:guid}/cancel", CancelAsync)
            .WithName("cancelReservation")
            .WithSummary("Cancel a booking")
            .WithDescription(
                "A diner may only cancel their own; anyone else's is 403. Free until the branch's "
                + "cancellation deadline and still allowed after it - a late cancellation is far "
                + "better than a no-show - with `cancelledAfterDeadline` recording which it was.")
            .Produces<ReservationView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "This booking belongs to somebody else.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, StateDescription);
    }

    private const string DecisionExpiredDescription =
        "`reservation-approval-expired`: too late to answer - the short-notice answer window "
        + "(`approvalExpiresAtUtc`) has closed, or the booking was already declined because nobody "
        + "answered in time (`declinedBecause` 2); approving also once the booking has started. "
        + "`context` carries `reservationId`, `status`, `startUtc` and `approvalExpiresAtUtc`. Drop the "
        + "request from the panel: the diner is told the venue did not answer in time. "
        + "`conflicting-state`: the booking is not waiting for approval. "
        + "`reservation-awaiting-earlier-guest`: the booking waits on the guest before it on the table to answer "
        + "in the app - theirs to answer, not the floor's (`context`: `reservationId`, `requestId`, `expiresAtUtc`). "
        + "`table-request-not-pending`: approving a booking whose guest before has moved since they were asked.";

    private static void MapStaffDecisions(IEndpointRouteBuilder app)
    {
        // A separate group because the identity is different. WaiterOrAbove carries no route
        // dependency, so it works on a route addressed by reservation id; BranchScoped would not,
        // and the service does that half. Waiter or above, not manager: a short-notice booking is a
        // question about a table somebody may have just sat down at, which the person on the floor
        // can see and the office cannot - and the answer is due within fifteen minutes. No
        // clientCommandId filter: the body is unchanged, and deciding twice is refused by the state.
        var group = app.MapGroup("/api/reservations")
            .WithTags(EndpointConventions.StaffTag, EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove);

        group.MapPost("/{id:guid}/approve", ApproveAsync)
            .WithName("approveReservation")
            .WithSummary("Accept a booking that is waiting for approval")
            .WithDescription(
                "A waiter or above at the booking's own branch - an owner or general manager at any "
                + "branch of their venue, a platform admin anywhere. Staff of one branch cannot decide "
                + "another's bookings by guessing an id.\n\n"
                + "A short-notice booking (`awaitingApprovalBecause` 4) can be approved until "
                + "`approvalExpiresAtUtc`, and no booking once it has started. The caller's **stored** "
                + "role counts, not only the token's: a waiter moved to the kitchen is refused at once. "
                + "A platform admin gets the booking back with `guestPhone` empty.")
            .Produces<ReservationView>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden, "Not a waiter or above at this booking's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, DecisionExpiredDescription);

        // Waiter or above, not manager: the person standing at the table is the one who knows
        // nobody came, and a hold that waits for somebody senior to walk past is a hold nobody
        // releases. Its own group for that reason.
        var floor = app.MapGroup("/api/reservations")
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove);

        floor.AddEndpointFilter<ClientCommandIdFilter>();

        floor.MapPost("/{id:guid}/no-show", MarkNoShowAsync)
            .WithName("markReservationNoShow")
            .WithSummary("Mark a booking as a no-show, and free the table")
            .WithDescription(
                "The same action as `release` with `outcome: 1`, under the name the tablet's "
                + "late-booking panel uses - one implementation, two doors, so the two cannot drift.\n\n"
                + "**Never automatic.** Prompt 2's rule that the decision belongs to a waiter applies "
                + "here more than anywhere: only a person standing in the room knows whether the "
                + "party is late or absent.\n\n"
                + "Counts toward the diner's rolling no-show threshold. If they telephoned to say "
                + "they could not come, use `release` with `outcome: 2` instead - a diner who let the "
                + "venue know must not be punished for it.")
            .Produces<ReservationReleaseResult>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff at this booking's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The booking cannot be released.");

        floor.MapPost("/{id:guid}/release", ReleaseAsync)
            .WithName("releaseReservation")
            .WithSummary("Let a late booking go, and free the table with it")
            .WithDescription(
                "**Two outcomes, and the difference matters to the diner.**\n\n"
                + "| `outcome` | Booking becomes | Counts toward the no-show threshold |\n"
                + "|---|---|---|\n"
                + "| `1` NoShow | `NoShow` | **Yes** |\n"
                + "| `2` CancelledByVenue | `CancelledByVenue` | **No** |\n\n"
                + "Render these as two buttons - *release, marked no-show* and *release, they let us "
                + "know*. With one button a busy waiter taps it for both, and the threshold ends up "
                + "punishing the people who phoned ahead. A table out of service is always the second "
                + "one: a broken chair is the venue's doing.\n\n"
                + "The table is freed when it is **held for this booking**, through the state "
                + "machine, so the audit row is written and the branch change sequence moves. A table "
                + "somebody is **sitting at** is left alone - the booking is released either way, and "
                + "a floor plan that shows an occupied table as free costs more than a stale hold.")
            .Produces<ReservationReleaseResult>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not staff at this booking's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking.")
            .ProducesProblemDetails(
                StatusCodes.Status409Conflict,
                "The booking is not in a state that can be released - it was already seated, "
                + "completed or cancelled by the diner.");

        group.MapPost("/{id:guid}/reject", RejectAsync)
            .WithName("rejectReservation")
            .WithSummary("Decline a booking that is waiting for approval")
            .WithDescription(
                "Scoped as `approve`. Refuses a booking that was already confirmed: the diner has been told "
                + "it is theirs. `reason` is optional and recorded as `cancellationReason`; the booking "
                + "answers `declinedBecause` 1 (StaffDeclined). A short-notice booking can be declined "
                + "until `approvalExpiresAtUtc` - after that it is declined for nobody answering, and the "
                + "diner is told that instead.\n\n"
                + "**A change of a confirmed booking** is declined as a change: when the slot it had is "
                + "still free the answer is the booking back there - `Confirmed`, its old `startUtc`, "
                + "`tableId` and `partySize` - and the diner is told it stands (`reservation-decided` with "
                + "`wasChange` and `originalKept` \"true\"); when that slot has gone too it is declined "
                + "as above, with `originalKept` \"false\". A platform admin gets `guestPhone` empty.")
            .Produces<ReservationView>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden, "Not a waiter or above at this booking's branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such booking.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, DecisionExpiredDescription);
    }

    private static async Task<IResult> MarkNoShowAsync(
        Guid id,
        MarkNoShowRequest request,
        IReservationService reservations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await reservations.ReleaseAsync(
            new ReleaseReservationCommand(
                id, ReleaseOutcome.NoShow, request.ClientCommandId, request.Reason),
            cancellationToken));
    }

    private static async Task<IResult> ExtendHoldAsync(
        Guid id,
        ExtendHoldRequest request,
        IReservationService reservations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await reservations.ExtendHoldAsync(
            new ExtendHoldCommand(id, request.ClientCommandId), cancellationToken));
    }

    private static async Task<IResult> ReleaseAsync(
        Guid id,
        ReleaseReservationRequest request,
        IReservationService reservations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await reservations.ReleaseAsync(
            new ReleaseReservationCommand(id, request.Outcome!.Value, request.ClientCommandId, request.Reason),
            cancellationToken));
    }

    private static async Task<IResult> GetAvailabilityAsync(
        Guid branchId,
        IAvailabilityQuery availability,
        CancellationToken cancellationToken,
        DateOnly? date = null,
        TimeOnly? time = null,
        int partySize = DefaultPartySize,
        Guid? excludeReservationId = null)
    {
        var result = await availability.GetAvailabilityAsync(
            new AvailabilityRequest(branchId, partySize, date, time, excludeReservationId), cancellationToken);

        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetDaySlotsAsync(
        Guid branchId,
        DateOnly date,
        IAvailabilityQuery availability,
        CancellationToken cancellationToken,
        int partySize = DefaultPartySize,
        Guid? excludeReservationId = null)
    {
        var result = await availability.GetDaySlotsAsync(
            new DaySlotsRequest(branchId, date, partySize, excludeReservationId), cancellationToken);

        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        CreateReservationRequest request,
        IReservationService reservations,
        CancellationToken cancellationToken)
    {
        var view = await reservations.CreateAsync(
            new CreateReservationCommand(
                request.BranchId,
                request.TableId,

                // The filter has already refused a null by the time this runs.
                request.Date!.Value,
                request.Time!.Value,
                request.PartySize,
                request.GuestName,
                request.GuestPhone,
                request.ClientCommandId,
                request.StayHint,
                request.Channel,
                request.Note,
                request.AcceptsFreeByUtc),
            cancellationToken);

        // A replay is answered 200, not 201: the second request created nothing, and telling a
        // client it did is how a retry ends up looking like a second booking in its own logs.
        return view.WasReplay
            ? Results.Ok(view)
            : Results.Created($"/api/reservations/{view.Id}", view);
    }

    private static async Task<IResult> RescheduleAsync(
        Guid reservationId,
        RescheduleReservationRequest request,
        IReservationService reservations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The validation filter has already refused a null date, time or table by the time this runs.
        return Results.Ok(await reservations.RescheduleAsync(
            new RescheduleReservationCommand(
                reservationId,
                request.DiningTableId!.Value,
                request.Date!.Value,
                request.Time!.Value,
                request.PartySize,
                request.ClientCommandId,
                request.AcceptsFreeByUtc),
            cancellationToken));
    }

    private static async Task<IResult> AnswerTableRequestAsync(
        Guid reservationId,
        Guid requestId,
        AnswerTableRequestRequest request,
        IReservationService reservations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The validation filter has already refused a missing answer by the time this runs.
        return Results.Ok(await reservations.AnswerTableRequestAsync(
            new AnswerTableRequestCommand(reservationId, requestId, request.Accept!.Value, request.ClientCommandId),
            cancellationToken));
    }

    private static async Task<IResult> GetMineAsync(
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.GetMineAsync(cancellationToken));

    private static async Task<IResult> CancelAsync(
        Guid id,
        CancelReservationRequest? request,
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.CancelAsync(
            new CancelReservationCommand(id, request?.Reason), cancellationToken));

    private static async Task<IResult> ApproveAsync(
        Guid id,
        DecideReservationRequest? request,
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.ApproveAsync(
            new DecideReservationCommand(id, request?.Reason), cancellationToken));

    private static async Task<IResult> RejectAsync(
        Guid id,
        DecideReservationRequest? request,
        IReservationService reservations,
        CancellationToken cancellationToken) =>
        Results.Ok(await reservations.RejectAsync(
            new DecideReservationCommand(id, request?.Reason), cancellationToken));

    /// <summary>
    /// The party size assumed when a browser does not say. Two is the commonest booking and the
    /// least restrictive useful default: it excludes nothing a larger party would have seen.
    /// </summary>
    private const int DefaultPartySize = 2;
}
