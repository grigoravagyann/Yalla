using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.Application.Reservations;

/// <summary>
/// Every table in a branch, answered for one requested slot, in one round trip.
/// </summary>
/// <remarks>
/// <para>
/// This is the screen a diner picks a table on, so it carries the floor-plan geometry as well as
/// the answer: the app draws the room and shades the tables it cannot offer, rather than showing a
/// list of times.
/// </para>
/// <para>
/// Like the floor read model it carries <b>no guest names and no money</b>. It is anonymous -
/// browsing needs no account - so a stranger must not learn who else is booked in.
/// </para>
/// </remarks>
public sealed record BranchAvailability
{
    public required Guid BranchId { get; init; }

    public required string BranchName { get; init; }

    /// <summary>IANA zone, so a client can render the UTC instants below in local time itself.</summary>
    public required string TimeZoneId { get; init; }

    public required int FloorWidth { get; init; }

    public required int FloorHeight { get; init; }

    /// <summary>The local date asked about, as the diner picked it.</summary>
    public required DateOnly LocalDate { get; init; }

    /// <summary>The local time asked about.</summary>
    public required TimeOnly LocalTime { get; init; }

    public required int PartySize { get; init; }

    /// <summary>The requested slot as an instant.</summary>
    public required DateTime RequestedStartUtc { get; init; }

    /// <summary>
    /// The requested slot's end: start plus the branch's turn time. The diner is never asked how
    /// long they intend to stay.
    /// </summary>
    public required DateTime RequestedEndUtc { get; init; }

    /// <summary>The branch's turn time, so a client can explain the window it is being shown.</summary>
    public required int TurnTimeMinutes { get; init; }

    /// <summary>The branch's clearing time between sittings.</summary>
    public required int BufferMinutes { get; init; }

    /// <summary>
    /// How long before the start a diner may still cancel freely - the branch's setting as it stands
    /// now, which is what a cancellation is judged against.
    /// </summary>
    public required int CancellationDeadlineMinutes { get; init; }

    /// <summary>
    /// The instant past which cancelling the slot asked about is recorded as late: the requested
    /// start less <see cref="CancellationDeadlineMinutes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What "free cancellation until" means, stated by the server. The app had nothing to show, so
    /// it promised free cancellation until the start - and a diner who cancelled at the time it gave
    /// them was recorded as a late canceller.
    /// </para>
    /// <para>
    /// <b>Can already be past</b>: a slot inside the window has no free cancellation at all, and a
    /// client should say so rather than promise one. Absent when no slot could be computed, because
    /// the local time asked about does not exist.
    /// </para>
    /// </remarks>
    public DateTime? CancellationDeadlineUtc { get; init; }

    /// <summary>
    /// A rule that refused the whole request before any table was considered - the date is too far
    /// out, the slot is too soon, the branch is shut. Null when the request itself was fine.
    /// </summary>
    /// <remarks>
    /// Reported once at the top rather than repeated on every table, so a client can say "we are
    /// closed at 03:00" instead of listing forty tables that are each individually unavailable for
    /// the same reason.
    /// </remarks>
    public ReservationRejectionReason? UnavailableReason { get; init; }

    /// <summary>The instant these answers were computed for.</summary>
    public required DateTime AsOfUtc { get; init; }

    public required IReadOnlyList<TableAvailability> Tables { get; init; }
}

/// <summary>
/// One table's answer for the requested slot: can the diner have it, and if so, for how long.
/// </summary>
/// <summary>
/// How long this table can be had for, stated rather than implied.
/// </summary>
/// <remarks>
/// <para>
/// The sheet has three cases to tell apart and used to be able to distinguish only two: bounded by
/// a later booking, unbounded, and "we do not know". A null <see cref="AvailableUntilUtc"/> meant
/// both of the last two, so <see cref="HasNoLaterBooking"/> is carried explicitly - and "nobody is
/// booked after you" is a thing the sheet is meant to say out loud rather than a gap in the data.
/// </para>
/// <para>
/// <see cref="IsShorterThanTurnTime"/> saves every client doing the same subtraction and reaching a
/// different conclusion about whether to warn.
/// </para>
/// </remarks>
/// <param name="AvailableFromUtc">The start of the slot, which is what was asked for.</param>
/// <param name="AvailableUntilUtc">
/// When the table has to be clear again - the next booking's start less the branch's clearing time.
/// Null when nothing is booked after it.
/// </param>
/// <param name="HasNoLaterBooking">
/// True when nothing is booked after this slot. Explicit, so a client never has to read a null as
/// either "unbounded" or "missing".
/// </param>
/// <param name="WindowMinutes">How long the table is free for, in minutes. Null when unbounded.</param>
/// <param name="IsShorterThanTurnTime">
/// True when the window is shorter than the branch's usual sitting, so the client can say plainly
/// that this table gives less time than normal.
/// </param>
/// <param name="AvailableFromLocal">
/// <paramref name="AvailableFromUtc"/> as branch wall-clock time, for rendering.
/// </param>
/// <param name="AvailableUntilLocal"><paramref name="AvailableUntilUtc"/> as branch wall-clock time.</param>
public sealed record TableAvailabilityWindow(
    DateTime AvailableFromUtc,
    DateTime? AvailableUntilUtc,
    bool HasNoLaterBooking,
    int? WindowMinutes,
    bool IsShorterThanTurnTime,
    TimeOnly AvailableFromLocal,
    TimeOnly? AvailableUntilLocal);

public sealed record TableAvailability
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

    /// <summary>
    /// What to draw, derived for the <i>requested</i> instant rather than for now.
    /// </summary>
    /// <remarks>
    /// The same <c>DerivedTableState</c> the floor plan uses, produced by the same
    /// <c>TableStateProjection</c> code. A table free now but booked at 20:00 reads as
    /// <c>ReservedSoon</c> when the question is about 20:00.
    /// </remarks>
    public required DerivedTableState State { get; init; }

    /// <summary>Whether this table can be booked for this party at this time.</summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// The one specific reason it cannot, when it cannot. Null when it can.
    /// </summary>
    /// <remarks>
    /// Distinct per rule so the app can say "seats 4" or "already booked" rather than greying the
    /// table out with no explanation - which is the difference between a diner picking another
    /// table and a diner leaving.
    /// </remarks>
    public ReservationRejectionReason? UnavailableReason { get; init; }

    /// <summary>
    /// True when booking this table will land as <c>PendingApproval</c> rather than confirmed: the
    /// branch approves every booking by hand, or the party is over its threshold. Not a refusal,
    /// and worth saying before the diner commits.
    /// </summary>
    /// <remarks>
    /// The one reason it cannot know is the diner's own no-show record, which needs the diner and is
    /// only known once they book - the booking carries it in <c>awaitingApprovalBecause</c>.
    /// </remarks>
    public required bool RequiresApproval { get; init; }

    /// <summary>Start of the window on offer. The requested slot, when the table is available.</summary>
    /// <summary>
    /// How long the table can be had for. Null when it is not available at all - there is no window
    /// to describe, and a zero-length one would read as though there were.
    /// </summary>
    public TableAvailabilityWindow? Window { get; init; }

    public Guid? NextReservationId { get; init; }

    public DateTime? NextReservationStartUtc { get; init; }
}
