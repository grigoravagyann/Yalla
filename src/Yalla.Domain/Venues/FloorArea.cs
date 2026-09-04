using Yalla.Domain.Common;

namespace Yalla.Domain.Venues;

/// <summary>
/// A named zone of the floor plan: Windows, Bar, Terrace. Diners filter by it and staff think in
/// it; tables belong to at most one.
/// </summary>
public sealed class FloorArea : Entity
{
    private readonly List<DiningTable> _diningTables = [];

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>Order the area is listed in, lowest first.</summary>
    public int DisplayOrder { get; private set; }

    public IReadOnlyCollection<DiningTable> DiningTables => _diningTables;

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
