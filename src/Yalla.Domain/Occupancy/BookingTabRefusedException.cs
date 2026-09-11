using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// "I'm at my table" was refused, and which of three refusals it was.
/// </summary>
/// <remarks>
/// <para>
/// The diner app turns each into its own sentence - "your table is yours from 19:10", "this booking
/// is over", "this booking is still waiting for the venue" or "was cancelled" - so each has its own
/// code on the wire rather than one conflict the app would have to guess about. The same facts
/// travel with all three: which booking, what state it is in, and the three instants a sentence
/// about it might need.
/// </para>
/// <para>
/// Still a <see cref="DomainStateException"/>, like <see cref="HoldExtensionRefusedException"/>, so
/// anything that handles a refusal in general still does; the API maps this one first, with its code.
/// </para>
/// </remarks>
public sealed class BookingTabRefusedException(
    string code,
    Reservation reservation,
    DateTime earliestUtc,
    string message)
    : DomainStateException(message)
{
    /// <summary>
    /// Confirmed, but the table is not being held for the party yet. It opens from
    /// <see cref="EarliestUtc"/>.
    /// </summary>
    public const string TooEarly = "booking-too-early";

    /// <summary>The booked interval is over, or the sitting the booking produced has finished.</summary>
    public const string Ended = "booking-ended";

    /// <summary>
    /// The booking is not expecting its party: still waiting for the venue's approval, cancelled by
    /// either side, or released as a no-show. <see cref="Status"/> says which.
    /// </summary>
    public const string NotActive = "booking-not-active";

    /// <summary>Which refusal: one of the three constants above. Reaches the wire as the error code.</summary>
    public string Code { get; } = code;

    /// <summary>The booking.</summary>
    public Guid ReservationId { get; } = reservation.Id;

    /// <summary>Its status, which tells the kinds of not-active apart.</summary>
    public ReservationStatus Status { get; } = reservation.Status;

    /// <summary>When the booking starts.</summary>
    public DateTime StartUtc { get; } = reservation.StartUtc;

    /// <summary>When it ends.</summary>
    public DateTime EndUtc { get; } = reservation.EndUtc;

    /// <summary>
    /// When the branch starts holding the table for the party - the earliest the booking code opens
    /// the tab.
    /// </summary>
    public DateTime EarliestUtc { get; } = earliestUtc;
}
