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
    /// <see cref="TableStatus.Occupied"/>. Covers SeatWalkIn, SeatReservation and SeatHeldParty,
    /// which differ only in what the caller records around them.
    /// </summary>
    public void Occupy(Guid sessionId)
    {
        Transition(TableStatus.Occupied);
        CurrentSessionId = Guard.NotEmpty(sessionId, nameof(sessionId));
    }

    /// <summary>HoldForLateParty: <see cref="TableStatus.Free"/> to <see cref="TableStatus.Held"/>.</summary>
    public void PlaceHold() => Transition(TableStatus.Held);

    /// <summary>ReleaseHold: <see cref="TableStatus.Held"/> back to <see cref="TableStatus.Free"/>.</summary>
    public void ReleaseHold()
    {
        if (Status != TableStatus.Held)
        {
            throw new InvalidTableTransitionException(Id, Label, Status, TableStatus.Free);
        }

        Transition(TableStatus.Free);
        CurrentSessionId = null;
    }

    /// <summary>FreeTable: <see cref="TableStatus.Occupied"/> to <see cref="TableStatus.Free"/>.</summary>
    public void Vacate()
    {
        if (Status != TableStatus.Occupied)
        {
            throw new InvalidTableTransitionException(Id, Label, Status, TableStatus.Free);
        }

        Transition(TableStatus.Free);
        CurrentSessionId = null;
    }

    /// <summary>
    /// MarkOutOfService: from <see cref="TableStatus.Free"/> or <see cref="TableStatus.Held"/>.
    /// </summary>
    /// <remarks>
    /// Refuses an occupied table. A table with diners at it cannot be marked broken - free it
    /// first - because the alternative is a floor plan that shows nobody sitting where somebody
    /// is sitting.
    /// </remarks>
    public void MarkOutOfService() => Transition(TableStatus.OutOfService);

    /// <summary>ReturnToService: <see cref="TableStatus.OutOfService"/> to <see cref="TableStatus.Free"/>.</summary>
    public void ReturnToService()
    {
        if (Status != TableStatus.OutOfService)
        {
            throw new InvalidTableTransitionException(Id, Label, Status, TableStatus.Free);
        }

        Transition(TableStatus.Free);
        CurrentSessionId = null;
    }

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
