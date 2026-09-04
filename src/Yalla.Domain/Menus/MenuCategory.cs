using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Menus;

/// <summary>
/// A section of one branch's menu: Coffee, Breakfast, Wine.
/// </summary>
/// <remarks>
/// The menu belongs to the branch, not the venue. Two locations of the same brand routinely
/// price differently and stock different things, and pretending otherwise forces an override
/// mechanism later.
/// </remarks>
public sealed class MenuCategory : Entity
{
    private readonly List<MenuItem> _items = [];

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public int DisplayOrder { get; private set; }

    public IReadOnlyCollection<MenuItem> Items => _items;

    private MenuCategory()
    {
    }

    public MenuCategory(Guid branchId, string name, int displayOrder)
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
