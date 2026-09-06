using Yalla.Domain.Enums;

namespace Yalla.Application.Reports;

/// <summary>
/// A date range in the <b>branch's local dates</b>, inclusive of both ends.
/// </summary>
/// <remarks>
/// <para>
/// Local, always. "Yesterday's covers" is a statement in the venue's own clock, and a report that
/// takes UTC midnight as the day boundary is wrong by four hours in Yerevan - every day, quietly,
/// and in the direction that moves the late sittings, which is where the interesting numbers are.
/// </para>
/// <para>
/// Capped by <see cref="ReportRange.MaxDays"/>. A range nobody meant to ask for is a table scan
/// nobody meant to run, and a refusal that names the limit is more use than a request that times out.
/// </para>
/// </remarks>
/// <param name="FromLocalDate">First day included.</param>
/// <param name="ToLocalDate">Last day included.</param>
public sealed record ReportRange(DateOnly FromLocalDate, DateOnly ToLocalDate)
{
    /// <summary>
    /// The longest range any report accepts.
    /// </summary>
    /// <remarks>
    /// A year and a day: long enough to compare this December with last, short enough that the
    /// query stays a range seek. Everything here is queried live - see <c>docs/reports.md</c> for
    /// when that stops being true.
    /// </remarks>
    public const int MaxDays = 366;

    /// <summary>How many local days the range covers.</summary>
    public int Days => ToLocalDate.DayNumber - FromLocalDate.DayNumber + 1;

    /// <summary>
    /// The equivalent range immediately before this one, for the comparison every numeric report
    /// carries.
    /// </summary>
    /// <remarks>
    /// <b>The same number of days, ending the day before this one starts.</b> "Covers last night"
    /// means nothing without "and the Friday before" - and a comparison against a different-length
    /// period would be worse than none, because it looks like a like-for-like.
    /// <para>
    /// Counted in days rather than in calendar months, so a range that crosses a month boundary
    /// compares against exactly as many days as it contains rather than against a February.
    /// </para>
    /// </remarks>
    public ReportRange Previous() => new(FromLocalDate.AddDays(-Days), FromLocalDate.AddDays(-1));
}

/// <summary>A range that no report will run.</summary>
public sealed class ReportRangeTooLongException(int requestedDays)
    : ArgumentOutOfRangeException(
        "range",
        requestedDays,
        $"A report may cover at most {ReportRange.MaxDays} days; {requestedDays} were asked for. "
        + "Narrow the range, or ask for the months separately.")
{
    public int RequestedDays { get; } = requestedDays;

    public int MaxDays => ReportRange.MaxDays;
}

/// <summary>
/// One number, beside what it was over the previous equivalent period.
/// </summary>
/// <param name="Value">This period.</param>
/// <param name="Previous">
/// The same measure over the equivalent period immediately before. Null when there is no prior
/// data at all - which is <b>not</b> the same as zero, and a client must render it differently: a
/// venue's first week has no previous week, and "down 100%" would be a lie about it.
/// </param>
public sealed record Compared(decimal Value, decimal? Previous)
{
    /// <summary>The change as a fraction, or null when there is nothing to compare against.</summary>
    /// <remarks>
    /// Null rather than infinity when the previous period was zero: growth from nothing has no
    /// percentage, and every client that has tried to render one has printed something absurd.
    /// </remarks>
    public decimal? ChangeFraction => Previous is null or 0m ? null : (Value - Previous.Value) / Previous.Value;
}

/// <summary>Which branches a report covers, and over what.</summary>
/// <param name="BranchIds">One branch, or every branch of a venue for an owner's rollup.</param>
/// <param name="Range">The local date range.</param>
/// <param name="TimeZoneId">The zone the local dates were interpreted in.</param>
public sealed record ReportScope(IReadOnlyList<Guid> BranchIds, ReportRange Range, string TimeZoneId);

// ------------------------------------------------------------------ occupancy

/// <param name="Hour">Local hour of day, 0 to 23.</param>
/// <param name="Sessions">Sittings that were in progress during that hour.</param>
public sealed record HourBucket(int Hour, int Sessions);

/// <param name="Day">Local day of week.</param>
/// <param name="Sessions">Sittings that started on that weekday.</param>
public sealed record WeekdayBucket(DayOfWeek Day, int Sessions);

/// <param name="UpToMinutes">The top of this bucket, in minutes. The last one is open-ended.</param>
/// <param name="Sessions">How many sittings fell in it.</param>
public sealed record DurationBucket(int UpToMinutes, int Sessions);

/// <summary>
/// How long parties actually stay, against what the policy assumes.
/// </summary>
/// <remarks>
/// <b>A distribution, not an average, and this is the most useful number in the whole set.</b> A
/// cafe whose policy says 120 minutes and whose real median is 165 is losing bookings it does not
/// know about - it is refusing a 20:00 sitting because it believes the 18:00 one ends at 20:00, and
/// half the time it does not. An average hides that: a room with a fast lunch and a slow dinner
/// averages to something plausible and describes neither service.
/// </remarks>
/// <param name="PolicyTurnTimeMinutes">What the branch's policy assumes.</param>
/// <param name="MedianMinutes">The middle sitting. Null when nothing closed in the period.</param>
/// <param name="P90Minutes">Nine sittings in ten finish inside this.</param>
/// <param name="Buckets">The whole shape, so the tail is visible rather than summarised away.</param>
/// <param name="OverPolicyFraction">
/// The fraction of sittings that ran past the policy's turn time. The single number to put next to
/// the chart, and the one that says whether the policy needs changing.
/// </param>
/// <param name="ClosedSessions">How many sittings the distribution was computed from.</param>
public sealed record TurnTimeDistribution(
    int PolicyTurnTimeMinutes,
    int? MedianMinutes,
    int? P90Minutes,
    IReadOnlyList<DurationBucket> Buckets,
    decimal OverPolicyFraction,
    int ClosedSessions);

/// <param name="Scope">What was asked for.</param>
/// <param name="Sessions">Sittings that started in the period.</param>
/// <param name="ByHour">In progress by local hour of day.</param>
/// <param name="ByWeekday">Started, by local weekday.</param>
/// <param name="TurnTime">Actual against policy.</param>
/// <param name="SeatsFilled">Seats occupied across every sitting.</param>
/// <param name="SeatsAvailable">Seats the room has, times the days in the period.</param>
/// <param name="WalkIns">Sittings with no booking behind them.</param>
/// <param name="FromReservations">Sittings seated against a booking.</param>
public sealed record OccupancyReport(
    ReportScope Scope,
    Compared Sessions,
    IReadOnlyList<HourBucket> ByHour,
    IReadOnlyList<WeekdayBucket> ByWeekday,
    TurnTimeDistribution TurnTime,
    Compared SeatsFilled,
    long SeatsAvailable,
    Compared WalkIns,
    Compared FromReservations);

// ------------------------------------------------------------------ reservations

/// <param name="UpToHours">Top of this bucket, in hours before the sitting.</param>
/// <param name="Bookings">How many were made that far ahead.</param>
public sealed record LeadTimeBucket(int UpToHours, int Bookings);

/// <param name="Scope">What was asked for.</param>
/// <param name="Booked">Bookings made for a sitting in the period.</param>
/// <param name="Seated">Those the party turned up for.</param>
/// <param name="Cancelled">Those called off, by anyone.</param>
/// <param name="NoShow">Those where grace ran out and the table was released.</param>
/// <param name="NoShowRate">No-shows over bookings. The number the whole reminder feature exists to move.</param>
/// <param name="CancellationRate">Cancellations over bookings.</param>
/// <param name="LateCancellations">
/// Called off inside the branch's own cancellation deadline. Distinguished from an ordinary
/// cancellation because a table given up in time is resold and one given up at 19:50 is not.
/// </param>
/// <param name="LeadTime">How far ahead people book - what <c>BookingWindowDays</c> should be set from.</param>
/// <param name="WebBookingsWithoutAnApp">
/// Booked from the public page by somebody with no registered device. <b>These people cannot be
/// reminded</b>: no app means no push channel, so the reminder, the late nudge and one-tap cancel -
/// the entire no-show story - do not reach them. This is the number that decides whether an SMS or
/// Telegram channel is worth paying for. See <c>docs/reports.md</c>.
/// </param>
public sealed record ReservationReport(
    ReportScope Scope,
    Compared Booked,
    Compared Seated,
    Compared Cancelled,
    Compared NoShow,
    Compared NoShowRate,
    Compared CancellationRate,
    Compared LateCancellations,
    IReadOnlyList<LeadTimeBucket> LeadTime,
    Compared WebBookingsWithoutAnApp);

// ------------------------------------------------------------------ revenue

/// <param name="LocalDate">The branch's own day.</param>
/// <param name="RevenueAmd">Taken that day, in whole dram.</param>
/// <param name="Tabs">How many tabs closed.</param>
public sealed record DailyRevenue(DateOnly LocalDate, long RevenueAmd, int Tabs);

/// <param name="Hour">Local hour of day.</param>
/// <param name="RevenueAmd">Taken in that hour, in whole dram.</param>
public sealed record HourlyRevenue(int Hour, long RevenueAmd);

/// <param name="Reason">What the manager typed.</param>
/// <param name="Kind">1 Discount, 2 Comp.</param>
/// <param name="StaffMemberId">Who authorised it.</param>
/// <param name="StaffName">Their name.</param>
/// <param name="Count">How many.</param>
/// <param name="TotalAmd">What they came to.</param>
public sealed record AdjustmentLine(
    string Reason,
    AdjustmentKind Kind,
    Guid StaffMemberId,
    string StaffName,
    int Count,
    long TotalAmd);

/// <param name="Scope">What was asked for.</param>
/// <param name="TotalAmd">Taken in the period.</param>
/// <param name="ServiceChargeAmd">Of which service charge.</param>
/// <param name="AverageTabAmd">Per tab.</param>
/// <param name="AveragePerHeadAmd">Per person on a tab. A different question from per tab, and the one a menu is priced against.</param>
/// <param name="ByDay">Day by day, in local dates.</param>
/// <param name="ByHour">Hour by hour, in local hours.</param>
/// <param name="CashAmd">Taken in cash.</param>
/// <param name="InAppAmd">Taken in the app. Zero until there is a wallet rail; the column is here so the day it exists nothing has to change.</param>
/// <param name="Adjustments">Comps and discounts, with who authorised them.</param>
/// <param name="AdjustmentsTotalAmd">What they came to altogether.</param>
public sealed record RevenueReport(
    ReportScope Scope,
    Compared TotalAmd,
    Compared ServiceChargeAmd,
    Compared AverageTabAmd,
    Compared AveragePerHeadAmd,
    IReadOnlyList<DailyRevenue> ByDay,
    IReadOnlyList<HourlyRevenue> ByHour,
    Compared CashAmd,
    Compared InAppAmd,
    IReadOnlyList<AdjustmentLine> Adjustments,
    Compared AdjustmentsTotalAmd);

// ------------------------------------------------------------------ menu

/// <param name="MenuItemId">The item.</param>
/// <param name="Name">Its name now, not the snapshot on the line.</param>
/// <param name="CategoryName">Which section of the menu it sits in.</param>
/// <param name="Quantity">How many were ordered.</param>
/// <param name="RevenueAmd">What they came to.</param>
public sealed record MenuItemPerformance(
    Guid MenuItemId,
    string Name,
    string CategoryName,
    int Quantity,
    long RevenueAmd);

/// <param name="MenuItemId">The item.</param>
/// <param name="Name">Its name.</param>
/// <param name="Reason">Why it was taken off, in the waiter's words.</param>
/// <param name="Count">How many times.</param>
public sealed record VoidLine(Guid MenuItemId, string Name, string Reason, int Count);

/// <param name="Scope">What was asked for.</param>
/// <param name="TopByCount">Most ordered.</param>
/// <param name="TopByRevenue">Most valuable, which is frequently a different list.</param>
/// <param name="NeverOrdered">
/// <b>The report that changes behaviour.</b> Items with no line in the period at all. A dish nobody
/// orders is inventory that spoils and menu space that could sell something else, and no venue
/// knows which those are - they are, by definition, the ones nobody mentions.
/// </param>
/// <param name="Voids">Voided lines by item and reason. A dish voided often is mis-described or badly made.</param>
public sealed record MenuReport(
    ReportScope Scope,
    IReadOnlyList<MenuItemPerformance> TopByCount,
    IReadOnlyList<MenuItemPerformance> TopByRevenue,
    IReadOnlyList<MenuItemPerformance> NeverOrdered,
    IReadOnlyList<VoidLine> Voids);

// ------------------------------------------------------------------ staff

/// <summary>
/// What the venue needs to run the floor, and nothing more.
/// </summary>
/// <remarks>
/// <b>Aggregated per branch, deliberately not per waiter.</b> The audit log has the data and an
/// owner will ask for it. A ranked list of employees that renders itself every morning is a
/// different product from a report somebody can request - see <c>docs/reports.md</c> for the
/// argument, which is short and is about what software should decide on a venue's behalf.
/// </remarks>
/// <param name="Scope">What was asked for.</param>
/// <param name="OrdersEntered">Orders keyed in by staff, across the branch.</param>
/// <param name="TablesTurned">Sittings closed, across the branch.</param>
/// <param name="ActiveStaff">How many people worked the period.</param>
public sealed record StaffReport(
    ReportScope Scope,
    Compared OrdersEntered,
    Compared TablesTurned,
    int ActiveStaff);

/// <summary>
/// The five report groups, queried live against the operational tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>Manager or owner within scope.</b> A manager reports on their own branch; an owner may ask
/// for any branch of their venue, and for a rollup across all of them.
/// </para>
/// <para>
/// Everything is already logged and none of it was queryable: <c>TableSession</c> has every
/// occupancy with its source, party size and duration; <c>TableStateChange</c> has every transition
/// with an actor; <c>Reservation</c> has every booking and its outcome; the tab tables have the
/// money and the items. These read them.
/// </para>
/// </remarks>
public interface IReportQuery
{
    Task<OccupancyReport> GetOccupancyAsync(
        ReportRequest request, CancellationToken cancellationToken = default);

    Task<ReservationReport> GetReservationsAsync(
        ReportRequest request, CancellationToken cancellationToken = default);

    Task<RevenueReport> GetRevenueAsync(
        ReportRequest request, CancellationToken cancellationToken = default);

    Task<MenuReport> GetMenuAsync(
        ReportRequest request, CancellationToken cancellationToken = default);

    Task<StaffReport> GetStaffAsync(
        ReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The SQL the slowest report executes, for diagnostics.
    /// </summary>
    /// <remarks>
    /// The menu report is the slowest: it joins lines to orders to tabs across the range and then
    /// anti-joins the branch's whole menu to find what never sold. Exposed for the same reason the
    /// availability query exposes its own - "is this still a range seek" is worth being able to
    /// answer without attaching a profiler.
    /// </remarks>
    string GetMenuReportSql(ReportRequest request);
}

/// <summary>
/// One report request: which branch, over what, and whether to roll up a venue.
/// </summary>
/// <param name="BranchId">The branch. For a rollup, any branch of the venue.</param>
/// <param name="FromLocalDate">First local day included.</param>
/// <param name="ToLocalDate">Last local day included.</param>
/// <param name="RollUpVenue">
/// Owner only: report across every branch of this branch's venue rather than this branch alone.
/// </param>
public sealed record ReportRequest(
    Guid BranchId,
    DateOnly FromLocalDate,
    DateOnly ToLocalDate,
    bool RollUpVenue = false)
{
    /// <summary>The range, checked against the cap.</summary>
    /// <exception cref="ReportRangeTooLongException">Longer than <see cref="ReportRange.MaxDays"/>.</exception>
    public ReportRange Range()
    {
        if (ToLocalDate < FromLocalDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ToLocalDate), ToLocalDate, "A report range must end on or after it starts.");
        }

        var range = new ReportRange(FromLocalDate, ToLocalDate);

        return range.Days > ReportRange.MaxDays
            ? throw new ReportRangeTooLongException(range.Days)
            : range;
    }
}
