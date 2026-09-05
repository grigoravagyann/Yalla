using Yalla.Application.Floor;
using Yalla.Application.Tables;

namespace Yalla.Application.Abstractions;

/// <summary>Reads the derived floor state for a branch.</summary>
public interface IFloorQuery
{
    /// <summary>
    /// The whole floor for one branch <b>as at <paramref name="atUtc"/></b>, in a single round
    /// trip. Null when the branch does not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instant is explicit and required. It used to be an implicit "now" read from the clock,
    /// which quietly made every future query wrong: physical status answers "who is sitting here at
    /// this moment", so a request about tomorrow evening reported a table as occupied because
    /// somebody was at it tonight, and the diner app dimmed tables that were free.
    /// </para>
    /// <para>
    /// Within <see cref="Tables.TableStateProjection.PhysicalStatusHorizon"/> of now, physical
    /// status is authoritative. Beyond it, only the sitting projected forward by the branch's turn
    /// time and the reservation overlay decide.
    /// </para>
    /// </remarks>
    Task<BranchFloorState?> GetFloorStateAsync(
        Guid branchId,
        DateTime atUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The SQL this query executes, for diagnostics. Exposed because "is the floor query still one
    /// statement?" is a question worth being able to answer without attaching a profiler.
    /// </summary>
    string GetFloorQuerySql(Guid branchId, DateTime atUtc);

    /// <summary>
    /// A page of the branch's change stream, everything after <paramref name="afterSequence"/>.
    /// </summary>
    /// <remarks>
    /// Capped, and the page says whether more remain. A client reconnecting after a dropped
    /// connection catches up through this rather than refetching the whole floor; the realtime hub,
    /// when it arrives, will push the same entries.
    /// </remarks>
    Task<BranchChangePage?> GetChangesAsync(
        Guid branchId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}
