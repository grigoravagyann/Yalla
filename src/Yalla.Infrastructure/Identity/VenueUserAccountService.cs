using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// The venue console's own account: an owner, general manager or branch manager renaming themself,
/// changing their password, and seeing and ending the places they are signed in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only their own row.</b> The caller is checked from the token and from the stored row - active, one
/// of the three venue ranks, with a sign-in - and nothing here takes an id. The platform admin is refused:
/// their account is <see cref="PlatformAdminAccountService"/>'s, with its own audit slugs.
/// </para>
/// <para>
/// <b>The address is not theirs to change.</b> Somebody who outranks them gives them a new sign-in link for
/// a new address; an owner's is the platform's to change.
/// </para>
/// <para>
/// <b>A sign-in is a refresh chain.</b> That is the one per-session record there is: it knows when the
/// password was typed and when the chain last renewed its access token, and nothing about the browser or
/// where it was. Ending the other sign-ins revokes their chains and moves the session generation, so their
/// access tokens stop now rather than within fifteen minutes; the caller's chain is kept, and the caller
/// refreshes. No fresh pair is minted here, on purpose: an access token alone must never be able to turn
/// itself into a thirty-day refresh token. A password change, which proves the password, does answer one.
/// </para>
/// </remarks>
internal sealed class VenueUserAccountService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    OwnAccountChanges changes,
    RefreshTokenStore refreshTokens,
    ITokenAuthorityCheck authority,
    ILogger<VenueUserAccountService> logger) : IVenueUserAccountService
{
    /// <summary>The audit action for a venue user renaming themself.</summary>
    public const string RenameAuditAction = "account.rename";

    /// <summary>The audit action for a venue user changing their password.</summary>
    public const string PasswordChangeAuditAction = "account.password-change";

    /// <summary>
    /// The audit action for a wrong current password - and the row the lockout counts. Kept out of the
    /// activity feed, as the admin's is.
    /// </summary>
    public const string PasswordChangeFailedAuditAction = "account.password-change-failed";

    /// <summary>The audit action for ending every other sign-in.</summary>
    public const string SignOutOthersAuditAction = "account.sign-out-others";

    private const string StaffTargetType = "StaffMember";

    public async Task<VenueUserProfile> GetAsync(CancellationToken cancellationToken = default) =>
        Profile(await RequireSelfAsync("Read your account", tracked: false, cancellationToken));

    public async Task<VenueUserProfile> UpdateAsync(
        UpdateVenueUserProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var me = await RequireSelfAsync("Rename your account", tracked: true, cancellationToken);
        await changes.RenameAsync(me, command.FullName, Audit(me), cancellationToken);

        return Profile(me);
    }

    public async Task<RefreshResult> ChangePasswordAsync(
        ChangeVenueUserPasswordCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var me = await RequireSelfAsync("Change your password", tracked: true, cancellationToken);

        return await changes.ChangePasswordAsync(me, command.CurrentPassword, command.NewPassword, Audit(me), cancellationToken);
    }

    public async Task<VenueUserSessions> ListSessionsAsync(Guid? currentChainId, CancellationToken cancellationToken = default)
    {
        var me = await RequireSelfAsync("List your sign-ins", tracked: false, cancellationToken);
        var nowUtc = clock.UtcNow;

        // A chain's live token is its newest - rotating spends the old one in the same save - so its
        // creation is when the chain last renewed, and the chain's oldest row is the sign-in itself.
        var live = await db.RefreshTokens
            .AsNoTracking()
            .Where(t => t.SubjectType == RefreshTokenSubject.VenueUser
                        && t.SubjectId == me.Id
                        && t.RevokedAtUtc == null
                        && t.RotatedAtUtc == null
                        && t.ExpiresAtUtc > nowUtc)
            .Select(t => new
            {
                t.ChainId,
                LastActiveAtUtc = t.CreatedAtUtc,
                SignedInAtUtc = db.RefreshTokens.Where(o => o.ChainId == t.ChainId).Min(o => o.CreatedAtUtc),
            })
            .ToListAsync(cancellationToken);

        return new VenueUserSessions(
        [
            .. live
                .GroupBy(t => t.ChainId)
                .Select(chain => new VenueUserSession(
                    chain.Key,
                    Utc(chain.Min(t => t.SignedInAtUtc)),
                    Utc(chain.Max(t => t.LastActiveAtUtc)),
                    chain.Key == currentChainId))
                .OrderByDescending(s => s.IsCurrent)
                .ThenByDescending(s => s.LastActiveAtUtc)
                .ThenBy(s => s.Id),
        ]);
    }

    public async Task<SignedOutElsewhere> SignOutOthersAsync(Guid? currentChainId, CancellationToken cancellationToken = default)
    {
        // A token that names no sign-in cannot say which one to keep, and keeping none would sign the
        // caller out with the rest. Refused before anything is read or written.
        if (currentChainId is not { } keep)
        {
            throw new SignInUnknownException();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var me = await RequireSelfAsync("Sign out your other sign-ins", tracked: true, cancellationToken);

        // Under the account's lock (the store takes it), so a refresh in flight on another device either
        // finished first - and its successor is ended here - or waits and finds its token revoked.
        var ended = await refreshTokens.RevokeAllForSubjectExceptChainAsync(
            RefreshTokenSubject.VenueUser, me.Id, keep, "signed-out-elsewhere", cancellationToken);

        // Every access token ends with the generation - this request's too, which is why the chain it
        // names is kept: the console refreshes on it and carries on under the new generation. Read
        // again under the lock, so the bump is from the generation as it stands.
        await db.Entry(me).ReloadAsync(cancellationToken);
        me.EndSessions();

        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            SignOutOthersAuditAction,
            new AuditTarget(StaffTargetType, me.Id, me.FullName, me.VenueId),
            new { endedSessions = ended },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // After the commit, so no other request re-reads the old generation into the cache.
        authority.InvalidateVenueUser(me.Id);

        logger.LogInformation("Staff member {StaffMemberId} signed {Count} other sign-ins out.", me.Id, ended);

        return new SignedOutElsewhere(ended);
    }

    /// <summary>
    /// The caller's own row, when the token and the row both say an active owner, general manager or
    /// branch manager who can sign in to the panel.
    /// </summary>
    private async Task<StaffMember> RequireSelfAsync(string operation, bool tracked, CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff
            || actor.StaffMemberId is not { } id
            || actor.Role is not { } role
            || !StaffRoleRules.ManagesAtVenue(role))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        var rows = tracked ? db.StaffMembers : db.StaffMembers.AsNoTracking();

        var me = await rows.FirstOrDefaultAsync(
                     s => s.Id == id && s.IsActive && s.VenueId != null && s.Email != null && s.PasswordHash != null,
                     cancellationToken)
                 ?? throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);

        // The row's rank, not the token's: a role changed since the token was minted is the one that holds.
        return StaffRoleRules.ManagesAtVenue(me.Role)
            ? me
            : throw new StaffPermissionException(operation, me.Role, StaffRole.Manager);
    }

    private static OwnAccountAudit Audit(StaffMember me) =>
        new(RenameAuditAction, PasswordChangeAuditAction, PasswordChangeFailedAuditAction, me.VenueId);

    /// <summary>
    /// The profile. A branch only for a branch manager: an owner's row may name one, but an owner runs
    /// every branch, and the console would read a branch here as "confined to it".
    /// </summary>
    private static VenueUserProfile Profile(StaffMember me) =>
        new(me.Id, me.FullName, me.Email!, me.Role, me.VenueId!.Value, me.Role == StaffRole.Manager ? me.BranchId : null);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
