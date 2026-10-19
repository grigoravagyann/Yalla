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
/// Raised only by the manage-link move, around the diner's move; the rule itself is unchanged.
/// </para>
/// </remarks>
public sealed class ManageLinkOverlapsOwnException()
    : DomainStateException(
        "There is already another booking under the same phone number at this branch around that time. "
        + "Choose another time, or change or cancel that booking first.");
