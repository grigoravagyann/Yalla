using Yalla.Application.Reservations;

namespace Yalla.Application.Abstractions;

/// <summary>Reads which tables a branch can offer for one slot.</summary>
public interface IAvailabilityQuery
{
    /// <summary>
    /// Every table in the branch, answered for one requested slot, in a single round trip. Null
    /// when the branch does not exist.
    /// </summary>
    Task<BranchAvailability?> GetAvailabilityAsync(
        AvailabilityRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The SQL this query executes, for diagnostics.
    /// </summary>
    /// <remarks>
    /// Exposed for the same reason as the floor query's: "is this still one statement, and is the
    /// upcoming-bookings join still a left join?" are questions worth being able to answer without
    /// attaching a profiler. An accidental inner join here silently drops the tables with no
    /// bookings - the most available tables in the room, and the ones a diner most wants to see.
    /// </remarks>
    string GetAvailabilityQuerySql(AvailabilityRequest request);

    /// <summary>
    /// Every half-hour start time on one day, each marked available or not for the party, in a
    /// single round trip. Null when the branch does not exist.
    /// </summary>
    /// <remarks>
    /// The same statement as <see cref="GetAvailabilityAsync"/>, and the same per-table rule applied
    /// to each time, so a time listed as free is one the single-slot read offers a table at.
    /// </remarks>
    Task<BranchDaySlots?> GetDaySlotsAsync(
        DaySlotsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>The SQL <see cref="GetDaySlotsAsync"/> executes, for diagnostics.</summary>
    string GetDaySlotsQuerySql(DaySlotsRequest request);
}
