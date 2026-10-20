namespace Yalla.Domain.Occupancy;

/// <summary>
/// A diner booked, or moved a booking to, a time whose sitting overlaps another of their own live
/// bookings - at the same branch, or too close to one at another venue: one diner, one table at a time, anywhere.
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
/// one the booking has - cut by a promise to leave by - and at the same branch sittings that only touch (one
/// ends at 19:30, the next starts at 19:30) do not overlap. At another venue (since 2026-10-20, the user's
/// revision) the two need <see cref="BookingLimits.OtherVenueGapMinutes"/> between them to get from one to the
/// other - with a 90-minute sitting, starts less than two hours apart - and the context also names the venue
/// and branch. A move leaves the booking being moved out. Judged under the diner's account lock and the number's,
/// inside the table's, so two bookings sent at once on two tables cannot both get through.
/// </para>
/// </remarks>
public sealed class ReservationOverlapsOwnException(
    Guid reservationId,
    string code,
    DateOnly localDate,
    TimeOnly localStartTime,
    string tableLabel,
    string? venueName = null,
    string? branchName = null)
    : DomainStateException(
        $"You already have a booking {(venueName is null ? string.Empty : "at " + venueName + " ")}at "
        + $"{localStartTime:HH\\:mm} (table {tableLabel}) that overlaps this time or is too close to it. "
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

    /// <summary>The venue it is at.</summary>
    public string? VenueName { get; } = venueName;

    /// <summary>The branch it is at.</summary>
    public string? BranchName { get; } = branchName;
}
