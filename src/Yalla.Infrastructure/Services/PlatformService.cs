using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Application.Platform;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The platform tier: venues and branches, created and configured through the API instead of by
/// hand in SQL.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation adds its audit row and then calls <c>SaveChanges</c> once, so the change and
/// the log entry are one transaction. Creating a venue is the clearest case: venue, first branch
/// and two audit rows commit together or not at all.
/// </para>
/// <para>
/// The caller must be a platform admin, checked here from the current actor. The
/// <c>PlatformAdminOnly</c> policy refuses everyone else at the endpoint; this is the second lock
/// for any caller that is not HTTP.
/// </para>
/// </remarks>
internal sealed class PlatformService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IBranchReadinessQuery readiness,
    ILogger<PlatformService> logger) : IPlatformService
{
    /// <summary>The audit action for a branch moving between Free and Paid.</summary>
    public const string TierChangeAuditAction = "branch.tier-change";

    private const string VenueType_ = "Venue";
    private const string BranchType = "Branch";

    public async Task<VenueDetail> CreateVenueAsync(
        CreateVenueCommand command,
        CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Create venue");
        ArgumentNullException.ThrowIfNull(command);

        // Client input, unlike the line above it - and every missing field at once rather than the
        // first. A body with no firstBranch used to reach ArgumentNullException.ThrowIfNull and
        // come back as a 500 logged as our fault; it is now one entry in a refusal that also names
        // the missing name and slug beside it. Presence only - bounds are still the constructors'.
        command.Validate();

        var venue = new Venue(command.Name, command.Type, command.Slug);
        var branch = NewBranch(venue, command.FirstBranch);

        db.Venues.Add(venue);
        db.Branches.Add(branch);

        await AuditAsync("venue.create", VenueTarget(venue), new
        {
            after = new { venue.Name, venue.Type, venue.Slug },
        }, cancellationToken);

        await AuditAsync("branch.create", BranchTarget(branch), new
        {
            venueId = venue.Id,
            after = BranchSnapshot(branch),
        }, cancellationToken);

        // One SaveChanges: venue, branch and both audit rows commit together or not at all.
        await SaveAsync(cancellationToken, venue.Slug);

        logger.LogInformation(
            "Platform admin {StaffMemberId} created venue {VenueId} ({Slug}) with branch {BranchId}.",
            actor.StaffMemberId, venue.Id, venue.Slug, branch.Id);

        return await GetVenueAsync(venue.Id, cancellationToken);
    }

    public async Task<PagedResult<VenueSummary>> ListVenuesAsync(
        VenueListQuery query,
        CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("List venues");
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var status = Pick(query.Status, query.IncludeDeleted ? VenueListFilters.All : VenueListFilters.Live, VenueListFilters.Statuses, "status");
        // Blank is "every tier", as an empty select sends it - not the first value Pick falls back to.
        var tier = string.IsNullOrWhiteSpace(query.Tier) ? null : Pick(query.Tier, null, VenueListFilters.Tiers, "tier");
        var sort = Pick(query.Sort, VenueListFilters.SortName, VenueListFilters.Sorts, "sort");
        var descending = Pick(query.Dir, VenueListFilters.Ascending, VenueListFilters.Directions, "dir") == VenueListFilters.Descending;

        var venues = db.Venues.AsNoTracking().AsQueryable();

        venues = status switch
        {
            VenueListFilters.All => venues,
            VenueListFilters.Active => venues.Where(v => v.DeletedAtUtc == null && v.SuspendedAtUtc == null && v.IsActive),
            VenueListFilters.Suspended => venues.Where(v => v.DeletedAtUtc == null && v.SuspendedAtUtc != null),
            VenueListFilters.Inactive => venues.Where(v => v.DeletedAtUtc == null && v.SuspendedAtUtc == null && !v.IsActive),
            VenueListFilters.Deleted => venues.Where(v => v.DeletedAtUtc != null),
            _ => venues.Where(v => v.DeletedAtUtc == null),
        };

        venues = tier switch
        {
            VenueListFilters.Free => venues.Where(v => !v.Branches.Any(b => b.SubscriptionTier == SubscriptionTier.Paid)),
            VenueListFilters.Paid => venues.Where(v => v.Branches.Any() && v.Branches.All(b => b.SubscriptionTier == SubscriptionTier.Paid)),
            VenueListFilters.Mixed => venues.Where(v =>
                v.Branches.Any(b => b.SubscriptionTier == SubscriptionTier.Paid)
                && v.Branches.Any(b => b.SubscriptionTier != SubscriptionTier.Paid)),
            _ => venues,
        };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            venues = venues.Where(v =>
                v.Name.Contains(term)
                || v.Slug.Contains(term)
                || v.Branches.Any(b => b.Name.Contains(term) || (b.District != null && b.District.Contains(term))));
        }

        var total = await venues.CountAsync(cancellationToken);

        var published = db.BranchReviews.Where(r => r.HiddenAtUtc == null);
        var activeTables = db.DiningTables.Where(t => t.IsActive);

        var ordered = sort switch
        {
            VenueListFilters.SortRating => Order(venues, v => published.Where(r => r.Branch.VenueId == v.Id).Average(r => (double?)r.Rating), descending),
            VenueListFilters.SortTables => Order(venues, v => activeTables.Count(t => t.Branch.VenueId == v.Id), descending),
            VenueListFilters.SortCreated => Order(venues, v => v.CreatedAtUtc, descending),
            _ => Order(venues, v => v.Name, descending),
        };

        // Name and id break every tie, so a page boundary never splits two equal rows differently on
        // the next request.
        var items = await SummariesAsync(
            ordered
                .ThenBy(v => v.Name)
                .ThenBy(v => v.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize),
            cancellationToken);

        return new PagedResult<VenueSummary>(items, page, pageSize, total);
    }

    public async Task<VenueDetail> GetVenueAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Read venue");

        var summaries = await SummariesAsync(db.Venues.AsNoTracking().Where(v => v.Id == venueId), cancellationToken);

        var summary = summaries.FirstOrDefault()
                      ?? throw new KeyNotFoundException($"Venue {venueId} was not found.");

        var branches = await db.Branches
            .AsNoTracking()
            .Where(b => b.VenueId == venueId)
            .OrderBy(b => b.Name)
            .Select(BranchProjection)
            .ToListAsync(cancellationToken);

        return new VenueDetail(summary, branches);
    }

    public async Task<VenueDetail> UpdateVenueAsync(
        Guid venueId,
        UpdateVenueCommand command,
        CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Update venue");
        ArgumentNullException.ThrowIfNull(command);

        var venue = await LoadVenueAsync(venueId, cancellationToken);
        var before = new { venue.Name, venue.Type, venue.Slug, venue.IsActive };

        if (command.Name is { } name)
        {
            venue.Rename(name);
        }

        if (command.Type is { } type)
        {
            venue.SetType(type);
        }

        if (command.Slug is { } slug)
        {
            venue.SetSlug(slug);
        }

        if (command.IsActive is { } active)
        {
            venue.SetActive(active);
        }

        await AuditAsync("venue.update", VenueTarget(venue), new
        {
            before,
            after = new { venue.Name, venue.Type, venue.Slug, venue.IsActive },
        }, cancellationToken);

        await SaveAsync(cancellationToken, venue.Slug);

        return await GetVenueAsync(venue.Id, cancellationToken);
    }

    public async Task<VenueDetail> SuspendVenueAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Suspend venue");

        var venue = await LoadVenueAsync(venueId, cancellationToken);
        var nowUtc = clock.UtcNow;

        venue.Suspend(nowUtc);

        await AuditAsync("venue.suspend", VenueTarget(venue), new
        {
            suspendedAtUtc = venue.SuspendedAtUtc,
        }, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Platform admin {StaffMemberId} suspended venue {VenueId}.", actor.StaffMemberId, venue.Id);

        return await GetVenueAsync(venue.Id, cancellationToken);
    }

    public async Task<VenueDetail> ReactivateVenueAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Reactivate venue");

        var venue = await LoadVenueAsync(venueId, cancellationToken);
        var wasSuspendedAt = venue.SuspendedAtUtc;

        venue.Reactivate();

        await AuditAsync("venue.reactivate", VenueTarget(venue), new
        {
            wasSuspendedAtUtc = wasSuspendedAt,
        }, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await GetVenueAsync(venue.Id, cancellationToken);
    }

    public async Task<VenueDetail> RestoreVenueAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Restore venue");

        var venue = await LoadVenueAsync(venueId, cancellationToken);
        var deletedAt = venue.DeletedAtUtc;

        // The unique index on the slug covers deleted venues, so a deleted venue keeps its address and
        // this cannot find it taken today. Asked anyway, by name, because the answer the console needs
        // when it does is "which venue has it", not a constraint error.
        var holder = await db.Venues
            .AsNoTracking()
            .Where(v => v.Id != venue.Id && v.Slug == venue.Slug)
            .Select(v => v.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (venue.IsDeleted && holder is not null)
        {
            throw new DomainStateException(
                $"The web address '{venue.Slug}' now belongs to '{holder}', so '{venue.Name}' cannot be restored "
                + "under it. Change that venue's address first.");
        }

        if (!venue.Restore())
        {
            // Not deleted: an undo that arrives twice is answered with the venue as it is.
            return await GetVenueAsync(venue.Id, cancellationToken);
        }

        await AuditAsync("venue.restore", VenueTarget(venue), new
        {
            wasDeletedAtUtc = deletedAt,
            after = new { venue.IsActive, isSuspended = venue.IsSuspended },
        }, cancellationToken);

        await SaveAsync(cancellationToken, venue.Slug);

        logger.LogWarning("Platform admin {StaffMemberId} restored venue {VenueId}.", actor.StaffMemberId, venue.Id);

        return await GetVenueAsync(venue.Id, cancellationToken);
    }

    /// <summary>
    /// Soft delete only. The venue's reservations, tabs and payments are financial and occupancy
    /// records and stay; the venue is switched off and stamped.
    /// </summary>
    public async Task<VenueDetail> DeleteVenueAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Delete venue");

        var venue = await LoadVenueAsync(venueId, cancellationToken);
        var nowUtc = clock.UtcNow;

        var branchIds = await db.Branches
            .Where(b => b.VenueId == venue.Id)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        var openTabs = await db.Tabs
            .AsNoTracking()
            .Where(t => branchIds.Contains(t.BranchId)
                        && (t.Status == TabStatus.Open || t.Status == TabStatus.Closing))
            .OrderBy(t => t.DiningTable.Label)
            .Select(t => t.DiningTable.Label)
            .ToListAsync(cancellationToken);

        var futureReservations = await db.Reservations
            .AsNoTracking()
            .Where(r => branchIds.Contains(r.BranchId)
                        && r.StartUtc > nowUtc
                        && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.PendingApproval))
            .OrderBy(r => r.StartUtc)
            .Select(r => r.Code)
            .ToListAsync(cancellationToken);

        if (openTabs.Count > 0 || futureReservations.Count > 0)
        {
            throw new VenueDeletionBlockedException(venue.Id, openTabs, futureReservations);
        }

        venue.SoftDelete(nowUtc);

        await AuditAsync("venue.delete", VenueTarget(venue), new
        {
            deletedAtUtc = venue.DeletedAtUtc,
            snapshot = new { venue.Name, venue.Type, venue.Slug, branchCount = branchIds.Count },
        }, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Platform admin {StaffMemberId} soft-deleted venue {VenueId}.", actor.StaffMemberId, venue.Id);

        return await GetVenueAsync(venue.Id, cancellationToken);
    }

    public async Task<BranchSummary> AddBranchAsync(
        Guid venueId,
        CreateBranchCommand command,
        CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Add branch");
        ArgumentNullException.ThrowIfNull(command);

        var venue = await LoadVenueAsync(venueId, cancellationToken);

        if (venue.IsDeleted)
        {
            throw new DomainStateException("A deleted venue cannot take a new branch.");
        }

        var branch = NewBranch(venue, command);
        db.Branches.Add(branch);

        await AuditAsync("branch.create", BranchTarget(branch), new
        {
            venueId = venue.Id,
            after = BranchSnapshot(branch),
        }, cancellationToken);

        await SaveAsync(cancellationToken, branch.Slug);

        return await GetBranchAsync(branch.Id, cancellationToken);
    }

    public async Task<BranchSummary> UpdateBranchAsync(
        Guid branchId,
        UpdateBranchCommand command,
        CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Update branch");
        ArgumentNullException.ThrowIfNull(command);

        var branch = await db.Branches
            .Include(b => b.Venue)
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        if (branch.Venue.IsDeleted)
        {
            throw new DomainStateException(
                $"Venue '{branch.Venue.Name}' has been deleted, so its branches can no longer be changed.");
        }

        var before = BranchSnapshot(branch);

        // Downgrading switches the tab endpoints off for this branch - including the reads and the
        // settlement of tabs that are already open. Doing that to a table mid-meal strands a real
        // bill nobody can see or pay, so it waits, exactly as deleting a venue does.
        if (command.SubscriptionTier is SubscriptionTier.Free && branch.IsPaid)
        {
            var openTabs = await db.Tabs
                .AsNoTracking()
                .Where(t => t.BranchId == branch.Id && (t.Status == TabStatus.Open || t.Status == TabStatus.Closing))
                .OrderBy(t => t.DiningTable.Label)
                .Select(t => t.DiningTable.Label)
                .ToListAsync(cancellationToken);

            if (openTabs.Count > 0)
            {
                throw new DomainStateException(
                    $"This branch has {openTabs.Count} open tab(s) on table(s) {string.Join(", ", openTabs)}. "
                    + "Moving it to Free would hide those bills from the people who owe them. "
                    + "Wait until they are settled.");
            }
        }

        // Going live is where the menu rule is enforced. Prompt 6 required a photo, ingredients,
        // allergens, a portion size and a prep time on create, for a good reason - the fields are
        // what stop a diner having to ask a waiter - but it made an eighty-dish menu unenterable.
        // The requirement did not go away; it moved here, to the moment the branch starts taking
        // diners, which is the moment it actually matters. See docs/menu-completeness.md.
        if (command.SubscriptionTier is SubscriptionTier.Paid && !branch.IsPaid)
        {
            var incomplete = await readiness.IncompleteMenuItemIdsAsync(branch.Id, cancellationToken);

            if (incomplete.Count > 0)
            {
                throw new BranchNotReadyForDinersException(branch.Id, incomplete.Count);
            }
        }

        if (command.Name is { } name)
        {
            branch.Rename(name);
        }

        if (command.Address is not null || command.Latitude is not null || command.Longitude is not null)
        {
            // The three travel together: an address at the old coordinates is a pin in the wrong place.
            branch.Relocate(
                command.Address ?? branch.Address,
                command.Latitude ?? branch.Latitude,
                command.Longitude ?? branch.Longitude);
        }

        if (command.TimeZoneId is { } timeZone)
        {
            branch.SetTimeZone(timeZone);
        }

        if (command.IsActive is { } active)
        {
            branch.SetActive(active);
        }

        if (command.SubscriptionTier is { } tier)
        {
            branch.SetSubscriptionTier(tier);
        }

        var after = BranchSnapshot(branch);
        var tierChanged = before.SubscriptionTier != after.SubscriptionTier;

        // A tier change is its own entry, because it is the one change the platform is asked about -
        // "when did Luna Cafe start paying" - and the feed says it in a sentence of its own. Anything
        // else the same request changed is the ordinary update beside it; a request that changed
        // nothing at all still leaves the update row it always has.
        if (tierChanged)
        {
            await AuditAsync(TierChangeAuditAction, BranchTarget(branch), new
            {
                venueId = branch.VenueId,
                from = before.SubscriptionTier,
                to = after.SubscriptionTier,
            }, cancellationToken);
        }

        if (!tierChanged || before with { SubscriptionTier = after.SubscriptionTier } != after)
        {
            await AuditAsync("branch.update", BranchTarget(branch), new
            {
                before,
                after,
            }, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (tierChanged)
        {
            logger.LogWarning(
                "Platform admin {StaffMemberId} moved branch {BranchId} from {From} to {To}.",
                actor.StaffMemberId, branch.Id, before.SubscriptionTier, branch.SubscriptionTier);
        }

        return await GetBranchAsync(branch.Id, cancellationToken);
    }

    // ---------------------------------------------------------------- the list's rows

    /// <summary>
    /// The venues the query selects, in its order, each with its rollups.
    /// </summary>
    /// <remarks>
    /// Three reads however long the page is, plus the readiness checklist's handful: the venue rows
    /// with their counts as correlated subqueries, every branch of those venues, and the checklist for
    /// the branches of the venues that are not deleted. Nothing here is per row.
    /// </remarks>
    private async Task<List<VenueSummary>> SummariesAsync(IQueryable<Venue> venues, CancellationToken cancellationToken)
    {
        var published = db.BranchReviews.Where(r => r.HiddenAtUtc == null);
        var activeTables = db.DiningTables.Where(t => t.IsActive);
        var openTabs = db.Tabs.Where(t => t.Status == TabStatus.Open || t.Status == TabStatus.Closing);

        var rows = await venues
            .Select(v => new VenueRow
            {
                VenueId = v.Id,
                Name = v.Name,
                Type = v.Type,
                Slug = v.Slug,
                IsActive = v.IsActive,
                SuspendedAtUtc = v.SuspendedAtUtc,
                DeletedAtUtc = v.DeletedAtUtc,
                CreatedAtUtc = v.CreatedAtUtc,
                BranchCount = v.Branches.Count,
                PaidBranchCount = v.Branches.Count(b => b.SubscriptionTier == SubscriptionTier.Paid),
                TableCount = activeTables.Count(t => t.Branch.VenueId == v.Id),
                ReviewCount = published.Count(r => r.Branch.VenueId == v.Id),
                RatingSum = published.Where(r => r.Branch.VenueId == v.Id).Sum(r => (long?)r.Rating) ?? 0L,
                OpenTabCount = openTabs.Count(t => t.Branch.VenueId == v.Id),
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var venueIds = rows.Select(r => r.VenueId).ToList();

        var branches = (await db.Branches
                .AsNoTracking()
                .Where(b => venueIds.Contains(b.VenueId))
                .Select(b => new BranchRow
                {
                    BranchId = b.Id,
                    VenueId = b.VenueId,
                    Name = b.Name,
                    SubscriptionTier = b.SubscriptionTier,
                    IsActive = b.IsActive,
                    District = b.District,
                    Cuisine = b.Cuisine,
                    CoverPhotoId = b.CoverPhotoId,
                    CoverIsExternal = (bool?)b.CoverPhoto!.IsExternallyHosted,
                    CoverThumbnailPath = b.CoverPhoto!.ThumbnailPath,
                    CoverCardPath = b.CoverPhoto!.CardPath,
                    CoverFullPath = b.CoverPhoto!.FullPath,
                    CoverWidth = b.CoverPhoto!.Width,
                    CoverHeight = b.CoverPhoto!.Height,
                })
                .ToListAsync(cancellationToken))
            .OrderByDescending(b => b.IsActive)
            .ThenBy(b => b.Name, StringComparer.CurrentCulture)
            .ThenBy(b => b.BranchId)
            .ToLookup(b => b.VenueId);

        // Setup progress: every active branch of a venue that is not deleted - suspended and switched-off
        // venues included, because somebody is still finishing them. A deleted venue is not worked out,
        // nobody is going to finish it; a switched-off branch is not either, so the list never names one.
        var deleted = rows.Where(r => r.DeletedAtUtc is not null).Select(r => r.VenueId).ToHashSet();

        var checklists = await readiness.GetManyAsync(
            [.. branches.SelectMany(g => g).Where(b => b.IsActive && !deleted.Contains(b.VenueId)).Select(b => b.BranchId)],
            cancellationToken);

        return [.. rows.Select(row => Summary(row, [.. branches[row.VenueId]], checklists))];
    }

    private static VenueSummary Summary(
        VenueRow row,
        List<BranchRow> branches,
        IReadOnlyDictionary<Guid, BranchReadinessView> checklists)
    {
        var isDeleted = row.DeletedAtUtc is not null;
        var isSuspended = row.SuspendedAtUtc is not null;

        var tiers = branches
            .Select(b => new BranchTierView(
                b.BranchId,
                b.Name,
                b.SubscriptionTier,
                b.IsActive,
                isDeleted || !b.IsActive ? [] : IssuesOf(b, checklists),
                b.District))
            .ToList();

        var cover = branches.FirstOrDefault(b => b.CoverPhotoId is not null);
        var issueAt = tiers.FirstOrDefault(t => t.Issues.Count > 0);

        return new VenueSummary(
            row.VenueId,
            row.Name,
            row.Type,
            row.Slug,
            row.IsActive,
            isSuspended,
            isDeleted,
            row.SuspendedAtUtc,
            row.DeletedAtUtc,
            row.BranchCount,
            row.TableCount,
            row.PaidBranchCount,
            row.BranchCount > 0 && row.PaidBranchCount == row.BranchCount ? SubscriptionTier.Paid : SubscriptionTier.Free,
            PlatformRollups.StatusOf(row.IsActive, isSuspended, isDeleted),
            PlatformRollups.TierMixOf(row.BranchCount, row.PaidBranchCount),
            tiers,
            BranchBadgeRules.AverageRating(row.ReviewCount, row.RatingSum),
            row.ReviewCount,
            row.OpenTabCount,
            issueAt is null ? null : new VenueReadinessIssue(issueAt.Issues[0].Code, issueAt.Issues[0].Count, issueAt.Name),
            tiers.Count(t => t.Issues.Count > 0),
            cover is null
                ? null
                : PhotoView.From(
                    cover.CoverPhotoId!.Value,
                    cover.CoverIsExternal == true,
                    cover.CoverThumbnailPath!,
                    cover.CoverCardPath!,
                    cover.CoverFullPath!,
                    cover.CoverWidth,
                    cover.CoverHeight).ThumbnailUrl,
            branches.Select(b => b.District).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
            branches.Select(b => b.Cuisine).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
            row.CreatedAtUtc);
    }

    /// <summary>
    /// What a guest would find missing at a branch: its checklist's guest-facing lines, then the cover
    /// photo the checklist does not ask about. One rule for the venue table and Home.
    /// </summary>
    internal static IReadOnlyList<ReadinessIssue> IssuesOf(
        Guid branchId,
        bool hasCover,
        IReadOnlyDictionary<Guid, BranchReadinessView> checklists) =>
        PlatformRollups.GuestFacingIssues(
            checklists.TryGetValue(branchId, out var view) ? view.Issues : [],
            hasCover);

    private static IReadOnlyList<ReadinessIssue> IssuesOf(
        BranchRow branch,
        IReadOnlyDictionary<Guid, BranchReadinessView> checklists) =>
        IssuesOf(branch.BranchId, branch.CoverPhotoId is not null, checklists);

    private static IOrderedQueryable<Venue> Order<TKey>(
        IQueryable<Venue> venues,
        Expression<Func<Venue, TKey>> key,
        bool descending) =>
        descending ? venues.OrderByDescending(key) : venues.OrderBy(key);

    /// <summary>A query parameter, lower-cased and held to the values it accepts.</summary>
    /// <exception cref="ArgumentException">Naming the parameter, with the values it takes.</exception>
    private static string Pick(string? value, string? fallback, IReadOnlyList<string> accepted, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback ?? accepted[0];
        }

        var normalised = value.Trim().ToLowerInvariant();

        return accepted.Contains(normalised, StringComparer.Ordinal)
            ? normalised
            : throw new ArgumentException($"{parameter} is one of: {string.Join(", ", accepted)}.", parameter);
    }

    private sealed class VenueRow
    {
        public Guid VenueId { get; init; }

        public string Name { get; init; } = null!;

        public VenueType Type { get; init; }

        public string Slug { get; init; } = null!;

        public bool IsActive { get; init; }

        public DateTime? SuspendedAtUtc { get; init; }

        public DateTime? DeletedAtUtc { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        public int BranchCount { get; init; }

        public int PaidBranchCount { get; init; }

        public int TableCount { get; init; }

        public int ReviewCount { get; init; }

        public long RatingSum { get; init; }

        public int OpenTabCount { get; init; }
    }

    private sealed class BranchRow
    {
        public Guid BranchId { get; init; }

        public Guid VenueId { get; init; }

        public string Name { get; init; } = null!;

        public SubscriptionTier SubscriptionTier { get; init; }

        public bool IsActive { get; init; }

        public string? District { get; init; }

        public string? Cuisine { get; init; }

        public Guid? CoverPhotoId { get; init; }

        public bool? CoverIsExternal { get; init; }

        public string? CoverThumbnailPath { get; init; }

        public string? CoverCardPath { get; init; }

        public string? CoverFullPath { get; init; }

        public int? CoverWidth { get; init; }

        public int? CoverHeight { get; init; }
    }

    // ---------------------------------------------------------------- helpers

    private void RequirePlatformAdmin(string operation)
    {
        if (actor.Type != ActorType.Staff || actor.Role != StaffRole.PlatformAdmin || actor.StaffMemberId is null)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.PlatformAdmin);
        }
    }

    private Task AuditAsync(string action, AuditTarget target, object changes, CancellationToken cancellationToken) =>
        PlatformAudit.RecordAsync(db, actor, clock, action, target, changes, cancellationToken);

    private static AuditTarget VenueTarget(Venue venue) => new(VenueType_, venue.Id, venue.Name, venue.Id);

    private static AuditTarget BranchTarget(Branch branch) => new(BranchType, branch.Id, branch.Name, branch.VenueId);

    private async Task<Venue> LoadVenueAsync(Guid venueId, CancellationToken cancellationToken) =>
        await db.Venues.FirstOrDefaultAsync(v => v.Id == venueId, cancellationToken)
        ?? throw new KeyNotFoundException($"Venue {venueId} was not found.");

    private async Task<BranchSummary> GetBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(BranchProjection)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException($"Branch {branchId} was just written but cannot be read back.");

    /// <summary>
    /// Commits, turning a slug collision into a message the admin can act on rather than a 500.
    /// Relying on the unique index rather than a check-then-insert is what makes the whole unit of
    /// work roll back together when it fires.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken, string slug)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.VenueSlug))
        {
            db.ChangeTracker.Clear();
            throw new DomainStateException($"A venue with the slug '{slug}' already exists. Choose another.");
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, "IX_Branches_VenueId_Slug"))
        {
            db.ChangeTracker.Clear();
            throw new DomainStateException($"This venue already has a branch with the slug '{slug}'. Choose another.");
        }
    }

    private static Branch NewBranch(Venue venue, CreateBranchCommand command) => new(
        venue,
        command.Name,
        command.Slug,
        command.Address,
        command.Latitude,
        command.Longitude,
        command.TimeZoneId,
        subscriptionTier: command.SubscriptionTier);

    private static BranchSnapshotShape BranchSnapshot(Branch b) => new(
        b.Name, b.Slug, b.Address, b.Latitude, b.Longitude, b.TimeZoneId, b.IsActive, b.SubscriptionTier);

    private sealed record BranchSnapshotShape(
        string Name,
        string Slug,
        string Address,
        double Latitude,
        double Longitude,
        string TimeZoneId,
        bool IsActive,
        SubscriptionTier SubscriptionTier);

    /// <summary>One branch with its counts. Active tables only; tabs open or closing.</summary>
    private Expression<Func<Branch, BranchSummary>> BranchProjection
    {
        get
        {
            var openTabs = db.Tabs.Where(t => t.Status == TabStatus.Open || t.Status == TabStatus.Closing);

            return b => new BranchSummary(
                b.Id,
                b.VenueId,
                b.Name,
                b.Slug,
                b.Address,
                b.Latitude,
                b.Longitude,
                b.TimeZoneId,
                b.IsActive,
                b.SubscriptionTier,
                b.DiningTables.Count(t => t.IsActive),
                openTabs.Count(t => t.BranchId == b.Id),
                b.District);
        }
    }
}
