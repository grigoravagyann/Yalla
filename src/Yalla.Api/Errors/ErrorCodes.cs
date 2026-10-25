using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;
using Yalla.Domain.Venues;

namespace Yalla.Api.Errors;

/// <summary>
/// The stable slugs clients branch on. Add to this list; never reword an existing value, because
/// a deployed app is matching on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The single source of every problem code.</b> Each constant carries a <see cref="ProblemAttribute"/>
/// - its status, its title, a one-line meaning - and a <see cref="ProblemContextAttribute"/> per member
/// of its <c>context</c>. <see cref="ProblemCatalogue"/> reads them; the exception mapper, the
/// <c>x-problem-codes</c> extension of the OpenAPI document (which the frontend generates its code
/// constants and context types from) and the catalogue table in <c>docs/error-contract.md</c> all come
/// from there. <c>ProblemCatalogueTests</c> fails on a code thrown anywhere that is not declared here.
/// </para>
/// <para>
/// Codes minted by an exception of their own - the booking rules, the sign-in reasons - are declared
/// here too, under the same slug the exception carries, because the domain cannot see this assembly.
/// </para>
/// </remarks>
public static class ErrorCodes
{
    /// <summary>The request violated a domain rule or arrived malformed. HTTP 400.</summary>
    [Problem(400, "The request violated a domain rule or arrived malformed; context names the field when one is to blame.", Title = "Invalid request")]
    [ProblemContext("field", typeof(string))]
    [ProblemContext("value", typeof(object), Optional = true)]
    public const string InvalidRequest = "invalid-request";

    /// <summary>Field-level validation failed. HTTP 422.</summary>
    [Problem(422, "Field-level validation failed; every offending field is named.", Title = "Validation failed")]
    [ProblemContext("field", typeof(string))]
    [ProblemContext("fields", typeof(FieldViolationShape[]))]
    public const string ValidationFailed = "validation-failed";

    /// <summary>The caller is not allowed to do this. HTTP 403.</summary>
    [Problem(403, "The caller is not allowed to do this.", Title = "Forbidden")]
    [ProblemContext("operation", typeof(string), Optional = true)]
    [ProblemContext("requiredRole", typeof(StaffRole), Optional = true)]
    [ProblemContext("requirement", typeof(string), Optional = true)]
    [ProblemContext("subject", typeof(string), Optional = true)]
    public const string Forbidden = "forbidden";

    /// <summary>The thing addressed does not exist. HTTP 404.</summary>
    [Problem(404, "The thing addressed does not exist; a staff order's missing guest is named in context.", Title = "Not found")]
    [ProblemContext("field", typeof(string))]
    [ProblemContext("participantId", typeof(Guid))]
    public const string NotFound = "not-found";

    /// <summary>
    /// The operation is not legal in the current state - seating a cancelled booking, paying a
    /// closed tab. HTTP 409.
    /// </summary>
    [Problem(409, "The operation is not legal in the current state; a staff order's unorderable guest is named in context.", Title = "Conflicting state")]
    [ProblemContext("field", typeof(string))]
    [ProblemContext("participantId", typeof(Guid))]
    [ProblemContext("participantStatus", typeof(ParticipantStatus))]
    public const string ConflictingState = "conflicting-state";

    /// <summary>
    /// Someone else changed the row first. The client should re-read and retry, and for tables,
    /// tabs and reservations this is a normal outcome rather than a bug: two waiters really do
    /// tap the same table at the same instant. HTTP 409.
    /// </summary>
    [Problem(409, "Someone else changed the row first: re-read and retry.", Title = "Concurrent update")]
    [ProblemContext("currentVersion", typeof(string))]
    public const string ConcurrentUpdate = "concurrent-update";

    /// <summary>
    /// Somebody else changed this table first. HTTP 409, with the table's current state in
    /// <c>details</c> so the client can redraw it. Never retried automatically: retrying would
    /// seat a walk-in at a table the diner's booking just took.
    /// </summary>
    [Problem(409, "Somebody else changed this table first; redraw it from the current state.", Title = "Table state conflict")]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("attemptedFromStatus", typeof(TableStatus))]
    [ProblemContext("currentStatus", typeof(TableStatus))]
    [ProblemContext("currentSessionId", typeof(Guid?))]
    public const string TableStateConflict = "table-state-conflict";

    /// <summary>
    /// The requested transition is not one the state machine allows - freeing an empty table,
    /// marking an occupied one broken. HTTP 422, with the legal transitions in <c>details</c>.
    /// </summary>
    [Problem(422, "The table cannot make that transition from its current status.", Title = "Invalid table transition")]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("fromStatus", typeof(TableStatus))]
    [ProblemContext("attemptedToStatus", typeof(TableStatus))]
    [ProblemContext("allowedFromHere", typeof(TableStatus[]))]
    public const string InvalidTableTransition = "invalid-table-transition";

    /// <summary>
    /// A branch rule refused a booking. HTTP 422.
    /// </summary>
    /// <remarks>
    /// <b>Never returned as-is.</b> Every refusal answers with its own slug -
    /// <c>reservation-party-exceeds-capacity</c>, <c>reservation-outside-opening-hours</c>, and so
    /// on, one per rule, declared on the exception that carries it. This constant only names the
    /// family, because a client that cannot tell "the table seats four" from "we are shut" ends up
    /// showing "invalid booking" to somebody who needed one sentence of help. Not a code, so not in
    /// the catalogue.
    /// </remarks>
    public const string ReservationRejectedPrefix = "reservation-";

    /// <summary>
    /// The table was booked by somebody else between the diner seeing it free and confirming.
    /// HTTP 409, with the clashing window and a fresh availability snapshot in <c>details</c> so
    /// the app can redraw the floor and show what changed.
    /// </summary>
    [Problem(409, "The table was booked by somebody else between seeing it free and confirming.", Title = "Table already booked")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("requestedStartUtc", typeof(DateTime))]
    [ProblemContext("requestedEndUtc", typeof(DateTime))]
    [ProblemContext("conflictingStartUtc", typeof(DateTime))]
    [ProblemContext("conflictingEndUtc", typeof(DateTime))]
    [ProblemContext("conflictingReservationId", typeof(Guid))]
    [ProblemContext("availability", typeof(BranchAvailability), Optional = true)]
    public const string TableAlreadyBooked = "table-already-booked";

    /// <summary>
    /// A party is seated at the table now and their sitting runs into the requested slot. HTTP 409,
    /// with the seating and projected finish times in <c>details</c>. Distinct from
    /// <see cref="TableAlreadyBooked"/>: nobody booked it, and it may free up early.
    /// </summary>
    [Problem(409, "A party seated at the table now runs into the requested slot; it may free up early.", Title = "Table currently occupied")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("requestedStartUtc", typeof(DateTime))]
    [ProblemContext("requestedEndUtc", typeof(DateTime))]
    [ProblemContext("seatedAtUtc", typeof(DateTime))]
    [ProblemContext("projectedFreeAtUtc", typeof(DateTime))]
    [ProblemContext("tableSessionId", typeof(Guid))]
    [ProblemContext("availability", typeof(BranchAvailability), Nullable = true)]
    public const string TableCurrentlyOccupied = "table-currently-occupied";

    /// <summary>A dish on the order has sold out. The context names it, so the client can too.</summary>
    [Problem(409, "A dish on the order has sold out; context names it.", Title = "Item unavailable")]
    [ProblemContext("menuItemId", typeof(Guid))]
    [ProblemContext("itemName", typeof(string))]
    public const string MenuItemUnavailable = "menu-item-unavailable";

    /// <summary>The bill has been asked for; the app should show it rather than the menu.</summary>
    [Problem(409, "The tab takes no more orders: the bill has been asked for, or it is closed.", Title = "Tab not accepting orders")]
    [ProblemContext("tabId", typeof(Guid))]
    [ProblemContext("status", typeof(TabStatus))]
    public const string TabNotAcceptingOrders = "tab-not-accepting-orders";

    /// <summary>
    /// Staff tried to open a tab on a table with nobody seated at it. Seat the party first. HTTP 409,
    /// with the table's status in the context. Minted by <c>TableNotSeatedException</c>.
    /// </summary>
    [Problem(409, "Staff tried to open a tab on a table with nobody seated at it.", Title = "Nobody seated at this table")]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("status", typeof(TableStatus))]
    public const string TableNotSeated = "table-not-seated";

    /// <summary>Removing this line would reverse money already taken. That is a refund.</summary>
    [Problem(409, "Removing this line would reverse money already taken; that is a refund.", Title = "Line already paid for")]
    [ProblemContext("tabId", typeof(Guid))]
    [ProblemContext("lineId", typeof(Guid))]
    public const string LineAlreadyPaid = "line-already-paid";

    /// <summary>More was offered than is owed. The context carries the current balance.</summary>
    [Problem(409, "More was offered than the tab still owes; context carries the balance.", Title = "Payment exceeds the balance")]
    [ProblemContext("tabId", typeof(Guid))]
    [ProblemContext("requestedAmd", typeof(long))]
    [ProblemContext("remainingAmd", typeof(long))]
    public const string PaymentExceedsRemaining = "payment-exceeds-remaining";

    /// <summary>This table has asked for too many things too quickly.</summary>
    [Problem(429, "This table has asked for staff too many times too quickly.", Title = "Too many requests from this table")]
    [ProblemContext("tabId", typeof(Guid))]
    [ProblemContext("limit", typeof(int))]
    [ProblemContext("windowMinutes", typeof(int))]
    public const string ServiceRequestRateLimited = "service-request-rate-limited";

    /// <summary>
    /// The booking could not get the table's lock in time. HTTP 503 and <b>retryable</b> - which
    /// is what makes it different from <see cref="TableAlreadyBooked"/>: the question was never
    /// asked, rather than answered no. Retry with the same <c>clientCommandId</c>.
    /// </summary>
    [Problem(503, "The booking could not get the table's lock in time; retry with the same clientCommandId.", Title = "Reservation lock timeout")]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("timeoutMilliseconds", typeof(int))]
    [ProblemContext("retryable", typeof(bool))]
    public const string ReservationLockTimeout = "reservation-lock-timeout";

    /// <summary>
    /// A table write waited too long for another writer. Retryable, and deliberately not a
    /// conflict: nothing was decided, so the same request will very likely succeed.
    /// </summary>
    [Problem(503, "A table write waited too long for another writer; retry, nothing was decided.", Title = "Table busy")]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("timeoutMilliseconds", typeof(int))]
    [ProblemContext("retryable", typeof(bool))]
    public const string TableLockTimeout = "table-lock-timeout";

    /// <summary>
    /// The <c>clientCommandId</c> on a booking request already belongs to another caller's booking.
    /// HTTP 409. A client bug rather than a retry: generate one per booking, and reuse it only when
    /// retrying that same booking.
    /// </summary>
    [Problem(409, "The clientCommandId already belongs to another caller's command; start again with a fresh one.", Title = "Command id already used")]
    public const string ClientCommandIdInUse = "client-command-id-in-use";

    /// <summary>Too many requests in the window. HTTP 429.</summary>
    [Problem(429, "Too many requests in the window; wait for Retry-After.", Title = "Rate limited")]
    public const string RateLimited = "rate-limited";

    /// <summary>
    /// No usable credential was presented, or the one presented was rejected. HTTP 401.
    /// </summary>
    /// <remarks>
    /// The specific sign-in failures replace this with their own slug - <c>pin-invalid</c>,
    /// <c>verification-code-expired</c>, <c>refresh-token-reused</c> and so on - because those are
    /// things the caller is entitled to know about their own credential. None of them ever
    /// distinguishes "no such account" from "wrong secret". A route refusing a missing token answers
    /// a bare 401 with no body, so this slug is declared for clients and never sent today.
    /// </remarks>
    [Problem(401, "No usable credential was presented (declared for clients; a bare 401 carries no body).", Title = "Not authenticated")]
    public const string Unauthenticated = "unauthenticated";

    /// <summary>
    /// A diner token from a session that has ended. HTTP 401, with
    /// <c>WWW-Authenticate: Bearer error="invalid_token"</c>. The account was deactivated or deleted,
    /// or its session generation moved on: the password was set or changed, or the number's owner
    /// proved it and displaced a registrant. The client tries its refresh token once; when that is
    /// refused too, it signs out.
    /// </summary>
    [Problem(401, "The token's session has ended; refresh once, then sign out.", Title = "Session revoked")]
    public const string SessionRevoked = "session-revoked";

    /// <summary>
    /// A one-time credential is out of attempts, or a per-identity limit is spent. HTTP 429.
    /// Asking again immediately will not help; ask for a new code.
    /// </summary>
    [Problem(429, "A one-time credential is out of attempts, or a per-identity limit is spent; ask for a new code.", Title = "Too many attempts")]
    [ProblemContext("retryAfterSeconds", typeof(int))]
    public const string TooManyAttempts = "too-many-attempts";

    /// <summary>
    /// No SMS provider is configured, so no one-time code can be sent. HTTP 503. Not retryable:
    /// prove the number with Telegram instead. <c>GET /api/auth/diner/phone-proof</c> says so in
    /// advance, so a client that reads it never meets this.
    /// </summary>
    [Problem(503, "No SMS provider is configured, so no code can be sent; prove the number with Telegram.", Title = "Codes by SMS unavailable")]
    public const string SmsUnavailable = "sms-unavailable";

    /// <summary>
    /// The account is locked, not the credential wrong. HTTP 403, with <c>lockedUntilUtc</c> in
    /// <c>context</c>. For a staff PIN, the fix is a manager clearing it rather than trying again.
    /// </summary>
    [Problem(403, "The account is locked, not the credential wrong.", Title = "Account locked")]
    [ProblemContext("lockedUntilUtc", typeof(DateTime?))]
    public const string AccountLocked = "account-locked";

    /// <summary>
    /// The branch is on a tier that does not include this feature - tabs on a Free branch. HTTP
    /// 409, not 403: the caller is allowed here, the branch has not paid for it. <c>context</c>
    /// carries <c>branchId</c> and <c>currentTier</c>.
    /// </summary>
    [Problem(409, "The branch's tier does not include this feature.", Title = "Feature not enabled for this branch")]
    [ProblemContext("feature", typeof(string))]
    [ProblemContext("branchId", typeof(Guid))]
    [ProblemContext("currentTier", typeof(SubscriptionTier))]
    public const string FeatureNotEnabled = "feature-not-enabled";

    /// <summary>
    /// The venue has open tabs or future bookings and cannot be deleted. HTTP 409, with the
    /// blockers named in <c>context</c>.
    /// </summary>
    [Problem(409, "The venue has open tabs or future bookings and cannot be deleted.", Title = "Venue cannot be deleted")]
    [ProblemContext("openTabs", typeof(string[]))]
    [ProblemContext("futureReservations", typeof(string[]))]
    public const string VenueDeletionBlocked = "venue-deletion-blocked";

    /// <summary>
    /// The floor plan cannot be applied: missing or repeated table labels or room names, or a table
    /// matched twice. HTTP 422, with every reason in <c>context.errors</c>.
    /// </summary>
    [Problem(422, "The floor plan cannot be applied; every reason is listed.", Title = "Floor plan invalid")]
    [ProblemContext("errors", typeof(string[]))]
    [ProblemContext("duplicateLabels", typeof(string[]))]
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
    [Problem(409, "The branch's menu has unfinished dishes, so it cannot be switched to Paid.", Title = "Branch is not ready for diners")]
    [ProblemContext("branchId", typeof(Guid))]
    [ProblemContext("incompleteMenuItemCount", typeof(int))]
    public const string BranchNotReady = "branch-not-ready";

    /// <summary>
    /// The branch is not open for business: its venue is suspended or deleted, or the branch is
    /// switched off. HTTP 409. Distinct from <see cref="FeatureNotEnabled"/>, which is about what a
    /// branch pays for rather than whether it is trading at all.
    /// </summary>
    [Problem(409, "The branch is not open for business: venue suspended or deleted, or branch switched off.", Title = "Branch not open for business")]
    [ProblemContext("branchId", typeof(Guid))]
    [ProblemContext("reason", typeof(string))]
    public const string BranchUnavailable = "branch-unavailable";

    /// <summary>
    /// The branch does not take bookings from its public page. The public page branches on
    /// this to say "call the venue" rather than "something went wrong".
    /// </summary>
    [Problem(409, "The branch does not take bookings from its public page.")]
    [ProblemContext("branchId", typeof(Guid))]
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
    [Problem(409, "A queued command describes a table that has since moved; resolve it from the conflict list.", Title = "Precondition failed")]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("expectedFromStatus", typeof(TableStatus))]
    [ProblemContext("currentStatus", typeof(TableStatus))]
    [ProblemContext("failure", typeof(PreconditionFailure))]
    [ProblemContext("clientCommandId", typeof(Guid))]
    public const string PreconditionFailed = "precondition-failed";

    /// <summary>
    /// A free that named a booking found another sitting - or none - open at the table. HTTP 409, with
    /// <c>tableId</c>, <c>tableLabel</c>, <c>reservationId</c>, <c>tableStatus</c> and, when somebody is
    /// sitting there, <c>currentSessionId</c> in <c>context</c>. The console's stale Overview row: refresh it.
    /// </summary>
    [Problem(409, "A free that named a booking found another sitting, or none, open at the table.")]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("tableStatus", typeof(TableStatus))]
    [ProblemContext("currentSessionId", typeof(Guid?))]
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
    [Problem(400, "The report range is longer than any report will run.", Title = "Report range too long")]
    [ProblemContext("field", typeof(string))]
    [ProblemContext("requestedDays", typeof(int))]
    [ProblemContext("maxDays", typeof(int))]
    public const string ReportRangeTooLong = "report-range-too-long";

    /// <summary>
    /// "Keep my table" with no table being held: the booking has not started, or is not a confirmed
    /// one. HTTP 409, with <c>reservationId</c> and <c>startUtc</c> in <c>context</c>. Offer the button
    /// from the late nudge, or once the start has passed. Minted by <c>HoldExtensionRefusedException</c>.
    /// </summary>
    [Problem(409, "\"Keep my table\" with no table being held yet.", Title = "No table being held yet")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("startUtc", typeof(DateTime))]
    public const string HoldNotActive = "hold-not-active";

    /// <summary>The booking's one hold extension is already used. HTTP 409.</summary>
    [Problem(409, "The booking's one hold extension is already used.", Title = "Hold already extended")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("startUtc", typeof(DateTime))]
    public const string HoldAlreadyExtended = "hold-already-extended";

    /// <summary>
    /// The branch offers no hold extensions. HTTP 409. Not the diner's doing, so never "you have
    /// already let them know": say to speak to the venue.
    /// </summary>
    [Problem(409, "The branch offers no hold extensions.", Title = "Hold extensions not offered")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("startUtc", typeof(DateTime))]
    public const string ExtensionsNotOffered = "extensions-not-offered";

    /// <summary>
    /// "I'm at my table" with a code none of the caller's bookings carries - whether or not somebody
    /// else's does. HTTP 404 with no <c>context</c>, and word for word the same for both, because the
    /// code is read out at the door and proves nothing. Minted from <c>BookingNotFoundException</c>.
    /// </summary>
    [Problem(404, "None of the caller's bookings carries that code.", Title = "Booking not found")]
    public const string BookingNotFound = "booking-not-found";

    /// <summary>
    /// The booking is confirmed but its table is not being held for the party yet. HTTP 409, with
    /// <c>context.earliestUtc</c> - the branch's walk-in holdback before the start - to say when it
    /// will be. Minted by <c>BookingTabRefusedException</c>, like the two below.
    /// </summary>
    [Problem(409, "The booking's table is not being held for the party yet; context says from when.", Title = "Too early for this booking")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("status", typeof(ReservationStatus))]
    [ProblemContext("startUtc", typeof(DateTime))]
    [ProblemContext("endUtc", typeof(DateTime))]
    [ProblemContext("earliestUtc", typeof(DateTime))]
    [ProblemContext("tableLabel", typeof(string), Nullable = true)]
    public const string BookingTooEarly = "booking-too-early";

    /// <summary>The booking is over: past its end, or its sitting has finished. HTTP 409.</summary>
    [Problem(409, "The booking is over: past its end, or its sitting has finished.", Title = "Booking has ended")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("status", typeof(ReservationStatus))]
    [ProblemContext("startUtc", typeof(DateTime))]
    [ProblemContext("endUtc", typeof(DateTime))]
    [ProblemContext("earliestUtc", typeof(DateTime))]
    [ProblemContext("tableLabel", typeof(string), Nullable = true)]
    public const string BookingEnded = "booking-ended";

    /// <summary>
    /// The booking is not expecting its party - waiting for approval, cancelled, or released as a
    /// no-show. HTTP 409, with <c>context.status</c> saying which.
    /// </summary>
    [Problem(409, "The booking is not expecting its party: awaiting approval, cancelled, or a no-show.", Title = "Booking not active")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("status", typeof(ReservationStatus))]
    [ProblemContext("startUtc", typeof(DateTime))]
    [ProblemContext("endUtc", typeof(DateTime))]
    [ProblemContext("earliestUtc", typeof(DateTime))]
    [ProblemContext("tableLabel", typeof(string), Nullable = true)]
    public const string BookingNotActive = "booking-not-active";

    /// <summary>
    /// The booked table still has another party's sitting open - not this booking's - so the booker
    /// is not put on that tab. HTTP 409, with the booking's facts. A member of staff frees the table.
    /// </summary>
    [Problem(409, "The booked table still has another party's sitting open; staff must free it.", Title = "Booked table still occupied")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("status", typeof(ReservationStatus))]
    [ProblemContext("startUtc", typeof(DateTime))]
    [ProblemContext("endUtc", typeof(DateTime))]
    [ProblemContext("earliestUtc", typeof(DateTime))]
    [ProblemContext("tableLabel", typeof(string), Nullable = true)]
    public const string BookingTableOccupied = "booking-table-occupied";

    /// <summary>
    /// A diner's username, email or password did not match a usable account. HTTP 401. Unknown
    /// identifier, wrong password, an account with no password and a deactivated account all
    /// arrive as this one code, so the sign-in form cannot be used to discover which exist.
    /// </summary>
    [Problem(401, "A diner's identifier and password, or current password, did not match a usable account.", Title = "Invalid credentials")]
    public const string InvalidCredentials = "invalid-credentials";

    /// <summary>
    /// The username on a diner sign-up or profile edit is somebody else's. HTTP 409, with
    /// <c>context.field</c> naming the input. Minted by <c>DinerIdentifierTakenException</c>, like
    /// the two below.
    /// </summary>
    [Problem(409, "The username on a diner sign-up or profile edit is somebody else's.", Title = "Username taken")]
    [ProblemContext("field", typeof(string))]
    public const string UsernameTaken = "username-taken";

    /// <summary>The email address on a diner sign-up or profile edit is somebody else's. HTTP 409.</summary>
    [Problem(409, "The email address on a diner sign-up or profile edit is somebody else's.", Title = "Email address taken")]
    [ProblemContext("field", typeof(string))]
    public const string EmailTaken = "email-taken";

    /// <summary>
    /// The phone number on a diner sign-up already has an account. HTTP 409. The app's answer is
    /// "log in with a code instead": that account's owner can prove the number, a stranger cannot.
    /// </summary>
    [Problem(409, "The phone number on a diner sign-up already has an account; log in with a code instead.", Title = "Phone number already has an account")]
    [ProblemContext("field", typeof(string))]
    public const string PhoneInUse = "phone-in-use";

    /// <summary>
    /// The diner's phone number has never been proved by a one-time code, and the request would
    /// land on it: creating a booking, extending its hold, opening a tab from it. HTTP 403. The
    /// app's answer is the verify link into the code flow. Minted by
    /// <c>PhoneNotVerifiedException</c>, from the stored account row rather than the token.
    /// </summary>
    [Problem(403, "The diner's phone number has never been proved, and the request would land on it.", Title = "Phone number not verified")]
    public const string PhoneNotVerified = "phone-not-verified";

    /// <summary>
    /// The booker's proved number already holds three active bookings (pending or confirmed, not over) at
    /// any venue - the most one number holds. HTTP 409, <c>context.maxActiveBookings</c> and the caller's own
    /// earliest active booking. Minted by <c>PhoneHasActiveBookingException</c>.
    /// </summary>
    [Problem(409, "The booker's proved number already holds as many active bookings as one number may.", Title = "This number holds as many bookings as it can")]
    [ProblemContext("maxActiveBookings", typeof(int))]
    [ProblemContext("reservationId", typeof(Guid), Optional = true)]
    [ProblemContext("code", typeof(string), Optional = true)]
    [ProblemContext("startUtc", typeof(DateTime), Optional = true)]
    [ProblemContext("localDate", typeof(DateOnly), Optional = true)]
    [ProblemContext("localStartTime", typeof(TimeOnly), Optional = true)]
    [ProblemContext("venueName", typeof(string), Optional = true)]
    [ProblemContext("branchName", typeof(string), Optional = true)]
    public const string PhoneHasActiveBooking = "phone-has-active-booking";

    /// <summary>
    /// The guest missed three bookings in three days and cannot book until <c>context.blockedUntilUtc</c>.
    /// HTTP 403. Minted by <c>BookingBlockedForNoShowsException</c>.
    /// </summary>
    [Problem(403, "The guest missed three bookings in three days and cannot book until the block ends.", Title = "Booking paused after missed bookings")]
    [ProblemContext("blockedUntilUtc", typeof(DateTime))]
    public const string BookingBlockedNoShows = "booking-blocked-no-shows";

    /// <summary>
    /// A no-show can no longer be withdrawn: its service day ended (05:00 the next morning, branch time). HTTP 409,
    /// <c>context.undoableUntilUtc</c>. Minted by <c>NoShowUndoWindowClosedException</c>.
    /// </summary>
    [Problem(409, "A no-show can no longer be withdrawn: its service day ended.", Title = "Too late to withdraw this no-show")]
    [ProblemContext("undoableUntilUtc", typeof(DateTime))]
    public const string NoShowUndoWindowClosed = "no-show-undo-window-closed";

    /// <summary>
    /// Approving or rejecting a booking a no-show block cancelled. HTTP 409, <c>context.reservationId</c> and
    /// <c>cancelledAtUtc</c>. Minted by <c>ReservationCancelledByBlockException</c>.
    /// </summary>
    [Problem(409, "Approving or rejecting a booking its guest's no-show block cancelled.", Title = "Cancelled: the guest is blocked for no-shows")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("cancelledAtUtc", typeof(DateTime?))]
    public const string ReservationCancelledByBlock = "reservation-cancelled-by-block";

    /// <summary>
    /// The pre-order can no longer be changed by the guest: it is two hours before the start or later. HTTP 409,
    /// <c>context.reservationId</c> and <c>lockedAtUtc</c>. Minted by <c>PreOrderLockedException</c>.
    /// </summary>
    [Problem(409, "The pre-order can no longer be changed by the guest: two hours before the start or later.", Title = "Pre-order closed")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("lockedAtUtc", typeof(DateTime))]
    public const string PreOrderLocked = "pre-order-locked";

    /// <summary>
    /// The kitchen already has the pre-order: it cannot be edited, cleared, marked not coming, or its booking moved.
    /// HTTP 409, <c>context.reservationId</c> and <c>sentAtUtc</c>. Minted by <c>PreOrderAlreadySentException</c>.
    /// </summary>
    [Problem(409, "The kitchen already has the pre-order, so it no longer changes.", Title = "Pre-order already sent to the kitchen")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("sentAtUtc", typeof(DateTime))]
    public const string PreOrderAlreadySent = "pre-order-already-sent";

    /// <summary>The branch does not take pre-orders. HTTP 409, <c>context.branchId</c>. Minted by <c>PreOrdersNotAcceptedException</c>.</summary>
    [Problem(409, "The branch does not take pre-orders.", Title = "Pre-orders not accepted")]
    [ProblemContext("branchId", typeof(Guid))]
    public const string PreOrdersNotAccepted = "pre-orders-not-accepted";

    /// <summary>
    /// Not the time or the state to send a pre-order to the kitchen, or mark the party not coming. HTTP 409,
    /// <c>context</c> <c>reservationId</c>, <c>status</c>, <c>sendableFromUtc</c>, <c>endUtc</c>. Minted by
    /// <c>PreOrderNotSendableException</c>.
    /// </summary>
    [Problem(409, "Not the time or the state to send a pre-order to the kitchen, or mark the party not coming.", Title = "Pre-order cannot be sent now")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("status", typeof(ReservationStatus))]
    [ProblemContext("sendableFromUtc", typeof(DateTime))]
    [ProblemContext("endUtc", typeof(DateTime))]
    public const string PreOrderNotSendable = "pre-order-not-sendable";

    /// <summary>
    /// Dishes that cannot be pre-ordered. HTTP 422, <c>context.items[]</c> = <c>{ menuItemId, reason }</c> (1 not on the
    /// menu, 2 sold out, 3 excluded from pre-orders). Minted by <c>PreOrderItemsUnavailableException</c>.
    /// </summary>
    [Problem(422, "Dishes that cannot be pre-ordered, every one named with why.", Title = "Dishes cannot be pre-ordered")]
    [ProblemContext("items", typeof(PreOrderItemRefusalShape[]))]
    public const string PreOrderItemsUnavailable = "pre-order-items-unavailable";

    /// <summary>
    /// "On my way" for a booking that is not confirmed or seated, is more than two hours away, or is over. HTTP 409,
    /// <c>context.reservationId</c>, <c>status</c> and <c>opensAtUtc</c> (start - 2 h). Minted by
    /// <c>OnMyWayNotOpenException</c>.
    /// </summary>
    [Problem(409, "\"On my way\" for a booking that is not expecting anybody now.", Title = "Booking not expecting you")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("status", typeof(ReservationStatus))]
    [ProblemContext("opensAtUtc", typeof(DateTime))]
    public const string OnMyWayNotOpen = "on-my-way-not-open";

    /// <summary>
    /// "Ask: are you coming?" for a booking that is not confirmed, or has started. HTTP 409, <c>context.reservationId</c>,
    /// <c>status</c>, <c>startUtc</c>. Minted by <c>AskOnMyWayNotOpenException</c>.
    /// </summary>
    [Problem(409, "\"Ask: are you coming?\" for a booking that is not confirmed, or has started.", Title = "Booking not expecting anybody")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("status", typeof(ReservationStatus))]
    [ProblemContext("startUtc", typeof(DateTime))]
    public const string AskOnMyWayNotOpen = "ask-on-my-way-not-open";

    /// <summary>
    /// "Ask: are you coming?" more than two hours before the start. HTTP 409, <c>context.reservationId</c> and
    /// <c>opensAtUtc</c>. Minted by <c>AskOnMyWayTooEarlyException</c>.
    /// </summary>
    [Problem(409, "\"Ask: are you coming?\" more than two hours before the start.", Title = "Too early to ask")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("opensAtUtc", typeof(DateTime))]
    public const string AskOnMyWayTooEarly = "ask-on-my-way-too-early";

    /// <summary>
    /// "Ask: are you coming?" within fifteen minutes of the last ask. HTTP 409, <c>context.reservationId</c>,
    /// <c>askedAtUtc</c>, <c>nextAllowedAtUtc</c>. Minted by <c>AskOnMyWayAskedRecentlyException</c>.
    /// </summary>
    [Problem(409, "\"Ask: are you coming?\" within fifteen minutes of the last ask.", Title = "Asked a moment ago")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("askedAtUtc", typeof(DateTime))]
    [ProblemContext("nextAllowedAtUtc", typeof(DateTime))]
    public const string AskOnMyWayAskedRecently = "ask-on-my-way-asked-recently";

    /// <summary>
    /// "Ask: are you coming?" when the guest already said on my way or running late. HTTP 409,
    /// <c>context.reservationId</c>, <c>answeredAtUtc</c>, <c>runningLateMinutes</c> (absent for "on my way"). Minted by
    /// <c>GuestAlreadyAnsweredException</c>.
    /// </summary>
    [Problem(409, "\"Ask: are you coming?\" when the guest already said on my way or running late.", Title = "The guest has answered")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("answeredAtUtc", typeof(DateTime))]
    [ProblemContext("runningLateMinutes", typeof(int), Optional = true)]
    public const string GuestAlreadyAnswered = "guest-already-answered";

    /// <summary>The branch does not take takeaway orders. HTTP 409, <c>context.branchId</c>. Minted by <c>TakeawayNotAcceptedException</c>.</summary>
    [Problem(409, "The branch does not take takeaway orders.", Title = "Takeaway not offered")]
    [ProblemContext("branchId", typeof(Guid))]
    public const string TakeawayNotAccepted = "takeaway-not-accepted";

    /// <summary>
    /// Closed for takeaway now - shut, or past the last order. HTTP 409, <c>context.branchId</c>, <c>opensAtUtc</c> and
    /// <c>opensAt</c> (local <c>HH:mm</c>) when it opens again within a week. Minted by <c>TakeawayClosedException</c>.
    /// </summary>
    [Problem(409, "Closed for takeaway now: shut, or past the last order.", Title = "Takeaway closed now")]
    [ProblemContext("branchId", typeof(Guid))]
    [ProblemContext("opensAtUtc", typeof(DateTime), Optional = true)]
    [ProblemContext("opensAt", typeof(string), Optional = true, Nullable = true)]
    public const string TakeawayClosed = "takeaway-closed";

    /// <summary>
    /// ASAP while the kitchen has as many takeaway orders in progress as the venue allows. HTTP 409,
    /// <c>context.branchId</c>, <c>maxInProgress</c>. Minted by <c>TakeawayBusyException</c>.
    /// </summary>
    [Problem(409, "ASAP while the kitchen has as many takeaway orders in progress as the venue allows.", Title = "Kitchen busy")]
    [ProblemContext("branchId", typeof(Guid))]
    [ProblemContext("maxInProgress", typeof(int))]
    public const string TakeawayBusy = "takeaway-busy";

    /// <summary>
    /// The pickup time is not one on offer. HTTP 409, <c>context.branchId</c>, <c>at</c>, <c>slots</c> (on offer now).
    /// Minted by <c>TakeawaySlotUnavailableException</c>.
    /// </summary>
    [Problem(409, "The pickup time asked for is not one on offer; context lists the slots on offer now.", Title = "Pickup time unavailable")]
    [ProblemContext("branchId", typeof(Guid))]
    [ProblemContext("at", typeof(string))]
    [ProblemContext("slots", typeof(string[]))]
    public const string TakeawaySlotUnavailable = "takeaway-slot-unavailable";

    /// <summary>
    /// Dishes that cannot be ordered to take away. HTTP 422, <c>context.items[]</c> = <c>{ menuItemId, reason }</c> (1 not
    /// on the menu, 2 sold out, 3 hall only). Minted by <c>TakeawayItemsUnavailableException</c>.
    /// </summary>
    [Problem(422, "Dishes that cannot be ordered to take away, every one named with why.", Title = "Dishes not for takeaway")]
    [ProblemContext("items", typeof(TakeawayItemRefusalShape[]))]
    public const string TakeawayItemsUnavailable = "takeaway-items-unavailable";

    /// <summary>
    /// The takeaway order is not in the state for that move. HTTP 409, <c>context.orderId</c> and <c>status</c> (where it
    /// is now). Minted by <c>TakeawayStatusConflictException</c>.
    /// </summary>
    [Problem(409, "The takeaway order is not in the state for that move; context says where it is now.", Title = "Order has moved on")]
    [ProblemContext("orderId", typeof(Guid))]
    [ProblemContext("status", typeof(TakeawayStatus))]
    public const string TakeawayStatusConflict = "takeaway-status-conflict";

    /// <summary>
    /// "No-show" before thirty minutes past ready. HTTP 409, <c>context.orderId</c> and <c>noShowFromUtc</c>. Minted by
    /// <c>TakeawayNoShowTooEarlyException</c>.
    /// </summary>
    [Problem(409, "\"No-show\" for a takeaway order before thirty minutes past ready.", Title = "Too early for a no-show")]
    [ProblemContext("orderId", typeof(Guid))]
    [ProblemContext("noShowFromUtc", typeof(DateTime))]
    public const string TakeawayNoShowTooEarly = "takeaway-no-show-too-early";

    /// <summary>
    /// Writing to a guest-message thread that has closed - two hours after its booking or order ended. HTTP 409,
    /// <c>context.subjectKind</c>, <c>subjectId</c>, <c>closedAtUtc</c>. Minted by <c>ConversationClosedException</c>.
    /// </summary>
    [Problem(409, "Writing to a guest-message thread that has closed.", Title = "Conversation closed")]
    [ProblemContext("subjectKind", typeof(string))]
    [ProblemContext("subjectId", typeof(Guid))]
    [ProblemContext("closedAtUtc", typeof(DateTime))]
    public const string ConversationClosed = "conversation-closed";

    /// <summary>
    /// A guest's typed message where the venue switched them off; the answer buttons still work. HTTP 403,
    /// <c>context.branchId</c>. Minted by <c>GuestsCannotWriteException</c>.
    /// </summary>
    [Problem(403, "A guest's typed message where the venue switched guest messages off.", Title = "This venue takes no written messages")]
    [ProblemContext("branchId", typeof(Guid))]
    public const string GuestsCannotWrite = "guests-cannot-write";

    /// <summary>
    /// A waiter writing to a guest where the venue keeps messages to managers. HTTP 403, <c>context.branchId</c>. Minted
    /// by <c>WaitersCannotWriteException</c>.
    /// </summary>
    [Problem(403, "A waiter writing to a guest where the venue keeps messages to managers.", Title = "Only managers write to guests here")]
    [ProblemContext("branchId", typeof(Guid))]
    public const string WaitersCannotWrite = "waiters-cannot-write";

    /// <summary>
    /// A guest writing more than 5 messages a minute or 60 in one thread. HTTP 429, <c>context.conversationId</c>,
    /// <c>perMinute</c>, <c>perConversation</c>, and <c>retryAfterUtc</c> (with a <c>Retry-After</c> header) when waiting
    /// helps. Minted by <c>MessagesTooFastException</c>.
    /// </summary>
    [Problem(429, "A guest writing more than five messages a minute, or sixty in one thread.", Title = "Messages too fast")]
    [ProblemContext("conversationId", typeof(Guid))]
    [ProblemContext("perMinute", typeof(int))]
    [ProblemContext("perConversation", typeof(int))]
    [ProblemContext("retryAfterUtc", typeof(DateTime), Optional = true)]
    public const string MessagesTooFast = "messages-too-fast";

    /// <summary>
    /// The tab was opened by a scan with no proved number and waits for a waiter to let it order. HTTP
    /// 409, <c>context.tabId</c>. Minted by <c>TabAwaitingAdmissionException</c>.
    /// </summary>
    [Problem(409, "The tab waits for a waiter to let it order.", Title = "Waiting for a waiter")]
    [ProblemContext("tabId", typeof(Guid))]
    public const string TabAwaitingAdmission = "tab-awaiting-admission";

    /// <summary>
    /// Admitting a tab already turned away, or turning away one already admitted. HTTP 409,
    /// <c>context.tabId</c> and <c>context.admission</c>. Minted by <c>TabAdmissionDecidedException</c>.
    /// </summary>
    [Problem(409, "Admitting a tab already turned away, or turning away one already admitted.", Title = "Already decided")]
    [ProblemContext("tabId", typeof(Guid))]
    [ProblemContext("admission", typeof(TabAdmission))]
    public const string TabAdmissionDecided = "tab-admission-decided";

    /// <summary>
    /// A scan refused for a while after a waiter turned a tab away - from a phone that was on it, or of the
    /// table it was at. HTTP 409, <c>context.retryAfterUtc</c>. Minted by <c>ScanTurnedAwayException</c>.
    /// </summary>
    [Problem(409, "A scan refused for a while after a waiter turned this phone or table away.", Title = "Ask a waiter")]
    [ProblemContext("retryAfterUtc", typeof(DateTime))]
    public const string ScanTurnedAway = "scan-turned-away";

    /// <summary>
    /// An upload is not a JPEG, PNG or WebP, or is too large to store. HTTP 409, with
    /// <c>context.detectedFormat</c> when the bytes were an image of some other kind. Decided by
    /// sniffing the bytes - the declared type and the file name are both the caller's to lie about.
    /// </summary>
    [Problem(409, "An upload is not a JPEG, PNG or WebP, or is too large to store.", Title = "Unsupported image")]
    [ProblemContext("detectedFormat", typeof(string))]
    public const string UnsupportedImage = "unsupported-image";

    /// <summary>
    /// The listing form moves the branch - its address or map pin - and only an owner or platform
    /// admin signed in to the admin panel may. HTTP 403. Nothing on the form was saved.
    /// </summary>
    [Problem(403, "Moving a branch (address or map pin) is for an owner or platform admin; nothing was saved.", Title = "Relocation not allowed")]
    [ProblemContext("branchId", typeof(Guid))]
    public const string RelocationNotAllowed = "relocation-not-allowed";

    /// <summary>
    /// The floor plan was saved by somebody else since the editor loaded it. HTTP 409, with
    /// <c>context.currentVersion</c>. Reload, reapply, save against the new version.
    /// </summary>
    [Problem(409, "The floor plan was saved by somebody else since the editor loaded it.", Title = "Floor plan changed")]
    [ProblemContext("currentVersion", typeof(string))]
    public const string FloorPlanChanged = "floor-plan-changed";

    /// <summary>
    /// A table with a booking still to come - Confirmed or PendingApproval, not yet ended - cannot be
    /// deleted, nor left out of a floor-plan save. HTTP 409, with <c>context.tables</c> (labels) and
    /// <c>context.bookings</c> (booking codes). Move or cancel the bookings, then try again. Nothing
    /// was written.
    /// </summary>
    [Problem(409, "A table with a booking still to come cannot be deleted, nor left out of a floor-plan save.", Title = "Table has upcoming bookings")]
    [ProblemContext("tables", typeof(string[]))]
    [ProblemContext("bookings", typeof(string[]))]
    public const string TableHasFutureBookings = "table-has-future-bookings";

    /// <summary>
    /// A first review of a branch from a diner with no visit there in the window (K8): no Seated or
    /// Completed booking and no place on one of its tabs in the last 180 days. HTTP 403, with
    /// <c>context.branchId</c> and <c>context.windowDays</c>. Revising a review the diner already has
    /// is never refused for this.
    /// </summary>
    [Problem(403, "A first review of a branch from a diner with no visit there in the window.", Title = "Review needs a visit")]
    [ProblemContext("branchId", typeof(Guid))]
    [ProblemContext("windowDays", typeof(int))]
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
    [Problem(409, "A change to a booking that is no longer pending or confirmed, or has started.", Title = "Booking can no longer be changed")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("status", typeof(ReservationStatus))]
    [ProblemContext("startUtc", typeof(DateTime))]
    public const string ReservationNotChangeable = "reservation-not-changeable";

    /// <summary>
    /// A no-show recorded against a confirmed booking whose start has not come yet - through
    /// <c>no-show</c> or <c>release</c> with <c>outcome</c> 1. HTTP 409, with
    /// <c>context.reservationId</c> and <c>context.startUtc</c>, from when a no-show may be recorded.
    /// Nothing changed. Letting the booking go as the venue's doing (<c>outcome</c> 2) and releasing
    /// the table's hold are allowed at any time. Minted by <c>ReservationNotStartedException</c>.
    /// </summary>
    [Problem(409, "A no-show recorded against a booking whose start has not come yet.", Title = "Booking has not started yet")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("startUtc", typeof(DateTime))]
    public const string ReservationNotStarted = "reservation-not-started";

    /// <summary>
    /// Approving or declining a booking whose chance to be answered has gone: its short-notice answer
    /// window closed, it was already declined because nobody answered in time, or - approving only -
    /// it has started. HTTP 409, with <c>context.reservationId</c>, <c>context.status</c>,
    /// <c>context.startUtc</c> and <c>context.approvalExpiresAtUtc</c> (null once declined). Minted by
    /// <c>ReservationApprovalExpiredException</c>. The tablet drops the request: the diner has been, or
    /// is about to be, told the venue did not answer in time.
    /// </summary>
    [Problem(409, "Approving or declining a booking whose chance to be answered has gone.", Title = "Too late to answer this booking")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("status", typeof(ReservationStatus))]
    [ProblemContext("startUtc", typeof(DateTime))]
    [ProblemContext("approvalExpiresAtUtc", typeof(DateTime?))]
    public const string ReservationApprovalExpired = "reservation-approval-expired";

    /// <summary>
    /// The table's next booking cuts a full sitting short and the booking did not accept the time to
    /// free the table by - <c>acceptsFreeByUtc</c> missing, or not that instant. HTTP 422, naming the field
    /// (<c>context.field</c> and <c>errors.acceptsFreeByUtc</c>), with <c>context.requiredFreeByUtc</c>,
    /// <c>context.requiredFreeByLocal</c> (<c>HH:mm</c>), <c>context.nextBookingStartUtc</c>,
    /// <c>context.tableId</c> and <c>context.tableLabel</c>. Ask "Will you free the table by 21:25?" and send
    /// again with <c>acceptsFreeByUtc</c> = <c>requiredFreeByUtc</c>. Minted by <c>FreeByNotAcceptedException</c>.
    /// </summary>
    [Problem(422, "The next booking cuts the sitting short and the booking did not accept the time to free the table by.", Title = "Free the table by the time required")]
    [ProblemContext("field", typeof(string))]
    [ProblemContext("requiredFreeByUtc", typeof(DateTime))]
    [ProblemContext("requiredFreeByLocal", typeof(string))]
    [ProblemContext("nextBookingStartUtc", typeof(DateTime))]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    public const string ReservationFreeByRequired = "reservation-free-by-required";

    /// <summary>
    /// The earlier guest answered a request for their table that no longer waits: answered the other
    /// way, past its deadline, or withdrawn because one of the bookings moved or ended. HTTP 409, with
    /// <c>context.reservationId</c>, <c>context.requestId</c>, <c>context.status</c> (the
    /// <c>TableShareRequestStatus</c>) and <c>context.expiresAtUtc</c>. Drop the card; the booking is
    /// unchanged. Minted by <c>TableRequestNotPendingException</c>.
    /// </summary>
    [Problem(409, "An answer to a request for the table that no longer waits.", Title = "This request no longer needs an answer")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("requestId", typeof(Guid))]
    [ProblemContext("status", typeof(TableShareRequestStatus))]
    [ProblemContext("expiresAtUtc", typeof(DateTime))]
    public const string TableRequestNotPending = "table-request-not-pending";

    /// <summary>
    /// Staff approving or declining a booking that waits on the guest before it on the table to answer
    /// in the app - theirs to answer, not the floor's. HTTP 409, with <c>context.reservationId</c>,
    /// <c>context.requestId</c> and <c>context.expiresAtUtc</c>. Minted by
    /// <c>ReservationAwaitingEarlierGuestException</c>.
    /// </summary>
    [Problem(409, "Staff deciding a booking that waits on the guest before it on the table to answer.", Title = "Waiting for the guest before to answer")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("requestId", typeof(Guid))]
    [ProblemContext("expiresAtUtc", typeof(DateTime))]
    public const string ReservationAwaitingEarlierGuest = "reservation-awaiting-earlier-guest";

    /// <summary>
    /// A signed-in diner booking, or moving a booking to, a time whose sitting overlaps another of their
    /// own live bookings (pending, confirmed or seated) at the same branch. HTTP 409, with
    /// <c>context.reservationId</c>, <c>context.code</c> (its door code), <c>context.localDate</c>
    /// (<c>"2026-10-22"</c>, the branch's calendar), <c>context.localStartTime</c> (<c>"19:30:00"</c>) and
    /// <c>context.tableLabel</c> - the booking already held - so the client can say
    /// "You already have a booking at 19:30 (table 2)". Minted by <c>ReservationOverlapsOwnException</c>.
    /// </summary>
    [Problem(409, "The sitting overlaps another of the diner's own live bookings; context names it (not on a manage link).", Title = "You already have a booking then")]
    [ProblemContext("reservationId", typeof(Guid))]
    [ProblemContext("code", typeof(string))]
    [ProblemContext("localDate", typeof(DateOnly))]
    [ProblemContext("localStartTime", typeof(TimeOnly))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("venueName", typeof(string), Nullable = true)]
    [ProblemContext("branchName", typeof(string), Nullable = true)]
    public const string ReservationOverlapsOwn = "reservation-overlaps-own";

    /// <summary>
    /// A booking from the diner app at a branch that is not taking app bookings (K9): online bookings
    /// are switched off, or nobody has saved the branch's reservation policy. HTTP 409, with
    /// <c>context.branchId</c>. The app reads <c>acceptsAppBookings</c> to hide the button.
    /// </summary>
    [Problem(409, "A booking from the diner app at a branch that is not taking app bookings.", Title = "Bookings not accepted")]
    [ProblemContext("branchId", typeof(Guid))]
    public const string BookingsNotAccepted = "bookings-not-accepted";

    /// <summary>
    /// A Telegram verification challenge that cannot be claimed: unknown, past its deadline, already
    /// claimed, or claimed under a different account than it was started under. HTTP 410, and one
    /// answer for all four on purpose - the app's response is the same: start again. Minted by
    /// <c>TelegramVerificationExpiredException</c>.
    /// </summary>
    [Problem(410, "A Telegram verification challenge that can no longer be claimed; start again.", Title = "Telegram verification no longer available")]
    public const string TelegramVerificationExpired = "telegram-verification-expired";

    /// <summary>
    /// The PIN being set is already used by somebody else who signs in on the same tablet: the
    /// branch's own people and the venue-wide ones, or for a venue-wide person the whole venue. HTTP
    /// 409, with <c>context.field = "pin"</c>. Minted by <c>PinTakenException</c>.
    /// </summary>
    [Problem(409, "The PIN is already used by somebody else who signs in on the same tablet.", Title = "PIN already in use on this tablet")]
    [ProblemContext("field", typeof(string))]
    public const string PinTaken = "pin-taken";

    /// <summary>
    /// The access token names no sign-in (no refresh-chain claim), so "sign out everywhere else" cannot
    /// tell which sign-in to keep. HTTP 409; nothing changed - sign in again and retry. Minted by
    /// <c>SignInUnknownException</c>.
    /// </summary>
    [Problem(409, "The token names no sign-in, so \"sign out everywhere else\" cannot tell which to keep.", Title = "Sign-in not identified")]
    public const string SignInUnknown = "sign-in-unknown";

    /// <summary>
    /// The waiter named cannot take the table: nobody by that id at the venue, deactivated, at another branch, or one
    /// who does not serve (the kitchen). HTTP 422, with <c>context.staffMemberId</c> and <c>context.reason</c>
    /// (<c>not-found</c>, <c>inactive</c>, <c>other-branch</c>, <c>does-not-serve</c>). Minted by
    /// <c>TransferTargetIneligibleException</c>.
    /// </summary>
    [Problem(422, "The waiter named cannot take the table.", Title = "Cannot take this table")]
    [ProblemContext("staffMemberId", typeof(Guid))]
    [ProblemContext("reason", typeof(string))]
    public const string TransferTargetIneligible = "transfer-target-ineligible";

    /// <summary>
    /// A table whose bill is closed or written off, or whose party has gone, has nobody to hand over. HTTP 409, with
    /// <c>context.tabId</c> and <c>context.status</c>. Minted by <c>TabNotTransferableException</c>.
    /// </summary>
    [Problem(409, "A table whose bill is closed or written off, or whose party has gone, has nobody to hand over.", Title = "Nothing to hand over")]
    [ProblemContext("tabId", typeof(Guid))]
    [ProblemContext("status", typeof(TabStatus))]
    public const string TabNotTransferable = "tab-not-transferable";

    /// <summary>
    /// A History export would carry more rows than a file may. HTTP 422, with <c>context.rows</c> (at least) and
    /// <c>context.maxRows</c>: narrow the dates or add a filter. Minted by <c>ActivityExportTooLargeException</c>.
    /// </summary>
    [Problem(422, "A History export would carry more rows than a file may.", Title = "Export too large")]
    [ProblemContext("rows", typeof(int))]
    [ProblemContext("maxRows", typeof(int))]
    public const string ActivityExportTooLarge = "activity-export-too-large";

    /// <summary>
    /// A phone cannot be paired to kitchen staff: the kitchen works on its tablet. HTTP 409, with
    /// <c>context.staffMemberId</c>. From issuing the code, or from redeeming it for somebody moved to the kitchen
    /// since. Minted by <c>KitchenPhoneNotAllowedException</c>.
    /// </summary>
    [Problem(409, "A phone cannot be paired to kitchen staff: the kitchen works on its tablet.", Title = "No phone for kitchen staff")]
    [ProblemContext("staffMemberId", typeof(Guid))]
    public const string KitchenPhoneNotAllowed = "kitchen-phone-not-allowed";

    /// <summary>
    /// The phone pairing code is unknown - mistyped, or a tablet's enrolment code. HTTP 401. Retype it or scan the
    /// QR again. An <c>AuthenticationFailedException</c> reason, listed here so the phone can branch on it.
    /// </summary>
    [Problem(401, "The phone pairing code is unknown: mistyped, or a tablet's enrolment code.", Title = "Pairing code not valid")]
    public const string PairingCodeInvalid = "pairing-code-invalid";

    /// <summary>
    /// The phone pairing code is past its ten minutes, or a newer code for the same person replaced it. HTTP 401.
    /// Retyping never helps; ask the manager for a new one.
    /// </summary>
    [Problem(401, "The phone pairing code is past its ten minutes, or a newer one replaced it.", Title = "Pairing code expired")]
    public const string PairingCodeExpired = "pairing-code-expired";

    // -----------------------------------------------------------------------------------------
    // Booking rules: the reservation- family, one code per rule, each minted by its own
    // ReservationRejectedException (Domain/Occupancy/ReservationRejectedException.cs). All 422.
    // -----------------------------------------------------------------------------------------

    /// <summary>The slot starts sooner than the branch's minimum lead time. Minted by <c>LeadTimeTooShortException</c>.</summary>
    [Problem(422, "The slot starts sooner than the branch's minimum lead time.")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("requestedStartUtc", typeof(DateTime))]
    [ProblemContext("earliestStartUtc", typeof(DateTime))]
    [ProblemContext("minLeadMinutes", typeof(int))]
    public const string ReservationLeadTimeTooShort = "reservation-lead-time-too-short";

    /// <summary>The date is further ahead than the branch takes bookings. Minted by <c>OutsideBookingWindowException</c>.</summary>
    [Problem(422, "The date is further ahead than the branch takes bookings.")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("requestedDate", typeof(DateOnly))]
    [ProblemContext("lastBookableDate", typeof(DateOnly))]
    [ProblemContext("bookingWindowDays", typeof(int))]
    public const string ReservationOutsideBookingWindow = "reservation-outside-booking-window";

    /// <summary>
    /// The sitting does not fit inside one opening block of the branch's local day. Minted by
    /// <c>OutsideOpeningHoursException</c>.
    /// </summary>
    [Problem(422, "The sitting does not fit inside one opening block of the branch's day.")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("localDate", typeof(DateOnly))]
    [ProblemContext("localStartTime", typeof(string))]
    [ProblemContext("localEndTime", typeof(string))]
    public const string ReservationOutsideOpeningHours = "reservation-outside-opening-hours";

    /// <summary>More guests than the table has seats. Minted by <c>PartyExceedsTableCapacityException</c>.</summary>
    [Problem(422, "More guests than the table has seats.")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("partySize", typeof(int))]
    [ProblemContext("seats", typeof(int))]
    public const string ReservationPartyExceedsCapacity = "reservation-party-exceeds-capacity";

    /// <summary>
    /// The party would leave more seats empty than the branch allows. Minted by
    /// <c>SeatOverhangExceededException</c>.
    /// </summary>
    [Problem(422, "The party would leave more seats empty than the branch allows.")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    [ProblemContext("partySize", typeof(int))]
    [ProblemContext("seats", typeof(int))]
    [ProblemContext("seatsLeftEmpty", typeof(int))]
    [ProblemContext("maxSeatOverhang", typeof(int))]
    public const string ReservationSeatOverhangExceeded = "reservation-seat-overhang-exceeded";

    /// <summary>The table never takes bookings - a bar stool, a counter seat. Minted by <c>TableNotBookableException</c>.</summary>
    [Problem(422, "The table never takes bookings: a bar stool, a counter seat.")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    public const string ReservationTableNotBookable = "reservation-table-not-bookable";

    /// <summary>The table is withdrawn from service. Minted by <c>TableOutOfServiceException</c>.</summary>
    [Problem(422, "The table is withdrawn from service.")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("tableId", typeof(Guid))]
    [ProblemContext("tableLabel", typeof(string))]
    public const string ReservationTableOutOfService = "reservation-table-out-of-service";

    /// <summary>
    /// The wall-clock time picked does not exist in the branch's zone: the hour a spring-forward skips. Minted by
    /// <c>LocalTimeDoesNotExistException</c>.
    /// </summary>
    [Problem(422, "The wall-clock time picked does not exist in the branch's zone (a clock change skips it).")]
    [ProblemContext("reason", typeof(ReservationRejectionReason))]
    [ProblemContext("localDate", typeof(DateOnly))]
    [ProblemContext("localTime", typeof(string))]
    [ProblemContext("timeZoneId", typeof(string))]
    public const string ReservationLocalTimeDoesNotExist = "reservation-local-time-does-not-exist";

    // -----------------------------------------------------------------------------------------
    // Sign-in reasons: AuthenticationFailedException.ReasonCode, minted in Infrastructure/Identity
    // and the services. All 401 and no context unless stated; none tells "no such account" from
    // "wrong secret".
    // -----------------------------------------------------------------------------------------

    /// <summary>A one-time code that was not accepted, with the tries left on the live code. Minted by <c>VerificationCodeInvalidException</c>.</summary>
    [Problem(401, "A one-time code that was not accepted; context says how many tries are left.")]
    [ProblemContext("attemptsRemaining", typeof(int))]
    public const string VerificationCodeInvalid = "verification-code-invalid";

    /// <summary>The one-time code has expired; ask for a new one.</summary>
    [Problem(401, "The one-time code has expired; ask for a new one.")]
    public const string VerificationCodeExpired = "verification-code-expired";

    /// <summary>An admin-panel email and password did not match an account.</summary>
    [Problem(401, "An admin-panel email and password did not match an account.")]
    public const string CredentialsInvalid = "credentials-invalid";

    /// <summary>The account or staff member behind the credential is no longer active.</summary>
    [Problem(401, "The account or staff member behind the credential is no longer active.")]
    public const string AccountInactive = "account-inactive";

    /// <summary>The refresh token is unknown, expired, revoked, or for another kind of account.</summary>
    [Problem(401, "The refresh token is unknown, expired or revoked; sign in again.")]
    public const string RefreshTokenInvalid = "refresh-token-invalid";

    /// <summary>A refresh token already rotated was presented again; its whole chain is now revoked.</summary>
    [Problem(401, "A refresh token already rotated was presented again; its whole chain is revoked.")]
    public const string RefreshTokenReused = "refresh-token-reused";

    /// <summary>The admin-panel password reset link is unknown, spent or expired.</summary>
    [Problem(401, "The password reset link is unknown, spent or expired; ask for a new one.")]
    public const string ResetTokenInvalid = "reset-token-invalid";

    /// <summary>The staff PIN was not recognised - wrong PIN, unknown person and another branch alike.</summary>
    [Problem(401, "The staff PIN was not recognised.")]
    public const string PinInvalid = "pin-invalid";

    /// <summary>The tablet enrolment code is unknown, or a phone's pairing code.</summary>
    [Problem(401, "The tablet enrolment code is not valid.")]
    public const string EnrolmentCodeInvalid = "enrolment-code-invalid";

    /// <summary>The tablet enrolment code is past its 24 hours; ask a manager for a new one.</summary>
    [Problem(401, "The tablet enrolment code has expired; ask a manager for a new one.")]
    public const string EnrolmentCodeExpired = "enrolment-code-expired";

    /// <summary>The tablet or device is no longer enrolled; a manager enrols it again.</summary>
    [Problem(401, "The tablet or device is no longer enrolled; ask a manager to enrol it again.")]
    public const string DeviceRevoked = "device-revoked";

    /// <summary>The staff PIN session is unknown or ended; tap the PIN again.</summary>
    [Problem(401, "The staff PIN session is no longer valid; tap the PIN again.")]
    public const string SessionInvalid = "session-invalid";

    /// <summary>The staff PIN session timed out; tap the PIN again.</summary>
    [Problem(401, "The staff PIN session timed out; tap the PIN again.")]
    public const string SessionExpired = "session-expired";

    /// <summary>The tab invitation is unknown, revoked or past its thirty minutes; the host makes a new one.</summary>
    [Problem(401, "The tab invitation is no longer valid; ask the host for a new one.")]
    public const string JoinTokenInvalid = "join-token-invalid";

    /// <summary>Anything unhandled. Details go to the log, never to the client. HTTP 500.</summary>
    [Problem(500, "Anything unhandled; quote the traceId when reporting it.", Title = "Internal error")]
    public const string InternalError = "internal-error";

    /// <summary>
    /// The stable, human-readable title for each slug.
    /// </summary>
    /// <remarks>
    /// RFC 7807 wants <c>title</c> to describe the problem <i>type</i> and stay the same for every
    /// occurrence, while <c>detail</c> carries the specifics. The title is declared once, on the
    /// code's <see cref="ProblemAttribute"/>, so the two cannot drift and no exception has to invent
    /// its own. A slug outside the catalogue is humanised, as a declared code with no title is.
    /// </remarks>
    public static string TitleFor(string code) => ProblemCatalogue.Find(code)?.Title ?? Humanise(code);

    /// <summary>The documentation URI for a slug, used as the problem document's <c>type</c>.</summary>
    public static string TypeFor(string code) => $"https://docs.yalla.app/errors/{code}";

    /// <summary>"pin-invalid" becomes "Pin invalid": the title of a code that declares none.</summary>
    internal static string Humanise(string code)
    {
        var words = code.Replace('-', ' ');

        return words.Length == 0 ? code : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
