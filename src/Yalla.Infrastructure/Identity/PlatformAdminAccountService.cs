using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Platform;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

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
/// row changed is always the caller's own. The rules for the change itself - the name's bounds, proving
/// the current password, the lockout, ending every session - are <see cref="OwnAccountChanges"/>', the
/// same ones the venue console's own account follows. Both changes are audited; neither entry carries a
/// password or a hash.
/// </para>
/// </remarks>
internal sealed class PlatformAdminAccountService(
    YallaDbContext db,
    ICurrentActor actor,
    OwnAccountChanges changes) : IPlatformAdminAccountService
{
    /// <summary>The audit action for the admin renaming themself.</summary>
    public const string RenameAuditAction = "admin.rename";

    /// <summary>The audit action for the admin changing their password.</summary>
    public const string PasswordChangeAuditAction = "admin.password-change";

    /// <summary>
    /// The audit action for a wrong current password - and the row the lockout counts. See
    /// <see cref="OwnAccountChanges"/>.
    /// </summary>
    public const string PasswordChangeFailedAuditAction = "admin.password-change-failed";

    /// <summary>The admin's own rows belong to no venue.</summary>
    private static readonly OwnAccountAudit Audit = new(
        RenameAuditAction, PasswordChangeAuditAction, PasswordChangeFailedAuditAction, VenueId: null);

    public async Task<PlatformAdminProfile> GetAsync(CancellationToken cancellationToken = default) =>
        Profile(await RequireAdminAsync("Read your account", tracked: false, cancellationToken));

    public async Task<PlatformAdminProfile> UpdateAsync(
        UpdatePlatformAdminProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var admin = await RequireAdminAsync("Rename your account", tracked: true, cancellationToken);
        await changes.RenameAsync(admin, command.FullName, Audit, cancellationToken);

        return Profile(admin);
    }

    public async Task<RefreshResult> ChangePasswordAsync(
        ChangePlatformAdminPasswordCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var admin = await RequireAdminAsync("Change your password", tracked: true, cancellationToken);

        return await changes.ChangePasswordAsync(admin, command.CurrentPassword, command.NewPassword, Audit, cancellationToken);
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

    private static PlatformAdminProfile Profile(StaffMember admin) => new(admin.Id, admin.FullName, admin.Email!);
}
