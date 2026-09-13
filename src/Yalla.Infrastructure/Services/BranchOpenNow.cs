using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Which branches are inside an opening block at an instant, decided in each one's own zone.
/// </summary>
/// <remarks>
/// One implementation for the browse list, the branch page and the diner app's listing routes, so
/// the web page and the app cannot disagree about whether a place is open. The hours are wall-clock
/// values, so this converts <i>now</i> into the branch's local time rather than the hours into UTC -
/// which would shift a venue's opening by an hour across a daylight-saving change.
/// </remarks>
internal static class BranchOpenNow
{
    /// <param name="db">The context.</param>
    /// <param name="branchIds">The branches to decide, or null for every branch that has hours.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<HashSet<Guid>> ComputeAsync(
        YallaDbContext db,
        IReadOnlyList<Guid>? branchIds,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var hours = db.OpeningHours.AsNoTracking();

        if (branchIds is not null)
        {
            hours = hours.Where(h => branchIds.Contains(h.BranchId));
        }

        var rows = await hours
            .Select(h => new
            {
                h.BranchId,
                h.Branch.TimeZoneId,
                h.Day,
                h.OpensAt,
                h.ClosesAt,
                h.ClosesNextDay,
            })
            .ToListAsync(cancellationToken);

        var open = new HashSet<Guid>();

        foreach (var group in rows.GroupBy(r => (r.BranchId, r.TimeZoneId)))
        {
            var local = BranchTime.ToLocal(nowUtc, group.Key.TimeZoneId);
            var today = TimeOnly.FromDateTime(local);
            var yesterdayDay = local.DayOfWeek == DayOfWeek.Sunday ? DayOfWeek.Saturday : local.DayOfWeek - 1;

            var isOpen = group.Any(h =>
                (h.Day == local.DayOfWeek && !h.ClosesNextDay && today >= h.OpensAt && today < h.ClosesAt)
                || (h.Day == local.DayOfWeek && h.ClosesNextDay && today >= h.OpensAt)

                // A block that ran past midnight is still the previous day's block until it closes.
                || (h.Day == yesterdayDay && h.ClosesNextDay && today < h.ClosesAt));

            if (isOpen)
            {
                open.Add(group.Key.BranchId);
            }
        }

        return open;
    }
}
