using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Infrastructure.Persistence;
using static Yalla.Infrastructure.Services.BranchSettingsLookups;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The floor plan as the editor loads it: rooms with their photos, tables, and the plan's revision.
/// </summary>
internal sealed class FloorPlanReads(
    YallaDbContext db,
    IStaffBranchGuard branchGuard,
    FloorPlanTables floorTables)
{
    public async Task<FloorPlanView> GetFloorPlanAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the floor plan", cancellationToken);

        return await ReadFloorPlanAsync(branchId, cancellationToken);
    }

    public async Task<FloorPlanView> ReadFloorPlanAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new { b.Id, b.FloorPlanVersion })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var rooms = await db.FloorAreas
            .AsNoTracking()
            .Where(a => a.BranchId == branchId)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.Name)
            .Select(a => new { a.Id, a.Name, a.DisplayOrder })
            .ToListAsync(cancellationToken);

        var photos = await RoomPhotosAsync(branchId, cancellationToken);

        var areas = rooms
            .Select(a => new FloorAreaView(a.Id, a.Name, a.DisplayOrder, photos.GetValueOrDefault(a.Id, [])))
            .ToList();

        var tables = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.BranchId == branchId)
            .OrderBy(t => t.Label)
            .Select(floorTables.TableProjection)
            .ToListAsync(cancellationToken);

        return new FloorPlanView(branch.Id, areas, tables, VersionOf(branch.FloorPlanVersion));
    }

    /// <summary>Every room's photos at this branch, each room's in its own order.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<PhotoView>>> RoomPhotosAsync(
        Guid branchId, CancellationToken cancellationToken)
    {
        var rows = await db.FloorAreaPhotos
            .AsNoTracking()
            .Where(p => p.FloorArea.BranchId == branchId)
            .OrderBy(p => p.Position)
            .Select(p => new { p.FloorAreaId, p.Photo })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.FloorAreaId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PhotoView>)[.. g.Select(r => PhotoView.From(r.Photo))]);
    }
}
