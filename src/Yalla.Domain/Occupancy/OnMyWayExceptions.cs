using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// "Ask: are you coming?" for a booking that is not confirmed, or has already started: 409
/// <c>ask-on-my-way-not-open</c>, <c>context</c> <c>reservationId</c>, <c>status</c>, <c>startUtc</c>.
/// </summary>
public sealed class AskOnMyWayNotOpenException(Guid reservationId, ReservationStatus status, DateTime startUtc)
    : DomainStateException("The guest can be asked only about a confirmed booking, until it starts.")
{
    public const string ErrorCode = "ask-on-my-way-not-open";

    public Guid ReservationId { get; } = reservationId;

    public ReservationStatus Status { get; } = status;

    public DateTime StartUtc { get; } = startUtc;
}

/// <summary>
/// "Ask: are you coming?" more than two hours before the start: 409 <c>ask-on-my-way-too-early</c>, <c>context</c>
/// <c>reservationId</c>, <c>opensAtUtc</c> (start - 2 h).
/// </summary>
public sealed class AskOnMyWayTooEarlyException(Guid reservationId, DateTime opensAtUtc)
    : DomainStateException("Too early to ask: the guest can be asked from two hours before the booking.")
{
    public const string ErrorCode = "ask-on-my-way-too-early";

    public Guid ReservationId { get; } = reservationId;

    public DateTime OpensAtUtc { get; } = opensAtUtc;
}

/// <summary>
/// The guest has answered already - on my way, or running late: 409 <c>guest-already-answered</c>, <c>context</c>
/// <c>reservationId</c>, <c>answeredAtUtc</c>, and <c>runningLateMinutes</c> when that was the answer.
/// </summary>
public sealed class GuestAlreadyAnsweredException(Guid reservationId, DateTime answeredAtUtc, int? runningLateMinutes)
    : DomainStateException("The guest has already answered.")
{
    public const string ErrorCode = "guest-already-answered";

    public Guid ReservationId { get; } = reservationId;

    public DateTime AnsweredAtUtc { get; } = answeredAtUtc;

    public int? RunningLateMinutes { get; } = runningLateMinutes;
}

/// <summary>
/// Asked within the last fifteen minutes: 409 <c>ask-on-my-way-asked-recently</c>, <c>context</c> <c>reservationId</c>,
/// <c>askedAtUtc</c>, <c>nextAllowedAtUtc</c>.
/// </summary>
public sealed class AskOnMyWayAskedRecentlyException(Guid reservationId, DateTime askedAtUtc, DateTime nextAllowedAtUtc)
    : DomainStateException("The guest was asked a moment ago. Ask again later, or call.")
{
    public const string ErrorCode = "ask-on-my-way-asked-recently";

    public Guid ReservationId { get; } = reservationId;

    public DateTime AskedAtUtc { get; } = askedAtUtc;

    public DateTime NextAllowedAtUtc { get; } = nextAllowedAtUtc;
}
