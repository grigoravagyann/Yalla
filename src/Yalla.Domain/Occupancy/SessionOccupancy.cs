using Yalla.Domain.Venues;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// An open sitting, treated as an interval so that availability can reason about it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TableSession"/> is the authoritative record of who is physically at a table, but it
/// has a start and no end - the party leaves when they leave. Booking conflicts and availability
/// both need an interval, so one is projected: the sitting is assumed to run for the branch's
/// current turn time from when they sat down.
/// </para>
/// <para>
/// The turn time is read at query time rather than stored on the session. An owner who shortens
/// turn time this afternoon means it for the tables occupied this afternoon, and a value frozen at
/// seating would keep answering with yesterday's policy.
/// </para>
/// <para>
/// It is a projection, not a promise. A party may leave early, which is why an occupied table is a
/// <i>conflict</i> for a booking rather than a permanent state, and why the message says the table
/// may free up.
/// </para>
/// </remarks>
public static class SessionOccupancy
{
    /// <summary>
    /// How long a sitting that started at <paramref name="seatedAtUtc"/> is assumed to run.
    /// </summary>
    public static BookedInterval ProjectedInterval(DateTime seatedAtUtc, int turnTimeMinutes)
    {
        if (turnTimeMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(turnTimeMinutes), turnTimeMinutes, "Turn time must be greater than zero.");
        }

        return new BookedInterval(seatedAtUtc, seatedAtUtc.AddMinutes(turnTimeMinutes));
    }

    /// <inheritdoc cref="ProjectedInterval(DateTime, int)"/>
    public static BookedInterval ProjectedInterval(TableSession session, ReservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(policy);

        return ProjectedInterval(session.SeatedAtUtc, policy.TurnTimeMinutes);
    }

    /// <summary>
    /// Whether a proposed booking runs into a sitting that is already under way. The same buffered
    /// rule as two bookings - clearing a table takes as long whoever was at it.
    /// </summary>
    public static bool Conflicts(
        BookedInterval proposed,
        DateTime seatedAtUtc,
        int turnTimeMinutes,
        int bufferMinutes) =>
        ReservationOverlap.Conflicts(
            proposed, ProjectedInterval(seatedAtUtc, turnTimeMinutes), bufferMinutes);

    /// <summary>
    /// Whether a walk-in being seated now is close enough to a booking that staff should be told.
    /// </summary>
    /// <remarks>
    /// A warning, never a block, and deliberately wider than the collision check: the point is to
    /// catch the seating that will be fine <i>if</i> the party is quick, so the waiter can decide
    /// with the information the system does not have.
    /// </remarks>
    public static bool WithinWalkInHoldback(
        DateTime? nextReservationStartUtc,
        DateTime nowUtc,
        int walkInHoldbackMinutes)
    {
        if (nextReservationStartUtc is not { } startUtc || walkInHoldbackMinutes <= 0)
        {
            return false;
        }

        // Already started, or starting inside the holdback window: from the instant the branch
        // starts holding the table back, by the one definition of that instant below.
        return nowUtc >= HoldbackBeginsAtUtc(startUtc, walkInHoldbackMinutes);
    }

    /// <summary>
    /// The instant the branch starts holding a table back for the booking that starts at
    /// <paramref name="startUtc"/>: its walk-in holdback before the start.
    /// </summary>
    /// <remarks>
    /// One definition, two readers. From here a waiter who seats somebody else at the table is
    /// warned (<see cref="WithinWalkInHoldback"/>), and from here the booked party may take the table
    /// with their booking code (<c>Reservation.RequireTableIsTheirsAt</c>) - the moment the venue
    /// starts keeping the table for somebody is the moment it is theirs to sit at, and two numbers
    /// for the one moment would sooner or later disagree.
    /// </remarks>
    public static DateTime HoldbackBeginsAtUtc(DateTime startUtc, int walkInHoldbackMinutes) =>
        startUtc.AddMinutes(-walkInHoldbackMinutes);
}
