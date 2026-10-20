namespace Yalla.Domain.Occupancy;

/// <summary>
/// <see cref="ReservationOverlapsOwnException"/> as somebody holding only a booking's manage link is
/// told it: the same refusal, naming nothing about the other booking.
/// </summary>
/// <remarks>
/// <para>
/// The same <b>409</b> and the same code, <c>reservation-overlaps-own</c>, with <b>no context</b> and a
/// message that carries no time, table or code. The diner's own refusal names the booking already held
/// so the app can offer to change that one - the diner is signed in, and it is theirs to see. A manage
/// link is a URL that goes round a group chat, and it shows one booking: what the diner has booked
/// besides, and when, is not something a refusal on it should hand to whoever opened it.
/// </para>
/// <para>
/// <b>What it does not hide</b> is that such a booking exists. The writer checks the diner's own
/// bookings before the table's own clash, so a link holder aiming at a taken table learns from the code
/// alone whether the diner holds another table at this venue around then - and nothing moves either
/// way. Accepted: the check order is shared with the app, and the person holding the link is already in
/// the diner's circle, at the same venue.
/// </para>
/// <para>
/// Raised only by the manage-link move, around the diner's move; the rule itself is unchanged.
/// </para>
/// </remarks>
public sealed class ManageLinkOverlapsOwnException()
    : DomainStateException(
        "There is already another booking under the same phone number at this branch around that time. "
        + "Choose another time, or change or cancel that booking first.");
