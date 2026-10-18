using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;

namespace Yalla.Domain.Venues;

/// <summary>
/// A brand. A venue owns one or more <see cref="Branch"/>es and the staff accounts that work
/// across them. Nothing is operated or billed at this level - see <see cref="Branch"/>.
/// </summary>
/// <remarks>
/// <para>
/// A venue is <b>never hard-deleted</b>. Reservations, tabs and payments hang off its branches,
/// and those are financial and occupancy records that must outlive the customer relationship.
/// <see cref="SoftDelete"/> stamps <see cref="DeletedAtUtc"/> and switches the venue off; the
/// rows stay. <see cref="Restore"/> undoes it, putting the venue back as it was - switched on or off,
/// suspended or not - because a deletion that kept every row has nothing to rebuild.
/// </para>
/// <para>
/// <see cref="Suspend"/> is the lighter state, for a venue that has stopped paying: it disappears
/// from diner browsing but keeps every row and stays visible to its owner, who can see exactly
/// what they would get back by paying.
/// </para>
/// </remarks>
public sealed class Venue : Entity
{
    private readonly List<Branch> _branches = [];
    private readonly List<StaffMember> _staff = [];

    public string Name { get; private set; } = null!;

    /// <summary>
    /// Cafe, restaurant or bar. Selects the shipped <see cref="ReservationPolicy"/> defaults for
    /// new branches.
    /// </summary>
    public VenueType Type { get; private set; }

    /// <summary>Unique URL segment for the brand.</summary>
    public string Slug { get; private set; } = null!;

    public bool IsActive { get; private set; }

    /// <summary>Set while the venue is suspended - typically for non-payment. Cleared by <see cref="Reactivate"/>.</summary>
    public DateTime? SuspendedAtUtc { get; private set; }

    /// <summary>Set when the venue was soft-deleted. Cleared only by <see cref="Restore"/>.</summary>
    public DateTime? DeletedAtUtc { get; private set; }

    /// <summary>
    /// Whether the venue was switched on when it was deleted - what <see cref="Restore"/> puts back.
    /// Null for a live venue, and for one deleted before this was recorded, which restores switched on.
    /// </summary>
    public bool? ActiveBeforeDeletion { get; private set; }

    public bool IsSuspended => SuspendedAtUtc is not null;

    public bool IsDeleted => DeletedAtUtc is not null;

    /// <summary>
    /// Whether a diner may find this venue at all. Active, not suspended, not deleted. The owner's
    /// own view is not gated by this - see the class remarks.
    /// </summary>
    public bool IsBrowsable => IsActive && !IsSuspended && !IsDeleted;

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

    /// <summary>
    /// Renames the venue. Refused once deleted, like every other change: a deleted venue is a
    /// closed record, and letting its name or slug move would make the audit trail lie about what
    /// was deleted.
    /// </summary>
    public void Rename(string name)
    {
        RequireNotDeleted();
        Name = Guard.NotBlank(name, nameof(name), FieldLengths.Name);
    }

    public void SetType(VenueType type)
    {
        RequireNotDeleted();
        Type = Guard.Defined(type, nameof(type));
    }

    public void SetSlug(string slug)
    {
        RequireNotDeleted();
        Slug = SlugText.Normalise(slug, nameof(slug));
    }

    public void SetActive(bool isActive)
    {
        RequireNotDeleted();
        IsActive = isActive;
    }

    /// <summary>Takes the venue out of diner browsing while keeping everything. What non-payment does.</summary>
    public void Suspend(DateTime atUtc)
    {
        RequireNotDeleted();
        SuspendedAtUtc ??= Guard.NotLocalTime(atUtc, nameof(atUtc));
    }

    /// <summary>Puts a suspended venue back.</summary>
    public void Reactivate()
    {
        RequireNotDeleted();
        SuspendedAtUtc = null;
    }

    /// <summary>
    /// Marks the venue deleted. The row and everything under it stay; the venue stops being
    /// browsable and stops being active. <see cref="Restore"/> is the only way back.
    /// </summary>
    public void SoftDelete(DateTime atUtc)
    {
        RequireNotDeleted();
        DeletedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        ActiveBeforeDeletion = IsActive;
        IsActive = false;
    }

    /// <summary>
    /// Undoes <see cref="SoftDelete"/>: the venue comes back as it was before - switched on or off as
    /// it was then, and still suspended if it was suspended, because deleting never touched the
    /// suspension. Nothing under it was removed, so nothing needs rebuilding.
    /// </summary>
    /// <returns>True when it was deleted and is now restored; false when it was not deleted.</returns>
    public bool Restore()
    {
        if (!IsDeleted)
        {
            return false;
        }

        IsActive = ActiveBeforeDeletion ?? true;
        DeletedAtUtc = null;
        ActiveBeforeDeletion = null;

        return true;
    }

    private void RequireNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainStateException($"Venue '{Name}' has been deleted and cannot be changed.");
        }
    }
}
