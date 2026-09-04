using Yalla.Application.Floor;

namespace Yalla.Application.Abstractions;

/// <summary>Reads the derived floor state for a branch.</summary>
public interface IFloorQuery
{
    /// <summary>
    /// The whole floor for one branch, in a single round trip. Null when the branch does not exist.
    /// </summary>
    Task<BranchFloorState?> GetFloorAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The SQL this query executes, for diagnostics. Exposed because "is the floor query still one
    /// statement?" is a question worth being able to answer without attaching a profiler.
    /// </summary>
    string GetFloorQuerySql(Guid branchId);
}
