using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
/// <para>
/// The caller commits, so a rotation and the sign-in it belongs to land in the same transaction
/// and a token can never exist for a sign-in that did not happen. The single exception is theft
/// detection, which commits its own revocation - see <see cref="RotateAsync"/>.
/// </para>
/// <para>
/// <b>Rotating and revoking are serialised per account.</b> Both read a token and then write it, and
/// nothing on the row says it changed in between: a rotation that read a token before a revocation
/// committed went on to mint a successor the revocation never saw, and a sign-in meant to end - by a
/// password change, signing out elsewhere, a deletion - carried on for thirty days. So each takes the
/// account's lock first (<see cref="LockSubjectAsync"/>: an update lock on the staff or diner row, held
/// to the commit) and reads the tokens after it. Whichever comes second waits for the other's commit: a
/// rotation that waited finds its token revoked and mints nothing; a revocation that waited finds the
/// successor and ends it too. Every method that reads or writes tokens therefore needs an open
/// transaction, and refuses without one.
/// </para>
/// </remarks>
internal sealed class RefreshTokenStore(YallaDbContext db, IClock clock)
{
    /// <summary>Starts a new chain: one sign-in, one family of tokens.</summary>
    /// <remarks>A new chain needs no lock: nothing can be revoking a token that does not exist yet.</remarks>
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
    /// Spends a presented token and issues its successor, under the account's lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interesting branch is the third one. A token that has already been rotated is being
    /// presented by someone - and there is no way to know whether that is the real client
    /// replaying a request or a thief using a copy. Revoking only that token would leave whichever
    /// party refreshed most recently in possession of a live session, and that is as likely to be
    /// the thief. Revoking the chain ends the argument: both sign in again.
    /// </para>
    /// <para>
    /// The token is found by its hash first - that names the account - and read again once the
    /// account's lock is held, so a revocation that committed while this waited is what it sees.
    /// </para>
    /// </remarks>
    /// <param name="presentedToken">The refresh token the client sent.</param>
    /// <param name="expectedSubjectType">Whose door it came in at: a diner's token is refused at the venue's.</param>
    /// <param name="transaction">
    /// The caller's transaction, open on this context and begun for this rotation: it holds the account's
    /// lock, the caller commits it after saving the successor - and the reuse alarm commits it itself,
    /// because the caller's next act is to throw. Passed in so that ownership is stated, not assumed: a
    /// transaction opened further out for some larger unit of work must not be committed from in here.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="AuthenticationFailedException">The token is unknown, expired, revoked or reused.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="transaction"/> is not the context's open transaction.</exception>
    public async Task<(RefreshToken Entity, string PlainToken, Guid SubjectId)> RotateAsync(
        string presentedToken,
        RefreshTokenSubject expectedSubjectType,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!ReferenceEquals(db.Database.CurrentTransaction, transaction))
        {
            throw new InvalidOperationException(
                "A rotation commits its own transaction when the reuse alarm fires, so it is given the one open on this context.");
        }

        var hash = Secrets.Hash(presentedToken ?? string.Empty);

        var existing = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existing is null || existing.SubjectType != expectedSubjectType)
        {
            throw new AuthenticationFailedException(
                "refresh-token-invalid", "That refresh token is not valid. Sign in again.");
        }

        await LockSubjectAsync(existing.SubjectType, existing.SubjectId, cancellationToken);
        await db.Entry(existing).ReloadAsync(cancellationToken);

        var nowUtc = clock.UtcNow;

        if (existing.WasAlreadyRotated)
        {
            await RevokeChainAsync(existing, "rotated-token-reused", cancellationToken);

            // Committed here rather than left to the caller: the caller's next act is to throw,
            // so an uncommitted revocation would be silently discarded and the chain would stay
            // live. This is the one path in this class that saves, and it is the one that must.
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

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

    /// <summary>Revokes every token descended from one sign-in - the one <paramref name="member"/> belongs to - under the account's lock.</summary>
    /// <remarks>
    /// The whole chain, spent tokens included, rather than only its live one: this is the reuse alarm and
    /// the sign-out, both rare, and a chain marked end to end is the record of why it ended.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No transaction is open.</exception>
    public async Task RevokeChainAsync(RefreshToken member, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        await LockSubjectAsync(member.SubjectType, member.SubjectId, cancellationToken);

        var chain = await db.RefreshTokens
            .Where(t => t.ChainId == member.ChainId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        Revoke(chain, reason);
    }

    /// <summary>
    /// Revokes everything an account holds, however many devices it is signed in on. Used when a
    /// password changes: the reason to change one is usually that somebody else knows it.
    /// </summary>
    /// <returns>How many sign-ins it ended.</returns>
    /// <exception cref="InvalidOperationException">No transaction is open.</exception>
    public Task<int> RevokeAllForSubjectAsync(
        RefreshTokenSubject subjectType,
        Guid subjectId,
        string reason,
        CancellationToken cancellationToken) =>
        RevokeForSubjectAsync(subjectType, subjectId, keepChainId: null, reason, cancellationToken);

    /// <summary>
    /// Revokes everything an account holds except one sign-in's chain - the one the request was made
    /// from. Used when a diner sets or changes a password, and when a venue user signs out everywhere
    /// else: every other device is signed out, and the app that made the change refreshes and carries
    /// on. A null <paramref name="keepChainId"/> keeps nothing.
    /// </summary>
    /// <returns>How many sign-ins it ended.</returns>
    /// <exception cref="InvalidOperationException">No transaction is open.</exception>
    public Task<int> RevokeAllForSubjectExceptChainAsync(
        RefreshTokenSubject subjectType,
        Guid subjectId,
        Guid? keepChainId,
        string reason,
        CancellationToken cancellationToken) =>
        RevokeForSubjectAsync(subjectType, subjectId, keepChainId, reason, cancellationToken);

    /// <summary>Finds the chain a presented token belongs to, or null when it is unknown.</summary>
    public Task<RefreshToken?> FindAsync(string presentedToken, CancellationToken cancellationToken)
    {
        var hash = Secrets.Hash(presentedToken ?? string.Empty);

        return db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
    }

    /// <summary>
    /// Takes the account's lock - an update lock on its <c>StaffMembers</c> or <c>DinerUsers</c> row - held
    /// to the end of the open transaction.
    /// </summary>
    /// <remarks>
    /// The lock every rotation and revocation of the account's refresh tokens takes, and the one a
    /// service takes first when what it decides depends on the account not changing under it - the
    /// password change counting wrong guesses. Taking it twice in one transaction is free.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No transaction is open, so there is nothing to hold the lock.</exception>
    public async Task LockSubjectAsync(RefreshTokenSubject subjectType, Guid subjectId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Refresh tokens are rotated and revoked under the account's lock, which a transaction holds. Begin one first.");
        }

        var query = subjectType switch
        {
            RefreshTokenSubject.VenueUser => db.Database.SqlQuery<Guid>(
                $"SELECT Id AS [Value] FROM StaffMembers WITH (UPDLOCK, HOLDLOCK) WHERE Id = {subjectId}"),
            RefreshTokenSubject.Diner => db.Database.SqlQuery<Guid>(
                $"SELECT Id AS [Value] FROM DinerUsers WITH (UPDLOCK, HOLDLOCK) WHERE Id = {subjectId}"),
            _ => throw new ArgumentOutOfRangeException(nameof(subjectType), subjectType, "Not a refresh-token subject."),
        };

        await query.ToListAsync(cancellationToken);
    }

    private async Task<int> RevokeForSubjectAsync(
        RefreshTokenSubject subjectType,
        Guid subjectId,
        Guid? keepChainId,
        string reason,
        CancellationToken cancellationToken)
    {
        await LockSubjectAsync(subjectType, subjectId, cancellationToken);

        var tokens = await Live(subjectType, subjectId)
            .Where(t => keepChainId == null || t.ChainId != keepChainId)
            .ToListAsync(cancellationToken);

        return Revoke(tokens, reason);
    }

    /// <summary>
    /// The account's tokens that could still be exchanged - one per sign-in that can carry on. Only these
    /// need revoking: a rotated token is refused already (and presenting one ends its chain), an expired
    /// one is refused by its date. Rotated rows are never pruned, so this is also what keeps a revocation
    /// from loading every token the account ever held.
    /// </summary>
    private IQueryable<RefreshToken> Live(RefreshTokenSubject subjectType, Guid subjectId)
    {
        var nowUtc = clock.UtcNow;

        return db.RefreshTokens.Where(t =>
            t.SubjectType == subjectType
            && t.SubjectId == subjectId
            && t.RevokedAtUtc == null
            && t.RotatedAtUtc == null
            && t.ExpiresAtUtc > nowUtc);
    }

    private int Revoke(List<RefreshToken> tokens, string reason)
    {
        var nowUtc = clock.UtcNow;

        foreach (var token in tokens)
        {
            token.Revoke(nowUtc, reason);
        }

        return tokens.Select(t => t.ChainId).Distinct().Count();
    }
}
