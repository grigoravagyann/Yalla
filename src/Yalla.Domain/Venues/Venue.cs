using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;

namespace Yalla.Domain.Venues;

/// <summary>
/// A brand. A venue owns one or more <see cref="Branch"/>es and the staff accounts that work
/// across them. Nothing is operated or billed at this level - see <see cref="Branch"/>.
/// </summary>
public sealed class Venue : Entity
{
    private readonly List<Branch> _branches = [];
    private readonly List<StaffMember> _staff = [];

    public string Name { get; private set; } = null!;

    /// <summary>
    /// Cafe or restaurant. Selects the shipped <see cref="ReservationPolicy"/> defaults for
    /// new branches.
    /// </summary>
    public VenueType Type { get; private set; }

    /// <summary>Unique URL segment for the brand.</summary>
    public string Slug { get; private set; } = null!;

    public bool IsActive { get; private set; }

    public IReadOnlyCollection<Branch> Branches => _branches;

    public IReadOnlyCollection<StaffMember> Staff => _staff;

    private Venue()
    {
    }

    public Venue(string name, VenueType type, string slug)
        : base(Guid.CreateVersion7())
    {
        Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);
        Type = Guard.Defined(type, nameof(type));
        Slug = SlugText.Normalise(slug, nameof(slug));
        IsActive = true;
    }

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);

    public void SetActive(bool isActive) => IsActive = isActive;
}
