using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Staff;

/// <summary>
/// Someone who works for a venue and signs in to the staff tablet or the admin panel - or, with
/// the <see cref="StaffRole.PlatformAdmin"/> role, someone who runs Yalla itself.
/// </summary>
/// <remarks>
/// <para>
/// A null <see cref="BranchId"/> means the person works across every branch of the venue, which
/// is the normal case for an owner and a common one for a manager.
/// </para>
/// <para>
/// A null <see cref="VenueId"/> is allowed for exactly one role. A platform admin is not staff of
/// any venue: they onboard venues, suspend the ones that stop paying and change what a customer
/// pays, so tying them to one venue would be a lie the scope policies would then have to work
/// around. The rule is enforced here as an invariant - a platform admin with a venue id is
/// invalid, and every other role <b>requires</b> one - so no code path can produce a row the
/// policies would misread.
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
    /// <summary>
    /// The <see cref="PinHash"/> of someone who has no PIN and never will. A platform admin signs
    /// in with a password only; a tablet never offers them, because they belong to no branch.
    /// </summary>
    public const string NoPin = "no-pin";

    /// <summary>The venue this person works for. Null only for a <see cref="StaffRole.PlatformAdmin"/>.</summary>
    public Guid? VenueId { get; private set; }

    public Venue? Venue { get; private set; }

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

    /// <summary>Whether this person runs Yalla rather than working for a venue.</summary>
    public bool IsPlatformAdmin => Role == StaffRole.PlatformAdmin;

    /// <summary>
    /// A real role, never <see cref="StaffRole.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// <c>Guard.Defined</c> is not enough on its own: <c>Unknown</c> <i>is</i> a defined member, so
    /// it would pass. It exists to make a forgotten role loud rather than to be assignable, and the
    /// place to be loud is here, before a row that means nothing reaches the database.
    /// </remarks>
    private static StaffRole RequireRealRole(StaffRole role, string paramName)
    {
        Guard.Defined(role, paramName);

        return role == StaffRole.Unknown
            ? throw new ArgumentOutOfRangeException(
                paramName, role, "A staff member needs a real role. Unknown means somebody forgot to set one.")
            : role;
    }

    private StaffMember()
    {
    }

    /// <summary>A member of a venue's staff. The venue is required; see the class remarks.</summary>
    public StaffMember(
        Guid venueId,
        string fullName,
        string phone,
        StaffRole role,
        string pinHash,
        Guid? branchId = null)
        : this(Guard.NotEmpty(venueId, nameof(venueId)), fullName, phone, role, pinHash, branchId, scoped: true)
    {
    }

    private StaffMember(
        Guid? venueId,
        string fullName,
        string phone,
        StaffRole role,
        string pinHash,
        Guid? branchId,
        bool scoped)
        : base(Guid.CreateVersion7())
    {
        _ = scoped;
        VenueId = venueId;
        FullName = Guard.NotBlank(fullName, nameof(fullName), FieldLengths.PersonName);
        Phone = Guard.NotBlank(phone, nameof(phone), FieldLengths.Phone);
        Role = RequireRealRole(role, nameof(role));
        PinHash = Guard.NotBlank(pinHash, nameof(pinHash), FieldLengths.PinHash);
        BranchId = branchId;
        IsActive = true;

        EnforceScopeInvariant();
    }

    /// <summary>
    /// Someone who runs Yalla. No venue, no branch, no PIN - a password is their only way in.
    /// </summary>
    public static StaffMember PlatformAdmin(string fullName, string phone, string email, string passwordHash)
    {
        var admin = new StaffMember(null, fullName, phone, StaffRole.PlatformAdmin, NoPin, null, scoped: false);
        admin.SetPasswordCredentials(email, passwordHash);

        return admin;
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    /// <summary>
    /// Changes the role. Refused when the new role would break the scope rule: a venue's staff
    /// member cannot become a platform admin, and a platform admin cannot be given a venue role.
    /// </summary>
    public void SetRole(StaffRole role)
    {
        var previous = Role;
        Role = RequireRealRole(role, nameof(role));

        try
        {
            EnforceScopeInvariant();
        }
        catch
        {
            Role = previous;
            throw;
        }
    }

    public void AssignToBranch(Guid? branchId)
    {
        if (IsPlatformAdmin && branchId is not null)
        {
            throw new ArgumentException("A platform admin belongs to no branch.", nameof(branchId));
        }

        BranchId = branchId;
    }

    public void Rename(string fullName) =>
        FullName = Guard.NotBlank(fullName, nameof(fullName), FieldLengths.PersonName);

    public void SetPhone(string phone) => Phone = Guard.NotBlank(phone, nameof(phone), FieldLengths.Phone);

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

    /// <summary>
    /// Names the address this person will sign in with, without giving them a password.
    /// </summary>
    /// <remarks>
    /// The half of a sign-in an owner is allowed to type for somebody else. The password is the
    /// other half and only its holder ever chooses it, through the reset link this address is
    /// about to be sent - so <see cref="PasswordHash"/> is left exactly as it was: null for a
    /// newcomer, and still working for somebody whose address is merely being corrected.
    /// Normalised the way sign-in reads it, so a stray space or capital never makes an account
    /// unreachable.
    /// </remarks>
    public void SetSignInEmail(string email) =>
        Email = Guard.NotBlank(email, nameof(email), FieldLengths.Email).Trim().ToLowerInvariant();

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

    /// <summary>
    /// The scope rule: a platform admin has no venue and no branch; everyone else has a venue.
    /// </summary>
    private void EnforceScopeInvariant()
    {
        if (Role == StaffRole.PlatformAdmin)
        {
            if (VenueId is not null || BranchId is not null)
            {
                throw new ArgumentException(
                    "A platform admin belongs to no venue and no branch. Give them a venue role instead.",
                    nameof(Role));
            }

            return;
        }

        if (VenueId is null)
        {
            throw new ArgumentException(
                $"A {Role} must belong to a venue. Only a platform admin has none.", nameof(VenueId));
        }
    }
}
