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
/// Bookings whose time came: <paramref name="Came"/>, <paramref name="NoShows"/>, <paramref name="LateCancellations"/>
/// and confirmed bookings nobody marked. A venue's cancellation or decline, a cancellation in good time and a
/// booking still waiting for the venue are not counted at all.
/// </param>
/// <param name="Came">Seated or completed.</param>
/// <param name="NoShows">Marked as not having come.</param>
/// <param name="LateCancellations">Cancelled by the guest after the venue's cancellation deadline.</param>
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
    bool IsNewGuest,
    DateTime? BlockedUntilUtc = null)
{
    /// <summary>A guest with nothing in the window.</summary>
    public static readonly GuestRecord New = new(0, 0, 0, 0, IsNewGuest: true);

    /// <summary>Whether this record is good enough for a venue's auto-confirm setting - see <see cref="Domain.Occupancy.GuestRecordRules.IsGood"/>.</summary>
    /// <remarks>A method, not a property: it is the server's rule, not part of what the wire carries.</remarks>
    public bool QualifiesAsGood() => Domain.Occupancy.GuestRecordRules.IsGood(Came, NoShows, LateCancellations);
}
