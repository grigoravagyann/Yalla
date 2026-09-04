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
    /// The booking could not get the table's lock in time. HTTP 503 and <b>retryable</b> - which
    /// is what makes it different from <see cref="TableAlreadyBooked"/>: the question was never
    /// asked, rather than answered no. Retry with the same <c>clientCommandId</c>.
    /// </summary>
    public const string ReservationLockTimeout = "reservation-lock-timeout";

    /// <summary>Too many requests in the window. HTTP 429.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>Anything unhandled. Details go to the log, never to the client. HTTP 500.</summary>
    public const string InternalError = "internal-error";
}
