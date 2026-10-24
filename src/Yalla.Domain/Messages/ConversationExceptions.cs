namespace Yalla.Domain.Messages;

/// <summary>
/// Writing to a thread that has closed: 409 <c>conversation-closed</c>, <c>context.subjectKind</c>, <c>subjectId</c> and
/// <c>closedAtUtc</c>. The thread can still be read.
/// </summary>
public sealed class ConversationClosedException(string subjectKind, Guid subjectId, DateTime closedAtUtc)
    : DomainStateException("This conversation is closed. Call the venue instead.")
{
    public const string ErrorCode = "conversation-closed";

    /// <summary><c>reservation</c> or <c>takeaway</c>.</summary>
    public string SubjectKind { get; } = subjectKind;

    public Guid SubjectId { get; } = subjectId;

    public DateTime ClosedAtUtc { get; } = closedAtUtc;
}

/// <summary>
/// A guest's text message where the venue has switched guest messages off: 403 <c>guests-cannot-write</c>,
/// <c>context.branchId</c>. The answer buttons still work.
/// </summary>
public sealed class GuestsCannotWriteException(Guid branchId)
    : Exception("This venue does not take written messages. Answer with the buttons, or call the venue.")
{
    public const string ErrorCode = "guests-cannot-write";

    public Guid BranchId { get; } = branchId;
}

/// <summary>
/// A waiter writing to a guest where the venue keeps messages to managers: 403 <c>waiters-cannot-write</c>,
/// <c>context.branchId</c>.
/// </summary>
public sealed class WaitersCannotWriteException(Guid branchId)
    : Exception("At this venue only managers write to guests.")
{
    public const string ErrorCode = "waiters-cannot-write";

    public Guid BranchId { get; } = branchId;
}

/// <summary>
/// A guest writing faster than a person types: 429 <c>messages-too-fast</c>, <c>context.conversationId</c>,
/// <c>perMinute</c>, <c>perConversation</c>, and <c>retryAfterUtc</c> when waiting helps (absent once the thread's own
/// limit is spent).
/// </summary>
/// <param name="conversationId">The thread.</param>
/// <param name="retryAfterUtc">When the next one will be taken; null when none will.</param>
/// <param name="nowUtc">
/// The service's clock when it refused - the <c>Retry-After</c> header is counted from it, never from the machine's own
/// clock, so the header and <c>retryAfterUtc</c> always say the same moment.
/// </param>
public sealed class MessagesTooFastException(Guid conversationId, DateTime? retryAfterUtc, DateTime nowUtc)
    : Exception(retryAfterUtc is null
        ? "This conversation has had as many messages from you as it takes. Call the venue instead."
        : "You are sending messages too fast. Wait a moment.")
{
    public const string ErrorCode = "messages-too-fast";

    public Guid ConversationId { get; } = conversationId;

    /// <summary>When the next one will be taken; null when none will.</summary>
    public DateTime? RetryAfterUtc { get; } = retryAfterUtc;

    /// <summary>How long until <see cref="RetryAfterUtc"/>, on the clock that refused; null when no wait helps.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfterUtc - nowUtc;
}
