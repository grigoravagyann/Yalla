using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Venues;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The venue a signed-in owner or manager works in, with the branches their own row covers.
/// </summary>
/// <remarks>
/// <para>
/// Coverage comes from the acting staff member's <i>stored</i> row, read the way
/// <see cref="StaffManagementService"/> reads it: the token names at most one branch and an
/// owner's names none, so a decision made from the claim is the decision the console got wrong.
/// </para>
/// <para>
/// A manager whose row names a branch is shown that branch and no other. <b>This is an enforcement
/// rule, not only a view rule (K4)</b>: <c>BranchScoped</c> refuses such a manager on every other
/// branch from their token, and <c>IStaffBranchGuard</c> refuses them again from the stored row. So
/// what this lists and what the server allows are the same set, in both directions.
/// </para>
/// <para>
/// Ordering: active first, then by name. The id tiebreak after that only makes the list stable;
/// it is SQL Server's <c>uniqueidentifier</c> order, which is neither .NET's nor a string sort,
/// and no caller may rely on it beyond "the same order every time".
/// </para>
/// </remarks>
internal sealed class ManagedVenueQuery(YallaDbContext db, ICurrentActor actor) : IManagedVenueQuery
{
    private const string Operation = "Read the venue";

    public async Task<ManagedVenueView> GetAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        var acting = await RequireActorInVenueAsync(venueId, cancellationToken);

        var venue = await db.Venues
            .AsNoTracking()
            .Where(v => v.Id == venueId)
            .Select(v => new { v.Id, v.Name, v.Type, v.Slug, v.SuspendedAtUtc, v.DeletedAtUtc })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Venue {venueId} was not found.");

        var branches = db.Branches.AsNoTracking().Where(b => b.VenueId == venueId);

        // The one narrowing: a manager whose row names a branch. Owners, venue-wide managers and
        // the platform tier cover the whole venue. The same rule the branch routes enforce (K4).
        if (acting.Role == StaffRole.Manager && acting.BranchId is { } home)
        {
            branches = branches.Where(b => b.Id == home);
        }

        var listed = await branches
            .OrderByDescending(b => b.IsActive)
            .ThenBy(b => b.Name)
            .ThenBy(b => b.Id)
            .Select(BranchProjection)
            .ToListAsync(cancellationToken);

        return new ManagedVenueView(
            venue.Id,
            venue.Name,
            venue.Type,
            venue.Slug,
            venue.SuspendedAtUtc != null,
            venue.DeletedAtUtc != null,
            listed);
    }

    /// <summary>
    /// The acting staff member's own row: their stored role, venue and branch, not the token's
    /// claims. A platform admin belongs to no venue and may read any.
    /// </summary>
    private async Task<StaffMember> RequireActorInVenueAsync(Guid venueId, CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff || actor.StaffMemberId is not { } actingId)
        {
            throw new StaffPermissionException(Operation, actor.Role, StaffRole.Manager);
        }

        var acting = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == actingId, cancellationToken)
                     ?? throw new StaffPermissionException(Operation, actor.Role, StaffRole.Manager);

        if (!acting.IsActive)
        {
            throw new StaffPermissionException(Operation, acting.Role, StaffRole.Manager);
        }

        if (acting.IsPlatformAdmin)
        {
            return acting;
        }

        if (acting.VenueId != venueId || acting.Role is not (StaffRole.Owner or StaffRole.Manager))
        {
            throw new StaffPermissionException(Operation, acting.Role, StaffRole.Manager);
        }

        return acting;
    }

    private static readonly System.Linq.Expressions.Expression<Func<Branch, ManagedBranchView>> BranchProjection =
        b => new ManagedBranchView(
            b.Id,
            b.VenueId,
            b.Name,
            b.Slug,
            b.TimeZoneId,
            b.IsActive,
            b.SubscriptionTier,
            b.DiningTables.Count);
}
