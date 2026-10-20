using System.Text.Json.Serialization;
using Yalla.Domain.Enums;

namespace Yalla.Api.Errors;

/// <summary>
/// The declared shape of one failure family, so <c>context</c> is a real type on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> Every error answers with <see cref="UnifiedErrorEnvelope"/>, whose
/// <c>Context</c> is an <c>IReadOnlyDictionary&lt;string, object?&gt;</c>. That is right at runtime -
/// one envelope, one serialiser - and wrong in the schema, where it generates as
/// <c>Record&lt;string, unknown&gt;</c>. So every named error a client branches on arrives typed as
/// "some object", and the frontend reads <c>context.remainingAmd</c> with a cast and a prayer.
/// </para>
/// <para>
/// Prompt 3 argued that a generated client is only worth having if the schema is precise enough to
/// catch a mistake at compile time. The extensions were the hole in that argument. These types close
/// it: they are declaration-only, referenced from <c>Produces&lt;T&gt;(status)</c>, and describe
/// exactly the JSON the envelope already produces. Nothing constructs one at runtime - if one ever
/// diverges from what <see cref="ApiExceptionMapper"/> writes, that is the bug.
/// </para>
/// <para>
/// Each subtype redeclares only <c>Context</c>. The seven standard members live on the base and are
/// flattened into the derived schema, so a generated client sees one complete object rather than an
/// intersection it has to unwrap.
/// </para>
/// </remarks>
public abstract record ProblemShape
{
    /// <summary>A URI naming the problem type, built from <see cref="Code"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Short, stable summary of the kind of problem.</summary>
    public required string Title { get; init; }

    /// <summary>The HTTP status code, repeated in the body.</summary>
    public required int Status { get; init; }

    /// <summary>What went wrong this time, in words.</summary>
    public required string Detail { get; init; }

    /// <summary>The request path this happened on.</summary>
    public string? Instance { get; init; }

    /// <summary>The stable kebab-case slug. <b>This is what a client branches on.</b></summary>
    public required string Code { get; init; }

    /// <summary>Correlates this response with the one log entry written for it.</summary>
    public required string TraceId { get; init; }

    /// <summary>Field-level complaints, when the failure was about the payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Ordering and billing
// ---------------------------------------------------------------------------------------------

/// <summary>A dish on the order has sold out.</summary>
/// <param name="MenuItemId">Which item.</param>
/// <param name="ItemName">
/// Its name. The reason this family is typed at all: the client has to be able to say <i>which</i>
/// dish, and "your order could not be placed" sends the diner back to a waiter.
/// </param>
public sealed record MenuItemUnavailableContext(Guid MenuItemId, string ItemName);

/// <summary><c>menu-item-unavailable</c>, 409.</summary>
public sealed record MenuItemUnavailableProblem : ProblemShape
{
    public required MenuItemUnavailableContext Context { get; init; }
}

/// <summary>The bill has been asked for, so nothing more goes on the tab.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="Status">1 Open, 2 Closing, 3 Closed, 4 Abandoned.</param>
public sealed record TabNotAcceptingOrdersContext(Guid TabId, TabStatus Status);

/// <summary><c>tab-not-accepting-orders</c>, 409. Show the bill, not the menu.</summary>
public sealed record TabNotAcceptingOrdersProblem : ProblemShape
{
    public required TabNotAcceptingOrdersContext Context { get; init; }
}

/// <summary>The guest a staff order named, when the refusal is about them.</summary>
/// <param name="Field">
/// Which field named them: <c>onBehalfOfParticipantId</c>, or <c>items[i].participantId</c> with the
/// line's index in <c>items</c>.
/// </param>
/// <param name="ParticipantId">The guest named.</param>
/// <param name="ParticipantStatus">
/// On the 409 only: 1 PendingApproval (not let on yet) or 3 Removed (taken off by the host). Absent on
/// the 404, where the guest is not on this tab at all.
/// </param>
public sealed record NamedGuestContext(string Field, Guid ParticipantId, ParticipantStatus? ParticipantStatus);

/// <summary>
/// <c>not-found</c> (404) or <c>conflicting-state</c> (409) on a staff order, told apart by its
/// context: present only when a guest the order named is the cause.
/// </summary>
public sealed record NamedGuestRefusedProblem : ProblemShape
{
    /// <summary>
    /// The guest and the field that named them. <b>Absent</b> for every other cause of the same status
    /// and code - a dish not on any menu, another branch's dish, an unknown tab - which is how a
    /// client tells "that guest is no longer orderable for" from those.
    /// </summary>
    public NamedGuestContext? Context { get; init; }
}

/// <summary>Nobody is seated at the table a tab was asked for.</summary>
/// <param name="TableId">The table.</param>
/// <param name="TableLabel">What staff call it.</param>
/// <param name="Status">1 Free, 2 Held, 5 Out of service.</param>
public sealed record TableNotSeatedContext(Guid TableId, string TableLabel, TableStatus Status);

/// <summary><c>table-not-seated</c>, 409. Seat the party first, then open the tab.</summary>
public sealed record TableNotSeatedProblem : ProblemShape
{
    public required TableNotSeatedContext Context { get; init; }
}

/// <summary>Removing this line would reverse money already taken.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="LineId">The line.</param>
public sealed record LineAlreadyPaidContext(Guid TabId, Guid LineId);

/// <summary><c>line-already-paid</c>, 409. That is a refund, which is a different thing.</summary>
public sealed record LineAlreadyPaidProblem : ProblemShape
{
    public required LineAlreadyPaidContext Context { get; init; }
}

/// <summary>More was offered than the tab still owes.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="RequestedAmd">What was offered, in whole dram.</param>
/// <param name="RemainingAmd">
/// What is actually owed. The waiter is standing at the table holding notes, so this is the field
/// the refusal exists to carry.
/// </param>
public sealed record PaymentExceedsRemainingContext(Guid TabId, long RequestedAmd, long RemainingAmd);

/// <summary><c>payment-exceeds-remaining</c>, 409.</summary>
public sealed record PaymentExceedsRemainingProblem : ProblemShape
{
    public required PaymentExceedsRemainingContext Context { get; init; }
}

/// <summary>This table has asked for too many things too quickly.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="Limit">How many are allowed in the window.</param>
/// <param name="WindowMinutes">How long the window is.</param>
public sealed record ServiceRequestRateLimitedContext(Guid TabId, int Limit, int WindowMinutes);

/// <summary><c>service-request-rate-limited</c>, 429.</summary>
public sealed record ServiceRequestRateLimitedProblem : ProblemShape
{
    public required ServiceRequestRateLimitedContext Context { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Tables and bookings - the families that already existed and were just as stringly-typed
// ---------------------------------------------------------------------------------------------

/// <summary>Somebody else changed the table first.</summary>
/// <param name="TableId">The table.</param>
/// <param name="TableLabel">Its label on the floor.</param>
/// <param name="AttemptedFromStatus">What the caller believed it was.</param>
/// <param name="CurrentStatus">What it is now, so the client can redraw it.</param>
public sealed record TableStateConflictContext(
    Guid TableId,
    string TableLabel,
    TableStatus AttemptedFromStatus,
    TableStatus CurrentStatus);

/// <summary><c>table-state-conflict</c>, 409.</summary>
public sealed record TableStateConflictProblem : ProblemShape
{
    public required TableStateConflictContext Context { get; init; }
}

/// <summary>A queued command described a table that has since moved.</summary>
/// <param name="TableId">The table.</param>
/// <param name="TableLabel">Its label.</param>
/// <param name="ExpectedFromStatus">What the waiter was looking at when they tapped.</param>
/// <param name="CurrentStatus">What it is now.</param>
/// <param name="ClientCommandId">So the client can match it to its own queue entry.</param>
public sealed record PreconditionFailedContext(
    Guid TableId,
    string TableLabel,
    TableStatus ExpectedFromStatus,
    TableStatus CurrentStatus,
    Guid ClientCommandId);

/// <summary><c>precondition-failed</c>, 409. A stale replay, not a live race.</summary>
public sealed record PreconditionFailedProblem : ProblemShape
{
    public required PreconditionFailedContext Context { get; init; }
}

/// <summary>Another writer held the table for longer than the wait allows.</summary>
/// <param name="TableId">The table.</param>
/// <param name="TableLabel">Its label.</param>
/// <param name="TimeoutMilliseconds">How long it waited.</param>
/// <param name="Retryable">Always true. Stated so no client has to know which codes are retryable.</param>
public sealed record LockTimeoutContext(
    Guid TableId,
    string TableLabel,
    int TimeoutMilliseconds,
    bool Retryable);

/// <summary><c>reservation-lock-timeout</c> or <c>table-lock-timeout</c>, 503.</summary>
public sealed record LockTimeoutProblem : ProblemShape
{
    public required LockTimeoutContext Context { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Field-level validation - the shape every bounds refusal now answers in
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One field of the request that was refused.
/// </summary>
/// <param name="Field">
/// The property, in the casing the OpenAPI schema uses - <c>turnTimeMinutes</c>, or
/// <c>[2].closesAt</c> where the payload is an array. <b>This is what a form keys on.</b> It
/// deliberately is not an English label: the console used to map server prose back to inputs
/// through a lookup table, which stopped working the moment either side was translated.
/// </param>
/// <param name="Message">What is wrong with it, in a sentence.</param>
/// <param name="Bound">
/// Which rule broke: <c>min</c>, <c>max</c>, <c>range</c>, <c>required</c> or <c>conflict</c>.
/// </param>
/// <param name="Min">The lowest accepted value, where the bound has one.</param>
/// <param name="Max">The highest accepted value, where the bound has one.</param>
/// <param name="Value">What was supplied, so the message can quote it back.</param>
public sealed record FieldViolationShape(
    string Field,
    string Message,
    string? Bound,
    object? Min,
    object? Max,
    object? Value);

/// <summary>
/// Every field the request got wrong.
/// </summary>
/// <param name="Field">
/// The first offending field, for a form that can only highlight one input at a time.
/// </param>
/// <param name="Fields">
/// All of them. A single request can break six bounds, and returning one at a time makes an owner
/// submit six times to discover that.
/// </param>
public sealed record ValidationFailedContext(string Field, IReadOnlyList<FieldViolationShape> Fields);

/// <summary>
/// <c>validation-failed</c>, 422. The reservation policy and opening-hours refusals, and every
/// other field-level refusal that used to carry prose and nothing else.
/// </summary>
/// <remarks>
/// The standard <c>errors</c> member carries the same complaints keyed by field, for generic
/// tooling that already knows RFC 7807. <c>context</c> carries them again with the bound and the
/// value, which <c>errors</c> has nowhere to put.
/// </remarks>
public sealed record ValidationFailedProblem : ProblemShape
{
    public required ValidationFailedContext Context { get; init; }
}

/// <summary>The branch's menu is not finished, so it cannot start taking diners.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="IncompleteMenuItemCount">
/// How many items are missing a photo, a description, ingredients, allergens, a portion size or a
/// prep time. The number the refusal exists to carry - the readiness endpoint lists which ones.
/// </param>
public sealed record BranchNotReadyContext(Guid BranchId, int IncompleteMenuItemCount);

/// <summary><c>branch-not-ready</c>, 409. Finish the menu, then switch the tier.</summary>
public sealed record BranchNotReadyProblem : ProblemShape
{
    public required BranchNotReadyContext Context { get; init; }
}

/// <summary>The floor plan the editor sent could not be applied.</summary>
/// <param name="Errors">Every reason, one sentence each, naming the tables and rooms involved.</param>
/// <param name="DuplicateLabels">Labels used more than once.</param>
public sealed record FloorPlanRejectedContext(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> DuplicateLabels);

/// <summary><c>floor-plan-invalid</c>, 422.</summary>
public sealed record FloorPlanRejectedProblem : ProblemShape
{
    public required FloorPlanRejectedContext Context { get; init; }
}

/// <summary>The floor plan was saved by somebody else since the editor loaded it.</summary>
/// <param name="CurrentVersion">The revision now stored. Reload the plan; it carries this as <c>version</c>.</param>
public sealed record FloorPlanChangedContext(string CurrentVersion);

/// <summary><c>floor-plan-changed</c>, 409. Nothing was written.</summary>
public sealed record FloorPlanChangedProblem : ProblemShape
{
    public required FloorPlanChangedContext Context { get; init; }
}

/// <summary>The tables a delete or a plan save would have taken away, and the bookings that hold them.</summary>
/// <param name="Tables">The labels of the tables refused, in label order.</param>
/// <param name="Bookings">The codes of the upcoming bookings on them, in code order.</param>
public sealed record TableHasFutureBookingsContext(IReadOnlyList<string> Tables, IReadOnlyList<string> Bookings);

/// <summary><c>table-has-future-bookings</c>, 409. Move or cancel the bookings first. Nothing was written.</summary>
public sealed record TableHasFutureBookingsProblem : ProblemShape
{
    public required TableHasFutureBookingsContext Context { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Reservations and sign-in
// ---------------------------------------------------------------------------------------------

/// <summary>"Keep my table" was refused.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="StartUtc">When it starts - so a refusal before then can say when to ask again.</param>
public sealed record HoldExtensionRefusedContext(Guid ReservationId, DateTime StartUtc);

/// <summary>
/// <c>hold-not-active</c>, <c>hold-already-extended</c> or <c>extensions-not-offered</c>, 409. Branch
/// on the code: the three need three different sentences.
/// </summary>
public sealed record HoldExtensionRefusedProblem : ProblemShape
{
    public required HoldExtensionRefusedContext Context { get; init; }
}

/// <summary>A diner's username, email or phone number is already somebody else's.</summary>
/// <param name="Field">
/// The request field to highlight: <c>username</c>, <c>email</c> or <c>phoneE164</c>. The same
/// information the code carries, in the form a form keys on.
/// </param>
public sealed record IdentifierTakenContext(string Field);

/// <summary>
/// <c>username-taken</c>, <c>email-taken</c> or <c>phone-in-use</c>, 409. Branch on the code: the
/// third is the one that offers "log in with a code instead", because the number's owner can
/// prove it and a stranger typing it cannot.
/// </summary>
public sealed record IdentifierTakenProblem : ProblemShape
{
    public required IdentifierTakenContext Context { get; init; }
}

/// <summary>A one-time code that was not accepted, and what is left on it.</summary>
/// <param name="AttemptsRemaining">
/// Tries left on the number's live code. <b>Zero</b> when there is no live code, or the last try was
/// just spent: say "ask for a new code", not "try again".
/// </param>
public sealed record VerificationCodeInvalidContext(int AttemptsRemaining);

/// <summary>
/// <c>verification-code-invalid</c>, 401, with the count. <c>verification-code-expired</c> arrives on
/// the same status with no <c>context</c>.
/// </summary>
public sealed record VerificationCodeInvalidProblem : ProblemShape
{
    public VerificationCodeInvalidContext? Context { get; init; }
}

/// <summary>"I'm at my table" was refused, and the facts a sentence about it needs.</summary>
/// <param name="ReservationId">The booking.</param>
/// <param name="Status">
/// Its status: 1 PendingApproval, 2 Confirmed, 4 Seated, 5 Completed, 6 CancelledByDiner,
/// 7 CancelledByVenue, 8 NoShow. What tells the kinds of <c>booking-not-active</c> apart.
/// </param>
/// <param name="StartUtc">When the booking starts.</param>
/// <param name="EndUtc">When it ends. From this instant on, <c>booking-ended</c>.</param>
/// <param name="EarliestUtc">
/// When the branch starts holding the table for the party - the earliest the code opens the tab.
/// Before it, <c>booking-too-early</c>: render it in the branch's time zone, "from 19:10".
/// </param>
/// <param name="TableLabel">
/// The booked table's label on <c>booking-table-occupied</c>, for "Table 1 still has another party
/// seated". Null on the other codes.
/// </param>
public sealed record BookingTabRefusedContext(
    Guid ReservationId,
    ReservationStatus Status,
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime EarliestUtc,
    string? TableLabel);

/// <summary>
/// <c>booking-too-early</c>, <c>booking-ended</c>, <c>booking-not-active</c> or
/// <c>booking-table-occupied</c> (another party still seated at the booked table), 409, with the
/// booking's facts. Branch on the code. The other 409s the route shares with the scan - the table out
/// of service, the tab being settled, the command id taken by another device - arrive on the same
/// status with no <c>context</c>.
/// </summary>
public sealed record BookingTabRefusedProblem : ProblemShape
{
    public BookingTabRefusedContext? Context { get; init; }
}

/// <summary>
/// The most active bookings one number holds, and the caller's own earliest active booking - named to the account
/// that holds it, and only to it.
/// </summary>
/// <param name="MaxActiveBookings">The limit: 3.</param>
/// <param name="ReservationId">That booking.</param>
/// <param name="Code">Its door code.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="LocalDate">Its date on the branch's calendar, e.g. <c>2026-10-22</c>.</param>
/// <param name="LocalStartTime">Its start on the branch's wall clock, e.g. <c>19:30:00</c>.</param>
/// <param name="VenueName">Where.</param>
/// <param name="BranchName">Which branch.</param>
public sealed record PhoneHasActiveBookingContext(
    int MaxActiveBookings,
    Guid? ReservationId,
    string? Code,
    DateTime? StartUtc,
    DateOnly? LocalDate,
    TimeOnly? LocalStartTime,
    string? VenueName,
    string? BranchName);

/// <summary>
/// <c>phone-has-active-booking</c>, 409: the booker's proved number already holds three pending or confirmed
/// bookings that are not over, at any venue - the most it holds (decided 2026-10-20, replacing one per number).
/// <c>context.maxActiveBookings</c> is the limit; the booking members name the caller's own earliest active
/// booking (absent only for a booking made without proof while Yalla#54 allowed it).
/// </summary>
public sealed record PhoneHasActiveBookingProblem : ProblemShape
{
    public required PhoneHasActiveBookingContext Context { get; init; }
}

/// <summary>The tab waiting for a waiter.</summary>
/// <param name="TabId">The tab.</param>
public sealed record TabAwaitingAdmissionContext(Guid TabId);

/// <summary>
/// <c>tab-awaiting-admission</c>, 409: a scan with no proved number opened this tab, and a waiter has not
/// let it order yet. Show "a waiter will be with you", not an error; read the tab again on its
/// <c>TabAdmitted</c> event.
/// </summary>
public sealed record TabAwaitingAdmissionProblem : ProblemShape
{
    public required TabAwaitingAdmissionContext Context { get; init; }
}

/// <summary>A tab whose admission was already decided the other way.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="Admission">2 admitted, 3 turned away.</param>
public sealed record TabAdmissionDecidedContext(Guid TabId, TabAdmission Admission);

/// <summary><c>tab-admission-decided</c>, 409: admitting a tab already turned away, or the other way round.</summary>
public sealed record TabAdmissionDecidedProblem : ProblemShape
{
    public required TabAdmissionDecidedContext Context { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Reviews
// ---------------------------------------------------------------------------------------------

/// <summary>A first review of a branch the diner has not visited in the window.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="WindowDays">How far back a visit counts, in days. The app says "after a visit" with it.</param>
public sealed record ReviewNeedsVisitContext(Guid BranchId, int WindowDays);

/// <summary>
/// <c>review-needs-visit</c>, 403, with the window. <c>phone-not-verified</c> arrives on the same status
/// with no <c>context</c>: branch on the code.
/// </summary>
public sealed record ReviewNeedsVisitProblem : ProblemShape
{
    public ReviewNeedsVisitContext? Context { get; init; }
}

/// <summary>When a guest blocked for no-shows may book again.</summary>
/// <param name="BlockedUntilUtc">The end of the block.</param>
public sealed record BookingBlockedContext(DateTime BlockedUntilUtc);

/// <summary>
/// <c>booking-blocked-no-shows</c>, 403: the guest missed three bookings in three days, every booking of theirs not
/// yet started was cancelled, and they cannot book or move a booking until <c>context.blockedUntilUtc</c>.
/// </summary>
public sealed record BookingBlockedProblem : ProblemShape
{
    public required BookingBlockedContext Context { get; init; }
}
