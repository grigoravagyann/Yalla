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
    /// True when booking this table will land as <c>PendingApproval</c> rather than confirmed -
    /// a party over the branch's threshold. Not a refusal, and worth saying before the diner
    /// commits.
    /// </summary>
    public required bool RequiresApproval { get; init; }

    /// <summary>Start of the window on offer. The requested slot, when the table is available.</summary>
    public DateTime? AvailableFromUtc { get; init; }

    /// <summary>
    /// When the table stops being theirs: the next booking's start less the branch's clearing
    /// time. <b>Null means no limit</b> - nothing is booked after them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how the product avoids asking people how long they intend to stay. The limit is
    /// shown <i>before</i> they confirm - "table 7, yours until 19:45" - and if that is too short
    /// for what they had in mind they can see which tables have no limit at all and pick one of
    /// those instead.
    /// </para>
    /// <para>
    /// Self-reported departure times are unreliable in a way a stated window is not: a party that
    /// said ninety minutes still leaves when they leave, whereas a party told the table is theirs
    /// until 19:45 has been told something true, by a venue that can hold itself to it.
    /// </para>
    /// <para>
    /// It is never earlier than the turn time the booking itself reserves. A table whose next
    /// booking leaves no room for a full sitting fails the overlap rule outright and is offered
    /// with <see cref="ReservationRejectionReason.TableAlreadyBooked"/> rather than with a window
    /// too short to use.
    /// </para>
    /// </remarks>
    public DateTime? AvailableUntilUtc { get; init; }

    /// <summary>The same window in the branch's wall clock, for display.</summary>
    public TimeOnly? AvailableFromLocal { get; init; }

    /// <inheritdoc cref="AvailableFromLocal"/>
    public TimeOnly? AvailableUntilLocal { get; init; }

    /// <summary>Length of the window in minutes. Null when there is no limit.</summary>
    public int? AvailableMinutes { get; init; }

    /// <summary>
    /// True when a later booking closes the window. False means the table has no limit at all,
    /// which is the answer a diner would rather have and the one this flag exists to let a client
    /// sort on.
    /// </summary>
    public required bool LimitedByNextBooking { get; init; }

    /// <summary>The booking that closes the window, when there is one.</summary>
    public Guid? NextReservationId { get; init; }

    public DateTime? NextReservationStartUtc { get; init; }
}
