using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reads a branch's <see cref="OpeningCalendar"/> - its week and its special days in a window - in one statement.
/// </summary>
/// <remarks>
/// The one loader the readers share, so none of them reads the weekly rows on their own again: a reader that
/// did would answer New Year's Day from Thursday's hours. The window is the reader's to choose, and the calendar
/// refuses a date outside it.
/// </remarks>
internal static class OpeningCalendars
{
    /// <summary>One branch's calendar with the special days from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <returns>The calendar; an empty week and no special days for a branch that does not exist.</returns>
    public static async Task<OpeningCalendar> ForBranchAsync(
        YallaDbContext db,
        Guid branchId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var rows = await Read(db, db.Branches.Where(b => b.Id == branchId), from, to)
            .FirstOrDefaultAsync(cancellationToken);

        return rows is null ? new OpeningCalendar([], [], from, to) : rows.ToCalendar(from, to);
    }

    /// <summary>
    /// The branch's service day at <paramref name="nowUtc"/> - its own date, or yesterday while last night's block still
    /// runs past midnight - with the local wall clock it was read at.
    /// </summary>
    public static async Task<(DateOnly ServiceDate, DateTime LocalNow)> ServiceDayAsync(
        YallaDbContext db,
        Guid branchId,
        string timeZoneId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var localNow = BranchTime.ToLocal(nowUtc, timeZoneId);
        var today = DateOnly.FromDateTime(localNow);
        var calendar = await ForBranchAsync(db, branchId, today.AddDays(-1), today, cancellationToken);

        return (calendar.ServiceDateOf(localNow), localNow);
    }

    /// <summary>One branch's special days from <paramref name="from"/> to <paramref name="to"/>, alone, in one statement.</summary>
    public static async Task<IReadOnlyList<SpecialDay>> SpecialDaysAsync(
        YallaDbContext db,
        Guid branchId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var rows = await db.SpecialOpeningDays
            .AsNoTracking()
            .Where(d => d.BranchId == branchId && d.Date >= from && d.Date <= to)
            .Select(d => new SpecialRow
            {
                Date = d.Date,
                Note = d.Note,
                Blocks = d.Blocks
                    .Select(x => new WeeklyRow(DayOfWeek.Sunday, x.OpensAt, x.ClosesAt, x.ClosesNextDay))
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r.ToSpecialDay())];
    }

    /// <summary>
    /// The special days a diner is shown: from the branch's service date - today, or yesterday while last night's
    /// block is still running past midnight - through <see cref="SpecialOpeningDayRules.PublicLookaheadDays"/> after
    /// today, earliest first.
    /// </summary>
    /// <remarks>
    /// From the service date and not the calendar date, so at 01:00 on New Year's Day the page still names the Eve
    /// whose 18:00-03:00 it is open in, rather than showing "closed today" beside "open now".
    /// </remarks>
    public static async Task<IReadOnlyList<SpecialOpeningDayView>> UpcomingAsync(
        YallaDbContext db,
        Guid branchId,
        string timeZoneId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var localNow = BranchTime.ToLocal(nowUtc, timeZoneId);
        var today = DateOnly.FromDateTime(localNow);
        var calendar = await ForBranchAsync(
            db, branchId, today.AddDays(-1), today.AddDays(SpecialOpeningDayRules.PublicLookaheadDays), cancellationToken);
        var from = calendar.ServiceDateOf(localNow);

        return [.. calendar.SpecialDays.Where(d => d.Date >= from).Select(SpecialOpeningDayView.From)];
    }

    /// <summary>
    /// Each branch's zone and calendar, for <paramref name="branchIds"/> or - when null - every branch with any
    /// weekly hours or a special day in the window.
    /// </summary>
    public static async Task<List<(Guid BranchId, string TimeZoneId, OpeningCalendar Calendar)>> ForBranchesAsync(
        YallaDbContext db,
        IReadOnlyList<Guid>? branchIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var branches = branchIds is null
            ? db.Branches.Where(b => b.OpeningHours.Any()
                                     || b.SpecialOpeningDays.Any(d => d.Date >= from && d.Date <= to))
            : db.Branches.Where(b => branchIds.Contains(b.Id));

        var rows = await Read(db, branches, from, to).ToListAsync(cancellationToken);

        return [.. rows.Select(r => (r.BranchId, r.TimeZoneId, r.ToCalendar(from, to)))];
    }

    /// <summary>The week and the window's special days beside the branch row - one statement however many branches.</summary>
    private static IQueryable<CalendarRow> Read(YallaDbContext db, IQueryable<Branch> branches, DateOnly from, DateOnly to) =>
        branches
            .AsNoTracking()
            .Select(b => new CalendarRow
            {
                BranchId = b.Id,
                TimeZoneId = b.TimeZoneId,
                Weekly = b.OpeningHours
                    .Select(h => new WeeklyRow(h.Day, h.OpensAt, h.ClosesAt, h.ClosesNextDay))
                    .ToList(),
                Special = db.SpecialOpeningDays
                    .Where(d => d.BranchId == b.Id && d.Date >= from && d.Date <= to)
                    .Select(d => new SpecialRow
                    {
                        Date = d.Date,
                        Note = d.Note,
                        Blocks = d.Blocks
                            .Select(x => new WeeklyRow(DayOfWeek.Sunday, x.OpensAt, x.ClosesAt, x.ClosesNextDay))
                            .ToList(),
                    })
                    .ToList(),
            });

    private sealed class CalendarRow
    {
        public Guid BranchId { get; init; }

        public string TimeZoneId { get; init; } = null!;

        public List<WeeklyRow> Weekly { get; init; } = [];

        public List<SpecialRow> Special { get; init; } = [];

        public OpeningCalendar ToCalendar(DateOnly from, DateOnly to) =>
            new(
                Weekly.Select(w => (w.Day, w.ToBlock())),
                Special.Select(s => s.ToSpecialDay()),
                from,
                to);
    }

    private sealed class SpecialRow
    {
        public DateOnly Date { get; init; }

        public string? Note { get; init; }

        public List<WeeklyRow> Blocks { get; init; } = [];

        public SpecialDay ToSpecialDay() => new(Date, Note, [.. Blocks.Select(b => b.ToBlock()).OrderBy(b => b.OpensAt)]);
    }

    private sealed record WeeklyRow(DayOfWeek Day, TimeOnly OpensAt, TimeOnly ClosesAt, bool ClosesNextDay)
    {
        public OpeningBlock ToBlock() => new(OpensAt, ClosesAt, ClosesNextDay);
    }
}
