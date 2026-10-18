namespace Yalla.Domain.Occupancy;

/// <summary>
/// Staff tried to approve or decline a booking that is waiting on the guest before it on the table to
/// answer in the app: "will you have left by 19:40?" is theirs to answer, not the floor's.
/// </summary>
/// <remarks>
/// A <b>409</b> with its own code, <c>reservation-awaiting-earlier-guest</c>. Approving would tell the
/// earlier guest they had agreed to leave by a time they were never asked about; declining would answer
/// for them too. The answer comes from them, or nobody answers and the sweep declines it at
/// <see cref="ExpiresAtUtc"/>. A booking the floor decides - the earlier guest booked on the web or has no
/// phone, or the booking needs a human anyway - is the floor's as before.
/// </remarks>
public sealed class ReservationAwaitingEarlierGuestException(
    Guid reservationId,
    Guid requestId,
    DateTime expiresAtUtc)
    : DomainStateException(
        "This booking is waiting for the guest before it on the table to answer in the app whether they will "
        + "have left in time. It is theirs to answer; it is decided when they do, or declined if they do not.")
{
    /// <summary>The waiting booking.</summary>
    public Guid ReservationId { get; } = reservationId;

    /// <summary>The question it is waiting on.</summary>
    public Guid RequestId { get; } = requestId;

    /// <summary>When the question runs out.</summary>
    public DateTime ExpiresAtUtc { get; } = expiresAtUtc;
}
