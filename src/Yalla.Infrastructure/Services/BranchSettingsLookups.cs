using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// What every settings part shares: the branch row, the branch write lock, and the wire form of a revision.
/// </summary>
internal sealed class BranchSettingsLookups(YallaDbContext db)
{
    public async Task<Branch> LoadBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
        ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

    /// <summary>
    /// Opens a transaction and takes an update lock on the branch row, for the writers that change
    /// the plan and move its version: the plan replace, the room add, rename and delete, and the
    /// table delete - and the special days' replace. See <see cref="BranchWriteLock"/>.
    /// </summary>
    public Task<BranchWriteLock> LockBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        BranchWriteLock.AcquireAsync(db, branchId, cancellationToken);

    /// <summary>Saves and commits inside the branch lock, answering a lock wait as a concurrent update.</summary>
    public static Task SaveUnderLockAsync(BranchWriteLock writeLock, Guid branchId, CancellationToken cancellationToken) =>
        writeLock.SaveAsync(branchId, cancellationToken);

    /// <summary>The wire form of a plan revision. Opaque to clients.</summary>
    public static string VersionOf(int floorPlanVersion) =>
        floorPlanVersion.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the version the editor sent is the one stored. Anything unreadable is stale.</summary>
    public static bool IsVersion(string expected, int current) =>
        int.TryParse(expected.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed == current;
}
