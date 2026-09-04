using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Identity;

/// <summary>
/// A one-time code a manager generates so a tablet can enrol itself to a branch.
/// </summary>
/// <remarks>
/// Single use is the whole security model: the code is read aloud or typed off a screen, so it
/// will be overheard. Redeeming it exchanges it for a device token bound to one
/// <see cref="StaffDevice"/> row, and a second redemption is refused - if the code leaked, the
/// manager sees a device they did not enrol and revokes it.
/// </remarks>
public sealed class StaffEnrolmentCode : Entity
{
    /// <summary>How long an unredeemed code stays usable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Guid VenueId { get; private set; }

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    /// <summary>Hash of the code. Never the code.</summary>
    public string CodeHash { get; private set; } = null!;

    /// <summary>The manager who generated it, so an unexpected enrolment has an author.</summary>
    public Guid CreatedByStaffMemberId { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? RedeemedAtUtc { get; private set; }

    /// <summary>The device this code produced, set at redemption.</summary>
    public Guid? RedeemedByDeviceId { get; private set; }

    private StaffEnrolmentCode()
    {
    }

    public StaffEnrolmentCode(
        Guid venueId,
        Guid branchId,
        string codeHash,
        Guid createdByStaffMemberId,
        DateTime issuedAtUtc)
        : base(Guid.CreateVersion7())
    {
        VenueId = Guard.NotEmpty(venueId, nameof(venueId));
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        CodeHash = Guard.NotBlank(codeHash, nameof(codeHash), FieldLengths.TokenHash);
        CreatedByStaffMemberId = Guard.NotEmpty(createdByStaffMemberId, nameof(createdByStaffMemberId));
        StampCreatedAt(issuedAtUtc);
        ExpiresAtUtc = issuedAtUtc.Add(Lifetime);
    }

    public bool IsUsableAt(DateTime atUtc) => RedeemedAtUtc is null && atUtc < ExpiresAtUtc;

    /// <summary>Spends the code.</summary>
    /// <exception cref="DomainStateException">Thrown when it has already been spent.</exception>
    public void Redeem(Guid deviceId, DateTime atUtc)
    {
        if (RedeemedAtUtc is not null)
        {
            throw new DomainStateException("This enrolment code has already been used.");
        }

        RedeemedByDeviceId = Guard.NotEmpty(deviceId, nameof(deviceId));
        RedeemedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
    }
}
