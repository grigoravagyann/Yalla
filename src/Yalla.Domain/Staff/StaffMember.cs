using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Staff;

/// <summary>
/// Someone who works for a venue and signs in to the staff tablet or the admin panel.
/// </summary>
/// <remarks>
/// <para>
/// A null <see cref="BranchId"/> means the person works across every branch of the venue, which
/// is the normal case for an owner and a common one for a manager.
/// </para>
/// <para>
/// One person, two ways in, deliberately on one row. A waiter taps <see cref="PinHash"/> on an
/// enrolled tablet; an owner types <see cref="Email"/> and <see cref="PasswordHash"/> into the
/// admin panel; a manager does both, on the same day, and must be the same
/// <c>StaffMemberId</c> in the audit log either way. Splitting the two credentials across two
/// tables would give one human two identities and quietly break the log that answers "who gave
/// away my reserved table".
/// </para>
/// <para>
/// Both credentials are optional in the sense that only one is usually set: a kitchen hand has a
/// PIN and no email, an off-site owner has an email and (in practice) a PIN they never use.
/// </para>
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

    /// <summary>Hash of the tablet sign-in PIN. Never the PIN itself, and never logged.</summary>
    public string PinHash { get; private set; } = null!;

    /// <summary>Consecutive wrong PINs. Reset by a correct PIN or by a manager.</summary>
    public int PinFailedAttempts { get; private set; }

    /// <summary>
    /// Set when too many wrong PINs locked this person out. A manager clears it - a waiter who
    /// fat-fingered their PIN three times during service cannot be made to wait out a timer.
    /// </summary>
    public DateTime? PinLockedUntilUtc { get; private set; }

    /// <summary>
    /// Admin-panel sign-in address for an owner or manager. Null for staff who only tap a PIN.
    /// </summary>
    public string? Email { get; private set; }

    /// <summary>Hash of the admin-panel password. Null when the person has no email sign-in.</summary>
    public string? PasswordHash { get; private set; }

    /// <summary>Whether this person can sign in to the admin panel with email and password.</summary>
    public bool HasPasswordCredentials => Email is not null && PasswordHash is not null;

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

    public void SetPinHash(string pinHash)
    {
        PinHash = Guard.NotBlank(pinHash, nameof(pinHash), FieldLengths.PinHash);
        ClearPinLockout();
    }

    /// <summary>Gives this person an admin-panel sign-in.</summary>
    public void SetPasswordCredentials(string email, string passwordHash)
    {
        Email = Guard.NotBlank(email, nameof(email), FieldLengths.Email).ToLowerInvariant();
        PasswordHash = Guard.NotBlank(passwordHash, nameof(passwordHash), FieldLengths.PasswordHash);
    }

    /// <summary>Replaces the password, leaving the address alone.</summary>
    /// <exception cref="DomainStateException">Thrown when this person has no email sign-in.</exception>
    public void SetPasswordHash(string passwordHash)
    {
        if (Email is null)
        {
            throw new DomainStateException(
                "This staff member has no admin-panel sign-in, so there is no password to set.");
        }

        PasswordHash = Guard.NotBlank(passwordHash, nameof(passwordHash), FieldLengths.PasswordHash);
    }

    /// <summary>Whether PIN sign-in is currently refused for this person.</summary>
    public bool IsPinLockedAt(DateTime atUtc) => PinLockedUntilUtc is { } until && atUtc < until;

    /// <summary>
    /// Counts a wrong PIN and locks the person out once the threshold is reached.
    /// </summary>
    /// <param name="atUtc">Now.</param>
    /// <param name="maxAttempts">Consecutive failures tolerated before the lockout.</param>
    /// <param name="lockoutDuration">
    /// How long the lockout lasts on its own. It is a floor, not the whole answer: a manager can
    /// clear it sooner, which is the path that actually gets used mid-service.
    /// </param>
    /// <returns>True when this failure caused the lockout.</returns>
    public bool RecordFailedPin(DateTime atUtc, int maxAttempts, TimeSpan lockoutDuration)
    {
        Guard.Positive(maxAttempts, nameof(maxAttempts));
        Guard.NotLocalTime(atUtc, nameof(atUtc));

        PinFailedAttempts++;

        if (PinFailedAttempts < maxAttempts)
        {
            return false;
        }

        PinLockedUntilUtc = atUtc.Add(lockoutDuration);
        return true;
    }

    /// <summary>Clears the failure count and any lockout. Called on a correct PIN, and by a manager.</summary>
    public void ClearPinLockout()
    {
        PinFailedAttempts = 0;
        PinLockedUntilUtc = null;
    }
}
