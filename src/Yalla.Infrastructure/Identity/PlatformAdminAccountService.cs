using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Platform;
using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
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
    IOptions<AuthOptions> authOptions,
    ILogger<PlatformAdminAccountService> logger) : IPlatformAdminAccountService
{
    /// <summary>The audit action for the admin renaming themself.</summary>
    public const string RenameAuditAction = "admin.rename";

    /// <summary>The audit action for the admin changing their password.</summary>
    public const string PasswordChangeAuditAction = "admin.password-change";

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

    public async Task ChangePasswordAsync(
        ChangePlatformAdminPasswordCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var admin = await RequireAdminAsync("Change your password", tracked: true, cancellationToken);
        var violations = new List<FieldViolation>();

        if (string.IsNullOrEmpty(command.CurrentPassword))
        {
            violations.Add(new FieldViolation("currentPassword", "Type the password you sign in with now.", FieldBounds.Required));
        }
        else if (!hasher.Verify(admin.PasswordHash!, command.CurrentPassword).Matches)
        {
            // A form error, not a 401: the admin is signed in, and the console shows it under the field
            // rather than treating it as a session that ended.
            logger.LogWarning("Platform admin {StaffMemberId} gave a wrong current password.", admin.Id);

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

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        admin.SetPasswordHash(hasher.Hash(command.NewPassword!));

        // Every other sign-in is signed out - the usual reason to change a password is that somebody
        // else might have it. The one this request came from keeps its chain and carries on; a token
        // from before the chain claim existed names none, and then every sign-in ends.
        await refreshTokens.RevokeAllForSubjectExceptChainAsync(
            RefreshTokenSubject.VenueUser, admin.Id, command.KeepRefreshChainId, "password-changed", cancellationToken);

        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            PasswordChangeAuditAction,
            new AuditTarget(StaffTargetType, admin.Id, admin.FullName, null),
            new { otherSignInsEnded = true, thisSignInKept = command.KeepRefreshChainId is not null },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Platform admin {StaffMemberId} changed their password.", admin.Id);
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
