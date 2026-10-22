namespace Yalla.Api.Errors;

/// <summary>
/// The stable slugs clients branch on. Add to this list; never reword an existing value, because
/// a deployed app is matching on it.
/// </summary>
public static class ErrorCodes
{
    /// <summary>The request violated a domain rule or arrived malformed. HTTP 400.</summary>
    public const string InvalidRequest = "invalid-request";

    /// <summary>Field-level validation failed. HTTP 422.</summary>
    public const string ValidationFailed = "validation-failed";

    /// <summary>The caller is not allowed to do this. HTTP 403.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The thing addressed does not exist. HTTP 404.</summary>
    public const string NotFound = "not-found";

    /// <summary>
    /// The operation is not legal in the current state - seating a cancelled booking, paying a
    /// closed tab. HTTP 409.
    /// </summary>
    public const string ConflictingState = "conflicting-state";

    /// <summary>
    /// Someone else changed the row first. The client should re-read and retry, and for tables,
    /// tabs and reservations this is a normal outcome rather than a bug: two waiters really do
    /// tap the same table at the same instant. HTTP 409.
    /// </summary>
    public const string ConcurrentUpdate = "concurrent-update";

    /// <summary>
    /// Somebody else changed this table first. HTTP 409, with the table's current state in
    /// <c>details</c> so the client can redraw it. Never retried automatically: retrying would
    /// seat a walk-in at a table the diner's booking just took.
    /// </summary>
    public const string TableStateConflict = "table-state-conflict";

    /// <summary>
    /// The requested transition is not one the state machine allows - freeing an empty table,
    /// marking an occupied one broken. HTTP 422, with the legal transitions in <c>details</c>.
    /// </summary>
    public const string InvalidTableTransition = "invalid-table-transition";

    /// <summary>
    /// A branch rule refused a booking. HTTP 422.
    /// </summary>
    /// <remarks>
    /// <b>Never returned as-is.</b> Every refusal answers with its own slug -
    /// <c>reservation-party-exceeds-capacity</c>, <c>reservation-outside-opening-hours</c>, and so
    /// on, one per rule, declared on the exception that carries it. This constant only names the
    /// family, because a client that cannot tell "the table seats four" from "we are shut" ends up
    /// showing "invalid booking" to somebody who needed one sentence of help.
    /// </remarks>
    public const string ReservationRejectedPrefix = "reservation-";

    /// <summary>
    /// The table was booked by somebody else between the diner seeing it free and confirming.
    /// HTTP 409, with the clashing window and a fresh availability snapshot in <c>details</c> so
    /// the app can redraw the floor and show what changed.
    /// </summary>
    public const string TableAlreadyBooked = "table-already-booked";

    /// <summary>
    /// A party is seated at the table now and their sitting runs into the requested slot. HTTP 409,
    /// with the seating and projected finish times in <c>details</c>. Distinct from
    /// <see cref="TableAlreadyBooked"/>: nobody booked it, and it may free up early.
    /// </summary>
    public const string TableCurrentlyOccupied = "table-currently-occupied";

    /// <summary>
    /// The booking could not get the table's lock in time. HTTP 503 and <b>retryable</b> - which
    /// is what makes it different from <see cref="TableAlreadyBooked"/>: the question was never
    /// asked, rather than answered no. Retry with the same <c>clientCommandId</c>.
    /// </summary>
    /// <summary>A dish on the order has sold out. The context names it, so the client can too.</summary>
    public const string MenuItemUnavailable = "menu-item-unavailable";

    /// <summary>The bill has been asked for; the app should show it rather than the menu.</summary>
    public const string TabNotAcceptingOrders = "tab-not-accepting-orders";

    /// <summary>
    /// Staff tried to open a tab on a table with nobody seated at it. Seat the party first. HTTP 409,
    /// with the table's status in the context. Minted by <c>TableNotSeatedException</c>.
    /// </summary>
    public const string TableNotSeated = "table-not-seated";

    /// <summary>Removing this line would reverse money already taken. That is a refund.</summary>
    public const string LineAlreadyPaid = "line-already-paid";

    /// <summary>More was offered than is owed. The context carries the current balance.</summary>
    public const string PaymentExceedsRemaining = "payment-exceeds-remaining";

    /// <summary>This table has asked for too many things too quickly.</summary>
    public const string ServiceRequestRateLimited = "service-request-rate-limited";

    public const string ReservationLockTimeout = "reservation-lock-timeout";

    /// <summary>
    /// A table write waited too long for another writer. Retryable, and deliberately not a
    /// conflict: nothing was decided, so the same request will very likely succeed.
    /// </summary>
    public const string TableLockTimeout = "table-lock-timeout";

    /// <summary>
    /// The <c>clientCommandId</c> on a booking request already belongs to another caller's booking.
    /// HTTP 409. A client bug rather than a retry: generate one per booking, and reuse it only when
    /// retrying that same booking.
    /// </summary>
    public const string ClientCommandIdInUse = "client-command-id-in-use";

    /// <summary>Too many requests in the window. HTTP 429.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>
    /// No usable credential was presented, or the one presented was rejected. HTTP 401.
    /// </summary>
    /// <remarks>
    /// The specific sign-in failures replace this with their own slug - <c>pin-invalid</c>,
    /// <c>verification-code-expired</c>, <c>refresh-token-reused</c> and so on - because those are
    /// things the caller is entitled to know about their own credential. None of them ever
    /// distinguishes "no such account" from "wrong secret".
    /// </remarks>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>
    /// A diner token from a session that has ended. HTTP 401, with
    /// <c>WWW-Authenticate: Bearer error="invalid_token"</c>. The account was deactivated or deleted,
    /// or its session generation moved on: the password was set or changed, or the number's owner
    /// proved it and displaced a registrant. The client tries its refresh token once; when that is
    /// refused too, it signs out.
    /// </summary>
    public const string SessionRevoked = "session-revoked";

    /// <summary>
    /// A one-time credential is out of attempts, or a per-identity limit is spent. HTTP 429.
    /// Asking again immediately will not help; ask for a new code.
    /// </summary>
    public const string TooManyAttempts = "too-many-attempts";

    /// <summary>
    /// No SMS provider is configured, so no one-time code can be sent. HTTP 503. Not retryable:
    /// prove the number with Telegram instead. <c>GET /api/auth/diner/phone-proof</c> says so in
    /// advance, so a client that reads it never meets this.
    /// </summary>
    public const string SmsUnavailable = "sms-unavailable";

    /// <summary>
    /// The account is locked, not the credential wrong. HTTP 403, with <c>lockedUntilUtc</c> in
    /// <c>context</c>. For a staff PIN, the fix is a manager clearing it rather than trying again.
    /// </summary>
    public const string AccountLocked = "account-locked";

    /// <summary>
    /// The branch is on a tier that does not include this feature - tabs on a Free branch. HTTP
    /// 409, not 403: the caller is allowed here, the branch has not paid for it. <c>context</c>
    /// carries <c>branchId</c> and <c>currentTier</c>.
    /// </summary>
    public const string FeatureNotEnabled = "feature-not-enabled";

    /// <summary>
    /// The venue has open tabs or future bookings and cannot be deleted. HTTP 409, with the
    /// blockers named in <c>context</c>.
    /// </summary>
    public const string VenueDeletionBlocked = "venue-deletion-blocked";

    /// <summary>
    /// The floor plan cannot be applied: missing or repeated table labels or room names, or a table
    /// matched twice. HTTP 422, with every reason in <c>context.errors</c>.
    /// </summary>
    public const string FloorPlanInvalid = "floor-plan-invalid";

    /// <summary>
    /// The branch's menu still has items that are not fit to show a diner, so it cannot be
    /// switched to Paid. HTTP 409, with <c>incompleteMenuItemCount</c> in <c>context</c>.
    /// </summary>
    /// <remarks>
    /// A conflict rather than a validation failure: nothing in the request is wrong, and nothing
    /// the caller can change in it would help. The fix is to finish the menu, which is what
    /// <c>GET /api/branches/{branchId}/readiness</c> is for.
    /// </remarks>
    public const string BranchNotReady = "branch-not-ready";

    /// <summary>
    /// The branch is not open for business: its venue is suspended or deleted, or the branch is
    /// switched off. HTTP 409. Distinct from <see cref="FeatureNotEnabled"/>, which is about what a
    /// branch pays for rather than whether it is trading at all.
    /// </summary>
    public const string BranchUnavailable = "branch-unavailable";

    /// <summary>
    /// The branch does not take bookings from its public page. The public page branches on
    /// this to say "call the venue" rather than "something went wrong".
    /// </summary>
    public const string WebBookingsNotAccepted = "web-bookings-not-accepted";

    /// <summary>
    /// A queued command arrived describing a table that has since moved. HTTP 409, with the
    /// expected and current status in <c>context</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="TableStateConflict"/>, which both the client and this
    /// codebase treat differently: a live race gets an immediate "that table just went" while the
    /// waiter is holding the tablet, and a stale replay goes into a conflict list to resolve later.
    /// Collapsing the two would put an hour-old tap in front of somebody as though it had just
    /// happened.
    /// </remarks>
    public const string PreconditionFailed = "precondition-failed";

    /// <summary>
    /// A free that named a booking found another sitting - or none - open at the table. HTTP 409, with
    /// <c>tableId</c>, <c>tableLabel</c>, <c>reservationId</c>, <c>tableStatus</c> and, when somebody is
    /// sitting there, <c>currentSessionId</c> in <c>context</c>. The console's stale Overview row: refresh it.
    /// </summary>
    public const string TableSittingChanged = "table-sitting-changed";

    /// <summary>
    /// The report range is longer than any report will run. HTTP 400, with the limit and the
    /// number of days asked for in <c>context</c>.
    /// </summary>
    /// <remarks>
    /// Named rather than a generic bad request, because the client's correct response is specific:
    /// narrow the range, or ask for the months separately. A range nobody meant to ask for is a
    /// table scan nobody meant to run, and a refusal that says the limit is more use than a request
    /// that times out.
    /// </remarks>
    public const string ReportRangeTooLong = "report-range-too-long";

    /// <summary>
    /// "Keep my table" with no table being held: the booking has not started, or is not a confirmed
    /// one. HTTP 409, with <c>reservationId</c> and <c>startUtc</c> in <c>context</c>. Offer the button
    /// from the late nudge, or once the start has passed. Minted by <c>HoldExtensionRefusedException</c>.
    /// </summary>
    public const string HoldNotActive = "hold-not-active";

    /// <summary>The booking's one hold extension is already used. HTTP 409.</summary>
    public const string HoldAlreadyExtended = "hold-already-extended";

    /// <summary>
    /// The branch offers no hold extensions. HTTP 409. Not the diner's doing, so never "you have
    /// already let them know": say to speak to the venue.
    /// </summary>
    public const string ExtensionsNotOffered = "extensions-not-offered";

    /// <summary>
    /// "I'm at my table" with a code none of the caller's bookings carries - whether or not somebody
    /// else's does. HTTP 404 with no <c>context</c>, and word for word the same for both, because the
    /// code is read out at the door and proves nothing. Minted from <c>BookingNotFoundException</c>.
    /// </summary>
    public const string BookingNotFound = "booking-not-found";

    /// <summary>
    /// The booking is confirmed but its table is not being held for the party yet. HTTP 409, with
    /// <c>context.earliestUtc</c> - the branch's walk-in holdback before the start - to say when it
    /// will be. Minted by <c>BookingTabRefusedException</c>, like the two below.
    /// </summary>
    public const string BookingTooEarly = "booking-too-early";

    /// <summary>The booking is over: past its end, or its sitting has finished. HTTP 409.</summary>
    public const string BookingEnded = "booking-ended";

    /// <summary>
    /// The booking is not expecting its party - waiting for approval, cancelled, or released as a
    /// no-show. HTTP 409, with <c>context.status</c> saying which.
    /// </summary>
    public const string BookingNotActive = "booking-not-active";

    /// <summary>
    /// The booked table still has another party's sitting open - not this booking's - so the booker
    /// is not put on that tab. HTTP 409, with the booking's facts. A member of staff frees the table.
    /// </summary>
    public const string BookingTableOccupied = "booking-table-occupied";

    /// <summary>
    /// A diner's username, email or password did not match a usable account. HTTP 401. Unknown
    /// identifier, wrong password, an account with no password and a deactivated account all
    /// arrive as this one code, so the sign-in form cannot be used to discover which exist.
    /// </summary>
    public const string InvalidCredentials = "invalid-credentials";

    /// <summary>
    /// The username on a diner sign-up or profile edit is somebody else's. HTTP 409, with
    /// <c>context.field</c> naming the input. Minted by <c>DinerIdentifierTakenException</c>, like
    /// the two below.
    /// </summary>
    public const string UsernameTaken = "username-taken";

    /// <summary>The email address on a diner sign-up or profile edit is somebody else's. HTTP 409.</summary>
    public const string EmailTaken = "email-taken";

    /// <summary>
    /// The phone number on a diner sign-up already has an account. HTTP 409. The app's answer is
    /// "log in with a code instead": that account's owner can prove the number, a stranger cannot.
    /// </summary>
    public const string PhoneInUse = "phone-in-use";

    /// <summary>
    /// The diner's phone number has never been proved by a one-time code, and the request would
    /// land on it: creating a booking, extending its hold, opening a tab from it. HTTP 403. The
    /// app's answer is the verify link into the code flow. Minted by
    /// <c>PhoneNotVerifiedException</c>, from the stored account row rather than the token.
    /// </summary>
    public const string PhoneNotVerified = "phone-not-verified";

    /// <summary>
    /// The booker's proved number already holds three active bookings (pending or confirmed, not over) at
    /// any venue - the most one number holds. HTTP 409, <c>context.maxActiveBookings</c> and the caller's own
    /// earliest active booking. Minted by <c>PhoneHasActiveBookingException</c>.
    /// </summary>
    public const string PhoneHasActiveBooking = "phone-has-active-booking";

    /// <summary>
    /// The guest missed three bookings in three days and cannot book until <c>context.blockedUntilUtc</c>.
    /// HTTP 403. Minted by <c>BookingBlockedForNoShowsException</c>.
    /// </summary>
    public const string BookingBlockedNoShows = "booking-blocked-no-shows";

    /// <summary>
    /// A no-show can no longer be withdrawn: its service day ended (05:00 the next morning, branch time). HTTP 409,
    /// <c>context.undoableUntilUtc</c>. Minted by <c>NoShowUndoWindowClosedException</c>.
    /// </summary>
    public const string NoShowUndoWindowClosed = "no-show-undo-window-closed";

    /// <summary>
    /// Approving or rejecting a booking a no-show block cancelled. HTTP 409, <c>context.reservationId</c> and
    /// <c>cancelledAtUtc</c>. Minted by <c>ReservationCancelledByBlockException</c>.
    /// </summary>
    public const string ReservationCancelledByBlock = "reservation-cancelled-by-block";

    /// <summary>
    /// The pre-order can no longer be changed by the guest: it is two hours before the start or later. HTTP 409,
    /// <c>context.reservationId</c> and <c>lockedAtUtc</c>. Minted by <c>PreOrderLockedException</c>.
    /// </summary>
    public const string PreOrderLocked = "pre-order-locked";

    /// <summary>
    /// The kitchen already has the pre-order: it cannot be edited, cleared, marked not coming, or its booking moved.
    /// HTTP 409, <c>context.reservationId</c> and <c>sentAtUtc</c>. Minted by <c>PreOrderAlreadySentException</c>.
    /// </summary>
    public const string PreOrderAlreadySent = "pre-order-already-sent";

    /// <summary>The branch does not take pre-orders. HTTP 409, <c>context.branchId</c>. Minted by <c>PreOrdersNotAcceptedException</c>.</summary>
    public const string PreOrdersNotAccepted = "pre-orders-not-accepted";

    /// <summary>
    /// Not the time or the state to send a pre-order to the kitchen, or mark the party not coming. HTTP 409,
    /// <c>context</c> <c>reservationId</c>, <c>status</c>, <c>sendableFromUtc</c>, <c>endUtc</c>. Minted by
    /// <c>PreOrderNotSendableException</c>.
    /// </summary>
    public const string PreOrderNotSendable = "pre-order-not-sendable";

    /// <summary>
    /// Dishes that cannot be pre-ordered. HTTP 422, <c>context.items[]</c> = <c>{ menuItemId, reason }</c> (1 not on the
    /// menu, 2 sold out, 3 excluded from pre-orders). Minted by <c>PreOrderItemsUnavailableException</c>.
    /// </summary>
    public const string PreOrderItemsUnavailable = "pre-order-items-unavailable";

    /// <summary>
    /// "On my way" for a booking that is not confirmed or seated, is more than two hours away, or is over. HTTP 409,
    /// <c>context.reservationId</c>, <c>status</c> and <c>opensAtUtc</c> (start - 2 h). Minted by
    /// <c>OnMyWayNotOpenException</c>.
    /// </summary>
    public const string OnMyWayNotOpen = "on-my-way-not-open";

    /// <summary>
    /// "Ask: are you coming?" for a booking that is not confirmed, or has started. HTTP 409, <c>context.reservationId</c>,
    /// <c>status</c>, <c>startUtc</c>. Minted by <c>AskOnMyWayNotOpenException</c>.
    /// </summary>
    public const string AskOnMyWayNotOpen = "ask-on-my-way-not-open";

    /// <summary>
    /// "Ask: are you coming?" more than two hours before the start. HTTP 409, <c>context.reservationId</c> and
    /// <c>opensAtUtc</c>. Minted by <c>AskOnMyWayTooEarlyException</c>.
    /// </summary>
    public const string AskOnMyWayTooEarly = "ask-on-my-way-too-early";

    /// <summary>
    /// "Ask: are you coming?" within fifteen minutes of the last ask. HTTP 409, <c>context.reservationId</c>,
    /// <c>askedAtUtc</c>, <c>nextAllowedAtUtc</c>. Minted by <c>AskOnMyWayAskedRecentlyException</c>.
    /// </summary>
    public const string AskOnMyWayAskedRecently = "ask-on-my-way-asked-recently";

    /// <summary>
    /// "Ask: are you coming?" when the guest already said on my way or running late. HTTP 409,
    /// <c>context.reservationId</c>, <c>answeredAtUtc</c>, <c>runningLateMinutes</c> (absent for "on my way"). Minted by
    /// <c>GuestAlreadyAnsweredException</c>.
    /// </summary>
    public const string GuestAlreadyAnswered = "guest-already-answered";

    /// <summary>The branch does not take takeaway orders. HTTP 409, <c>context.branchId</c>. Minted by <c>TakeawayNotAcceptedException</c>.</summary>
    public const string TakeawayNotAccepted = "takeaway-not-accepted";

    /// <summary>
    /// Closed for takeaway now - shut, or past the last order. HTTP 409, <c>context.branchId</c>, <c>opensAtUtc</c> and
    /// <c>opensAt</c> (local <c>HH:mm</c>) when it opens again within a week. Minted by <c>TakeawayClosedException</c>.
    /// </summary>
    public const string TakeawayClosed = "takeaway-closed";

    /// <summary>
    /// ASAP while the kitchen has as many takeaway orders in progress as the venue allows. HTTP 409,
    /// <c>context.branchId</c>, <c>maxInProgress</c>. Minted by <c>TakeawayBusyException</c>.
    /// </summary>
    public const string TakeawayBusy = "takeaway-busy";

    /// <summary>
    /// The pickup time is not one on offer. HTTP 409, <c>context.branchId</c>, <c>at</c>, <c>slots</c> (on offer now).
    /// Minted by <c>TakeawaySlotUnavailableException</c>.
    /// </summary>
    public const string TakeawaySlotUnavailable = "takeaway-slot-unavailable";

    /// <summary>
    /// Dishes that cannot be ordered to take away. HTTP 422, <c>context.items[]</c> = <c>{ menuItemId, reason }</c> (1 not
    /// on the menu, 2 sold out, 3 hall only). Minted by <c>TakeawayItemsUnavailableException</c>.
    /// </summary>
    public const string TakeawayItemsUnavailable = "takeaway-items-unavailable";

    /// <summary>
    /// The takeaway order is not in the state for that move. HTTP 409, <c>context.orderId</c> and <c>status</c> (where it
    /// is now). Minted by <c>TakeawayStatusConflictException</c>.
    /// </summary>
    public const string TakeawayStatusConflict = "takeaway-status-conflict";

    /// <summary>
    /// "No-show" before thirty minutes past ready. HTTP 409, <c>context.orderId</c> and <c>noShowFromUtc</c>. Minted by
    /// <c>TakeawayNoShowTooEarlyException</c>.
    /// </summary>
    public const string TakeawayNoShowTooEarly = "takeaway-no-show-too-early";

    /// <summary>
    /// The tab was opened by a scan with no proved number and waits for a waiter to let it order. HTTP
    /// 409, <c>context.tabId</c>. Minted by <c>TabAwaitingAdmissionException</c>.
    /// </summary>
    public const string TabAwaitingAdmission = "tab-awaiting-admission";

    /// <summary>
    /// Admitting a tab already turned away, or turning away one already admitted. HTTP 409,
    /// <c>context.tabId</c> and <c>context.admission</c>. Minted by <c>TabAdmissionDecidedException</c>.
    /// </summary>
    public const string TabAdmissionDecided = "tab-admission-decided";

    /// <summary>
    /// A scan refused for a while after a waiter turned a tab away - from a phone that was on it, or of the
    /// table it was at. HTTP 409, <c>context.retryAfterUtc</c>. Minted by <c>ScanTurnedAwayException</c>.
    /// </summary>
    public const string ScanTurnedAway = "scan-turned-away";

    /// <summary>
    /// An upload is not a JPEG, PNG or WebP, or is too large to store. HTTP 409, with
    /// <c>context.detectedFormat</c> when the bytes were an image of some other kind. Decided by
    /// sniffing the bytes - the declared type and the file name are both the caller's to lie about.
    /// </summary>
    public const string UnsupportedImage = "unsupported-image";

    /// <summary>
    /// The listing form moves the branch - its address or map pin - and only an owner or platform
    /// admin signed in to the admin panel may. HTTP 403. Nothing on the form was saved.
    /// </summary>
    public const string RelocationNotAllowed = "relocation-not-allowed";

    /// <summary>
    /// The floor plan was saved by somebody else since the editor loaded it. HTTP 409, with
    /// <c>context.currentVersion</c>. Reload, reapply, save against the new version.
    /// </summary>
    public const string FloorPlanChanged = "floor-plan-changed";

    /// <summary>
    /// A table with a booking still to come - Confirmed or PendingApproval, not yet ended - cannot be
    /// deleted, nor left out of a floor-plan save. HTTP 409, with <c>context.tables</c> (labels) and
    /// <c>context.bookings</c> (booking codes). Move or cancel the bookings, then try again. Nothing
    /// was written.
    /// </summary>
    public const string TableHasFutureBookings = "table-has-future-bookings";

    /// <summary>
    /// A first review of a branch from a diner with no visit there in the window (K8): no Seated or
    /// Completed booking and no place on one of its tabs in the last 180 days. HTTP 403, with
    /// <c>context.branchId</c> and <c>context.windowDays</c>. Revising a review the diner already has
    /// is never refused for this.
    /// </summary>
    public const string ReviewNeedsVisit = "review-needs-visit";

    /// <summary>
    /// A change to a booking that can no longer be changed: it is not pending or confirmed - seated,
    /// finished, cancelled, a no-show - or it has already started. HTTP 409, with
    /// <c>context.reservationId</c>, <c>context.status</c> and <c>context.startUtc</c>. Checked before
    /// any rule about the new slot. Minted by <c>ReservationNotChangeableException</c>.
    /// </summary>
    /// <remarks>
    /// A 409 although it shares the <c>reservation-</c> prefix of the 422 booking refusals: nothing in
    /// the request is wrong, the booking simply is not in a state to move.
    /// </remarks>
    public const string ReservationNotChangeable = "reservation-not-changeable";

    /// <summary>
    /// A no-show recorded against a confirmed booking whose start has not come yet - through
    /// <c>no-show</c> or <c>release</c> with <c>outcome</c> 1. HTTP 409, with
    /// <c>context.reservationId</c> and <c>context.startUtc</c>, from when a no-show may be recorded.
    /// Nothing changed. Letting the booking go as the venue's doing (<c>outcome</c> 2) and releasing
    /// the table's hold are allowed at any time. Minted by <c>ReservationNotStartedException</c>.
    /// </summary>
    public const string ReservationNotStarted = "reservation-not-started";

    /// <summary>
    /// Approving or declining a booking whose chance to be answered has gone: its short-notice answer
    /// window closed, it was already declined because nobody answered in time, or - approving only -
    /// it has started. HTTP 409, with <c>context.reservationId</c>, <c>context.status</c>,
    /// <c>context.startUtc</c> and <c>context.approvalExpiresAtUtc</c> (null once declined). Minted by
    /// <c>ReservationApprovalExpiredException</c>. The tablet drops the request: the diner has been, or
    /// is about to be, told the venue did not answer in time.
    /// </summary>
    public const string ReservationApprovalExpired = "reservation-approval-expired";

    /// <summary>
    /// The table's next booking cuts a full sitting short and the booking did not accept the time to
    /// free the table by - <c>acceptsFreeByUtc</c> missing, or not that instant. HTTP 422, naming the field
    /// (<c>context.field</c> and <c>errors.acceptsFreeByUtc</c>), with <c>context.requiredFreeByUtc</c>,
    /// <c>context.requiredFreeByLocal</c> (<c>HH:mm</c>), <c>context.nextBookingStartUtc</c>,
    /// <c>context.tableId</c> and <c>context.tableLabel</c>. Ask "Will you free the table by 21:25?" and send
    /// again with <c>acceptsFreeByUtc</c> = <c>requiredFreeByUtc</c>. Minted by <c>FreeByNotAcceptedException</c>.
    /// </summary>
    public const string ReservationFreeByRequired = "reservation-free-by-required";

    /// <summary>
    /// The earlier guest answered a request for their table that no longer waits: answered the other
    /// way, past its deadline, or withdrawn because one of the bookings moved or ended. HTTP 409, with
    /// <c>context.reservationId</c>, <c>context.requestId</c>, <c>context.status</c> (the
    /// <c>TableShareRequestStatus</c>) and <c>context.expiresAtUtc</c>. Drop the card; the booking is
    /// unchanged. Minted by <c>TableRequestNotPendingException</c>.
    /// </summary>
    public const string TableRequestNotPending = "table-request-not-pending";

    /// <summary>
    /// Staff approving or declining a booking that waits on the guest before it on the table to answer
    /// in the app - theirs to answer, not the floor's. HTTP 409, with <c>context.reservationId</c>,
    /// <c>context.requestId</c> and <c>context.expiresAtUtc</c>. Minted by
    /// <c>ReservationAwaitingEarlierGuestException</c>.
    /// </summary>
    public const string ReservationAwaitingEarlierGuest = "reservation-awaiting-earlier-guest";

    /// <summary>
    /// A signed-in diner booking, or moving a booking to, a time whose sitting overlaps another of their
    /// own live bookings (pending, confirmed or seated) at the same branch. HTTP 409, with
    /// <c>context.reservationId</c>, <c>context.code</c> (its door code), <c>context.localDate</c>
    /// (<c>"2026-10-22"</c>, the branch's calendar), <c>context.localStartTime</c> (<c>"19:30:00"</c>) and
    /// <c>context.tableLabel</c> - the booking already held - so the client can say
    /// "You already have a booking at 19:30 (table 2)". Minted by <c>ReservationOverlapsOwnException</c>.
    /// </summary>
    public const string ReservationOverlapsOwn = "reservation-overlaps-own";

    /// <summary>
    /// A booking from the diner app at a branch that is not taking app bookings (K9): online bookings
    /// are switched off, or nobody has saved the branch's reservation policy. HTTP 409, with
    /// <c>context.branchId</c>. The app reads <c>acceptsAppBookings</c> to hide the button.
    /// </summary>
    public const string BookingsNotAccepted = "bookings-not-accepted";

    /// <summary>
    /// A Telegram verification challenge that cannot be claimed: unknown, past its deadline, already
    /// claimed, or claimed under a different account than it was started under. HTTP 410, and one
    /// answer for all four on purpose - the app's response is the same: start again. Minted by
    /// <c>TelegramVerificationExpiredException</c>.
    /// </summary>
    public const string TelegramVerificationExpired = "telegram-verification-expired";

    /// <summary>
    /// The PIN being set is already used by somebody else who signs in on the same tablet: the
    /// branch's own people and the venue-wide ones, or for a venue-wide person the whole venue. HTTP
    /// 409, with <c>context.field = "pin"</c>. Minted by <c>PinTakenException</c>.
    /// </summary>
    public const string PinTaken = "pin-taken";

    /// <summary>
    /// The access token names no sign-in (no refresh-chain claim), so "sign out everywhere else" cannot
    /// tell which sign-in to keep. HTTP 409; nothing changed - sign in again and retry. Minted by
    /// <c>SignInUnknownException</c>.
    /// </summary>
    public const string SignInUnknown = "sign-in-unknown";

    /// <summary>Anything unhandled. Details go to the log, never to the client. HTTP 500.</summary>
    public const string InternalError = "internal-error";

    /// <summary>
    /// The stable, human-readable title for each slug.
    /// </summary>
    /// <remarks>
    /// RFC 7807 wants <c>title</c> to describe the problem <i>type</i> and stay the same for every
    /// occurrence, while <c>detail</c> carries the specifics. Keeping the mapping here means the
    /// two cannot drift and no exception has to invent its own.
    /// </remarks>
    public static string TitleFor(string code) => code switch
    {
        InvalidRequest => "Invalid request",
        ValidationFailed => "Validation failed",
        Forbidden => "Forbidden",
        NotFound => "Not found",
        ConflictingState => "Conflicting state",
        ConcurrentUpdate => "Concurrent update",
        TableStateConflict => "Table state conflict",
        PreconditionFailed => "Precondition failed",
        InvalidTableTransition => "Invalid table transition",
        TableAlreadyBooked => "Table already booked",
        TableCurrentlyOccupied => "Table currently occupied",
        MenuItemUnavailable => "Item unavailable",
        TabNotAcceptingOrders => "Tab not accepting orders",
        TableNotSeated => "Nobody seated at this table",
        LineAlreadyPaid => "Line already paid for",
        PaymentExceedsRemaining => "Payment exceeds the balance",
        ServiceRequestRateLimited => "Too many requests from this table",
        ReservationLockTimeout => "Reservation lock timeout",
        TableLockTimeout => "Table busy",
        ClientCommandIdInUse => "Command id already used",
        RateLimited => "Rate limited",
        Unauthenticated => "Not authenticated",
        SessionRevoked => "Session revoked",
        TooManyAttempts => "Too many attempts",
        SmsUnavailable => "Codes by SMS unavailable",
        AccountLocked => "Account locked",
        FeatureNotEnabled => "Feature not enabled for this branch",
        BranchUnavailable => "Branch not open for business",
        VenueDeletionBlocked => "Venue cannot be deleted",
        FloorPlanInvalid => "Floor plan invalid",
        BranchNotReady => "Branch is not ready for diners",
        ReportRangeTooLong => "Report range too long",
        HoldNotActive => "No table being held yet",
        HoldAlreadyExtended => "Hold already extended",
        ExtensionsNotOffered => "Hold extensions not offered",
        BookingNotFound => "Booking not found",
        BookingTooEarly => "Too early for this booking",
        BookingEnded => "Booking has ended",
        BookingNotActive => "Booking not active",
        BookingTableOccupied => "Booked table still occupied",
        InvalidCredentials => "Invalid credentials",
        UsernameTaken => "Username taken",
        EmailTaken => "Email address taken",
        PhoneInUse => "Phone number already has an account",
        PhoneNotVerified => "Phone number not verified",
        PhoneHasActiveBooking => "This number holds as many bookings as it can",
        BookingBlockedNoShows => "Booking paused after missed bookings",
        NoShowUndoWindowClosed => "Too late to withdraw this no-show",
        ReservationCancelledByBlock => "Cancelled: the guest is blocked for no-shows",
        PreOrderLocked => "Pre-order closed",
        PreOrderAlreadySent => "Pre-order already sent to the kitchen",
        PreOrdersNotAccepted => "Pre-orders not accepted",
        PreOrderNotSendable => "Pre-order cannot be sent now",
        PreOrderItemsUnavailable => "Dishes cannot be pre-ordered",
        OnMyWayNotOpen => "Booking not expecting you",
        AskOnMyWayNotOpen => "Booking not expecting anybody",
        AskOnMyWayTooEarly => "Too early to ask",
        AskOnMyWayAskedRecently => "Asked a moment ago",
        GuestAlreadyAnswered => "The guest has answered",
        TakeawayNotAccepted => "Takeaway not offered",
        TakeawayClosed => "Takeaway closed now",
        TakeawayBusy => "Kitchen busy",
        TakeawaySlotUnavailable => "Pickup time unavailable",
        TakeawayItemsUnavailable => "Dishes not for takeaway",
        TakeawayStatusConflict => "Order has moved on",
        TakeawayNoShowTooEarly => "Too early for a no-show",
        TabAwaitingAdmission => "Waiting for a waiter",
        TabAdmissionDecided => "Already decided",
        ScanTurnedAway => "Ask a waiter",
        UnsupportedImage => "Unsupported image",
        RelocationNotAllowed => "Relocation not allowed",
        FloorPlanChanged => "Floor plan changed",
        TableHasFutureBookings => "Table has upcoming bookings",
        ReviewNeedsVisit => "Review needs a visit",
        BookingsNotAccepted => "Bookings not accepted",
        ReservationNotChangeable => "Booking can no longer be changed",
        ReservationNotStarted => "Booking has not started yet",
        ReservationApprovalExpired => "Too late to answer this booking",
        ReservationFreeByRequired => "Free the table by the time required",
        TableRequestNotPending => "This request no longer needs an answer",
        ReservationAwaitingEarlierGuest => "Waiting for the guest before to answer",
        ReservationOverlapsOwn => "You already have a booking then",
        TelegramVerificationExpired => "Telegram verification no longer available",
        PinTaken => "PIN already in use on this tablet",
        SignInUnknown => "Sign-in not identified",
        InternalError => "Internal error",

        // Auth reason codes are minted by the domain and are already kebab-case sentences of a
        // sort. Turning the slug into a title beats maintaining a second list that goes stale.
        _ => Humanise(code),
    };

    /// <summary>The documentation URI for a slug, used as the problem document's <c>type</c>.</summary>
    public static string TypeFor(string code) => $"https://docs.yalla.app/errors/{code}";

    private static string Humanise(string code)
    {
        var words = code.Replace('-', ' ');

        return words.Length == 0 ? code : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
