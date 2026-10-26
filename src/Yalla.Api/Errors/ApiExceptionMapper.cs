using Yalla.Domain;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;

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
/// <param name="Errors">
/// Field-level complaints, keyed by the wire name of the field. Present only for the failures that
/// are about the payload; RFC 7807's <c>errors</c> member is where generic tooling looks for them.
/// </param>
/// <param name="RetryAfterSeconds">Written as the <c>Retry-After</c> header when known.</param>
public readonly record struct MappedError(
    int Status,
    string Code,
    string Message,
    bool LogAsError,
    IReadOnlyDictionary<string, object?>? Context = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    int? RetryAfterSeconds = null);

/// <summary>
/// Translates exceptions into <see cref="UnifiedErrorEnvelope"/> values.
/// </summary>
/// <remarks>
/// The domain enforces its invariants by throwing from constructors and mutators, so the mapping
/// from exception type to code is the seam where those refusals become HTTP answers. Later tasks
/// add their own exception types here; nothing else in the pipeline needs to change. Each arm names
/// a code and builds its context; the status comes from the code's declaration in
/// <see cref="ErrorCodes"/> (see <see cref="ProblemCatalogue"/>).
/// </remarks>
internal static partial class ApiExceptionMapper
{
    /// <summary>Message returned for anything unhandled. Internals never reach a client.</summary>
    private const string InternalErrorMessage =
        "The request could not be completed. Quote the traceId when reporting this.";

    /// <summary>Whole seconds, rounded up and never below one: a client told 0 retries at once.</summary>
    private static int RetryAfterSeconds(TimeSpan wait) => Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));

    /// <summary>
    /// A refusal: a catalogued code, answered with the status its <see cref="ProblemAttribute"/> declares.
    /// </summary>
    /// <remarks>
    /// The status is never written here. A code has one status, declared once in <see cref="ErrorCodes"/>,
    /// and <c>ProblemCatalogueTests</c> holds every arm below to it and to the context it declares.
    /// </remarks>
    private static MappedError Refuse(
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? context = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        int? retryAfterSeconds = null) =>
        new(
            ProblemCatalogue.Get(code).Status,
            code,
            message,
            LogAsError: false,
            context,
            errors,
            retryAfterSeconds);

    /// <summary>
    /// A refusal whose code the exception mints - a booking rule, a sign-in reason - answered with that
    /// code's catalogued status.
    /// </summary>
    /// <remarks>
    /// <paramref name="familyStatus"/> is what the whole family answers with, and is used only for a
    /// code the catalogue does not know: a slug minted at run time and missed by
    /// <c>ProblemCatalogueTests</c> still gets its family's answer rather than turning a refusal into a 500.
    /// </remarks>
    private static MappedError RefuseFamily(
        string code,
        int familyStatus,
        string message,
        IReadOnlyDictionary<string, object?>? context = null) =>
        new(ProblemCatalogue.Find(code)?.Status ?? familyStatus, code, message, LogAsError: false, context);

    /// <summary>Anything unhandled: <c>internal-error</c>, logged as an error, saying nothing about internals.</summary>
    private static MappedError Fault() =>
        new(
            ProblemCatalogue.Get(ErrorCodes.InternalError).Status,
            ErrorCodes.InternalError,
            InternalErrorMessage,
            LogAsError: true);

    /// <summary>
    /// What <paramref name="exception"/> becomes on the wire. The first arm that matches wins, so the order is the
    /// contract: an arm for a derived type sits above the arm for its base ("Must stay ABOVE" on an arm says which).
    /// </summary>
    /// <remarks>
    /// The arms are grouped by area, one switch per group in <c>ApiExceptionMapper.&lt;Area&gt;.cs</c>, and the groups
    /// are asked in the order below; a group answers null for an exception it has no arm for, and the next is asked.
    /// Read top to bottom, the groups are the one switch they were cut from, arm for arm. A new arm goes into the
    /// group its position belongs in - above every arm for a type it derives from - and the general arms
    /// (<see cref="DomainStateException"/>, <see cref="UnauthorizedAccessException"/>,
    /// <see cref="KeyNotFoundException"/>, then the 500) stay last, in <see cref="MapFallbacks"/>.
    /// </remarks>
    public static MappedError Map(Exception exception) =>
        MapTables(exception)
        ?? MapOrders(exception)
        ?? MapContention(exception)
        ?? MapSignIn(exception)
        ?? MapBranchSettings(exception)
        ?? MapPayload(exception)
        ?? MapStaleWrites(exception)
        ?? MapVenueAvailability(exception)
        ?? MapBookings(exception)
        ?? MapAccounts(exception)
        ?? MapScan(exception)
        ?? MapBookingLimits(exception)
        ?? MapPreOrdersAndOnMyWay(exception)
        ?? MapTakeaway(exception)
        ?? MapMessages(exception)
        ?? MapNoShows(exception)
        ?? MapAdmission(exception)
        ?? MapTransfers(exception)
        ?? MapFallbacks(exception);

    /// <summary>The general arms every arm above must precede, and the 500 for anything unhandled.</summary>
    private static MappedError MapFallbacks(Exception exception) => exception switch
    {
        // "This session is already closed", "the settlement mode is locked". Note this must stay
        // BELOW InvalidTableTransitionException, which derives from it.
        //
        // Deliberately DomainStateException and not InvalidOperationException. .NET raises the
        // latter for a whole class of genuine bugs - an unresolved service, First() on an empty
        // sequence, "sequence contains more than one element", EF's transient-failure wrapper -
        // and catching it here reported every one of them as a 409 "you have a conflict", echoed
        // the internal message to the caller, and logged none of it as an error. Those now fall
        // through to the 500 below, where they are logged and say nothing about internals.
        DomainStateException e => Refuse(ErrorCodes.ConflictingState, e.Message),

        // On the tab, but not allowed this: a guest approving a joiner, a diner reassigning the
        // host. The message names the missing role. Must stay ABOVE the general arm it derives from.
        TabPermissionException e => Refuse(
            ErrorCodes.Forbidden,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["operation"] = e.Operation,
                ["requirement"] = e.Requirement,
            }),

        // A move from a manage link whose booking's account cannot take it - the app's 403 and code, in
        // words the link holder can act on, and never the account lock's 401: there is no session.
        ManageLinkAccountUnavailableException e => Refuse(ErrorCodes.PhoneNotVerified, e.Message),

        // A diner whose number was typed at sign-up and never proved, trying to book or open a tab
        // on it. Its own code, not the generic 403, because the app has something to offer - the
        // verify link - and no context, because the id is the caller's own and the operation is
        // in the message.
        PhoneNotVerifiedException e => Refuse(ErrorCodes.PhoneNotVerified, e.Message),

        UnauthorizedAccessException => Refuse(ErrorCodes.Forbidden, "You are not allowed to perform this action."),

        // No booking of the caller's has that code. Its own slug so the app can say "check the
        // code", and deliberately no context: somebody else's booking answers word for word the
        // same, and anything more would say which codes are live. Must stay ABOVE
        // KeyNotFoundException, which it derives from.
        BookingNotFoundException e => Refuse(ErrorCodes.BookingNotFound, e.Message),

        KeyNotFoundException e => Refuse(ErrorCodes.NotFound, e.Message),

        _ => Fault(),
    };
}
