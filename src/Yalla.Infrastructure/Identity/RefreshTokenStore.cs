using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Issuing, rotating and revoking refresh tokens, in one place for both identity types that have
/// them.
/// </summary>
/// <remarks>
/// The caller commits, so a rotation and the sign-in it belongs to land in the same transaction
/// and a token can never exist for a sign-in that did not happen. The single exception is theft
/// detection, which commits its own revocation - see <see cref="RotateAsync"/>.
/// </remarks>
internal sealed class RefreshTokenStore(YallaDbContext db, IClock clock)
{
    /// <summary>Starts a new chain: one sign-in, one family of tokens.</summary>
    public (RefreshToken Entity, string PlainToken) Issue(RefreshTokenSubject subjectType, Guid subjectId) =>
        Issue(subjectType, subjectId, Guid.CreateVersion7());

    private (RefreshToken Entity, string PlainToken) Issue(
        RefreshTokenSubject subjectType,
        Guid subjectId,
        Guid chainId)
    {
        var plain = Secrets.NewOpaqueToken();
        var entity = new RefreshToken(subjectType, subjectId, chainId, Secrets.Hash(plain), clock.UtcNow);

        db.RefreshTokens.Add(entity);

        return (entity, plain);
    }

    /// <summary>
    /// Spends a presented token and issues its successor.
    /// </summary>
    /// <remarks>
    /// The interesting branch is the third one. A token that has already been rotated is being
    /// presented by someone - and there is no way to know whether that is the real client
    /// replaying a request or a thief using a copy. Revoking only that token would leave whichever
    /// party refreshed most recently in possession of a live session, and that is as likely to be
    /// the thief. Revoking the chain ends the argument: both sign in again.
    /// </remarks>
    /// <exception cref="AuthenticationFailedException">The token is unknown, expired, revoked or reused.</exception>
    public async Task<(RefreshToken Entity, string PlainToken, Guid SubjectId)> RotateAsync(
        string presentedToken,
        RefreshTokenSubject expectedSubjectType,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;
        var hash = Secrets.Hash(presentedToken ?? string.Empty);

        var existing = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existing is null || existing.SubjectType != expectedSubjectType)
        {
            throw new AuthenticationFailedException(
                "refresh-token-invalid", "That refresh token is not valid. Sign in again.");
        }

        if (existing.WasAlreadyRotated)
        {
            await RevokeChainAsync(existing.ChainId, "rotated-token-reused", cancellationToken);

            // Committed here rather than left to the caller: the caller's next act is to throw,
            // so an uncommitted revocation would be silently discarded and the chain would stay
            // live. This is the one path in this class that saves, and it is the one that must.
            await db.SaveChangesAsync(cancellationToken);

            throw new AuthenticationFailedException(
                "refresh-token-reused",
                "That refresh token was already used. Every session from this sign-in has been "
                + "ended as a precaution. Sign in again.");
        }

        if (!existing.IsUsableAt(nowUtc))
        {
            throw new AuthenticationFailedException(
                "refresh-token-invalid", "That refresh token is not valid. Sign in again.");
        }

        var (successor, plain) = Issue(existing.SubjectType, existing.SubjectId, existing.ChainId);
        existing.Rotate(successor.Id, nowUtc);

        return (successor, plain, existing.SubjectId);
    }

    /// <summary>Revokes every live token descended from one sign-in.</summary>
    public async Task RevokeChainAsync(Guid chainId, string reason, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        var chain = await db.RefreshTokens
            .Where(t => t.ChainId == chainId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in chain)
        {
            token.Revoke(nowUtc, reason);
        }
    }

    /// <summary>
    /// Revokes everything an account holds, however many devices it is signed in on. Used when a
    /// password changes: the reason to change one is usually that somebody else knows it.
    /// </summary>
    public async Task RevokeAllForSubjectAsync(
        RefreshTokenSubject subjectType,
        Guid subjectId,
        string reason,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        var tokens = await db.RefreshTokens
            .Where(t => t.SubjectType == subjectType && t.SubjectId == subjectId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.Revoke(nowUtc, reason);
        }
    }

    /// <summary>Finds the chain a presented token belongs to, or null when it is unknown.</summary>
    public Task<RefreshToken?> FindAsync(string presentedToken, CancellationToken cancellationToken)
    {
        var hash = Secrets.Hash(presentedToken ?? string.Empty);

        return db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
    }
}
