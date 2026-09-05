using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
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

        // Somebody else took the table between the diner seeing it free and confirming. 409, not
        // 422: the request was right when it was made and the world moved. The body carries the
        // clashing window AND a fresh availability snapshot, so the app can redraw the floor and
        // show what changed instead of firing a second request into the same contention.
        TableAlreadyBookedException e => new MappedError(
            StatusCodes.Status409Conflict,
            ErrorCodes.TableAlreadyBooked,
            e.Message,
            LogAsError: false,
            Context: BookedContext(e)),

        // Two callers minted the same idempotency key. Not a replay - a replay is the SAME caller
        // sending the same command again - so the honest answer is that the id is taken, and
        // nothing at all about the booking that holds it.
        ClientCommandIdAlreadyUsedException e => new MappedError(
            StatusCodes.Status409Conflict,
            ErrorCodes.ClientCommandIdInUse,
            e.Message,
            LogAsError: false),

        // Contention, not refusal. 503 with retryable set, because the client's correct response
        // is to try again - with the same clientCommandId - whereas a 409 will never succeed no
        // matter how often it is repeated. Collapsing the two would teach clients to retry
        // conflicts, which is how a party ends up with two tables.
        ReservationLockTimeoutException e => new MappedError(
            StatusCodes.Status503ServiceUnavailable,
            ErrorCodes.ReservationLockTimeout,
            e.Message,
            LogAsError: false,
            Context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                ["timeoutMilliseconds"] = e.TimeoutMilliseconds,
                ["retryable"] = e.Retryable,
            }),

        // One entry for the whole family of booking refusals, each answering with its own code
        // and its own numbers. 422: understood, and semantically wrong. Adding a rule is a new
        // exception type and a new constant - this mapper does not change.
        ReservationRejectedException e => new MappedError(
            StatusCodes.Status422UnprocessableEntity,
            e.Code,
            e.Message,
            LogAsError: false,
            Context: e.Context),

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
        // A floor plan that cannot be applied, with the offending tables named so the editor can
        // highlight them. Must stay ABOVE ArgumentException, which it derives from.
        FloorPlanInvalidException e => new MappedError(
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.FloorPlanInvalid,
            e.Message,
            LogAsError: false,
            Context: new Dictionary<string, object?>
            {
                ["errors"] = e.Errors,
                ["tablesOutsideCanvas"] = e.TablesOutsideCanvas,
                ["duplicateLabels"] = e.DuplicateLabels,
            }),

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
        // Both derive from DomainStateException and must stay ABOVE it.
        //
        // Not a 403. The caller is allowed to be here; the branch has not paid for what they
        // asked. The code is what the apps branch on to show "ask the venue to enable ordering".
        FeatureNotEnabledException e => new MappedError(
            StatusCodes.Status409Conflict,
            ErrorCodes.FeatureNotEnabled,
            e.Message,
            LogAsError: false,
            Context: new Dictionary<string, object?>
            {
                ["feature"] = e.Feature,
                ["branchId"] = e.BranchId,
                ["currentTier"] = e.CurrentTier,
            }),

        VenueDeletionBlockedException e => new MappedError(
            StatusCodes.Status409Conflict,
            ErrorCodes.VenueDeletionBlocked,
            e.Message,
            LogAsError: false,
            Context: new Dictionary<string, object?>
            {
                ["openTabs"] = e.OpenTabs,
                ["futureReservations"] = e.FutureReservations,
            }),

        DomainStateException e => new MappedError(
            StatusCodes.Status409Conflict, ErrorCodes.ConflictingState, e.Message, LogAsError: false),

        // On the tab, but not allowed this: a guest approving a joiner, a diner reassigning the
        // host. The message names the missing role. Must stay ABOVE the general arm it derives from.
        TabPermissionException e => new MappedError(
            StatusCodes.Status403Forbidden,
            ErrorCodes.Forbidden,
            e.Message,
            LogAsError: false,
            Context: new Dictionary<string, object?>
            {
                ["operation"] = e.Operation,
                ["requirement"] = e.Requirement,
            }),

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

    /// <summary>
    /// The context of a lost race for a table.
    /// </summary>
    /// <remarks>
    /// The availability snapshot is added only when there is one. It is best-effort - a floor that
    /// could not be read must not turn a 409 the client can act on into a 500 it cannot - and
    /// <c>DefaultIgnoreCondition</c> does not reach inside a dictionary, so an unconditional entry
    /// would put a bare <c>"availability": null</c> on the wire for a generated client to trip over.
    /// </remarks>
    private static Dictionary<string, object?> BookedContext(TableAlreadyBookedException exception)
    {
        var context = new Dictionary<string, object?>
        {
            // Numeric, like every other enum on the wire and in the schema.
            ["reason"] = (int)exception.Reason,
            ["tableId"] = exception.TableId,
            ["tableLabel"] = exception.TableLabel,
            ["requestedStartUtc"] = exception.Requested.StartUtc,
            ["requestedEndUtc"] = exception.Requested.EndUtc,
            ["conflictingStartUtc"] = exception.Conflicting.StartUtc,
            ["conflictingEndUtc"] = exception.Conflicting.EndUtc,
            ["conflictingReservationId"] = exception.ConflictingReservationId,
        };

        if (exception.Availability is { } availability)
        {
            context["availability"] = availability;
        }

        return context;
    }
}
