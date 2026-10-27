using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The platform's feature switches, asked by a branch id when the service has no <see cref="Branch"/> loaded. Where it
/// has one, <see cref="Branch.RequireAllowed"/> answers the same question without a read. See
/// <c>docs/branch-features.md</c> for what each feature gates and what runs on.
/// </summary>
internal static class BranchFeatureGate
{
    /// <summary>The branch's allowed bits: one column, read untracked. 0 for a branch that does not exist.</summary>
    public static async Task<int> BitsAsync(YallaDbContext db, Guid branchId, CancellationToken cancellationToken) =>
        await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => (int?)b.AllowedFeatureBits)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

    /// <summary>Whether the platform allows <paramref name="feature"/> at the branch.</summary>
    public static async Task<bool> AllowsAsync(
        YallaDbContext db, Guid branchId, BranchFeature feature, CancellationToken cancellationToken) =>
        BranchFeatures.Has(await BitsAsync(db, branchId, cancellationToken), feature);

    /// <summary>Refuses a new action that needs <paramref name="feature"/> at a branch where it is blocked.</summary>
    /// <exception cref="FeatureNotEnabledException">409 <c>feature-not-enabled</c>, context <c>feature</c> and <c>branchId</c>.</exception>
    public static async Task RequireAsync(
        YallaDbContext db, Guid branchId, BranchFeature feature, CancellationToken cancellationToken) =>
        BranchFeatures.Require(await BitsAsync(db, branchId, cancellationToken), feature, branchId);
}
