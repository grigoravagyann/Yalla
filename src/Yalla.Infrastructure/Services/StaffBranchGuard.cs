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
/// The widening for owners and managers is deliberate and is the point of those accounts: they are
/// venue-scoped rather than branch-scoped, so they pass for any branch of their own venue. A
/// waiter is confined to the one branch their tablet is enrolled at, and a platform admin belongs
/// to no venue and passes for all of them.
/// </para>
/// </remarks>
internal sealed class StaffBranchGuard(YallaDbContext db, ICurrentActor actor)
{
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

        if (venueOwnsBranch && staff.Role is StaffRole.Owner or StaffRole.Manager)
        {
            return;
        }

        throw new StaffBranchScopeException(subject);
    }
}
