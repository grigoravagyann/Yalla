using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// The audit slugs one kind of console account writes about its own changes, and the venue the rows
/// belong to.
/// </summary>
/// <param name="Rename">Written when the name changes.</param>
/// <param name="PasswordChange">Written when the password changes - and the point the lockout counts from.</param>
/// <param name="PasswordChangeFailed">Written for a wrong current password; these rows are the lockout's counter.</param>
/// <param name="VenueId">The venue the account works for; null for the platform admin.</param>
internal sealed record OwnAccountAudit(string Rename, string PasswordChange, string PasswordChangeFailed, Guid? VenueId);

/// <summary>
/// A console user changing their own name or password: the platform admin from Settings, a venue's owner,
/// general manager or branch manager from Իմ հաշիվը. One set of rules for both.
/// </summary>
/// <remarks>
/// <para>
/// The caller hands over the account row, already checked to be the actor's own and tracked. What
/// happens here is the same whoever it is: the name is trimmed and bounded, the current password is
/// proved, the new one is at least <c>Auth:MinimumPasswordLength</c> long, and a change ends every
/// session the account has and answers a fresh pair for the sign-in that made it.
/// </para>
/// <para>
/// Both changes are audited; neither entry carries a password or a hash.
/// </para>
/// </remarks>
internal sealed class OwnAccountChanges(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    SecretHasher hasher,
    RefreshTokenStore refreshTokens,
    TokenIssuer tokens,
    ITokenAuthorityCheck authority,
    IOptions<AuthOptions> authOptions,
    ILogger<OwnAccountChanges> logger)
{
    private const string StaffTargetType = "StaffMember";

    private readonly AuthOptions _options = authOptions.Value;

    /// <summary>Renames the account; a name that does not change writes nothing.</summary>
    /// <exception cref="FieldValidationException">The name is missing or too long.</exception>
    public async Task RenameAsync(
        StaffMember account,
        string? fullName,
        OwnAccountAudit audit,
        CancellationToken cancellationToken)
    {
        var name = CheckName(fullName);

        if (string.Equals(name, account.FullName, StringComparison.Ordinal))
        {
            return;
        }

        var before = account.FullName;
        account.Rename(name);

        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            audit.Rename,
            Target(account, audit),
            new { before, after = account.FullName },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the password after proving the current one, ends every session the account has and answers
    /// a fresh pair for the caller.
    /// </summary>
    /// <exception cref="TooManyAttemptsException">Too many wrong current passwords inside the window.</exception>
    /// <exception cref="FieldValidationException">Every problem with the two passwords, at once.</exception>
    public async Task<RefreshResult> ChangePasswordAsync(
        StaffMember account,
        string? currentPassword,
        string? newPassword,
        OwnAccountAudit audit,
        CancellationToken cancellationToken)
    {
        // One account, one change at a time: the account's lock, held to the commit, is taken before
        // the wrong guesses are counted - so eight sent at once cannot all find nine and all be heard -
        // and it is the lock every refresh of the account's tokens takes, so none can mint a successor
        // behind the revocation below. The row is read again under it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await refreshTokens.LockSubjectAsync(RefreshTokenSubject.VenueUser, account.Id, cancellationToken);
        await db.Entry(account).ReloadAsync(cancellationToken);

        var nowUtc = clock.UtcNow;

        // Refused before the hash is checked at all: a locked account answers the same whether the
        // password typed is right or wrong, or the lockout would be an oracle.
        if (await LockedForAsync(account.Id, audit, nowUtc, cancellationToken) is { } retryAfter)
        {
            logger.LogWarning("Staff member {StaffMemberId} is locked out of changing their password.", account.Id);

            throw new TooManyAttemptsException(
                "Too many wrong passwords. Wait a few minutes and try again.", retryAfter);
        }

        var violations = new List<FieldViolation>();
        var wrongPassword = false;

        if (string.IsNullOrEmpty(currentPassword))
        {
            violations.Add(new FieldViolation("currentPassword", "Type the password you sign in with now.", FieldBounds.Required));
        }
        else if (!hasher.Verify(account.PasswordHash!, currentPassword).Matches)
        {
            // A form error, not a 401: they are signed in, and the console shows it under the field
            // rather than treating it as a session that ended.
            logger.LogWarning("Staff member {StaffMemberId} gave a wrong current password.", account.Id);

            wrongPassword = true;
            violations.Add(new FieldViolation("currentPassword", "That is not your current password.", FieldBounds.Conflict));
        }

        if (string.IsNullOrEmpty(newPassword))
        {
            violations.Add(new FieldViolation("newPassword", "Choose a new password.", FieldBounds.Required));
        }
        else if (newPassword.Length < _options.MinimumPasswordLength)
        {
            // Length is the only rule, the same one the reset link applies.
            violations.Add(new FieldViolation(
                "newPassword",
                $"Password must be at least {_options.MinimumPasswordLength} characters.",
                FieldBounds.Min,
                Min: _options.MinimumPasswordLength,
                Value: newPassword.Length));
        }

        if (wrongPassword)
        {
            // The failure is the record, and the record is the counter: LockedForAsync counts these
            // rows. Saved before the refusal, on its own - nothing else has changed.
            await PlatformAudit.RecordAsync(
                db,
                actor,
                clock,
                audit.PasswordChangeFailed,
                Target(account, audit),
                new { reason = "current-password-wrong" },
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        account.SetPasswordHash(hasher.Hash(newPassword!));

        // Every sign-in ends, now: the generation ends every access token the account holds - this
        // request's too - and every refresh token is revoked. The usual reason to change a password is
        // that somebody else might have it. A reset link still outstanding would let them set another.
        account.EndSessions();
        await refreshTokens.RevokeAllForSubjectAsync(
            RefreshTokenSubject.VenueUser, account.Id, "password-changed", cancellationToken);

        var outstanding = await db.PasswordResetTokens
            .Where(t => t.StaffMemberId == account.Id && t.ConsumedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var link in outstanding)
        {
            link.Supersede(nowUtc);
        }

        // The sign-in that made the change carries on, on a fresh pair minted under the new generation.
        var (refresh, refreshToken) = refreshTokens.Issue(RefreshTokenSubject.VenueUser, account.Id);
        var (accessToken, _) = tokens.IssueVenueUserToken(
            account.Id, account.VenueId, account.BranchId, account.Role, account.SessionGeneration, refresh.ChainId);

        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            audit.PasswordChange,
            Target(account, audit),
            new { otherSignInsEnded = true, resetLinksEnded = outstanding.Count },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // After the commit, so no other request re-reads the old generation into the cache.
        authority.InvalidateVenueUser(account.Id);

        logger.LogInformation("Staff member {StaffMemberId} changed their password.", account.Id);

        return new RefreshResult(accessToken, refreshToken, tokens.AccessTokenSeconds);
    }

    /// <summary>
    /// How long until this account may try again, or null when it may now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counter is the audit log itself: the <see cref="OwnAccountAudit.PasswordChangeFailed"/> rows
    /// inside the window (<c>Auth:PasswordAttemptWindowMinutes</c>, 15) and after the last successful
    /// change. At <c>Auth:PasswordAttemptsPerIdentifier</c> (10) of them the account waits until the oldest
    /// that still counts leaves the window. Stored, so it holds across restarts and instances, and read on
    /// the application clock.
    /// </para>
    /// </remarks>
    private async Task<TimeSpan?> LockedForAsync(Guid accountId, OwnAccountAudit audit, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, _options.PasswordAttemptsPerIdentifier);
        var window = TimeSpan.FromMinutes(Math.Max(1, _options.PasswordAttemptWindowMinutes));
        var since = nowUtc - window;

        var lastChange = await db.PlatformAuditLogs
            .AsNoTracking()
            .Where(l => l.ActorStaffMemberId == accountId && l.Action == audit.PasswordChange)
            .MaxAsync(l => (DateTime?)l.AtUtc, cancellationToken);

        if (lastChange is { } changed && changed > since)
        {
            since = changed;
        }

        var failures = await db.PlatformAuditLogs
            .AsNoTracking()
            .Where(l => l.ActorStaffMemberId == accountId && l.Action == audit.PasswordChangeFailed && l.AtUtc > since)
            .OrderBy(l => l.AtUtc)
            .Select(l => l.AtUtc)
            .ToListAsync(cancellationToken);

        if (failures.Count < limit)
        {
            return null;
        }

        // Fewer than the limit remain once this one has aged out of the window.
        var freedAt = DateTime.SpecifyKind(failures[failures.Count - limit], DateTimeKind.Utc) + window;

        return freedAt > nowUtc ? freedAt - nowUtc : null;
    }

    private static AuditTarget Target(StaffMember account, OwnAccountAudit audit) =>
        new(StaffTargetType, account.Id, account.FullName, audit.VenueId);

    private static string CheckName(string? fullName)
    {
        var trimmed = fullName?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new FieldValidationException(new FieldViolation("fullName", "Type your name.", FieldBounds.Required));
        }

        return trimmed.Length > FieldLengths.PersonName
            ? throw new FieldValidationException(new FieldViolation(
                "fullName",
                $"A name is at most {FieldLengths.PersonName} characters.",
                FieldBounds.Max,
                Max: FieldLengths.PersonName,
                Value: trimmed.Length))
            : trimmed;
    }
}
