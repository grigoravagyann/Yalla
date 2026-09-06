using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
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

        PlatformAudit.Record(db, actor, clock, "venue.create", VenueType_, venue.Id, new
        {
            after = new { venue.Name, venue.Type, venue.Slug },
        });

        PlatformAudit.Record(db, actor, clock, "branch.create", BranchType, branch.Id, new
        {
            venueId = venue.Id,
            after = BranchSnapshot(branch),
        });

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

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var venues = db.Venues.AsNoTracking().AsQueryable();

        if (!query.IncludeDeleted)
        {
            venues = venues.Where(v => v.DeletedAtUtc == null);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            venues = venues.Where(v => v.Name.Contains(term) || v.Slug.Contains(term));
        }

        var total = await venues.CountAsync(cancellationToken);

        var items = await venues
            .OrderBy(v => v.Name)
            .ThenBy(v => v.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(SummaryProjection)
            .ToListAsync(cancellationToken);

        return new PagedResult<VenueSummary>(items, page, pageSize, total);
    }

    public async Task<VenueDetail> GetVenueAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Read venue");

        var summary = await db.Venues
            .AsNoTracking()
            .Where(v => v.Id == venueId)
            .Select(SummaryProjection)
            .FirstOrDefaultAsync(cancellationToken)
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

        PlatformAudit.Record(db, actor, clock, "venue.update", VenueType_, venue.Id, new
        {
            before,
            after = new { venue.Name, venue.Type, venue.Slug, venue.IsActive },
        });

        await SaveAsync(cancellationToken, venue.Slug);

        return await GetVenueAsync(venue.Id, cancellationToken);
    }

    public async Task<VenueDetail> SuspendVenueAsync(Guid venueId, CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Suspend venue");

        var venue = await LoadVenueAsync(venueId, cancellationToken);
        var nowUtc = clock.UtcNow;

        venue.Suspend(nowUtc);

        PlatformAudit.Record(db, actor, clock, "venue.suspend", VenueType_, venue.Id, new
        {
            suspendedAtUtc = venue.SuspendedAtUtc,
        });

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

        PlatformAudit.Record(db, actor, clock, "venue.reactivate", VenueType_, venue.Id, new
        {
            wasSuspendedAtUtc = wasSuspendedAt,
        });

        await db.SaveChangesAsync(cancellationToken);

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

        PlatformAudit.Record(db, actor, clock, "venue.delete", VenueType_, venue.Id, new
        {
            deletedAtUtc = venue.DeletedAtUtc,
            snapshot = new { venue.Name, venue.Type, venue.Slug, branchCount = branchIds.Count },
        });

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

        PlatformAudit.Record(db, actor, clock, "branch.create", BranchType, branch.Id, new
        {
            venueId = venue.Id,
            after = BranchSnapshot(branch),
        });

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

        if (command.FloorWidth is not null || command.FloorHeight is not null)
        {
            branch.ResizeFloor(command.FloorWidth ?? branch.FloorWidth, command.FloorHeight ?? branch.FloorHeight);
        }

        if (command.IsActive is { } active)
        {
            branch.SetActive(active);
        }

        if (command.SubscriptionTier is { } tier)
        {
            branch.SetSubscriptionTier(tier);
        }

        PlatformAudit.Record(db, actor, clock, "branch.update", BranchType, branch.Id, new
        {
            before,
            after = BranchSnapshot(branch),
        });

        await db.SaveChangesAsync(cancellationToken);

        if (before.SubscriptionTier != branch.SubscriptionTier)
        {
            logger.LogWarning(
                "Platform admin {StaffMemberId} moved branch {BranchId} from {From} to {To}.",
                actor.StaffMemberId, branch.Id, before.SubscriptionTier, branch.SubscriptionTier);
        }

        return await GetBranchAsync(branch.Id, cancellationToken);
    }

    // ---------------------------------------------------------------- helpers

    private void RequirePlatformAdmin(string operation)
    {
        if (actor.Type != ActorType.Staff || actor.Role != StaffRole.PlatformAdmin || actor.StaffMemberId is null)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.PlatformAdmin);
        }
    }

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
        command.FloorWidth,
        command.FloorHeight,
        subscriptionTier: command.SubscriptionTier);

    private static BranchSnapshotShape BranchSnapshot(Branch b) => new(
        b.Name, b.Slug, b.Address, b.Latitude, b.Longitude, b.TimeZoneId,
        b.FloorWidth, b.FloorHeight, b.IsActive, b.SubscriptionTier);

    private sealed record BranchSnapshotShape(
        string Name,
        string Slug,
        string Address,
        double Latitude,
        double Longitude,
        string TimeZoneId,
        int FloorWidth,
        int FloorHeight,
        bool IsActive,
        SubscriptionTier SubscriptionTier);

    private static readonly System.Linq.Expressions.Expression<Func<Venue, VenueSummary>> SummaryProjection =
        v => new VenueSummary(
            v.Id,
            v.Name,
            v.Type,
            v.Slug,
            v.IsActive,
            v.SuspendedAtUtc != null,
            v.DeletedAtUtc != null,
            v.SuspendedAtUtc,
            v.DeletedAtUtc,
            v.Branches.Count,
            v.Branches.SelectMany(b => b.DiningTables).Count(),
            v.Branches.Count(b => b.SubscriptionTier == SubscriptionTier.Paid),
            v.Branches.Count > 0 && v.Branches.All(b => b.SubscriptionTier == SubscriptionTier.Paid)
                ? SubscriptionTier.Paid
                : SubscriptionTier.Free);

    private static readonly System.Linq.Expressions.Expression<Func<Branch, BranchSummary>> BranchProjection =
        b => new BranchSummary(
            b.Id,
            b.VenueId,
            b.Name,
            b.Slug,
            b.Address,
            b.Latitude,
            b.Longitude,
            b.TimeZoneId,
            b.FloorWidth,
            b.FloorHeight,
            b.IsActive,
            b.SubscriptionTier,
            b.DiningTables.Count);
}
