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
/// <param name="TablesOutsideCanvas">Labels of tables that fall outside the canvas.</param>
/// <param name="DuplicateLabels">Labels used more than once.</param>
public sealed record FloorPlanRejectedContext(
    IReadOnlyList<string> TablesOutsideCanvas,
    IReadOnlyList<string> DuplicateLabels);

/// <summary><c>floor-plan-invalid</c>, 422.</summary>
public sealed record FloorPlanRejectedProblem : ProblemShape
{
    public required FloorPlanRejectedContext Context { get; init; }
}
