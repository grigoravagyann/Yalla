using Yalla.Domain.Enums;

namespace Yalla.Domain.Occupancy;

/// <summary>One booked UTC interval, as stored on a <see cref="Reservation"/>.</summary>
/// <param name="StartUtc">When the party is expected.</param>
/// <param name="EndUtc">Start plus the branch's turn time. The buffer is deliberately not in here.</param>
public readonly record struct BookedInterval(DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    public static BookedInterval Of(Reservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        return new BookedInterval(reservation.StartUtc, reservation.EndUtc);
    }
}

/// <summary>
/// The rule that decides whether two bookings on one table can both exist. Pure, so it can be
/// tested at its boundaries without a database.
/// </summary>
/// <remarks>
/// <para>
/// A reservation is an interval. The branch's <c>BufferMinutes</c> is <b>clearing time between
/// sittings</b> and is applied here, when checking, rather than baked into <c>EndUtc</c>: the
/// stored interval stays the one the diner booked and sees, while turnaround padding remains a
/// setting the owner can change tomorrow without rewriting history.
/// </para>
/// <para>
/// Two bookings conflict when
/// <c>newStart &lt; existingEnd + buffer</c> <b>and</b> <c>existingStart &lt; newEnd + buffer</c>.
/// Both comparisons are strict, which is the whole point: a sitting that starts <i>exactly</i> at
/// <c>existingEnd + buffer</c> is legal, and back-to-back sittings with the buffer respected are
/// how a venue makes money. One minute earlier is a genuine double-booking.
/// </para>
/// <para>
/// This is the only implementation of the rule. The database query that finds candidate conflicts
/// uses an index-friendly range predicate that is algebraically the same test, and every candidate
/// it returns is then confirmed through <see cref="Conflicts(BookedInterval, BookedInterval, int)"/>
/// - so the boundary behaviour proved by the unit tests is the behaviour that ships.
/// </para>
/// </remarks>
public static class ReservationOverlap
{
    /// <summary>
    /// Whether a booking in this status still holds the table.
    /// </summary>
    /// <remarks>
    /// A cancellation, a no-show or a completed sitting releases the slot. Counting any of them
    /// as a conflict would make a table unbookable for the rest of the evening because somebody
    /// changed their mind at lunchtime.
    /// </remarks>
    public static bool Blocks(ReservationStatus status) => status
        is ReservationStatus.PendingApproval
        or ReservationStatus.Confirmed
        or ReservationStatus.Seated;

    /// <summary>The rule itself.</summary>
    public static bool Conflicts(
        DateTime newStartUtc,
        DateTime newEndUtc,
        DateTime existingStartUtc,
        DateTime existingEndUtc,
        int bufferMinutes)
    {
        if (bufferMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferMinutes), bufferMinutes, "Clearing time must not be negative.");
        }

        var buffer = TimeSpan.FromMinutes(bufferMinutes);

        return newStartUtc < existingEndUtc + buffer
               && existingStartUtc < newEndUtc + buffer;
    }

    /// <inheritdoc cref="Conflicts(DateTime, DateTime, DateTime, DateTime, int)"/>
    public static bool Conflicts(BookedInterval proposed, BookedInterval existing, int bufferMinutes) =>
        Conflicts(proposed.StartUtc, proposed.EndUtc, existing.StartUtc, existing.EndUtc, bufferMinutes);

    /// <summary>
    /// The rule with the status filter applied: an existing booking blocks only when it is still
    /// alive and its buffered interval overlaps.
    /// </summary>
    public static bool Conflicts(
        BookedInterval proposed,
        BookedInterval existing,
        ReservationStatus existingStatus,
        int bufferMinutes) =>
        Blocks(existingStatus) && Conflicts(proposed, existing, bufferMinutes);

    /// <summary>
    /// The widened window a conflicting booking must touch, used to build the index-friendly
    /// range predicate the persistence layer sends to SQL Server.
    /// </summary>
    /// <remarks>
    /// <c>newStart &lt; existingEnd + buffer</c> rearranges to <c>existingEnd &gt; newStart - buffer</c>,
    /// and <c>existingStart &lt; newEnd + buffer</c> is already in that shape. So every conflicting
    /// row has <c>EndUtc &gt; SearchFromUtc</c> and <c>StartUtc &lt; SearchToUtc</c> - a plain range
    /// scan of <c>IX_Reservations_DiningTableId_StartUtc_EndUtc</c>, with no arithmetic per row.
    /// </remarks>
    public static (DateTime SearchFromUtc, DateTime SearchToUtc) SearchWindow(
        BookedInterval proposed,
        int bufferMinutes)
    {
        if (bufferMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferMinutes), bufferMinutes, "Clearing time must not be negative.");
        }

        var buffer = TimeSpan.FromMinutes(bufferMinutes);

        return (proposed.StartUtc - buffer, proposed.EndUtc + buffer);
    }
}
