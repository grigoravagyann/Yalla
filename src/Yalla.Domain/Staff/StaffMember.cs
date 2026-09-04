using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Staff;

/// <summary>
/// Someone who works for a venue and signs in to the staff tablet or the admin panel.
/// </summary>
/// <remarks>
/// A null <see cref="BranchId"/> means the person works across every branch of the venue, which
/// is the normal case for an owner and a common one for a manager. Authentication itself is a
/// later module: <see cref="PinHash"/> exists so the schema does not have to change when it
/// arrives, and nothing reads it yet.
/// </remarks>
public sealed class StaffMember : Entity
{
    public Guid VenueId { get; private set; }

    public Venue Venue { get; private set; } = null!;

    /// <summary>The single branch this person works at, or null for all branches of the venue.</summary>
    public Guid? BranchId { get; private set; }

    public Branch? Branch { get; private set; }

    public string FullName { get; private set; } = null!;

    public string Phone { get; private set; } = null!;

    public StaffRole Role { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Hash of the tablet sign-in PIN. Never the PIN itself.</summary>
    public string PinHash { get; private set; } = null!;

    private StaffMember()
    {
    }

    public StaffMember(
        Guid venueId,
        string fullName,
        string phone,
        StaffRole role,
        string pinHash,
        Guid? branchId = null)
        : base(Guid.CreateVersion7())
    {
        VenueId = Guard.NotEmpty(venueId, nameof(venueId));
        FullName = Guard.NotBlank(fullName, nameof(fullName), FieldLengths.PersonName);
        Phone = Guard.NotBlank(phone, nameof(phone), FieldLengths.Phone);
        Role = Guard.Defined(role, nameof(role));
        PinHash = Guard.NotBlank(pinHash, nameof(pinHash), FieldLengths.PinHash);
        BranchId = branchId;
        IsActive = true;
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    public void SetRole(StaffRole role) => Role = Guard.Defined(role, nameof(role));

    public void AssignToBranch(Guid? branchId) => BranchId = branchId;

    public void SetPinHash(string pinHash) =>
        PinHash = Guard.NotBlank(pinHash, nameof(pinHash), FieldLengths.PinHash);
}
