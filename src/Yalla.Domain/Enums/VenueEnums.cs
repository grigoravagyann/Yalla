namespace Yalla.Domain.Enums;

/// <summary>What kind of place a venue is. Drives the shipped reservation-policy defaults.</summary>
public enum VenueType
{
    Cafe = 1,
    Restaurant = 2,
}

/// <summary>Footprint of a table on the floor plan.</summary>
public enum TableShape
{
    Rectangle = 1,
    Round = 2,
}

/// <summary>
/// The <b>physical</b> state of a table: what somebody did to it.
/// </summary>
/// <remarks>
/// <para>
/// Every member here is the result of a person performing an action - seating a party, placing a
/// hold, taking a table out of service. That is the admission rule for this column.
/// </para>
/// <para>
/// There is deliberately <b>no <c>Reserved</c></b> member. "Reserved" is time-dependent: a table
/// becomes reserved at 19:45 because a booking starts at 20:00, and no person performs that
/// transition. Storing it would need a background job flipping rows on a timer, and every failure
/// mode of that job is bad - a stalled sweep leaves tables sellable that are not, a cancellation
/// leaves a table reserved forever, and clock drift between the job and the tablet makes the two
/// disagree. It is derived at read time instead: see <see cref="DerivedTableState"/>.
/// </para>
/// <para>
/// Numeric values are not contiguous. <c>3</c> was <c>Reserved</c> and is left permanently vacant
/// so it can never be silently reused and mean two different things across rows written at
/// different times.
/// </para>
/// <para>
/// This column is a <b>denormalised cache</b> so the floor plan renders in one query. The
/// authoritative record of who is sitting at a table is <c>TableSession</c>; when the two
/// disagree the session wins and this is to be recomputed from it. Every transition is also
/// written to <c>TableStateChange</c>.
/// </para>
/// </remarks>
public enum TableStatus
{
    /// <summary>Nobody is seated and no hold is in force.</summary>
    Free = 1,

    /// <summary>
    /// Held by staff for a party expected imminently - typically a late booking the waiter has
    /// decided to keep the table for.
    /// </summary>
    Held = 2,

    // 3 was Reserved. Permanently retired - see the remarks above. Do not reuse.

    /// <summary>A party is seated: an open <c>TableSession</c> exists.</summary>
    Occupied = 4,

    /// <summary>Withdrawn from service (broken, being repaired, staff-blocked). Never auto-assigned.</summary>
    OutOfService = 5,
}

/// <summary>
/// What a client should draw for a table: its physical <see cref="TableStatus"/> with the
/// reservation overlay applied.
/// </summary>
/// <remarks>
/// A presentation concern computed per request and never stored. It exists so the three clients
/// do not each reimplement "is this table reserved soon?" from raw bookings and then disagree
/// with each other.
/// </remarks>
public enum DerivedTableState
{
    /// <summary>Free, with no booking close enough to matter.</summary>
    Free = 1,

    /// <summary>
    /// Physically free, but a booking starts soon enough that the table should not be sold. The
    /// threshold is the branch's own turnaround buffer, not a constant.
    /// </summary>
    ReservedSoon = 2,

    /// <summary>Held by staff.</summary>
    Held = 3,

    /// <summary>A party is seated.</summary>
    Occupied = 4,

    /// <summary>Withdrawn from service.</summary>
    OutOfService = 5,
}
