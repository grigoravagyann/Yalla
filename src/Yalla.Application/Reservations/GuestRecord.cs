namespace Yalla.Application.Reservations;

/// <summary>
/// How a guest has kept their bookings over the last twelve months, at every venue - what a venue sees beside a
/// booking before it decides, and what the guest sees of themselves.
/// </summary>
/// <remarks>
/// <para>
/// Counted only from bookings the guest's own <b>verified account</b> made (<c>Reservation.DinerUserId</c>, made
/// with a proved number): never from a phone number typed on somebody else's booking, so nobody can spoil another
/// person's record. Bookings whose start is within the last <see cref="Domain.Occupancy.GuestRecordRules.WindowDays"/>
/// days and has passed. No venue names, no dates, nothing per booking - see <c>docs/reservations.md</c>, "Guest
/// record", and its privacy note.
/// </para>
/// </remarks>
/// <param name="Booked">
/// Bookings whose time came, exactly <paramref name="Came"/> + <paramref name="NoShows"/> +
/// <paramref name="LateCancellations"/> + <paramref name="Unmarked"/>. A venue's cancellation or decline (or the
/// platform's), a cancellation in good time and a booking still waiting for the venue are not counted at all.
/// </param>
/// <param name="Came">
/// Visits the venue vouched for: a waiter seated the party, or the sitting had an order sent to the kitchen or a
/// payment (review S1). Opening a tab from the booking code alone never counts.
/// </param>
/// <param name="NoShows">Marked as not having come (and not withdrawn).</param>
/// <param name="LateCancellations">
/// Cancelled by the guest after the venue's deadline, when the booking was confirmed and had been made before that
/// deadline (review S2).
/// </param>
/// <param name="Unmarked">
/// Bookings whose time came and went with nobody on the floor settling them (review S5): confirmed and past their end
/// with nobody seating or marking them, or opened only from the booking code with nothing ordered or paid. Neither a
/// visit nor a no-show - most often a venue that does not seat guests in Yalla.
/// </param>
/// <param name="IsNewGuest">Nothing booked in the window: show a neutral "new guest", never a warning.</param>
/// <param name="BlockedUntilUtc">
/// While the guest is blocked from booking for no-shows (<see cref="Domain.Occupancy.NoShowBlockRules"/>): when the
/// block ends. Absent otherwise.
/// </param>
public sealed record GuestRecord(
    int Booked,
    int Came,
    int NoShows,
    int LateCancellations,
    int Unmarked,
    bool IsNewGuest,
    DateTime? BlockedUntilUtc = null)
{
    /// <summary>A guest with nothing in the window.</summary>
    public static readonly GuestRecord New = new(0, 0, 0, 0, 0, IsNewGuest: true);

    /// <summary>Whether this record is good enough for a venue's auto-confirm setting - see <see cref="Domain.Occupancy.GuestRecordRules.IsGood"/>.</summary>
    /// <remarks>A method, not a property: it is the server's rule, not part of what the wire carries.</remarks>
    public bool QualifiesAsGood() => Domain.Occupancy.GuestRecordRules.IsGood(Came, NoShows, LateCancellations);
}
