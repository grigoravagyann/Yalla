using System.Security.Cryptography;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// One table on a branch's floor plan, positioned so the diner app can draw the room from above.
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

    /// <summary>Left edge on the floor-plan canvas, in the same design units as <c>Branch.FloorWidth</c>.</summary>
    public int X { get; private set; }

    /// <summary>Top edge on the floor-plan canvas, in the same design units as <c>Branch.FloorHeight</c>.</summary>
    public int Y { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Clockwise rotation of the table on the canvas, 0 to 360 degrees.</summary>
    public double RotationDegrees { get; private set; }

    public TableShape Shape { get; private set; }

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
        int x,
        int y,
        int width,
        int height,
        TableShape shape,
        Guid? floorAreaId = null,
        double rotationDegrees = 0d,
        bool isBookable = true,
        string? qrToken = null)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        FloorAreaId = floorAreaId;
        Label = Guard.NotBlank(label, nameof(label), FieldLengths.TableLabel);
        Seats = Guard.Positive(seats, nameof(seats));
        X = Guard.NotNegative(x, nameof(x));
        Y = Guard.NotNegative(y, nameof(y));
        Width = Guard.Positive(width, nameof(width));
        Height = Guard.Positive(height, nameof(height));
        RotationDegrees = NormaliseRotation(rotationDegrees);
        Shape = Guard.Defined(shape, nameof(shape));
        IsBookable = isBookable;
        IsActive = true;
        QrToken = qrToken is null
            ? GenerateQrToken()
            : Guard.NotBlank(qrToken, nameof(qrToken), FieldLengths.QrToken);
        Status = TableStatus.Free;
    }

    /// <summary>A fresh, unguessable QR token.</summary>
    public static string GenerateQrToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Updates the cached occupancy. A table is <see cref="TableStatus.Occupied"/> exactly when a
    /// session is sitting at it, which is the one thing this cache must never get wrong.
    /// </summary>
    public void ApplyStatus(TableStatus status, Guid? currentSessionId = null)
    {
        Guard.Defined(status, nameof(status));

        if (status == TableStatus.Occupied && currentSessionId is null)
        {
            throw new ArgumentException(
                "An occupied table must reference the session seated at it.", nameof(currentSessionId));
        }

        if (status != TableStatus.Occupied && currentSessionId is not null)
        {
            throw new ArgumentException(
                "Only an occupied table may reference a session.", nameof(currentSessionId));
        }

        Status = status;
        CurrentSessionId = currentSessionId;
    }

    public void Relabel(string label) =>
        Label = Guard.NotBlank(label, nameof(label), FieldLengths.TableLabel);

    public void MoveTo(int x, int y, double rotationDegrees)
    {
        X = Guard.NotNegative(x, nameof(x));
        Y = Guard.NotNegative(y, nameof(y));
        RotationDegrees = NormaliseRotation(rotationDegrees);
    }

    public void Resize(int width, int height)
    {
        Width = Guard.Positive(width, nameof(width));
        Height = Guard.Positive(height, nameof(height));
    }

    public void SetSeats(int seats) => Seats = Guard.Positive(seats, nameof(seats));

    public void AssignToArea(Guid? floorAreaId) => FloorAreaId = floorAreaId;

    public void SetBookable(bool isBookable) => IsBookable = isBookable;

    public void SetActive(bool isActive) => IsActive = isActive;

    private static double NormaliseRotation(double rotationDegrees) =>
        double.IsNaN(rotationDegrees) || rotationDegrees < 0d || rotationDegrees > 360d
            ? throw new ArgumentOutOfRangeException(
                nameof(rotationDegrees), rotationDegrees, "Rotation must be between 0 and 360 degrees.")
            : rotationDegrees;
}
