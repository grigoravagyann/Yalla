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
    /// A one-time credential is out of attempts, or a per-identity limit is spent. HTTP 429.
    /// Asking again immediately will not help; ask for a new code.
    /// </summary>
    public const string TooManyAttempts = "too-many-attempts";

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
    /// The floor plan cannot be applied: tables outside the canvas or repeated labels. HTTP 422,
    /// with the offending tables in <c>context</c>.
    /// </summary>
    public const string FloorPlanInvalid = "floor-plan-invalid";

    /// <summary>
    /// The branch is not open for business: its venue is suspended or deleted, or the branch is
    /// switched off. HTTP 409. Distinct from <see cref="FeatureNotEnabled"/>, which is about what a
    /// branch pays for rather than whether it is trading at all.
    /// </summary>
    public const string BranchUnavailable = "branch-unavailable";

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
        LineAlreadyPaid => "Line already paid for",
        PaymentExceedsRemaining => "Payment exceeds the balance",
        ServiceRequestRateLimited => "Too many requests from this table",
        ReservationLockTimeout => "Reservation lock timeout",
        TableLockTimeout => "Table busy",
        ClientCommandIdInUse => "Command id already used",
        RateLimited => "Rate limited",
        Unauthenticated => "Not authenticated",
        TooManyAttempts => "Too many attempts",
        AccountLocked => "Account locked",
        FeatureNotEnabled => "Feature not enabled for this branch",
        BranchUnavailable => "Branch not open for business",
        VenueDeletionBlocked => "Venue cannot be deleted",
        FloorPlanInvalid => "Floor plan invalid",
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
