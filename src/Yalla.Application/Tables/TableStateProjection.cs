using Yalla.Domain.Enums;

namespace Yalla.Application.Tables;

/// <summary>
/// The single implementation of the two time-dependent rules the clients would otherwise each
/// reinvent: when a free table counts as reserved, and when seating one deserves a warning.
/// </summary>
/// <remarks>
/// Both thresholds come from the branch's own <c>ReservationPolicy</c>. Neither is a constant in
/// code, because a breakfast cafe and a tasting-menu restaurant answer them differently.
/// </remarks>
public static class TableStateProjection
{
    /// <summary>
    /// Applies the reservation overlay to a physical status.
    /// </summary>
    /// <remarks>
    /// A physically free table reads as <see cref="DerivedTableState.ReservedSoon"/> once the next
    /// booking is within the branch's turnaround <paramref name="bufferMinutes"/> - the point at
    /// which the table has to be clear and reset, so it can no longer be sold. Held, Occupied and
    /// OutOfService are facts about the here and now and are never overridden by a booking.
    /// </remarks>
    public static DerivedTableState Derive(
        TableStatus physicalStatus,
        DateTime? nextReservationStartUtc,
        DateTime nowUtc,
        int bufferMinutes) => physicalStatus switch
        {
            TableStatus.Held => DerivedTableState.Held,
            TableStatus.Occupied => DerivedTableState.Occupied,
            TableStatus.OutOfService => DerivedTableState.OutOfService,
            TableStatus.Free when IsReservedSoon(nextReservationStartUtc, nowUtc, bufferMinutes) =>
                DerivedTableState.ReservedSoon,
            TableStatus.Free => DerivedTableState.Free,
            _ => DerivedTableState.Free,
        };

    /// <summary>
    /// How far ahead a query still counts as "now", and so may trust the table's physical status.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Physical status answers "who is sitting here <i>at this moment</i>". A floor query for
    /// tomorrow evening that reported it would say a table is occupied because somebody is at it
    /// tonight - which is what the diner app was dimming free tables over.
    /// </para>
    /// <para>
    /// A few minutes rather than zero, because the staff tablet polls with a slightly stale clock
    /// and a waiter looking at the floor "now" means the next few minutes, not this instant.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan PhysicalStatusHorizon = TimeSpan.FromMinutes(5);

    /// <summary>Whether a query instant is close enough to now that physical status still applies.</summary>
    public static bool IsWithinPhysicalHorizon(DateTime atUtc, DateTime nowUtc) =>
        atUtc - nowUtc <= PhysicalStatusHorizon && nowUtc - atUtc <= PhysicalStatusHorizon;

    /// <summary>
    /// The derived state at an arbitrary instant, which may be days away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Beyond <see cref="PhysicalStatusHorizon"/> the table's momentary status is dropped and only
    /// two things decide: whether the current sitting, projected forward by the branch's turn time,
    /// still covers <paramref name="atUtc"/>, and whether a booking does. A table occupied tonight
    /// is <see cref="DerivedTableState.Free"/> for tomorrow.
    /// </para>
    /// <para>
    /// <see cref="TableStatus.OutOfService"/> is the one status that survives the horizon, because
    /// it is not a statement about this moment - a broken table stays broken until somebody returns
    /// it to service, and the booking rules already refuse it at any time. <see cref="TableStatus.Held"/>
    /// does not survive: a hold is for a party arriving now and means nothing tomorrow.
    /// </para>
    /// </remarks>
    public static DerivedTableState DeriveAt(
        TableStatus physicalStatus,
        DateTime? seatedAtUtc,
        DateTime? nextReservationStartUtc,
        DateTime atUtc,
        DateTime nowUtc,
        int turnTimeMinutes,
        int bufferMinutes)
    {
        if (IsWithinPhysicalHorizon(atUtc, nowUtc))
        {
            return Derive(physicalStatus, nextReservationStartUtc, atUtc, bufferMinutes);
        }

        if (physicalStatus == TableStatus.OutOfService)
        {
            return DerivedTableState.OutOfService;
        }

        if (SittingCovers(seatedAtUtc, atUtc, turnTimeMinutes))
        {
            return DerivedTableState.Occupied;
        }

        return IsReservedSoon(nextReservationStartUtc, atUtc, bufferMinutes)
            ? DerivedTableState.ReservedSoon
            : DerivedTableState.Free;
    }

    /// <summary>
    /// Whether a sitting that began at <paramref name="seatedAtUtc"/> is still expected to be
    /// running at <paramref name="atUtc"/>, projected by the branch's turn time.
    /// </summary>
    public static bool SittingCovers(DateTime? seatedAtUtc, DateTime atUtc, int turnTimeMinutes) =>
        seatedAtUtc is { } seated
        && atUtc >= seated
        && atUtc < seated.AddMinutes(turnTimeMinutes);

    /// <summary>Whether the next booking is close enough that the table should not be sold.</summary>
    public static bool IsReservedSoon(DateTime? nextReservationStartUtc, DateTime nowUtc, int bufferMinutes) =>
        nextReservationStartUtc is not null
        && nextReservationStartUtc.Value <= nowUtc.AddMinutes(bufferMinutes);

    /// <summary>
    /// When the table has to be clear again: the next booking's start, less the turnaround the
    /// branch needs to reset it. Null when nothing is booked.
    /// </summary>
    public static DateTime? FreeUntil(DateTime? nextReservationStartUtc, int bufferMinutes) =>
        nextReservationStartUtc?.AddMinutes(-bufferMinutes);

    /// <summary>
    /// Whether seating a party now would run into an existing booking, and so deserves a warning.
    /// </summary>
    /// <remarks>
    /// The horizon is the branch's <paramref name="turnTimeMinutes"/>: if the booking starts
    /// before the party being seated is expected to leave, the two collide. Using the turn time
    /// rather than the shorter buffer is deliberate - the waiter wants to know before they sit
    /// four people down, not fifteen minutes before the other party walks in.
    /// </remarks>
    public static bool SeatingCollidesWithReservation(
        DateTime? nextReservationStartUtc,
        DateTime nowUtc,
        int turnTimeMinutes) =>
        nextReservationStartUtc is not null
        && nextReservationStartUtc.Value < nowUtc.AddMinutes(turnTimeMinutes);
}
