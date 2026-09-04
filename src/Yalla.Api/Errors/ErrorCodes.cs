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
        InvalidTableTransition => "Invalid table transition",
        RateLimited => "Rate limited",
        Unauthenticated => "Not authenticated",
        TooManyAttempts => "Too many attempts",
        AccountLocked => "Account locked",
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
