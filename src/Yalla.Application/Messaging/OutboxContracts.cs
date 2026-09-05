namespace Yalla.Application.Messaging;

/// <summary>How the dispatcher behaves. All of it configurable, none of it guessed at a call site.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>How often the dispatcher looks for due messages.</summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>How many to take in one pass.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>How long a lease is held before another dispatcher may take the message.</summary>
    /// <remarks>
    /// Long enough to cover a slow provider call, short enough that a process which died mid-send
    /// does not strand the message for an hour.
    /// </remarks>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>Attempts before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>First retry delay. Doubles each attempt, capped by <see cref="MaxBackoffSeconds"/>.</summary>
    public int BaseBackoffSeconds { get; set; } = 30;

    public int MaxBackoffSeconds { get; set; } = 3600;

    /// <summary>
    /// How overdue a message may be and still be worth sending.
    /// </summary>
    /// <remarks>
    /// <b>This exists because a laptop sleeps.</b> Due messages pile up while it is shut and would
    /// all fire at once on wake - including a reminder for a booking that happened yesterday, which
    /// is worse than no reminder. Anything overdue by more than this is dead-lettered with a reason
    /// and logged, rather than sent.
    /// </remarks>
    public int StaleAfterMinutes { get; set; } = 60;

    /// <summary>Whether the background dispatcher runs at all. Off in tests that drive it by hand.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>What the platform view shows about the outbox.</summary>
/// <param name="Pending">Written, not yet sent, not dead. Includes ones not yet due.</param>
/// <param name="Due">Pending and due now - the backlog, if there is one.</param>
/// <param name="Failed">Pending with at least one failed attempt behind them.</param>
/// <param name="DeadLettered">Given up on. These need a person.</param>
/// <param name="SentLastDay">Sent in the last twenty-four hours, so "nothing is sending" is visible.</param>
/// <param name="DeadLetters">The dead-lettered messages themselves, newest first.</param>
public sealed record OutboxHealthView(
    int Pending,
    int Due,
    int Failed,
    int DeadLettered,
    int SentLastDay,
    IReadOnlyList<DeadLetterView> DeadLetters);

/// <summary>One message nobody is going to retry.</summary>
/// <param name="Id">The message.</param>
/// <param name="Type">What it was, e.g. <c>reservation.reminder</c>.</param>
/// <param name="IdempotencyKey">What caused it - this names the booking or the tab.</param>
/// <param name="ScheduledForUtc">When it was due.</param>
/// <param name="AttemptCount">How many times it was tried before being given up on.</param>
/// <param name="DeadLetteredAtUtc">When it was given up on.</param>
/// <param name="LastError">Why. The first thing to read.</param>
public sealed record DeadLetterView(
    Guid Id,
    string Type,
    string IdempotencyKey,
    DateTime ScheduledForUtc,
    int AttemptCount,
    DateTime? DeadLetteredAtUtc,
    string? LastError);

/// <summary>
/// Writing messages into the outbox, alongside the change that causes them.
/// </summary>
/// <remarks>
/// Deliberately has no <c>SaveChangesAsync</c>. The caller saves, and the message goes in the same
/// transaction as its cause - which is the entire point of an outbox and the one thing that stops
/// being true the moment this interface grows a save of its own.
/// </remarks>
public interface IOutbox
{
    /// <summary>
    /// Queues a message to be sent at <paramref name="scheduledForUtc"/>.
    /// </summary>
    /// <param name="type">Routes it to a handler.</param>
    /// <param name="payload">Serialised as JSON. Self-contained - see <c>OutboxMessage</c>.</param>
    /// <param name="scheduledForUtc">When it becomes due. Now, for anything immediate.</param>
    /// <param name="idempotencyKey">
    /// Derived from the cause, e.g. <c>reservation:{id}:reminder</c>. Unique across the table, so the
    /// same message cannot be written twice however many times the request is retried.
    /// </param>
    void Enqueue(string type, object payload, DateTime scheduledForUtc, string idempotencyKey);

    /// <summary>
    /// Removes unsent messages whose cause has gone away, in the caller's transaction.
    /// </summary>
    /// <remarks>
    /// Cancelling a booking cancels its reminder. A push arriving for something somebody cancelled an
    /// hour ago is worse than silence, because it is the notification the diner remembers.
    /// </remarks>
    Task CancelAsync(string idempotencyKeyPrefix, CancellationToken cancellationToken = default);

    /// <summary>What the platform admin sees. <b>Platform admin only.</b></summary>
    Task<OutboxHealthView> GetHealthAsync(int deadLetterLimit = 50, CancellationToken cancellationToken = default);
}

/// <summary>Sends one outbox message. One implementation per message type.</summary>
/// <remarks>
/// The dispatcher knows nothing about reminders or order-ready pushes; it knows about leases,
/// backoff and dead-lettering. Adding a message type is a new handler and a new
/// <c>OutboxMessageTypes</c> constant, and the dispatcher does not change.
/// </remarks>
public interface IOutboxHandler
{
    /// <summary>The <c>Type</c> this handles.</summary>
    string MessageType { get; }

    /// <summary>
    /// Sends it. Throwing means "retry later"; returning means it is done.
    /// </summary>
    Task HandleAsync(string payloadJson, CancellationToken cancellationToken);
}

/// <summary>The message types, in one place so a handler and a producer cannot disagree.</summary>
public static class OutboxMessageTypes
{
    public const string ReservationReminder = "reservation.reminder";

    public const string ReservationLateNudge = "reservation.late-nudge";

    public const string ReservationDecided = "reservation.decided";

    public const string ParticipantApproved = "tab.participant-approved";

    public const string OrderReady = "tab.order-ready";

    /// <summary>The key that names what caused a message, so cancelling the cause can find it.</summary>
    public static string KeyFor(string subject, Guid id, string what) => $"{subject}:{id}:{what}";

    /// <summary>Everything caused by one subject, for cancellation.</summary>
    public static string PrefixFor(string subject, Guid id) => $"{subject}:{id}:";
}
