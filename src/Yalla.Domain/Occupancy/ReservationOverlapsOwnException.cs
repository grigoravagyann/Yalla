namespace Yalla.Domain.Occupancy;

/// <summary>
/// A diner booked, or moved a booking to, a time whose sitting overlaps another of their own live
/// bookings at the same branch: one diner holds one table there at a time.
/// </summary>
/// <remarks>
/// <para>
/// A <b>409</b> with its own code, <c>reservation-overlaps-own</c>. Found in QA: one diner held table 1
/// at 19:45 and table 2 at 19:30 the same evening - two tables kept from everybody else for a party that
/// can sit at only one. The context names the booking already held - its id, door code, wall-clock start
/// and table - so the app can say "You already have a booking at 19:30 (table 2)" and offer to change
/// that one instead.
/// </para>
/// <para>
/// Live means pending, confirmed or seated (<see cref="ReservationOverlap.Blocks"/>); each sitting is the
/// one the booking has - cut by a promise to leave by - and sittings that only touch (one ends at 19:30,
/// the next starts at 19:30) do not overlap. Another branch is another evening
/// out and is not refused. A move leaves the booking being moved out. Judged under the diner's account
/// lock, inside the table's, so two bookings sent at once on two tables cannot both get through.
/// </para>
/// </remarks>
public sealed class ReservationOverlapsOwnException(
    Guid reservationId,
    string code,
    DateOnly localDate,
    TimeOnly localStartTime,
    string tableLabel)
    : DomainStateException(
        $"You already have a booking at {localStartTime:HH\\:mm} (table {tableLabel}) that overlaps this time. "
        + "Change that booking, or cancel it first.")
{
    /// <summary>The diner's own booking already holding that time.</summary>
    public Guid ReservationId { get; } = reservationId;

    /// <summary>Its door code, as the diner knows it.</summary>
    public string Code { get; } = code;

    /// <summary>
    /// Its day on the branch's wall clock - for saying the day when it is not the one being booked (a
    /// 00:30 after a 23:30, a move to another day).
    /// </summary>
    public DateOnly LocalDate { get; } = localDate;

    /// <summary>Its start on the branch's wall clock.</summary>
    public TimeOnly LocalStartTime { get; } = localStartTime;

    /// <summary>The label of the table it holds.</summary>
    public string TableLabel { get; } = tableLabel;
}
