using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Platform;
using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// The signed-in platform admin's own name and password.
/// </summary>
/// <remarks>
/// <para>
/// <b>The address is configuration's.</b> <c>PlatformAdmin:Email</c> is how <see cref="PlatformAdminSeeder"/>
/// finds the account on every start, so changing it here would make the next start create a second
/// admin. There is no method that changes it.
/// </para>
/// <para>
/// <b>The name and the password are the admin's.</b> The seed creates the row once, from
/// <c>PlatformAdmin:FullName</c> and <c>PlatformAdmin:Password</c>, and never touches an existing row:
/// what is changed here survives every restart. Configuration names the first password, not the
/// current one.
/// </para>
/// <para>
/// The caller is checked from the token and from the stored row - an active platform admin - and the
/// row changed is always the caller's own. Both changes are audited; neither entry carries a password
/// or a hash.
/// </para>
/// </remarks>
internal sealed class PlatformAdminAccountService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    SecretHasher hasher,
    RefreshTokenStore refreshTokens,
    TokenIssuer tokens,
    ITokenAuthorityCheck authority,
    IOptions<AuthOptions> authOptions,
    ILogger<PlatformAdminAccountService> logger) : IPlatformAdminAccountService
{
    /// <summary>The audit action for the admin renaming themself.</summary>
    public const string RenameAuditAction = "admin.rename";

    /// <summary>The audit action for the admin changing their password.</summary>
    public const string PasswordChangeAuditAction = "admin.password-change";

    /// <summary>
    /// The audit action for a wrong current password - and the row the lockout counts. See
    /// <see cref="LockedForAsync"/>.
    /// </summary>
    public const string PasswordChangeFailedAuditAction = "admin.password-change-failed";

    private const string StaffTargetType = "StaffMember";

    private readonly AuthOptions _options = authOptions.Value;

    public async Task<PlatformAdminProfile> GetAsync(CancellationToken cancellationToken = default) =>
        Profile(await RequireAdminAsync("Read your account", tracked: false, cancellationToken));

    public async Task<PlatformAdminProfile> UpdateAsync(
        UpdatePlatformAdminProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var admin = await RequireAdminAsync("Rename your account", tracked: true, cancellationToken);
        var name = CheckName(command.FullName);

        if (string.Equals(name, admin.FullName, StringComparison.Ordinal))
        {
            return Profile(admin);
        }

        var before = admin.FullName;
        admin.Rename(name);

        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            RenameAuditAction,
            new AuditTarget(StaffTargetType, admin.Id, admin.FullName, null),
            new { before, after = admin.FullName },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return Profile(admin);
    }

    public async Task<RefreshResult> ChangePasswordAsync(
        ChangePlatformAdminPasswordCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var admin = await RequireAdminAsync("Change your password", tracked: true, cancellationToken);
        var nowUtc = clock.UtcNow;

        // Refused before the hash is checked at all: a locked account answers the same whether the
        // password typed is right or wrong, or the lockout would be an oracle.
        if (await LockedForAsync(admin.Id, nowUtc, cancellationToken) is { } retryAfter)
        {
            logger.LogWarning("Platform admin {StaffMemberId} is locked out of changing their password.", admin.Id);

            throw new TooManyAttemptsException(
                "Too many wrong passwords. Wait a few minutes and try again.", retryAfter);
        }

        var violations = new List<FieldViolation>();
        var wrongPassword = false;

        if (string.IsNullOrEmpty(command.CurrentPassword))
        {
            violations.Add(new FieldViolation("currentPassword", "Type the password you sign in with now.", FieldBounds.Required));
        }
        else if (!hasher.Verify(admin.PasswordHash!, command.CurrentPassword).Matches)
        {
            // A form error, not a 401: the admin is signed in, and the console shows it under the field
            // rather than treating it as a session that ended.
            logger.LogWarning("Platform admin {StaffMemberId} gave a wrong current password.", admin.Id);

            wrongPassword = true;
            violations.Add(new FieldViolation("currentPassword", "That is not your current password.", FieldBounds.Conflict));
        }

        if (string.IsNullOrEmpty(command.NewPassword))
        {
            violations.Add(new FieldViolation("newPassword", "Choose a new password.", FieldBounds.Required));
        }
        else if (command.NewPassword.Length < _options.MinimumPasswordLength)
        {
            // Length is the only rule, the same one the reset link applies.
            violations.Add(new FieldViolation(
                "newPassword",
                $"Password must be at least {_options.MinimumPasswordLength} characters.",
                FieldBounds.Min,
                Min: _options.MinimumPasswordLength,
                Value: command.NewPassword.Length));
        }

        if (wrongPassword)
        {
            // The failure is the record, and the record is the counter: LockedForAsync counts these
            // rows. Saved before the refusal, on its own - nothing else has changed.
            await PlatformAudit.RecordAsync(
                db,
                actor,
                clock,
                PasswordChangeFailedAuditAction,
                new AuditTarget(StaffTargetType, admin.Id, admin.FullName, null),
                new { reason = "current-password-wrong" },
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        admin.SetPasswordHash(hasher.Hash(command.NewPassword!));

        // Every sign-in ends, now: the generation ends every access token the account holds - this
        // request's too - and every refresh token is revoked. The usual reason to change a password is
        // that somebody else might have it. A reset link still outstanding would let them set another.
        admin.EndSessions();
        await refreshTokens.RevokeAllForSubjectAsync(
            RefreshTokenSubject.VenueUser, admin.Id, "password-changed", cancellationToken);

        var outstanding = await db.PasswordResetTokens
            .Where(t => t.StaffMemberId == admin.Id && t.ConsumedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var link in outstanding)
        {
            link.Supersede(nowUtc);
        }

        // The sign-in that made the change carries on, on a fresh pair minted under the new generation.
        var (refresh, refreshToken) = refreshTokens.Issue(RefreshTokenSubject.VenueUser, admin.Id);
        var (accessToken, _) = tokens.IssueVenueUserToken(
            admin.Id, admin.VenueId, admin.BranchId, admin.Role, admin.SessionGeneration, refresh.ChainId);

        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            PasswordChangeAuditAction,
            new AuditTarget(StaffTargetType, admin.Id, admin.FullName, null),
            new { otherSignInsEnded = true, resetLinksEnded = outstanding.Count },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        // After the commit, so no other request re-reads the old generation into the cache.
        authority.InvalidateVenueUser(admin.Id);

        logger.LogInformation("Platform admin {StaffMemberId} changed their password.", admin.Id);

        return new RefreshResult(accessToken, refreshToken, tokens.AccessTokenSeconds);
    }

    /// <summary>
    /// How long until this admin may try again, or null when they may now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counter is the audit log itself: the <c>admin.password-change-failed</c> rows inside the
    /// window (<c>Auth:PasswordAttemptWindowMinutes</c>, 15) and after the last successful change. At
    /// <c>Auth:PasswordAttemptsPerIdentifier</c> (10) of them the account waits until the oldest that
    /// still counts leaves the window. Stored, so it holds across restarts and instances, and read on
    /// the application clock.
    /// </para>
    /// </remarks>
    private async Task<TimeSpan?> LockedForAsync(Guid adminId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, _options.PasswordAttemptsPerIdentifier);
        var window = TimeSpan.FromMinutes(Math.Max(1, _options.PasswordAttemptWindowMinutes));
        var since = nowUtc - window;

        var lastChange = await db.PlatformAuditLogs
            .AsNoTracking()
            .Where(l => l.ActorStaffMemberId == adminId && l.Action == PasswordChangeAuditAction)
            .MaxAsync(l => (DateTime?)l.AtUtc, cancellationToken);

        if (lastChange is { } changed && changed > since)
        {
            since = changed;
        }

        var failures = await db.PlatformAuditLogs
            .AsNoTracking()
            .Where(l => l.ActorStaffMemberId == adminId && l.Action == PasswordChangeFailedAuditAction && l.AtUtc > since)
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

    /// <summary>The caller's own row, when the token and the row both say active platform admin.</summary>
    private async Task<StaffMember> RequireAdminAsync(string operation, bool tracked, CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff || actor.Role != StaffRole.PlatformAdmin || actor.StaffMemberId is not { } id)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.PlatformAdmin);
        }

        var rows = tracked ? db.StaffMembers : db.StaffMembers.AsNoTracking();

        return await rows.FirstOrDefaultAsync(
                   s => s.Id == id && s.IsActive && s.Role == StaffRole.PlatformAdmin && s.Email != null && s.PasswordHash != null,
                   cancellationToken)
               ?? throw new StaffPermissionException(operation, actor.Role, StaffRole.PlatformAdmin);
    }

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

    private static PlatformAdminProfile Profile(StaffMember admin) => new(admin.Id, admin.FullName, admin.Email!);
}
