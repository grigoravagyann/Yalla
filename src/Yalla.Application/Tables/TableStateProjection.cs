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
