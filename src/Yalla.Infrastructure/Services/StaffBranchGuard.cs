using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Confines a staff member to their own branch. One copy, for every staff write that needs it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a class and not a private helper.</b> It was a private helper - three of them,
/// character for character the same, in <c>TabOrderService</c>, <c>TabPaymentService</c> and
/// <c>ServiceRequestService</c>. That is not a style problem. The check reached those last two
/// only after a security audit found them missing: a waiter at any branch could acknowledge any
/// other branch's service requests, and settle or write off any other branch's bill. The
/// duplication is what let one service be written without the check while its neighbours had it,
/// and nothing failed - the code compiled, the tests passed, and the boundary was open.
/// </para>
/// <para>
/// A single injected guard makes the omission visible instead: a service that needs the check
/// takes this in its constructor, so "does this service confine callers to a branch?" is answered
/// by reading its signature rather than by auditing every method in it.
/// </para>
/// <para>
/// <b>Why the check cannot live in a policy.</b> The route-level <c>BranchScoped</c> policy covers
/// the routes that name a branch, a table or a tab. It cannot cover the ones addressed by a bare
/// entity id - <c>/api/orders/{orderId}/status</c>,
/// <c>/api/service-requests/{id}/acknowledge</c>, <c>/api/tab-adjustments/{adjustmentId}/void</c> -
/// because there is no branch route value to compare a claim against, and the handler would fail
/// closed on every call. Those routes have to be guarded where the entity is loaded, which is
/// here. Where the policy does apply, this runs underneath it as the second line.
/// </para>
/// <para>
/// The widening is for the accounts whose job is every branch: an owner passes for any branch of
/// their own venue, and so does a manager whose stored row names <b>no</b> branch. A manager whose
/// row names a home branch is confined to it (K4) - the same rule <c>BranchScopedHandler</c> applies
/// from the token, applied again here from the stored row, so a reassignment takes effect before
/// the token expires. A waiter is confined to the one branch their tablet is enrolled at, and a
/// platform admin belongs to no venue and passes for all of them.
/// </para>
/// </remarks>
internal sealed class StaffBranchGuard(YallaDbContext db, ICurrentActor actor) : IStaffBranchGuard
{
    /// <inheritdoc />
    public async Task<Guid> RequireAtBranchAsync(
        Guid branchId,
        string operation,
        CancellationToken cancellationToken = default)
    {
        // The token first, failing closed: this is the manager-or-above check, and a caller the
        // route policy should already have refused is refused again rather than trusted.
        if (actor.Type != ActorType.Staff
            || actor.StaffMemberId is not { } staffId
            || actor.Role is not (StaffRole.Manager or StaffRole.Owner or StaffRole.PlatformAdmin))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.BranchId, s.VenueId, s.IsActive, s.Role })
            .FirstOrDefaultAsync(cancellationToken);

        // Deactivated, or a token naming somebody who is no longer a row.
        if (staff is not { IsActive: true })
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        if (staff.Role == StaffRole.PlatformAdmin)
        {
            return staffId;
        }

        // The stored role, not the token's: a manager demoted to waiter keeps a manager token until
        // it expires, and must not keep a manager's reach with it.
        if (staff.Role is not (StaffRole.Owner or StaffRole.Manager))
        {
            throw new StaffPermissionException(operation, staff.Role, StaffRole.Manager);
        }

        var venueOwnsBranch = await db.Branches
            .AsNoTracking()
            .AnyAsync(b => b.Id == branchId && b.VenueId == staff.VenueId, cancellationToken);

        // StaffPermissionException, as the booking decisions have always answered: the subject of this
        // refusal is the caller's reach, not a thing they addressed that lives elsewhere. Both are 403.
        if (!venueOwnsBranch || !Covers(staff.Role, staff.BranchId, branchId))
        {
            throw new StaffPermissionException(operation, staff.Role, StaffRole.Manager);
        }

        return staffId;
    }

    /// <summary>
    /// Whether an owner or manager of the branch's own venue covers it: an owner always, a manager
    /// only with no home branch or at it.
    /// </summary>
    private static bool Covers(StaffRole role, Guid? homeBranchId, Guid branchId) =>
        role == StaffRole.Owner
        || (role == StaffRole.Manager && (homeBranchId is null || homeBranchId == branchId));

    /// <summary>
    /// Refuses the caller unless <paramref name="branchId"/> is a branch they may act on.
    /// </summary>
    /// <param name="staffId">The acting staff member, already established by the caller.</param>
    /// <param name="branchId">The branch the addressed thing belongs to.</param>
    /// <param name="operation">
    /// What is being attempted, for the message when the staff row is missing or deactivated -
    /// "Settling this tab", "Acknowledging this request".
    /// </param>
    /// <param name="subject">
    /// What was addressed, as a bare noun, for the message when the branch does not match -
    /// "tab", "request".
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="StaffPermissionException">
    /// There is no such staff member, or the account is deactivated.
    /// </exception>
    /// <exception cref="StaffBranchScopeException">
    /// The staff member is real and active, and this is not their branch. Answers 403.
    /// </exception>
    public async Task RequireAsync(
        Guid staffId,
        Guid branchId,
        string operation,
        string subject,
        CancellationToken cancellationToken)
    {
        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.BranchId, s.VenueId, s.IsActive, s.Role })
            .FirstOrDefaultAsync(cancellationToken);

        // Deactivated, or a token naming somebody who is no longer a row. Distinct from the branch
        // mismatch below: this one is about the caller, not about what they addressed.
        if (staff is not { IsActive: true })
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }

        // The platform tier belongs to no venue and passes for every branch.
        if (staff.Role == StaffRole.PlatformAdmin)
        {
            return;
        }

        if (staff.BranchId == branchId)
        {
            return;
        }

        var venueOwnsBranch = await db.Branches
            .AsNoTracking()
            .AnyAsync(b => b.Id == branchId && b.VenueId == staff.VenueId, cancellationToken);

        if (venueOwnsBranch && Covers(staff.Role, staff.BranchId, branchId))
        {
            return;
        }

        throw new StaffBranchScopeException(subject);
    }
}

/// <summary>
/// The branch boundary for the owner-and-manager services: settings, listing, photos, bookings.
/// </summary>
/// <remarks>
/// <para>
/// <c>BranchScopedHandler</c> decides the same thing from the token, before the handler runs. This
/// decides it again from the <b>stored</b> staff row, so a manager moved to another branch, demoted
/// or deactivated is refused at once rather than when their token expires.
/// </para>
/// <para>
/// <b>The rule (K4).</b> A platform admin passes for every branch. An owner passes for every branch
/// of their own venue. A manager passes for every branch of their own venue when their row names no
/// branch, and only for that branch when it names one. Everyone else is refused.
/// </para>
/// </remarks>
internal interface IStaffBranchGuard
{
    /// <summary>
    /// Refuses the caller unless they are an active owner, manager or platform admin who covers
    /// <paramref name="branchId"/>.
    /// </summary>
    /// <param name="branchId">The branch being read or changed.</param>
    /// <param name="operation">What is being attempted, for the refusal message.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The acting staff member's id.</returns>
    /// <exception cref="StaffPermissionException">
    /// Not a staff member, below manager, deactivated, or an owner or manager for whom this branch is
    /// not one they cover. Answers 403 <c>forbidden</c>.
    /// </exception>
    Task<Guid> RequireAtBranchAsync(Guid branchId, string operation, CancellationToken cancellationToken = default);
}
