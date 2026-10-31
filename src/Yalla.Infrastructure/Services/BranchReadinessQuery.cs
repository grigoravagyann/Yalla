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
        var views = await GetManyAsync([branchId], cancellationToken);

        return views.TryGetValue(branchId, out var view)
            ? view
            : throw new KeyNotFoundException($"Branch {branchId} was not found.");
    }

    /// <remarks>
    /// <para>
    /// The same small counts as ever, each grouped by branch rather than filtered to one, so the
    /// platform's venue list and Home pay for the same handful of queries whether they ask about one
    /// branch or two hundred. <see cref="GetAsync"/> is a call to this with one id, which is what keeps
    /// the checklist a manager reads and the platform's "not ready" column one definition.
    /// </para>
    /// <para>
    /// Every row the grouped queries return is a few columns about a branch that was asked about; the
    /// largest is the incomplete-dish ids, which the checklist has always returned in full.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<Guid, BranchReadinessView>> GetManyAsync(
        IReadOnlyCollection<Guid> branchIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branchIds);

        var ids = branchIds.Distinct().ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, BranchReadinessView>();
        }

        var branches = await db.Branches
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new
            {
                b.Id,
                b.VenueId,
                b.ReservationPolicyReviewedAtUtc,
                b.AcceptsWebBookings,
                TimeOnly = b.ReservationPolicy.BookingMode == BookingMode.TimeOnly && b.ReservationPolicy.GuestsAtOnce > 0,
            })
            .ToListAsync(cancellationToken);

        if (branches.Count == 0)
        {
            return new Dictionary<Guid, BranchReadinessView>();
        }

        var found = branches.Select(b => b.Id).ToList();
        var venueIds = branches.Select(b => b.VenueId).Distinct().ToList();

        // Inactive tables are excluded: a table that was deleted-but-kept for its history is not a
        // table the room has, and counting it would tick "floor plan drawn" for an empty room.
        var tables = (await db.DiningTables
                .AsNoTracking()
                .Where(t => found.Contains(t.BranchId) && t.IsActive)
                .Select(t => new { t.BranchId, t.Label, Bookable = t.IsBookable && t.PhotoId != null })
                .ToListAsync(cancellationToken))
            .ToLookup(t => t.BranchId, t => new TableLine(t.Label, t.Bookable));

        var categoryCounts = await db.MenuCategories
            .AsNoTracking()
            .Where(c => found.Contains(c.BranchId))
            .GroupBy(c => c.BranchId)
            .Select(g => new { BranchId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.BranchId, g => g.Count, cancellationToken);

        var itemCounts = await db.MenuItems
            .AsNoTracking()
            .Where(i => found.Contains(i.MenuCategory.BranchId))
            .GroupBy(i => i.MenuCategory.BranchId)
            .Select(g => new { BranchId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.BranchId, g => g.Count, cancellationToken);

        var incomplete = (await IncompleteItems(found)
                .Select(i => new { i.MenuCategory.BranchId, i.Id })
                .ToListAsync(cancellationToken))
            .ToLookup(i => i.BranchId, i => i.Id);

        var openDays = (await db.OpeningHours
                .AsNoTracking()
                .Where(h => found.Contains(h.BranchId))
                .Select(h => new { h.BranchId, h.Day })
                .Distinct()
                .ToListAsync(cancellationToken))
            .GroupBy(h => h.BranchId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Staff belong to the venue and may be branch-scoped or venue-wide, so somebody assigned to
        // no particular branch counts for every branch of their venue - which is what an owner is.
        // Platform admins are excluded: they run Yalla, they do not staff a floor.
        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.IsActive
                        && s.Role != StaffRole.PlatformAdmin
                        && s.VenueId != null
                        && venueIds.Contains(s.VenueId.Value))
            .GroupBy(s => new { s.VenueId, s.BranchId })
            .Select(g => new { g.Key.VenueId, g.Key.BranchId, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var deviceCounts = await db.StaffDevices
            .AsNoTracking()
            .Where(d => found.Contains(d.BranchId) && d.RevokedAtUtc == null)
            .GroupBy(d => d.BranchId)
            .Select(g => new { BranchId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.BranchId, g => g.Count, cancellationToken);

        var views = new Dictionary<Guid, BranchReadinessView>(branches.Count);

        foreach (var branch in branches)
        {
            var staffCount = staff
                .Where(s => s.VenueId == branch.VenueId && (s.BranchId == null || s.BranchId == branch.Id))
                .Sum(s => s.Count);

            views[branch.Id] = Compose(
                branch.Id,
                [.. tables[branch.Id]],
                categoryCounts.GetValueOrDefault(branch.Id),
                itemCounts.GetValueOrDefault(branch.Id),
                [.. incomplete[branch.Id]],
                openDays.GetValueOrDefault(branch.Id),
                branch.ReservationPolicyReviewedAtUtc is not null,
                staffCount,
                deviceCounts.GetValueOrDefault(branch.Id),
                branch.AcceptsWebBookings,
                branch.TimeOnly);
        }

        return views;
    }

    /// <summary>A table as the checklist counts it: its label, and whether a diner can book it.</summary>
    private readonly record struct TableLine(string Label, bool Bookable);

    /// <summary>
    /// The checklist from its counts. One place writes both the sentences and the codes, line for
    /// line, so <see cref="BranchReadinessView.Blockers"/> and <see cref="BranchReadinessView.Issues"/>
    /// cannot drift apart.
    /// </summary>
    private static BranchReadinessView Compose(
        Guid branchId,
        List<TableLine> tables,
        int categoryCount,
        int itemCount,
        List<Guid> incomplete,
        int openDays,
        bool policyReviewed,
        int staffCount,
        int deviceCount,
        bool acceptsWebBookings,
        bool booksByTimeOnly)
    {
        var floorPlanDrawn = tables.Count > 0;
        var tablesLabelled = floorPlanDrawn && tables.TrueForAll(t => !string.IsNullOrWhiteSpace(t.Label));
        var bookableTableCount = tables.Count(t => t.Bookable);
        var categoriesPresent = categoryCount > 0;
        var menuComplete = categoriesPresent && itemCount > 0 && incomplete.Count == 0;
        var hoursSet = openDays > 0;
        var staffEnrolled = staffCount > 0;
        var deviceEnrolled = deviceCount > 0;

        var blockers = new List<string>();
        var issues = new List<ReadinessIssue>();

        void Block(string sentence, string code, int? count = null)
        {
            blockers.Add(sentence);
            issues.Add(new ReadinessIssue(code, count));
        }

        if (!floorPlanDrawn)
        {
            Block("The floor plan has no tables on it yet.", ReadinessIssueCodes.NoTables);
        }
        else if (!tablesLabelled)
        {
            Block("Some tables have no label printed on them.", ReadinessIssueCodes.TablesUnlabelled);
        }

        if (!categoriesPresent)
        {
            Block("The menu has no categories yet.", ReadinessIssueCodes.NoMenu);
        }
        else if (itemCount == 0)
        {
            Block("The menu has categories but no items.", ReadinessIssueCodes.MenuEmpty);
        }
        else if (incomplete.Count > 0)
        {
            Block(
                $"{incomplete.Count} menu item(s) are missing a photo, a description, ingredients, "
                + "allergens, a portion size or a prep time, and are hidden from diners until they are finished.",
                ReadinessIssueCodes.IncompleteDishes,
                incomplete.Count);
        }

        if (!hoursSet)
        {
            Block("Opening hours have not been set.", ReadinessIssueCodes.NoHours);
        }

        if (!policyReviewed)
        {
            Block(
                "Nobody has reviewed the reservation policy; it is still on the defaults.",
                ReadinessIssueCodes.PolicyNotReviewed);
        }

        if (!staffEnrolled)
        {
            Block("No staff work at this branch yet.", ReadinessIssueCodes.NoStaff);
        }

        if (!deviceEnrolled)
        {
            Block("No tablet or staff phone is connected at this branch.", ReadinessIssueCodes.NoDevice);
        }

        // The lines without which diners do not see the branch at all, in checklist order. The
        // same four DinerVisibility filters every diner surface by; the rest above are advice.
        var missingForGuests = new List<string>();

        // A branch that books by time only needs no table a diner can tap: its guests at once stand in for it.
        if (booksByTimeOnly)
        {
        }
        else if (!floorPlanDrawn)
        {
            missingForGuests.Add(ReadinessIssueCodes.NoTables);
        }
        else if (bookableTableCount == 0)
        {
            missingForGuests.Add(ReadinessIssueCodes.NoBookableTable);
        }

        if (!hoursSet)
        {
            missingForGuests.Add(ReadinessIssueCodes.NoHours);
        }

        if (!staffEnrolled)
        {
            missingForGuests.Add(ReadinessIssueCodes.NoStaff);
        }

        if (!deviceEnrolled)
        {
            missingForGuests.Add(ReadinessIssueCodes.NoDevice);
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
            BookableTableCount: bookableTableCount,
            BooksByTimeOnly: booksByTimeOnly,
            TakingGuests: missingForGuests.Count == 0,
            MissingForGuests: missingForGuests,

            // Reported, never a blocker - see BranchReadinessView. It is deliberately absent from
            // the blockers list above, and adding it there would stop every venue that does not
            // want web bookings from ever reading as ready.
            AcceptsWebBookings: acceptsWebBookings,
            Blockers: blockers,
            Issues: issues);
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
        await IncompleteItems([branchId])
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

    /// <summary>The unfinished dishes of these branches, oldest first - one filter for both reads.</summary>
    private IQueryable<MenuItem> IncompleteItems(List<Guid> branchIds) =>
        db.MenuItems
            .AsNoTracking()
            .Where(i => branchIds.Contains(i.MenuCategory.BranchId))
            .Where(MenuItemCompleteness.Incomplete)
            .OrderBy(i => i.Id);
}
