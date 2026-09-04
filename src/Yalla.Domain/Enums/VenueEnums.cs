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
/// Current occupancy of a table.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>denormalised cache</b>. It exists so the diner and staff floor plans can be
/// rendered from a single query over the branch's tables, without joining sessions and
/// reservations for every square on the canvas.
/// </para>
/// <para>
/// The authoritative record of who is sitting at a table is <c>TableSession</c>: one row per
/// physical occupancy, with <c>SeatedAtUtc</c>/<c>ClosedAtUtc</c>. When the two disagree,
/// <c>TableSession</c> wins and this column is to be recomputed from it. Every transition is
/// also written to <c>TableStateChange</c>.
/// </para>
/// </remarks>
public enum TableStatus
{
    /// <summary>Nobody is seated and no booking is currently claiming the table.</summary>
    Free = 1,

    /// <summary>Briefly reserved while a diner completes a booking, or a staff hold. See <c>Reservation.HoldExpiresAtUtc</c>.</summary>
    Held = 2,

    /// <summary>A confirmed booking covers the table now or imminently, but nobody has been seated.</summary>
    Reserved = 3,

    /// <summary>A party is seated: an open <c>TableSession</c> exists.</summary>
    Occupied = 4,

    /// <summary>Withdrawn from service (broken, being cleaned, staff-blocked). Never auto-assigned.</summary>
    OutOfService = 5,
}
