using Microsoft.EntityFrameworkCore;

namespace Yalla.Api.Errors;

/// <summary>What one exception should become on the wire.</summary>
/// <param name="Status">HTTP status code.</param>
/// <param name="Code">Stable slug from <see cref="ErrorCodes"/>.</param>
/// <param name="Message">Message safe to put in the response body.</param>
/// <param name="LogAsError">
/// False for failures that are part of normal operation - a rejected request, a lost concurrency
/// race - so the error log stays a list of things that are actually wrong.
/// </param>
public readonly record struct MappedError(int Status, string Code, string Message, bool LogAsError);

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

        // "This session is already closed", "the settlement mode is locked".
        InvalidOperationException e => new MappedError(
            StatusCodes.Status409Conflict, ErrorCodes.ConflictingState, e.Message, LogAsError: false),

        UnauthorizedAccessException => new MappedError(
            StatusCodes.Status403Forbidden,
            ErrorCodes.Forbidden,
            "You are not allowed to perform this action.",
            LogAsError: false),

        KeyNotFoundException => new MappedError(
            StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found.", LogAsError: false),

        _ => new MappedError(
            StatusCodes.Status500InternalServerError,
            ErrorCodes.InternalError,
            InternalErrorMessage,
            LogAsError: true),
    };
}
