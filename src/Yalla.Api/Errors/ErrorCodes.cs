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

    /// <summary>Too many requests in the window. HTTP 429.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>Anything unhandled. Details go to the log, never to the client. HTTP 500.</summary>
    public const string InternalError = "internal-error";
}
