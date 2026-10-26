using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;
using static Yalla.Infrastructure.Services.BranchSettingsLookups;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The floor plan changed one room or table at a time: adding, renaming and removing a room, deleting a table,
/// and replacing a table's QR code.
/// </summary>
/// <remarks>
/// Each takes the plan's lock and moves the plan's version, as the whole-plan save does, so an editor that loaded
/// the plan before hears 409 rather than saving the change away. The QR code is the exception: it is not part of
/// the plan, and is audited instead.
/// </remarks>
internal sealed class FloorPlanEdits(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IStaffBranchGuard branchGuard,
    ILogger logger,
    BranchSettingsLookups lookups,
    FloorPlanTables floorTables)
{
    public async Task<FloorAreaView> CreateFloorAreaAsync(
        Guid branchId,
        FloorAreaCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Add a floor area", cancellationToken);

        // The plan's lock and the plan's version: a room added here is a change to the plan, and an
        // editor that loaded it before must hear 409 rather than save over it and delete the room.
        await using var writeLock = await lookups.LockBranchAsync(branchId, cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        await RequireUnusedAreaNameAsync(branchId, command.Name, null, cancellationToken);

        var area = new FloorArea(branch.Id, command.Name, command.DisplayOrder);
        db.FloorAreas.Add(area);
        branch.BumpFloorPlanVersion();
        await SaveUnderLockAsync(writeLock, branchId, cancellationToken);

        // A new room has no photos; they are added through the floor-plan save.
        return new FloorAreaView(area.Id, area.Name, area.DisplayOrder, []);
    }

    public async Task<FloorAreaView> UpdateFloorAreaAsync(
        Guid branchId,
        Guid areaId,
        FloorAreaCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Edit a floor area", cancellationToken);

        // As for an added room: a stale editor's save would undo the rename, so it moves the version.
        await using var writeLock = await lookups.LockBranchAsync(branchId, cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        var area = await db.FloorAreas.FirstOrDefaultAsync(a => a.Id == areaId && a.BranchId == branchId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Floor area {areaId} was not found at this branch.");

        await RequireUnusedAreaNameAsync(branchId, command.Name, area.Id, cancellationToken);

        area.Rename(command.Name);
        area.SetDisplayOrder(command.DisplayOrder);
        branch.BumpFloorPlanVersion();
        await SaveUnderLockAsync(writeLock, branchId, cancellationToken);

        var photos = await db.FloorAreaPhotos
            .AsNoTracking()
            .Where(p => p.FloorAreaId == area.Id)
            .OrderBy(p => p.Position)
            .Select(p => p.Photo)
            .ToListAsync(cancellationToken);

        return new FloorAreaView(area.Id, area.Name, area.DisplayOrder, [.. photos.Select(PhotoView.From)]);
    }

    /// <summary>
    /// Refuses a room name another room at the branch already has, ignoring case and spaces at the
    /// ends - the rule the plan save applies to the whole plan.
    /// </summary>
    /// <remarks>
    /// Without it these routes made the very plan the save refuses: two rooms called Terrace, and an
    /// editor that loads them, sends them back, and is told its names repeat with no way to see why
    /// it is stuck. Checked inside the branch lock, so two adds cannot both pass it. A blank name is
    /// left to the room's own guard.
    /// </remarks>
    /// <exception cref="FieldValidationException">The name is taken: field <c>name</c>, bound <c>conflict</c>.</exception>
    private async Task RequireUnusedAreaNameAsync(Guid branchId, string? name, Guid? exceptAreaId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var trimmed = name.Trim();

        var names = await db.FloorAreas
            .AsNoTracking()
            .Where(a => a.BranchId == branchId && a.Id != exceptAreaId)
            .Select(a => a.Name)
            .ToListAsync(cancellationToken);

        if (names.Any(n => string.Equals(n.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new FieldValidationException(new FieldViolation(
                "name",
                $"Area names must be unique within a branch. {trimmed} is already used.",
                FieldBounds.Conflict,
                Value: trimmed));
        }
    }

    public async Task DeleteFloorAreaAsync(Guid branchId, Guid areaId, CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Remove a floor area", cancellationToken);

        // The same lock as the plan save: this changes pins, and a save validating pins against the
        // room's photos must not interleave with the room going. And the same version: an editor that
        // loaded the plan with this room in it would otherwise save it back, photos and pins and all.
        await using var writeLock = await lookups.LockBranchAsync(branchId, cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        var area = await db.FloorAreas.FirstOrDefaultAsync(a => a.Id == areaId && a.BranchId == branchId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Floor area {areaId} was not found at this branch.");

        // Tables stay; they just stop belonging to a room - and come off its photos, which go with
        // it (cascade). A pin on a picture no room shows is a table nobody can find.
        var tables = await db.DiningTables.Where(t => t.FloorAreaId == area.Id).ToListAsync(cancellationToken);

        foreach (var table in tables)
        {
            table.AssignToArea(null);
            table.TakeOffPhoto();
        }

        db.FloorAreas.Remove(area);
        branch.BumpFloorPlanVersion();
        await SaveUnderLockAsync(writeLock, branchId, cancellationToken);
    }

    public async Task<TableDeletionResult> DeleteTableAsync(
        Guid branchId,
        Guid tableId,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Delete a table", cancellationToken);

        // The plan's lock and the plan's version, as for the room routes: either outcome takes the
        // table off the plan, and an editor that loaded the plan before would otherwise save it
        // back - the version still matching - as a new table with a new id and a new QR code.
        await using var writeLock = await lookups.LockBranchAsync(branchId, cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == tableId && t.BranchId == branchId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Table {tableId} was not found at this branch.");

        if (table.Status == TableStatus.Occupied)
        {
            throw new DomainStateException($"Table {table.Label} has a party seated at it. Free it first.");
        }

        var used = await floorTables.TablesWithHistoryAsync([table.Id], cancellationToken);

        if (used.Contains(table.Id))
        {
            // History earns deactivation, but not while a booking is still to come: the booking
            // would be left on a table nobody can see. Move or cancel it first.
            await floorTables.RefuseUpcomingBookingsAsync([table.Id], cancellationToken);

            table.SetActive(false);
            branch.BumpFloorPlanVersion();
            await SaveUnderLockAsync(writeLock, branchId, cancellationToken);

            return new TableDeletionResult(
                table.Id,
                table.Label,
                Deleted: false,
                Deactivated: true,
                $"Table {table.Label} has reservations, seatings or tabs in its history, so it was deactivated rather than deleted. "
                + "Its records are kept; it no longer appears on the floor plan or takes bookings.",
                VersionOf(branch.FloorPlanVersion));
        }

        db.DiningTables.Remove(table);
        branch.BumpFloorPlanVersion();
        await SaveUnderLockAsync(writeLock, branchId, cancellationToken);

        return new TableDeletionResult(
            table.Id, table.Label, Deleted: true, Deactivated: false, $"Table {table.Label} was never used and has been removed.",
            VersionOf(branch.FloorPlanVersion));
    }

    public async Task<FloorTableView> RegenerateQrTokenAsync(Guid tableId, CancellationToken cancellationToken = default)
    {
        var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == tableId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Table {tableId} was not found.");

        await branchGuard.RequireAtBranchAsync(table.BranchId, "Replace a table's QR code", cancellationToken);

        var previous = table.QrToken;
        table.RegenerateQrToken();

        // The printed code on the physical table just stopped working. That is worth a record of
        // who did it and when, whatever their role.
        // The previous token only - it is dead the moment this commits, and recording it is what
        // lets someone answer "which code stopped working". The NEW token is a live credential:
        // anyone who can read it can open a tab on that table anonymously, so it does not go into
        // an append-only log that reporting and backups can reach.
        var venueId = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == table.BranchId)
            .Select(b => b.VenueId)
            .FirstAsync(cancellationToken);

        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            "table.regenerate-qr",
            new AuditTarget("DiningTable", table.Id, table.Label, venueId),
            new
            {
                table.BranchId,
                table.Label,
                previousQrToken = previous,
            },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "QR token regenerated for table {TableId} ({Label}) at branch {BranchId} by staff member {StaffMemberId}.",
            table.Id, table.Label, table.BranchId, actor.StaffMemberId);

        return await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == table.Id)
            .Select(floorTables.TableProjection)
            .FirstAsync(cancellationToken);
    }
}
