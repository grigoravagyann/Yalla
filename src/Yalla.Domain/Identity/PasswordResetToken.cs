using Yalla.Domain.Common;
using Yalla.Domain.Staff;

namespace Yalla.Domain.Identity;

/// <summary>
/// A single-use, short-lived handle emailed to an owner or manager who forgot their password.
/// </summary>
/// <remarks>
/// Stored as a hash for the same reason as everything else here: possession of the database must
/// not be possession of a way in. Consuming one revokes every refresh token the account holds -
/// the usual reason to reset a password is that somebody else might have had it.
/// </remarks>
public sealed class PasswordResetToken : Entity
{
    /// <summary>How long a reset link stays usable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public Guid StaffMemberId { get; private set; }

    public StaffMember StaffMember { get; private set; } = null!;

    /// <summary>Hex SHA-256 of the handle in the link. Never the handle.</summary>
    public string TokenHash { get; private set; } = null!;

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? ConsumedAtUtc { get; private set; }

    private PasswordResetToken()
    {
    }

    public PasswordResetToken(Guid staffMemberId, string tokenHash, DateTime issuedAtUtc)
        : base(Guid.CreateVersion7())
    {
        StaffMemberId = Guard.NotEmpty(staffMemberId, nameof(staffMemberId));
        TokenHash = Guard.NotBlank(tokenHash, nameof(tokenHash), FieldLengths.TokenHash);
        StampCreatedAt(issuedAtUtc);
        ExpiresAtUtc = issuedAtUtc.Add(Lifetime);
    }

    public bool IsUsableAt(DateTime atUtc) => ConsumedAtUtc is null && atUtc < ExpiresAtUtc;

    public void Consume(DateTime atUtc) =>
        ConsumedAtUtc ??= Guard.NotLocalTime(atUtc, nameof(atUtc));
}
