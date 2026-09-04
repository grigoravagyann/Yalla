using Yalla.Domain.Common;
using Yalla.Domain.Staff;

namespace Yalla.Domain.Identity;

/// <summary>
/// One person's shift on one tablet: opened by a PIN, closed by inactivity.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "thirty minutes of inactivity" mean what it says. A JWT cannot expire on
/// idleness - it only knows the wall clock - so the short-lived session token is paired with this
/// row, whose <see cref="LastActivityAtUtc"/> moves forward every time the tablet renews. Stop
/// using the tablet for half an hour and the next renewal is refused, so the next action needs
/// the PIN again.
/// </para>
/// <para>
/// <see cref="AbsoluteExpiresAtUtc"/> is the backstop: a tablet polled by a background task would
/// otherwise renew forever and never ask for a PIN again.
/// </para>
/// </remarks>
public sealed class StaffSession : Entity
{
    /// <summary>How long a session survives with no renewal.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The longest a session can live however busy the tablet is - one long shift.</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(16);

    public Guid StaffMemberId { get; private set; }

    public StaffMember StaffMember { get; private set; } = null!;

    public Guid StaffDeviceId { get; private set; }

    public StaffDevice StaffDevice { get; private set; } = null!;

    /// <summary>Copied from the device at sign-in, so the session cannot outlive its branch scope.</summary>
    public Guid BranchId { get; private set; }

    /// <summary>Hash of the renewal handle currently valid for this session. Rotated on every use.</summary>
    public string RenewalTokenHash { get; private set; } = null!;

    public DateTime LastActivityAtUtc { get; private set; }

    public DateTime AbsoluteExpiresAtUtc { get; private set; }

    /// <summary>Set when the session was signed out, timed out on renewal, or the device revoked.</summary>
    public DateTime? EndedAtUtc { get; private set; }

    private StaffSession()
    {
    }

    public StaffSession(
        Guid staffMemberId,
        Guid staffDeviceId,
        Guid branchId,
        string renewalTokenHash,
        DateTime startedAtUtc)
        : base(Guid.CreateVersion7())
    {
        StaffMemberId = Guard.NotEmpty(staffMemberId, nameof(staffMemberId));
        StaffDeviceId = Guard.NotEmpty(staffDeviceId, nameof(staffDeviceId));
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        RenewalTokenHash = Guard.NotBlank(renewalTokenHash, nameof(renewalTokenHash), FieldLengths.TokenHash);
        StampCreatedAt(startedAtUtc);
        LastActivityAtUtc = startedAtUtc;
        AbsoluteExpiresAtUtc = startedAtUtc.Add(AbsoluteLifetime);
    }

    public bool IsRenewableAt(DateTime atUtc) =>
        EndedAtUtc is null
        && atUtc < AbsoluteExpiresAtUtc
        && atUtc - LastActivityAtUtc < IdleTimeout;

    /// <summary>Moves the idle window forward and rotates the renewal handle.</summary>
    public void Renew(string renewalTokenHash, DateTime atUtc)
    {
        RenewalTokenHash = Guard.NotBlank(renewalTokenHash, nameof(renewalTokenHash), FieldLengths.TokenHash);
        LastActivityAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
    }

    public void End(DateTime endedAtUtc) =>
        EndedAtUtc ??= Guard.NotLocalTime(endedAtUtc, nameof(endedAtUtc));
}
