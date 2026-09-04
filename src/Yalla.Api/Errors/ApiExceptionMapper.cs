using Microsoft.EntityFrameworkCore;
using Yalla.Domain;
using Yalla.Domain.Identity;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;

namespace Yalla.Api.Errors;

/// <summary>What one exception should become on the wire.</summary>
/// <param name="Status">HTTP status code.</param>
/// <param name="Code">Stable slug from <see cref="ErrorCodes"/>.</param>
/// <param name="Message">Message safe to put in the response body.</param>
/// <param name="LogAsError">
/// False for failures that are part of normal operation - a rejected request, a lost concurrency
/// race - so the error log stays a list of things that are actually wrong.
/// </param>
/// <param name="Context">Machine-readable facts the client needs in order to react.</param>
public readonly record struct MappedError(
    int Status,
    string Code,
    string Message,
    bool LogAsError,
    IReadOnlyDictionary<string, object?>? Context = null);

/// <summary>
/// Translates exceptions into <see cref="UnifiedErrorEnvelope"/> values.
/// </summary>
/// <remarks>
/// The domain enforces its invariants by throwing from constructors and mutators, so the mapping
/// from exception type to status code is the seam where those refusals become HTTP answers. Later
/// tasks add their own exception types here; nothing else in the pipeline needs to change.
/// </remarks>
internal static class ApiExceptionMapper
{
    /// <summary>Message returned for anything unhandled. Internals never reach a client.</summary>
    private const string InternalErrorMessage =
        "The request could not be completed. Quote the traceId when reporting this.";

    public static MappedError Map(Exception exception) => exception switch
    {
        // Somebody else changed the table first. 409 with the table's CURRENT state, so the
        // client can redraw it and tell the user what actually happened rather than just failing.
        // Expected under load - two people really do tap table 7 at the same moment.
        TableStateConflictException e => new MappedError(
            StatusCodes.Status409Conflict,
            ErrorCodes.TableStateConflict,
            e.Message,
            LogAsError: false,
            Context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                // The numeric enum values, matching what the schema declares and what every other
                // response carries. A client comparing this to its generated TableStatus enum
                // should not have to know that this one place spelled it out in English.
                ["attemptedFromStatus"] = (int)e.AttemptedFromStatus,
                ["currentStatus"] = (int)e.CurrentStatus,
                ["currentSessionId"] = e.CurrentSessionId,
            }),

        // An impossible transition, not a race: freeing a table nobody is sitting at, or marking
        // an occupied one broken. 422 - the request was understood and is semantically wrong.
        InvalidTableTransitionException e => new MappedError(
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.InvalidTableTransition,
            e.Message,
            LogAsError: false,
            Context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                ["fromStatus"] = (int)e.FromStatus,
                ["attemptedToStatus"] = (int)e.ToStatus,
                ["allowedFromHere"] = TableStatusTransitions.From(e.FromStatus)
                    .Select(s => (int)s)
                    .ToArray(),
            }),

        // The account is locked, not the credential wrong. Must stay ABOVE the general
        // authentication arm it derives from: reporting a lockout as a plain 401 leaves someone
        // standing at a tablet retyping a PIN that was correct all along.
        AccountLockedException e => new MappedError(
            StatusCodes.Status403Forbidden,
            ErrorCodes.AccountLocked,
            e.Message,
            LogAsError: false,
            Context: new Dictionary<string, object?>
            {
                ["lockedUntilUtc"] = e.LockedUntilUtc,
            }),

        // A one-time credential is out of attempts. 429 rather than 401, because the useful
        // signal to a client is "stop retrying and ask for a new code", not "try again".
        TooManyAttemptsException e => new MappedError(
            StatusCodes.Status429TooManyRequests,
            ErrorCodes.TooManyAttempts,
            e.Message,
            LogAsError: false),

        // Every other sign-in failure. The slug comes from the exception, which is deliberately
        // never specific enough to say whether an account exists - see AuthenticationFailedException.
        AuthenticationFailedException e => new MappedError(
            StatusCodes.Status401Unauthorized,
            e.ReasonCode,
            e.Message,
            LogAsError: false),

        StaffPermissionException e => new MappedError(
            StatusCodes.Status403Forbidden,
            ErrorCodes.Forbidden,
            e.Message,
            LogAsError: false,
            Context: new Dictionary<string, object?>
            {
                ["operation"] = e.Operation,
                ["requiredRole"] = (int)e.RequiredRole,
            }),

        // A null where the domain requires an object - a Venue, a ReservationPolicy. Those are
        // built internally and never arrive over HTTP, so this is a bug in our code, not bad
        // input, and no caller can act on it. Must stay ABOVE ArgumentException, which it derives
        // from, or it would be answered as a 400 quoting an internal parameter name.
        ArgumentNullException => new MappedError(
            StatusCodes.Status500InternalServerError,
            ErrorCodes.InternalError,
            InternalErrorMessage,
            LogAsError: true),

        // The domain's own refusals. Guard and the entity constructors throw these with messages
        // written to be read, so they are safe and useful to pass back.
        ArgumentOutOfRangeException e => new MappedError(
            StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, e.Message, LogAsError: false),

        ArgumentException e => new MappedError(
            StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, e.Message, LogAsError: false),

        // Someone else won the race for a table, a tab or a booking. Expected under load.
        DbUpdateConcurrencyException => new MappedError(
            StatusCodes.Status409Conflict,
            ErrorCodes.ConcurrentUpdate,
            "This record changed while you were working on it. Reload and try again.",
            LogAsError: false),

        // "This session is already closed", "the settlement mode is locked". Note this must stay
        // BELOW InvalidTableTransitionException, which derives from it.
        //
        // Deliberately DomainStateException and not InvalidOperationException. .NET raises the
        // latter for a whole class of genuine bugs - an unresolved service, First() on an empty
        // sequence, "sequence contains more than one element", EF's transient-failure wrapper -
        // and catching it here reported every one of them as a 409 "you have a conflict", echoed
        // the internal message to the caller, and logged none of it as an error. Those now fall
        // through to the 500 below, where they are logged and say nothing about internals.
        DomainStateException e => new MappedError(
            StatusCodes.Status409Conflict, ErrorCodes.ConflictingState, e.Message, LogAsError: false),

        UnauthorizedAccessException => new MappedError(
            StatusCodes.Status403Forbidden,
            ErrorCodes.Forbidden,
            "You are not allowed to perform this action.",
            LogAsError: false),

        KeyNotFoundException e => new MappedError(
            StatusCodes.Status404NotFound, ErrorCodes.NotFound, e.Message, LogAsError: false),

        _ => new MappedError(
            StatusCodes.Status500InternalServerError,
            ErrorCodes.InternalError,
            InternalErrorMessage,
            LogAsError: true),
    };
}
