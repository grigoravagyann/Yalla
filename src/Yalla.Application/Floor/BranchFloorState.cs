using Yalla.Domain.Enums;

namespace Yalla.Application.Floor;

/// <summary>
/// Every active table in one branch with its live state, as of one instant: what the staff floor
/// screen lists, grouped by room.
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

    /// <summary>
    /// The branch's guest messages at a glance - the Messages tab's badge and the remind - so the tablet needs no second
    /// read to badge. See <c>docs/messages.md</c>.
    /// </summary>
    public required FloorMessages Messages { get; init; }
}

/// <summary>The branch's guest messages on the floor.</summary>
/// <param name="UnreadTotal">
/// Guest messages nobody at the venue has read - typed ones and answers - in threads that are open or had a message in
/// the last 24 hours: the same threads as the Messages tab.
/// </param>
/// <param name="OldestUnansweredUtc">
/// The oldest typed guest message the venue has not answered since (with a text or an ask), across those threads that
/// are still open: ring once it has waited the venue's <c>remindAfterMinutes</c>. A guest's answer to "are you
/// coming?" never waits - it is counted in <paramref name="UnreadTotal"/> only. Absent when nothing waits.
/// </param>
public sealed record FloorMessages(int UnreadTotal, DateTime? OldestUnansweredUtc);

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

    public Guid? FloorAreaId { get; init; }

    public string? FloorAreaName { get; init; }

    public int FloorAreaDisplayOrder { get; init; }

    /// <summary>
    /// The room photo the table is pinned on, or null when it has no pin. Staff seat an unpinned
    /// table like any other; diners are not offered one.
    /// </summary>
    public Guid? PhotoId { get; init; }

    /// <summary>Where the pin sits on <see cref="PhotoId"/>, 0-1 across; null when there is no pin.</summary>
    public double? PhotoX { get; init; }

    /// <summary>Where the pin sits on <see cref="PhotoId"/>, 0-1 down; null when there is no pin.</summary>
    public double? PhotoY { get; init; }

    public required bool IsBookable { get; init; }

    /// <summary>The stored physical state: what somebody did to this table.</summary>
    public required TableStatus PhysicalStatus { get; init; }

    /// <summary>What to draw: <see cref="PhysicalStatus"/> with the booking overlay applied.</summary>
    public required DerivedTableState State { get; init; }

    /// <summary>
    /// The table's row version, base64, as it stood when this floor was read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Status is not a version.</b> The staff app captures this at the moment a waiter taps, so a
    /// command queued offline can be checked against the world it was issued in. A table that goes
    /// Free to Occupied and back to Free between the tap and the sync passes a status check while
    /// the world moved underneath it - the party was seated, ordered, paid and left, and the queued
    /// "hold this for the Sarkisyans" applies to a table that is no longer the one the waiter was
    /// looking at.
    /// </para>
    /// <para>
    /// Base64 rather than the raw bytes because it is a token to be sent back untouched, not a
    /// number to be compared or ordered. A client that tries to interpret it has misunderstood it.
    /// </para>
    /// </remarks>
    public required string RowVersion { get; init; }

    /// <summary>The open occupancy, when somebody is sitting here.</summary>
    public Guid? CurrentSessionId { get; init; }

    /// <summary>
    /// The open tab on this table, when the party sitting here has one.
    /// </summary>
    /// <remarks>
    /// From the same open-session join the query already performs, so it costs nothing. Without it a
    /// cold-loaded staff app cannot find the bill for a table somebody is already sitting at - it
    /// only knows the tab id when one happens to come back in a transition result, which means the
    /// bill is reachable after you seat a party and unreachable after you refresh the page.
    /// </remarks>
    public Guid? OpenTabId { get; init; }

    /// <summary>
    /// True when <see cref="OpenTabId"/> was opened by a scan with no proved number and waits for a waiter
    /// to admit it before anybody on it can order. The tablet's table panel offers Admit / Reject.
    /// </summary>
    public bool OpenTabAwaitingAdmission { get; init; }

    public DateTime? SeatedAtUtc { get; init; }

    public int? PartySize { get; init; }

    /// <summary>
    /// The waiter whose table this is - who seated it or opened its tab, or the first to order on it, until a transfer
    /// (<c>POST /api/tabs/{tabId}/transfer</c>). Absent with nobody seated, or nobody on staff serving yet.
    /// </summary>
    public Guid? ServerStaffMemberId { get; init; }

    /// <summary>That waiter's name as stored now.</summary>
    public string? ServerName { get; init; }

    public TableSessionSource? OccupancySource { get; init; }

    /// <summary>The next relevant booking, so the staff app can seat it straight from the floor.</summary>
    public Guid? NextReservationId { get; init; }

    public DateTime? NextReservationStartUtc { get; init; }

    /// <summary>
    /// How long this table can be given away for: the next booking's start less the branch's
    /// turnaround buffer. Null when nothing is booked.
    /// </summary>
    public DateTime? FreeUntilUtc { get; init; }

    /// <summary>
    /// The time the next booking's guest promised to free the table by - "must free by 21:25" on the
    /// next-booking line - or absent when they made no such promise.
    /// </summary>
    /// <remarks>
    /// Another booking follows theirs that a full sitting would run into (they accepted the time when
    /// booking), or they said yes to a later guest's request for the table.
    /// </remarks>
    public DateTime? NextReservationPromisedFreeByUtc { get; init; }

    /// <summary>
    /// True when the next booking was made without a proved phone number - "number not verified" beside
    /// it. False when there is no next booking.
    /// </summary>
    public bool NextReservationPhoneUnverified { get; init; }

    /// <summary>
    /// When the next booking's guest answered "yes, I'm on my way" - asked of every booking 30 minutes before (45 while
    /// a pre-order waits). Set, the waiter does not call; <c>null</c> when they have not said.
    /// </summary>
    public DateTime? NextReservationGuestConfirmedOnWayAtUtc { get; init; }

    /// <summary>The next booking's guest said they are running late: 10, 15 or 30 minutes. <c>null</c> when they have not.</summary>
    public int? NextReservationGuestRunningLateMinutes { get; init; }

    /// <summary>When somebody at the venue last asked the next booking's guest "are you coming?". <c>null</c> until asked.</summary>
    public DateTime? NextReservationStaffAskedOnMyWayAtUtc { get; init; }

    /// <summary>
    /// The next booking's guest record - as on the console's list, and under the same rule: a proved account behind
    /// it and the booking today or still to come. Absent otherwise. See <see cref="Reservations.GuestRecord"/>.
    /// </summary>
    public Reservations.GuestRecord? NextReservationGuestRecord { get; init; }

    /// <summary>
    /// The guest record of the party sitting here now, when they were seated against a booking a proved account
    /// made. Absent for a walk-in, a scan, or nobody.
    /// </summary>
    public Reservations.GuestRecord? SittingGuestRecord { get; init; }

    /// <summary>
    /// The time the party sitting here now promised to free the table by, when they were seated against
    /// a booking that made such a promise; absent otherwise.
    /// </summary>
    public DateTime? SittingPromisedFreeByUtc { get; init; }

    /// <summary>Unread guest messages in the next booking's thread - the table tile's and the Now rail's badge. 0 without one.</summary>
    public int NextReservationUnreadMessages { get; init; }

    /// <summary>Unread guest messages in the thread of the booking the party sitting here came in on. 0 without one.</summary>
    public int SittingUnreadMessages { get; init; }
}
