using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Identity;

/// <summary>
/// A rotating refresh token for a diner or a venue user, stored only as a hash.
/// </summary>
/// <remarks>
/// <para>
/// Rotation is what makes a thirty-day token acceptable: each use retires the token and issues
/// its successor, so a stolen copy is only useful until the real client next refreshes.
/// </para>
/// <para>
/// <see cref="ChainId"/> is what makes theft <i>detectable</i>. Every token descended from one
/// sign-in shares a chain. Presenting a token that has already been rotated means two parties
/// hold the same secret, and there is no way to tell which of them is the thief - so the whole
/// chain is revoked and both are made to sign in again. Revoking only the reused token would let
/// whichever party refreshed last keep the session, which is as likely to be the attacker.
/// </para>
/// </remarks>
public sealed class RefreshToken : Entity
{
    /// <summary>How long a freshly issued token stays usable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public RefreshTokenSubject SubjectType { get; private set; }

    /// <summary>The <c>DinerUser</c> or <c>StaffMember</c> this token signs in as.</summary>
    public Guid SubjectId { get; private set; }

    /// <summary>
    /// The family this token belongs to: one sign-in, however many rotations. Reusing any member
    /// revokes all of them.
    /// </summary>
    public Guid ChainId { get; private set; }

    /// <summary>Hex SHA-256 of the token. Never the token.</summary>
    public string TokenHash { get; private set; } = null!;

    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>Set when this token was exchanged for its successor.</summary>
    public DateTime? RotatedAtUtc { get; private set; }

    /// <summary>The successor issued when this one was rotated.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    public DateTime? RevokedAtUtc { get; private set; }

    /// <summary>Why it was revoked: rotated-token-reused, signed-out, password-changed.</summary>
    public string? RevokedReason { get; private set; }

    private RefreshToken()
    {
    }

    public RefreshToken(
        RefreshTokenSubject subjectType,
        Guid subjectId,
        Guid chainId,
        string tokenHash,
        DateTime issuedAtUtc)
        : base(Guid.CreateVersion7())
    {
        SubjectType = Guard.Defined(subjectType, nameof(subjectType));
        SubjectId = Guard.NotEmpty(subjectId, nameof(subjectId));
        ChainId = Guard.NotEmpty(chainId, nameof(chainId));
        TokenHash = Guard.NotBlank(tokenHash, nameof(tokenHash), FieldLengths.TokenHash);
        StampCreatedAt(issuedAtUtc);
        ExpiresAtUtc = issuedAtUtc.Add(Lifetime);
    }

    /// <summary>Whether this token may be exchanged.</summary>
    public bool IsUsableAt(DateTime atUtc) =>
        RevokedAtUtc is null && RotatedAtUtc is null && atUtc < ExpiresAtUtc;

    /// <summary>
    /// True for a token that was already spent. Presenting one is the theft signal that revokes
    /// the chain.
    /// </summary>
    public bool WasAlreadyRotated => RotatedAtUtc is not null;

    /// <summary>Retires this token in favour of its successor.</summary>
    /// <exception cref="DomainStateException">Thrown when it was already rotated.</exception>
    public void Rotate(Guid replacedByTokenId, DateTime atUtc)
    {
        if (RotatedAtUtc is not null)
        {
            throw new DomainStateException("This refresh token has already been rotated.");
        }

        ReplacedByTokenId = Guard.NotEmpty(replacedByTokenId, nameof(replacedByTokenId));
        RotatedAtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
    }

    public void Revoke(DateTime revokedAtUtc, string reason)
    {
        RevokedAtUtc ??= Guard.NotLocalTime(revokedAtUtc, nameof(revokedAtUtc));
        RevokedReason ??= Guard.NotBlank(reason, nameof(reason), FieldLengths.Slug);
    }
}
