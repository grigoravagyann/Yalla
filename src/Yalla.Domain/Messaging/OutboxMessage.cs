using Yalla.Domain.Common;

namespace Yalla.Domain.Messaging;

/// <summary>
/// One message that must be sent exactly once, from a background process, possibly long after the
/// request that caused it finished.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written in the same <c>SaveChanges</c> as its cause.</b> A reservation and its reminder are one
/// transaction or neither happened. The alternative - book, commit, then enqueue - has a window in
/// which the booking exists and the reminder does not, and the diner finds out by not being
/// reminded.
/// </para>
/// <para>
/// <b><see cref="IdempotencyKey"/> is derived from the cause</b>, not generated: <c>reservation:{id}:reminder</c>.
/// With a unique index behind it, writing the same message twice is impossible rather than merely
/// unlikely, and no amount of retrying a request can produce two reminders for one booking.
/// </para>
/// <para>
/// <b>Cancelling the cause cancels the message.</b> A cancelled booking's unsent reminder is deleted
/// in the same transaction as the cancellation - a push arriving for a booking somebody cancelled an
/// hour ago is worse than no push at all, because it teaches the diner the notifications are wrong.
/// </para>
/// </remarks>
public sealed class OutboxMessage : Entity
{
    /// <summary>What kind of message, e.g. <c>reservation.reminder</c>. Routes it to a handler.</summary>
    public string Type { get; private set; } = null!;

    /// <summary>Everything the handler needs, as JSON. Self-contained on purpose.</summary>
    /// <remarks>
    /// A payload that only carried ids would have to re-read the world at send time, and the world
    /// has moved: the booking may be cancelled, the diner may have changed their name. What was true
    /// when the message was written is what the message should say, so it carries it.
    /// </remarks>
    public string PayloadJson { get; private set; } = null!;

    /// <summary>When it becomes due. A reminder three hours before a booking is written today.</summary>
    public DateTime ScheduledForUtc { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>
    /// While set and in the future, another dispatcher must leave this alone.
    /// </summary>
    /// <remarks>
    /// A lease rather than a lock: a process that takes a message and dies does not hold it for ever,
    /// because the lease simply expires and somebody else picks it up. Worth less on one laptop than
    /// it will be later, and it cannot be retrofitted casually once messages are live.
    /// </remarks>
    public DateTime? LockedUntilUtc { get; private set; }

    /// <summary>Which dispatcher holds the lease. For working out which log to read.</summary>
    public string? LockedBy { get; private set; }

    public DateTime? SentAtUtc { get; private set; }

    /// <summary>Why the last attempt failed. Kept even after a later attempt succeeds.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Set when this stopped being retried. Nothing silently vanishes and nothing retries for ever.
    /// </summary>
    public DateTime? DeadLetteredAtUtc { get; private set; }

    /// <summary>Derived from the cause, unique across the table. See the type's remarks.</summary>
    public string IdempotencyKey { get; private set; } = null!;

    public bool IsSent => SentAtUtc is not null;

    public bool IsDeadLettered => DeadLetteredAtUtc is not null;

    private OutboxMessage()
    {
    }

    public OutboxMessage(
        string type,
        string payloadJson,
        DateTime scheduledForUtc,
        string idempotencyKey,
        DateTime createdAtUtc)
        : base(Guid.CreateVersion7())
    {
        Type = Guard.NotBlank(type, nameof(type), FieldLengths.MessageType);
        PayloadJson = Guard.NotBlank(payloadJson, nameof(payloadJson), int.MaxValue);
        ScheduledForUtc = Guard.NotLocalTime(scheduledForUtc, nameof(scheduledForUtc));
        IdempotencyKey = Guard.NotBlank(idempotencyKey, nameof(idempotencyKey), FieldLengths.IdempotencyKey);
        StampCreatedAt(createdAtUtc);
    }

    /// <summary>Whether this is due, unsent, not dead and not held by somebody else.</summary>
    public bool IsDispatchable(DateTime nowUtc) =>
        !IsSent
        && !IsDeadLettered
        && ScheduledForUtc <= nowUtc
        && (LockedUntilUtc is null || LockedUntilUtc <= nowUtc);

    /// <summary>Takes the lease. The caller must have re-read this row under a transaction.</summary>
    public void Lease(string owner, DateTime untilUtc)
    {
        LockedBy = Guard.NotBlank(owner, nameof(owner), FieldLengths.DeviceName);
        LockedUntilUtc = Guard.NotLocalTime(untilUtc, nameof(untilUtc));
    }

    public void MarkSent(DateTime atUtc)
    {
        SentAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        LockedUntilUtc = null;
        LockedBy = null;
    }

    /// <summary>
    /// Records a failure and schedules the next attempt, or gives up.
    /// </summary>
    /// <remarks>
    /// The backoff grows and is capped, so a provider having a bad hour does not turn into a tight
    /// loop against it. Past <paramref name="maxAttempts"/> the message is dead-lettered with the
    /// error that killed it: somebody has to be able to find out what happened, and a row that
    /// silently stopped is indistinguishable from one that was never written.
    /// </remarks>
    public void RecordFailure(string error, DateTime nowUtc, int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        AttemptCount++;
        LastError = Truncate(error);
        LockedUntilUtc = null;
        LockedBy = null;

        if (AttemptCount >= maxAttempts)
        {
            DeadLetteredAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
            return;
        }

        var backoff = TimeSpan.FromTicks(Math.Min(
            baseDelay.Ticks * (long)Math.Pow(2, AttemptCount - 1),
            maxDelay.Ticks));

        ScheduledForUtc = nowUtc + backoff;
    }

    /// <summary>
    /// Gives up on a message without trying it, and says why.
    /// </summary>
    /// <remarks>
    /// The laptop-was-asleep case. A reminder for a booking that already happened is not worth
    /// sending, and sending it is worse than not: the diner is told to come to dinner they have
    /// already eaten.
    /// </remarks>
    public void DeadLetter(string reason, DateTime nowUtc)
    {
        LastError = Truncate(reason);
        DeadLetteredAtUtc = Guard.NotLocalTime(nowUtc, nameof(nowUtc));
        LockedUntilUtc = null;
        LockedBy = null;
    }

    private static string Truncate(string text) =>
        text.Length <= FieldLengths.ErrorText ? text : text[..FieldLengths.ErrorText];
}
