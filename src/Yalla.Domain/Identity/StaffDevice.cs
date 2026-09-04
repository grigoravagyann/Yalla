using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Identity;

/// <summary>
/// A tablet enrolled to one branch, holding a long-lived device token.
/// </summary>
/// <remarks>
/// <para>
/// The device is not a person and holds no permissions of its own: it can do exactly one thing,
/// which is offer a PIN and be given a staff session in return. That separation is what lets the
/// tablet stay signed in for months while the person acting on it changes every few hours.
/// </para>
/// <para>
/// <see cref="RevokedAtUtc"/> is the reason this is a table rather than a claim in a token. A
/// tablet left in a taxi has to stop working from the admin panel, immediately, without waiting
/// for a token to expire - so every request carrying a device or session token checks this row.
/// </para>
/// </remarks>
public sealed class StaffDevice : Entity
{
    public Guid VenueId { get; private set; }

    /// <summary>
    /// The one branch this tablet belongs to. A device token is branch-scoped by construction,
    /// which is half of why a waiter at branch A cannot act on branch B.
    /// </summary>
    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    /// <summary>What a manager calls it in the admin panel: "Bar tablet", "Terrace".</summary>
    public string Name { get; private set; } = null!;

    /// <summary>Last time a token from this device was seen, so an unused tablet is obvious.</summary>
    public DateTime? LastSeenAtUtc { get; private set; }

    /// <summary>Set when a manager revoked the tablet. Never cleared - enrol a new device instead.</summary>
    public DateTime? RevokedAtUtc { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    private StaffDevice()
    {
    }

    public StaffDevice(Guid venueId, Guid branchId, string name, DateTime enrolledAtUtc)
        : base(Guid.CreateVersion7())
    {
        VenueId = Guard.NotEmpty(venueId, nameof(venueId));
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        Name = Guard.NotBlank(name, nameof(name), FieldLengths.DeviceName);
        StampCreatedAt(enrolledAtUtc);
        LastSeenAtUtc = enrolledAtUtc;
    }

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(name), FieldLengths.DeviceName);

    /// <summary>
    /// Records that the tablet was seen. Callers throttle this - a write per request would put
    /// the busiest table in the database on the read path.
    /// </summary>
    public void Touch(DateTime atUtc) => LastSeenAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));

    public void Revoke(DateTime revokedAtUtc) =>
        RevokedAtUtc ??= Guard.NotLocalTime(revokedAtUtc, nameof(revokedAtUtc));
}
