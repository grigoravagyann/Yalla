using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Domain.Enums;
using Yalla.Domain.Menus;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// How far along a branch's setup is: the onboarding checklist, answered by the server.
/// </summary>
/// <remarks>
/// <para>
/// The console rendered this checklist from a client-side guess, which meant two definitions of
/// "ready" - and only the server's decides whether the branch may go Paid. This is that one.
/// </para>
/// <para>
/// Seven small counts over six tables, none of them on a hot path: this is a settings screen and a
/// tier switch, not something a diner's phone polls. Written as separate scalar queries rather than
/// one join, because counting across six unrelated one-to-many relationships in a single statement
/// multiplies the rows out and is slower as well as unreadable.
/// </para>
/// </remarks>
internal sealed class BranchReadinessQuery(YallaDbContext db) : IBranchReadinessQuery
{
    public async Task<BranchReadinessView> GetAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new { b.Id, b.VenueId, b.ReservationPolicyReviewedAtUtc, b.AcceptsWebBookings })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        // Inactive tables are excluded: a table that was deleted-but-kept for its history is not a
        // table the room has, and counting it would tick "floor plan drawn" for an empty room.
        var tables = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.IsActive)
            .Select(t => t.Label)
            .ToListAsync(cancellationToken);

        var categoryCount = await db.MenuCategories
            .AsNoTracking()
            .CountAsync(c => c.BranchId == branchId, cancellationToken);

        var itemCount = await db.MenuItems
            .AsNoTracking()
            .CountAsync(i => i.MenuCategory.BranchId == branchId, cancellationToken);

        var incomplete = await IncompleteMenuItemIdsAsync(branchId, cancellationToken);

        var openDays = await db.OpeningHours
            .AsNoTracking()
            .Where(h => h.BranchId == branchId)
            .Select(h => h.Day)
            .Distinct()
            .CountAsync(cancellationToken);

        // Staff belong to the venue and may be branch-scoped or venue-wide, so somebody assigned to
        // no particular branch counts for every branch of their venue - which is what an owner is.
        // Platform admins are excluded: they run Yalla, they do not staff a floor.
        var staffCount = await db.StaffMembers
            .AsNoTracking()
            .CountAsync(
                s => s.IsActive
                     && s.Role != StaffRole.PlatformAdmin
                     && s.VenueId == branch.VenueId
                     && (s.BranchId == null || s.BranchId == branchId),
                cancellationToken);

        var deviceCount = await db.StaffDevices
            .AsNoTracking()
            .CountAsync(d => d.BranchId == branchId && d.RevokedAtUtc == null, cancellationToken);

        var floorPlanDrawn = tables.Count > 0;
        var tablesLabelled = floorPlanDrawn && tables.TrueForAll(label => !string.IsNullOrWhiteSpace(label));
        var categoriesPresent = categoryCount > 0;
        var menuComplete = categoriesPresent && itemCount > 0 && incomplete.Count == 0;
        var hoursSet = openDays > 0;
        var policyReviewed = branch.ReservationPolicyReviewedAtUtc is not null;
        var staffEnrolled = staffCount > 0;
        var deviceEnrolled = deviceCount > 0;

        var blockers = new List<string>();

        if (!floorPlanDrawn)
        {
            blockers.Add("The floor plan has no tables on it yet.");
        }
        else if (!tablesLabelled)
        {
            blockers.Add("Some tables have no label printed on them.");
        }

        if (!categoriesPresent)
        {
            blockers.Add("The menu has no categories yet.");
        }
        else if (itemCount == 0)
        {
            blockers.Add("The menu has categories but no items.");
        }
        else if (incomplete.Count > 0)
        {
            blockers.Add(
                $"{incomplete.Count} menu item(s) are missing a photo, a description, ingredients, "
                + "allergens, a portion size or a prep time, and are hidden from diners until they are finished.");
        }

        if (!hoursSet)
        {
            blockers.Add("Opening hours have not been set.");
        }

        if (!policyReviewed)
        {
            blockers.Add("Nobody has reviewed the reservation policy; it is still on the defaults.");
        }

        if (!staffEnrolled)
        {
            blockers.Add("No staff work at this branch yet.");
        }

        if (!deviceEnrolled)
        {
            blockers.Add("No tablet has been enrolled at this branch.");
        }

        return new BranchReadinessView(
            BranchId: branchId,
            IsReadyForDiners: blockers.Count == 0,
            FloorPlanDrawn: floorPlanDrawn,
            TableCount: tables.Count,
            TablesLabelled: tablesLabelled,
            MenuCategoriesPresent: categoriesPresent,
            MenuCategoryCount: categoryCount,
            MenuItemCount: itemCount,
            IncompleteMenuItemCount: incomplete.Count,
            IncompleteMenuItemIds: incomplete,
            MenuComplete: menuComplete,
            OpeningHoursSet: hoursSet,
            OpeningHoursDayCount: openDays,
            ReservationPolicyReviewed: policyReviewed,
            StaffEnrolled: staffEnrolled,
            StaffCount: staffCount,
            DeviceEnrolled: deviceEnrolled,
            DeviceCount: deviceCount,

            // Reported, never a blocker - see BranchReadinessView. It is deliberately absent from
            // the blockers list above, and adding it there would stop every venue that does not
            // want web bookings from ever reading as ready.
            AcceptsWebBookings: branch.AcceptsWebBookings,
            Blockers: blockers);
    }

    /// <summary>
    /// The items that are not fit to show a diner, oldest first.
    /// </summary>
    /// <remarks>
    /// Filtered in SQL with <see cref="MenuItemCompleteness.Incomplete"/> - the negation of the one
    /// rule, derived from its own expression tree - so this count and the badge the console draws on
    /// each item cannot disagree about what "complete" means.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> IncompleteMenuItemIdsAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        await db.MenuItems
            .AsNoTracking()
            .Where(i => i.MenuCategory.BranchId == branchId)
            .Where(MenuItemCompleteness.Incomplete)
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
}
