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
/// which would shift a venue's opening by an hour across a daylight-saving change. And it asks each
/// branch's <see cref="OpeningCalendar"/>, so a special day - shut for New Year, open late on the Eve - is
/// on every card at once.
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
        // Every zone is within a day of UTC, so the local date is the UTC one or a neighbour, and the question
        // needs that date and the one before: two days either side covers every branch in one read.
        var utcDate = DateOnly.FromDateTime(nowUtc);

        var calendars = await OpeningCalendars.ForBranchesAsync(
            db, branchIds, utcDate.AddDays(-2), utcDate.AddDays(1), cancellationToken);

        // A block that ran past midnight is still the previous day's block until it closes, and a special day
        // replaces its weekday's blocks: both are the calendar's to decide.
        return [.. calendars
            .Where(c => c.Calendar.IsOpenAt(BranchTime.ToLocal(nowUtc, c.TimeZoneId)))
            .Select(c => c.BranchId)];
    }
}
