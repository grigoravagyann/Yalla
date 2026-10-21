using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A waiter or above, at this branch, from the stored staff row - the floor's guard: deciding bookings, the awaiting
/// list, releasing a late booking, and the pre-order rail and its actions.
/// </summary>
/// <remarks>
/// <para>
/// The stored role, not only the token's: a waiter moved to the kitchen keeps a waiter's token until it expires, and
/// must not keep a waiter's reach with it - the same rule the manager guard applies to a demoted manager.
/// </para>
/// <para>
/// A waiter and a branch manager at their own branch; an owner or general manager at any branch of their venue (those
/// accounts are venue-scoped, which is their point); a platform admin anywhere. Shared rather than copied: the rule has
/// been the same for every floor action, and two copies are how it stops being.
/// </para>
/// </remarks>
internal static class FloorStaffGuard
{
    /// <summary>The roles that work the floor - and decide what it can take.</summary>
    public static bool IsFloorRole(StaffRole role) =>
        role is StaffRole.Waiter or StaffRole.Manager or StaffRole.GeneralManager or StaffRole.Owner
            or StaffRole.PlatformAdmin;

    /// <summary>The caller's staff id when they may act on this branch's floor; otherwise throws.</summary>
    /// <exception cref="StaffPermissionException">Not floor staff of this branch.</exception>
    public static async Task<Guid> RequireAtBranchAsync(
        YallaDbContext db,
        ICurrentActor actor,
        IAuthorizationQueries authorization,
        Guid branchId,
        string operation,
        CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff
            || actor.StaffMemberId is not { } staffId
            || actor.Role is not { } tokenRole
            || !IsFloorRole(tokenRole))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }

        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.BranchId, s.VenueId, s.IsActive, s.Role })
            .FirstOrDefaultAsync(cancellationToken);

        if (staff is not { IsActive: true })
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }

        if (!IsFloorRole(staff.Role))
        {
            throw new StaffPermissionException(operation, staff.Role, StaffRole.Waiter);
        }

        if (staff.Role == StaffRole.PlatformAdmin || staff.BranchId == branchId)
        {
            return staffId;
        }

        if (StaffRoleRules.CoversWholeVenue(staff.Role)
            && staff.VenueId is { } venueId
            && await authorization.BranchBelongsToVenueAsync(branchId, venueId, cancellationToken))
        {
            return staffId;
        }

        throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
    }
}
