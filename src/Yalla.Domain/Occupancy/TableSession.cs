using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// One physical occupancy of one table: this party, this table, from this moment until they left.
/// </summary>
/// <remarks>
/// <para>
/// This is where the reservation flow and the walk-in flow converge. A booked party and a
/// stranger who scanned the QR code produce the same kind of row here, differing only in
/// <see cref="Source"/>, so occupancy, floor state and billing never need two parallel code
/// paths. It is also the authoritative answer to "is table 7 free?" -
/// <see cref="DiningTable.Status"/> is only a cache of it.
/// </para>
/// <para>
/// Because one row is written per seating and closed on departure, this table is also the raw
/// data for turnover analytics later: covers per night, minutes per seating, table utilisation.
/// </para>
/// </remarks>
public sealed class TableSession : Entity
{
    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public Guid DiningTableId { get; private set; }

    public DiningTable DiningTable { get; private set; } = null!;

    /// <summary>Whether the party arrived on a booking or off the street.</summary>
    public TableSessionSource Source { get; private set; }

    /// <summary>The booking this seating honoured. Always null for a walk-in.</summary>
    public Guid? ReservationId { get; private set; }

    public Reservation? Reservation { get; private set; }

    /// <summary>
    /// The tab opened for this party, once one exists. Not every seating opens a tab
    /// (a cash coffee at the counter never does).
    /// </summary>
    /// <remarks>
    /// Deliberately not a foreign key: <c>Tab.TableSessionId</c> is the real, required key in the
    /// other direction, and adding an opposing key here would make the pair circular and
    /// uninsertable. This is a convenience pointer.
    /// </remarks>
    public Guid? TabId { get; private set; }

    public int PartySize { get; private set; }

    public DateTime SeatedAtUtc { get; private set; }

    /// <summary>Null while the party is still at the table.</summary>
    public DateTime? ClosedAtUtc { get; private set; }

    /// <summary>The waiter who seated them, when it was done from the staff app.</summary>
    public Guid? SeatedByStaffId { get; private set; }

    public StaffMember? SeatedByStaff { get; private set; }

    /// <summary>How long the party occupied the table, once closed.</summary>
    public TimeSpan? Duration => ClosedAtUtc is null ? null : ClosedAtUtc - SeatedAtUtc;

    public bool IsOpen => ClosedAtUtc is null;

    private TableSession()
    {
    }

    private TableSession(
        Guid branchId,
        Guid diningTableId,
        TableSessionSource source,
        Guid? reservationId,
        int partySize,
        DateTime seatedAtUtc,
        Guid? seatedByStaffId)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        DiningTableId = Guard.NotEmpty(diningTableId, nameof(diningTableId));
        Source = Guard.Defined(source, nameof(source));
        ReservationId = reservationId;
        PartySize = Guard.Positive(partySize, nameof(partySize));
        SeatedAtUtc = Guard.NotLocalTime(seatedAtUtc, nameof(seatedAtUtc));
        SeatedByStaffId = seatedByStaffId;

        if (source == TableSessionSource.Reservation && reservationId is null)
        {
            throw new ArgumentException(
                "A session seated from a reservation must reference it.", nameof(reservationId));
        }

        if (source == TableSessionSource.WalkIn && reservationId is not null)
        {
            throw new ArgumentException(
                "A walk-in session must not reference a reservation.", nameof(reservationId));
        }
    }

    /// <summary>Seats a party that had booked.</summary>
    public static TableSession SeatReservation(
        Guid branchId,
        Guid diningTableId,
        Guid reservationId,
        int partySize,
        DateTime seatedAtUtc,
        Guid? seatedByStaffId = null) =>
        new(
            branchId,
            diningTableId,
            TableSessionSource.Reservation,
            Guard.NotEmpty(reservationId, nameof(reservationId)),
            partySize,
            seatedAtUtc,
            seatedByStaffId);

    /// <summary>
    /// Seats a party with no booking: a walk-in the waiter sat down, or anyone who scanned the
    /// table's QR code. Ordering is never gated behind a reservation.
    /// </summary>
    public static TableSession SeatWalkIn(
        Guid branchId,
        Guid diningTableId,
        int partySize,
        DateTime seatedAtUtc,
        Guid? seatedByStaffId = null) =>
        new(branchId, diningTableId, TableSessionSource.WalkIn, null, partySize, seatedAtUtc, seatedByStaffId);

    /// <summary>Records the tab opened for this seating.</summary>
    public void AttachTab(Guid tabId)
    {
        if (TabId is not null && TabId != tabId)
        {
            throw new InvalidOperationException("This session already has a different tab.");
        }

        TabId = Guard.NotEmpty(tabId, nameof(tabId));
    }

    /// <summary>Ends the occupancy. A session closes once and cannot close before it started.</summary>
    public void Close(DateTime closedAtUtc)
    {
        Guard.NotLocalTime(closedAtUtc, nameof(closedAtUtc));

        if (ClosedAtUtc is not null)
        {
            throw new InvalidOperationException("This session is already closed.");
        }

        if (closedAtUtc < SeatedAtUtc)
        {
            throw new ArgumentException("A session cannot close before it was seated.", nameof(closedAtUtc));
        }

        ClosedAtUtc = closedAtUtc;
    }
}
