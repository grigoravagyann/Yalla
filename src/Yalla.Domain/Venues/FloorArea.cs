using Yalla.Domain.Common;

namespace Yalla.Domain.Venues;

/// <summary>
/// A room of the branch: Hall, Terrace, VIP. Diners pick one and swipe through its photos, staff
/// think in it; tables belong to at most one.
/// </summary>
/// <remarks>
/// Called an area in the schema and the API for history - it began as a zone on a drawn floor plan.
/// The drawing is gone; the room's photos, and the pins on them, are what a diner sees now.
/// </remarks>
public sealed class FloorArea : Entity
{
    private readonly List<DiningTable> _diningTables = [];

    private readonly List<FloorAreaPhoto> _photos = [];

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>Order the area is listed in, lowest first.</summary>
    public int DisplayOrder { get; private set; }

    public IReadOnlyCollection<DiningTable> DiningTables => _diningTables;

    /// <summary>The room's pictures. Order by <see cref="FloorAreaPhoto.Position"/>; this collection is not ordered.</summary>
    public IReadOnlyCollection<FloorAreaPhoto> Photos => _photos;

    private FloorArea()
    {
    }

    public FloorArea(Guid branchId, string name, int displayOrder)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);
        DisplayOrder = Guard.NotNegative(displayOrder, nameof(displayOrder));
    }

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);

    public void SetDisplayOrder(int displayOrder) =>
        DisplayOrder = Guard.NotNegative(displayOrder, nameof(displayOrder));
}
