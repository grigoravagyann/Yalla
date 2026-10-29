using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Which branches a diner can see and start something at: the one definition every diner surface
/// filters by.
/// </summary>
/// <remarks>
/// <para>
/// Two halves. <b>Published</b> is what it always was - the branch is switched on and its venue is
/// active, not suspended, not deleted. <b>Taking guests</b> is the readiness checklist's required
/// lines: a table a diner can book (active, bookable, pinned on a room photo), opening hours, a
/// device at the branch that will see the booking arrive (an enrolled tablet or a paired staff
/// phone) and somebody on the staff to sign in on it.
/// </para>
/// <para>
/// The second half exists because a branch with no tablet took bookings nobody saw: the
/// short-notice approval lapsed, the guest's message went unanswered and the party arrived to a
/// room that did not expect them. A branch missing a required line is therefore not listed,
/// searched, shown on its public page or offered times, and a booking or takeaway order for it is
/// refused - the same as a closed branch. The rest of the checklist (the menu, the reviewed policy)
/// is advice and hides nothing.
/// </para>
/// <para>
/// Kept as one expression so it composes into each caller's own query instead of costing a round
/// trip, and mirrored line for line by <see cref="BranchReadinessQuery"/>, whose
/// <c>TakingGuests</c> is what the console's checklist shows. The two are tested against each other.
/// </para>
/// </remarks>
internal static class DinerVisibility
{
    /// <summary>Published, and every required checklist line satisfied.</summary>
    public static Expression<Func<Branch, bool>> Visible(YallaDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        return b => b.IsActive
                    && b.Venue.IsActive
                    && b.Venue.SuspendedAtUtc == null
                    && b.Venue.DeletedAtUtc == null
                    && db.DiningTables.Any(t => t.BranchId == b.Id && t.IsActive && t.IsBookable && t.PhotoId != null)
                    && db.OpeningHours.Any(h => h.BranchId == b.Id)
                    && db.StaffDevices.Any(d => d.BranchId == b.Id && d.RevokedAtUtc == null)
                    && db.StaffMembers.Any(s => s.IsActive
                                                && s.Role != StaffRole.PlatformAdmin
                                                && s.VenueId == b.VenueId
                                                && (s.BranchId == null || s.BranchId == b.Id));
    }

    /// <summary>The visible branches, untracked.</summary>
    public static IQueryable<Branch> Branches(YallaDbContext db) =>
        db.Branches.AsNoTracking().Where(Visible(db));

    /// <summary>Whether this one branch is visible to diners right now.</summary>
    public static Task<bool> IsVisibleAsync(YallaDbContext db, Guid branchId, CancellationToken cancellationToken) =>
        Branches(db).AnyAsync(b => b.Id == branchId, cancellationToken);

    /// <summary>
    /// Refuses a new booking or order at a branch that is not taking guests. Called after
    /// <see cref="VenueGate.RequireOpenForBusiness"/>, which has already said why a closed or
    /// suspended one is refused; what is left is a branch that has not finished its setup.
    /// </summary>
    /// <exception cref="BranchUnavailableException">The branch is missing a required checklist line.</exception>
    public static async Task RequireTakingGuestsAsync(
        YallaDbContext db,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        if (!await IsVisibleAsync(db, branchId, cancellationToken))
        {
            throw new BranchUnavailableException(branchId, "it is not taking guests yet.");
        }
    }
}
