using Yalla.Domain.Common;
using Yalla.Domain.Staff;

namespace Yalla.Domain.Identity;

/// <summary>
/// A single-use, short-lived handle that lets an owner or manager set their admin-panel password:
/// emailed to one who forgot it, or handed over by the person who issued their sign-in.
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

    /// <summary>
    /// How long a link issued by hand stays usable.
    /// </summary>
    /// <remarks>
    /// An hour is right for a link somebody asked for and is sitting at the screen waiting to
    /// receive. It is wrong for one an owner pastes into a chat for a manager who will open it on
    /// their phone after the shift - a link that dies before it is read is a second request, a
    /// third, and then a password typed on their behalf. A day is the product's onboarding window
    /// already: <see cref="StaffEnrolmentCode.Lifetime"/> gives a tablet code the same. Single use
    /// and the hash at rest are what keep the longer window safe.
    /// </remarks>
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(24);

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
        : this(staffMemberId, tokenHash, issuedAtUtc, Lifetime)
    {
    }

    public PasswordResetToken(Guid staffMemberId, string tokenHash, DateTime issuedAtUtc, TimeSpan lifetime)
        : base(Guid.CreateVersion7())
    {
        StaffMemberId = Guard.NotEmpty(staffMemberId, nameof(staffMemberId));
        TokenHash = Guard.NotBlank(tokenHash, nameof(tokenHash), FieldLengths.TokenHash);
        StampCreatedAt(issuedAtUtc);
        ExpiresAtUtc = issuedAtUtc.Add(lifetime);
    }

    public bool IsUsableAt(DateTime atUtc) => ConsumedAtUtc is null && atUtc < ExpiresAtUtc;

    public void Consume(DateTime atUtc) =>
        ConsumedAtUtc ??= Guard.NotLocalTime(atUtc, nameof(atUtc));

    /// <summary>
    /// Retires a link without anyone having used it, because a newer one was issued for the same
    /// person.
    /// </summary>
    /// <remarks>
    /// A lost link has to stop working the moment a replacement is sent, or "send a new one" is
    /// not a way to take the old one back. It is not a reset: no password changed and no session
    /// ended, which is why this is its own word rather than <see cref="Consume"/> - a table that
    /// showed resets nobody completed would be lying to whoever reads it later.
    /// </remarks>
    public void Supersede(DateTime atUtc) =>
        ConsumedAtUtc ??= Guard.NotLocalTime(atUtc, nameof(atUtc));
}
