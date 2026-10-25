using System.Security.Cryptography;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// One table in a branch, optionally pinned on one of its room's photos so a diner can tap it.
/// </summary>
/// <remarks>
/// Named <c>DiningTable</c> rather than <c>Table</c> because <c>Table</c> collides with too much
/// of the persistence and reporting vocabulary to be worth the shorter name.
/// </remarks>
public sealed class DiningTable : Entity
{
    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    /// <summary>The zone this table sits in, if the branch divides its floor into areas.</summary>
    public Guid? FloorAreaId { get; private set; }

    public FloorArea? FloorArea { get; private set; }

    /// <summary>What is printed on the table, e.g. 7. Unique within the branch.</summary>
    public string Label { get; private set; } = null!;

    public int Seats { get; private set; }

    /// <summary>False for tables that only ever take walk-ins, e.g. bar stools.</summary>
    public bool IsBookable { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// Stable opaque token behind the QR code stuck to this table. A walk-in scans it to open a
    /// tab, so it must be unguessable and it must not change when the table is renamed or moved.
    /// </summary>
    public string QrToken { get; private set; } = null!;

    /// <summary>
    /// Denormalised occupancy, cached here so a floor plan renders in one query.
    /// See <see cref="TableStatus"/>: the authoritative record is <c>TableSession</c>.
    /// </summary>
    public TableStatus Status { get; private set; }

    /// <summary>
    /// The open <c>TableSession</c> occupying this table, or null when nobody is seated.
    /// </summary>
    /// <remarks>
    /// Deliberately not a foreign key. <c>TableSession</c> already points at the table, and making
    /// this a second, opposing key would create a circular FK that neither EF Core nor SQL Server
    /// can insert into without deferring one side. It is a cache pointer, like <see cref="Status"/>.
    /// </remarks>
    public Guid? CurrentSessionId { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. Two waiters seating a walk-in on the same table at the same
    /// moment must not both succeed.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    private DiningTable()
    {
    }

    public DiningTable(
        Guid branchId,
        string label,
        int seats,
        Guid? floorAreaId = null,
        bool isBookable = true,
        string? qrToken = null)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        FloorAreaId = floorAreaId;
        Label = Guard.NotBlank(label, nameof(label), FieldLengths.TableLabel);
        Seats = Guard.Positive(seats, nameof(seats));
        IsBookable = isBookable;
        IsActive = true;
        QrToken = qrToken is null
            ? GenerateQrToken()
            : NormaliseQrToken(Guard.NotBlank(qrToken, nameof(qrToken), FieldLengths.QrToken));
        Status = TableStatus.Free;
    }

    /// <summary>A fresh, unguessable QR token.</summary>
    public static string GenerateQrToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// A QR token as it is stored and looked up: trimmed and lower-case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written this way and read this way, so a scan matches by the server's own rule. The column
    /// compares exactly (see <c>DiningTableConfiguration</c>); before it did, scanning worked only
    /// because it had no collation of its own and SQL Server's default ignores case - which quietly
    /// hid the diner app upper-casing every scan.
    /// </para>
    /// <para>
    /// Lower-casing cannot make two tokens collide: the unique index already refused two that
    /// differed only by case. A token read back in capitals off a sticker is the same token.
    /// </para>
    /// </remarks>
    public static string NormaliseQrToken(string? qrToken) =>
        (qrToken ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Seats a party: <see cref="TableStatus.Free"/> or <see cref="TableStatus.Held"/> to
    /// <see cref="TableStatus.Occupied"/>, and raises <see cref="TableSeated"/>. Covers every seating - a walk-in, a
    /// booking, a scan, a held party - which differ only in what the event says about it.
    /// </summary>
    /// <param name="sitting">The sitting this opens, at this table.</param>
    /// <param name="how">Who seated whom.</param>
    /// <param name="booking">The booking honoured, already Seated; null when there is none.</param>
    /// <param name="reason">The reason as the caller gave it, or null.</param>
    /// <param name="clientCommandId">The command's id.</param>
    /// <param name="atUtc">Now.</param>
    /// <exception cref="InvalidTableTransitionException">The table is Occupied or out of service.</exception>
    public void Occupy(
        Occupancy.TableSession sitting,
        TableSeating how,
        Occupancy.Reservation? booking,
        string? reason,
        Guid clientCommandId,
        DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(sitting);

        var from = Status;

        Transition(TableStatus.Occupied);
        CurrentSessionId = Guard.NotEmpty(sitting.Id, nameof(sitting));

        Raise(new TableSeated(this, sitting, from, Guard.Defined(how, nameof(how)), booking, reason, clientCommandId, atUtc));
    }

    /// <summary>
    /// HoldForLateParty: <see cref="TableStatus.Free"/> to <see cref="TableStatus.Held"/>, and raises
    /// <see cref="TableHeld"/>.
    /// </summary>
    /// <param name="forReservationId">The table's next booking, if it has one.</param>
    /// <param name="reason">The reason as the caller gave it, or null.</param>
    /// <param name="clientCommandId">The command's id.</param>
    /// <param name="atUtc">Now.</param>
    public void PlaceHold(Guid? forReservationId, string? reason, Guid clientCommandId, DateTime atUtc)
    {
        var from = Status;

        Transition(TableStatus.Held);

        Raise(new TableHeld(this, from, forReservationId, reason, clientCommandId, atUtc));
    }

    /// <summary>
    /// ReleaseHold: <see cref="TableStatus.Held"/> back to <see cref="TableStatus.Free"/>, and raises
    /// <see cref="TableHoldReleased"/>.
    /// </summary>
    /// <param name="reason">The reason as the caller gave it, or null.</param>
    /// <param name="clientCommandId">The command's id.</param>
    /// <param name="atUtc">Now.</param>
    public void ReleaseHold(string? reason, Guid clientCommandId, DateTime atUtc)
    {
        if (Status != TableStatus.Held)
        {
            throw new InvalidTableTransitionException(Id, Label, Status, TableStatus.Free);
        }

        var from = Status;

        Transition(TableStatus.Free);
        CurrentSessionId = null;

        Raise(new TableHoldReleased(this, from, reason, clientCommandId, atUtc));
    }

    /// <summary>
    /// FreeTable: <see cref="TableStatus.Occupied"/> to <see cref="TableStatus.Free"/>, and raises
    /// <see cref="TableFreed"/> with what became of the sitting's tab. The caller closes the sitting, completes its
    /// booking and closes the tab when nothing is owed.
    /// </summary>
    /// <param name="sittingId">The sitting it ends, if any.</param>
    /// <param name="reservationId">The sitting's booking, if any.</param>
    /// <param name="tabId">The sitting's open tab, if any.</param>
    /// <param name="outstandingAmd">What is still owed on that tab, which stays open; null when nothing is, and the tab closes.</param>
    /// <param name="reason">The reason as the caller gave it, or null.</param>
    /// <param name="clientCommandId">The command's id.</param>
    /// <param name="atUtc">Now.</param>
    public void Vacate(
        Guid? sittingId,
        Guid? reservationId,
        Guid? tabId,
        long? outstandingAmd,
        string? reason,
        Guid clientCommandId,
        DateTime atUtc)
    {
        if (Status != TableStatus.Occupied)
        {
            throw new InvalidTableTransitionException(Id, Label, Status, TableStatus.Free);
        }

        var from = Status;

        Transition(TableStatus.Free);
        CurrentSessionId = null;

        Raise(new TableFreed(
            this, from, sittingId, reservationId, tabId, tabId is not null && outstandingAmd is null, outstandingAmd, reason,
            clientCommandId, atUtc));
    }

    /// <summary>
    /// MarkOutOfService: from <see cref="TableStatus.Free"/> or <see cref="TableStatus.Held"/>, and raises
    /// <see cref="TableTakenOutOfService"/>.
    /// </summary>
    /// <remarks>
    /// Refuses an occupied table. A table with diners at it cannot be marked broken - free it
    /// first - because the alternative is a floor plan that shows nobody sitting where somebody
    /// is sitting.
    /// </remarks>
    /// <param name="strandedBookings">How many bookings the table still has to come.</param>
    /// <param name="reason">The reason as the caller gave it, or null.</param>
    /// <param name="clientCommandId">The command's id.</param>
    /// <param name="atUtc">Now.</param>
    public void MarkOutOfService(int strandedBookings, string? reason, Guid clientCommandId, DateTime atUtc)
    {
        var from = Status;

        Transition(TableStatus.OutOfService);

        Raise(new TableTakenOutOfService(this, from, strandedBookings, reason, clientCommandId, atUtc));
    }

    /// <summary>
    /// ReturnToService: <see cref="TableStatus.OutOfService"/> to <see cref="TableStatus.Free"/>, and raises
    /// <see cref="TableReturnedToService"/>.
    /// </summary>
    /// <param name="reason">The reason as the caller gave it, or null.</param>
    /// <param name="clientCommandId">The command's id.</param>
    /// <param name="atUtc">Now.</param>
    public void ReturnToService(string? reason, Guid clientCommandId, DateTime atUtc)
    {
        if (Status != TableStatus.OutOfService)
        {
            throw new InvalidTableTransitionException(Id, Label, Status, TableStatus.Free);
        }

        var from = Status;

        Transition(TableStatus.Free);
        CurrentSessionId = null;

        Raise(new TableReturnedToService(this, from, reason, clientCommandId, atUtc));
    }

    /// <summary>
    /// Records that the diner kept the hold on this table for longer, raising <see cref="TableHoldExtended"/>. Changes
    /// nothing about the table: it is the event every tablet following the branch's change stream hears.
    /// </summary>
    /// <param name="reservationId">The booking whose hold was extended.</param>
    /// <param name="holdExpiresAtUtc">When the hold now ends.</param>
    /// <param name="reason">The reason as the caller gave it, or null.</param>
    /// <param name="clientCommandId">The command's id.</param>
    /// <param name="atUtc">Now.</param>
    public void NoteHoldExtended(
        Guid reservationId, DateTime holdExpiresAtUtc, string? reason, Guid clientCommandId, DateTime atUtc) =>
        Raise(new TableHoldExtended(this, reservationId, holdExpiresAtUtc, reason, clientCommandId, atUtc));

    /// <summary>
    /// The single mutation point for <see cref="Status"/>, validated against
    /// <see cref="TableStatusTransitions"/>. Private on purpose: a public
    /// <c>SetStatus(status)</c> is how illegal states get in.
    /// </summary>
    private void Transition(TableStatus to)
    {
        if (!TableStatusTransitions.IsAllowed(Status, to))
        {
            throw new InvalidTableTransitionException(Id, Label, Status, to);
        }

        Status = to;

        if (to != TableStatus.Occupied)
        {
            // A table is Occupied exactly when a session is sitting at it. Leaving a stale
            // pointer behind is the one thing this cache must never do.
            CurrentSessionId = null;
        }
    }

    public void Relabel(string label) =>
        Label = Guard.NotBlank(label, nameof(label), FieldLengths.TableLabel);

    /// <summary>
    /// The room photo this table is pinned on, or null when it is on none. Always one of its own
    /// room's photos - see <c>FloorAreaPhoto</c> - which the floor-plan save checks.
    /// </summary>
    /// <remarks>
    /// A diner books by tapping a pin, so a table with no pin is not offered to diners at all,
    /// however bookable it is. Staff still seat it: the pin is how a stranger finds the table, not
    /// whether it exists.
    /// </remarks>
    public Guid? PhotoId { get; private set; }

    public Media.Photo? Photo { get; private set; }

    /// <summary>
    /// Where the pin sits on <see cref="PhotoId"/>, as a fraction of the photo's width (0 left,
    /// 1 right). Null exactly when <see cref="PhotoId"/> is.
    /// </summary>
    /// <remarks>
    /// A fraction rather than pixels so the pin stays on the table at whatever size the phone draws
    /// the picture.
    /// </remarks>
    public double? PhotoX { get; private set; }

    /// <summary>As <see cref="PhotoX"/>, down the photo's height (0 top, 1 bottom).</summary>
    public double? PhotoY { get; private set; }

    /// <summary>Pins the table on a room photo, or takes it off with three nulls.</summary>
    /// <exception cref="FieldValidationException">Some of the three without the others, or a coordinate outside 0-1.</exception>
    public void PlaceOnPhoto(Guid? photoId, double? photoX, double? photoY) =>
        (PhotoId, PhotoX, PhotoY) = BranchListingRules.PhotoPosition(photoId, photoX, photoY, Label);

    /// <summary>
    /// Takes the table off its photo. Used when the photo leaves the room, or the room goes: a pin
    /// on a picture the room no longer shows is a table a diner cannot find.
    /// </summary>
    public void TakeOffPhoto() => (PhotoId, PhotoX, PhotoY) = (null, null, null);

    public void SetSeats(int seats) => Seats = Guard.Positive(seats, nameof(seats));

    /// <summary>
    /// Replaces the QR token. <b>Never called from an edit.</b> The code printed on the physical
    /// table must keep working when the table is renamed, re-pinned or moved to another room, so this is a separate,
    /// deliberate, audited action for the one case where the code is compromised.
    /// </summary>
    public void RegenerateQrToken() => QrToken = GenerateQrToken();

    public void AssignToArea(Guid? floorAreaId) => FloorAreaId = floorAreaId;

    public void SetBookable(bool isBookable) => IsBookable = isBookable;

    public void SetActive(bool isActive) => IsActive = isActive;
}
