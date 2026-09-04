namespace Yalla.Domain.Occupancy;

/// <summary>
/// Why a table cannot be booked for a particular slot.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a <b>distinct</b> answer with its own message on the client. There is
/// deliberately no generic "invalid booking": a diner told "that table seats four" changes table,
/// a diner told "we are closed then" changes time, and a diner told "invalid booking" leaves.
/// </para>
/// <para>
/// The same list drives two things, which is why it is not called an error code: the exception
/// thrown when a booking is refused, and the <c>UnavailableReason</c> the availability read model
/// puts on each table it cannot offer. One list, so the two can never describe the same situation
/// differently.
/// </para>
/// <para>
/// Every threshold behind these comes from the branch's own <c>ReservationPolicy</c>. None of them
/// is a constant in code.
/// </para>
/// </remarks>
public enum ReservationRejectionReason
{
    /// <summary>The slot starts sooner than <c>MinLeadMinutes</c> from now.</summary>
    LeadTimeTooShort = 1,

    /// <summary>The date is further ahead than <c>BookingWindowDays</c>.</summary>
    OutsideBookingWindow = 2,

    /// <summary>The interval is not wholly inside an opening block for the branch's local day.</summary>
    OutsideOpeningHours = 3,

    /// <summary>The party is larger than the table has seats.</summary>
    PartyExceedsCapacity = 4,

    /// <summary>The party would leave more seats empty than <c>MaxSeatOverhang</c> allows.</summary>
    SeatOverhangExceeded = 5,

    /// <summary>The table never takes bookings - bar stools, counter seats.</summary>
    TableNotBookable = 6,

    /// <summary>The table is withdrawn from service.</summary>
    TableOutOfService = 7,

    /// <summary>Another live booking already holds the table over this interval.</summary>
    TableAlreadyBooked = 8,

    /// <summary>
    /// The local time the diner picked does not exist in the branch's zone - the hour a
    /// spring-forward skips. Named explicitly rather than left as an exception escaping from a
    /// conversion.
    /// </summary>
    LocalTimeDoesNotExist = 9,
}
