namespace Yalla.Domain.Occupancy;

/// <summary>
/// A no-show was recorded against a booking whose start has not come yet.
/// </summary>
/// <remarks>
/// <para>
/// A no-show is a statement that the party did not come, and nobody can have failed to come before they
/// were due. It also counts against the diner's record, so recording one early is not a harmless slip:
/// the tablet's hold attaches to the table's next booking whatever day it is on, and a waiter clearing
/// that hold with the wrong button would have put a no-show on somebody due tomorrow.
/// </para>
/// <para>
/// <b>The start is the line, with no grace.</b> Whether a party a few minutes past their time is late or
/// absent is the waiter's call - only a person in the room knows - so nothing waits for the branch's grace
/// period. Letting a booking go as the venue's doing, and letting a hold go, are allowed at any time.
/// Still a <see cref="DomainStateException"/>, so anything that handles a refused state handles this; the
/// API maps it first, with its own code.
/// </para>
/// </remarks>
public sealed class ReservationNotStartedException(Guid reservationId, string code, DateTime startUtc)
    : DomainStateException(
        $"Booking {code} has not started yet, so nobody can have failed to come. A no-show can be recorded "
        + "from its start; to let it go before then, release it as cancelled by the venue.")
{
    /// <summary>The booking.</summary>
    public Guid ReservationId { get; } = reservationId;

    /// <summary>When it starts - the earliest a no-show may be recorded.</summary>
    public DateTime StartUtc { get; } = startUtc;
}
