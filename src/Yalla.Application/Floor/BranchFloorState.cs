using Yalla.Domain.Enums;

namespace Yalla.Application.Floor;

/// <summary>
/// Everything needed to draw one branch's floor from above, as of one instant.
/// </summary>
/// <remarks>
/// This backs the hottest endpoint in the product: the diner app polls it to pick a table and the
/// staff tablet keeps it on screen all service. It is therefore assembled in a single round trip,
/// with the reservation overlay and open sessions folded in as correlated subqueries rather than
/// a query per table.
/// </remarks>
public sealed record BranchFloorState
{
    public required Guid BranchId { get; init; }

    public required string BranchName { get; init; }

    /// <summary>IANA zone, so a client can render <see cref="TableFloorState.NextReservationStartUtc"/> locally.</summary>
    public required string TimeZoneId { get; init; }

    public required int FloorWidth { get; init; }

    public required int FloorHeight { get; init; }

    /// <summary>
    /// The instant the derived states were computed for. Every <c>ReservedSoon</c> in this
    /// payload is relative to this, not to the client's clock.
    /// </summary>
    public required DateTime AsOfUtc { get; init; }

    /// <summary>
    /// The branch's highest change-log sequence at the moment this was read.
    /// </summary>
    /// <remarks>
    /// Every floor response carries it so a client always knows where it is in the stream. After a
    /// dropped connection it asks <c>/changes?afterSequence=</c> with this number rather than
    /// refetching the whole floor and diffing. Zero when the branch has never had a state change.
    /// </remarks>
    public required long MaxSequence { get; init; }

    public required IReadOnlyList<TableFloorState> Tables { get; init; }
}

/// <summary>
/// One table's physical state plus the derived reservation overlay.
/// </summary>
/// <remarks>
/// Deliberately carries <b>no guest names, no party names and no money</b>. The diner app calls
/// this endpoint too, and until authorisation exists - and arguably after - a stranger picking a
/// table must not learn who else is booked in or what they owe. Occupancy is visible because a
/// diner can see for themselves that a table is taken.
/// </remarks>
public sealed record TableFloorState
{
    public required Guid TableId { get; init; }

    public required string Label { get; init; }

    public required int Seats { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required double RotationDegrees { get; init; }

    public required TableShape Shape { get; init; }

    public Guid? FloorAreaId { get; init; }

    public string? FloorAreaName { get; init; }

    public int FloorAreaDisplayOrder { get; init; }

    public required bool IsBookable { get; init; }

    /// <summary>The stored physical state: what somebody did to this table.</summary>
    public required TableStatus PhysicalStatus { get; init; }

    /// <summary>What to draw: <see cref="PhysicalStatus"/> with the booking overlay applied.</summary>
    public required DerivedTableState State { get; init; }

    /// <summary>The open occupancy, when somebody is sitting here.</summary>
    public Guid? CurrentSessionId { get; init; }

    public DateTime? SeatedAtUtc { get; init; }

    public int? PartySize { get; init; }

    public TableSessionSource? OccupancySource { get; init; }

    /// <summary>The next relevant booking, so the staff app can seat it straight from the floor.</summary>
    public Guid? NextReservationId { get; init; }

    public DateTime? NextReservationStartUtc { get; init; }

    /// <summary>
    /// How long this table can be given away for: the next booking's start less the branch's
    /// turnaround buffer. Null when nothing is booked.
    /// </summary>
    public DateTime? FreeUntilUtc { get; init; }
}
