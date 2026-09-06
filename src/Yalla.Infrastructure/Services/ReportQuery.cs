using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Reports;
using Yalla.Domain.Enums;
using Yalla.Domain.Menus;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The five report groups, read live off the operational tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live queries with indexes, not a rollup pipeline.</b> At pilot scale - twenty venues, a few
/// thousand sittings a month - a range seek over an indexed column answers in milliseconds, and a
/// rollup pipeline is a second copy of the truth that can be wrong, be stale, or need backfilling
/// after every schema change. <c>docs/reports.md</c> says at what point that stops being the right
/// trade and what would be rolled up first.
/// </para>
/// <para>
/// <b>Every range is converted from the branch's local dates.</b> A report for "yesterday" that
/// used UTC boundaries is wrong by four hours in Yerevan, every day, and in the direction that
/// moves the late sittings into the wrong day - which is where the interesting numbers are. The
/// conversion happens once, in <see cref="BranchTime"/>, and every method here takes its interval
/// from it.
/// </para>
/// <para>
/// <b>Grouping by local hour and local day happens in memory.</b> SQL Server cannot apply an IANA
/// zone, and <c>AT TIME ZONE</c> takes a Windows zone name that would then have to be mapped - so
/// the rows come back with their UTC instants and are bucketed after materialising. The row count
/// is bounded by the range cap, which is what makes that affordable.
/// </para>
/// </remarks>
internal sealed class ReportQuery(YallaDbContext db, ICurrentActor actor) : IReportQuery
{
    /// <summary>How many items a "top items" list returns. A menu screen, not a data dump.</summary>
    private const int TopItems = 20;

    /// <summary>Turn-time buckets, in minutes. The last is open-ended.</summary>
    private static readonly int[] TurnTimeBuckets = [30, 45, 60, 75, 90, 105, 120, 150, 180, 240, int.MaxValue];

    /// <summary>Lead-time buckets, in hours before the sitting. The last is open-ended.</summary>
    private static readonly int[] LeadTimeBuckets = [1, 3, 6, 12, 24, 48, 24 * 7, 24 * 30, int.MaxValue];

    // ------------------------------------------------------------ occupancy

    public async Task<OccupancyReport> GetOccupancyAsync(
        ReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(request, cancellationToken);

        var current = await SessionsAsync(scope, scope.Range, cancellationToken);
        var previous = await SessionsAsync(scope, scope.Range.Previous(), cancellationToken);

        var policyTurnTime = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == request.BranchId)
            .Select(b => b.ReservationPolicy.TurnTimeMinutes)
            .FirstAsync(cancellationToken);

        var seatCount = await db.DiningTables
            .AsNoTracking()
            .Where(t => scope.BranchIds.Contains(t.BranchId) && t.IsActive)
            .SumAsync(t => (long?)t.Seats, cancellationToken) ?? 0L;

        return new OccupancyReport(
            scope.AsScope(),
            Compare(current.Count, previous.Count),
            ByHour(current, scope.TimeZoneId),
            ByWeekday(current, scope.TimeZoneId),
            TurnTime(current, policyTurnTime),
            Compare(current.Sum(s => (long)s.PartySize), previous.Sum(s => (long)s.PartySize)),
            SeatsAvailable: seatCount * scope.Range.Days,
            Compare(
                current.Count(s => s.Source == TableSessionSource.WalkIn),
                previous.Count(s => s.Source == TableSessionSource.WalkIn)),
            Compare(
                current.Count(s => s.Source == TableSessionSource.Reservation),
                previous.Count(s => s.Source == TableSessionSource.Reservation)));
    }

    private sealed record SessionRow(
        DateTime SeatedAtUtc,
        DateTime? ClosedAtUtc,
        int PartySize,
        TableSessionSource Source);

    private async Task<List<SessionRow>> SessionsAsync(
        Scope scope,
        ReportRange range,
        CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(range.FromLocalDate, range.ToLocalDate, scope.TimeZoneId);

        return await db.TableSessions
            .AsNoTracking()
            .Where(s => scope.BranchIds.Contains(s.BranchId) && s.SeatedAtUtc >= fromUtc && s.SeatedAtUtc < toUtc)
            .Select(s => new SessionRow(s.SeatedAtUtc, s.ClosedAtUtc, s.PartySize, s.Source))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Sittings in progress during each local hour.
    /// </summary>
    /// <remarks>
    /// A sitting spans hours, so it is counted in every hour it touches rather than only the one it
    /// started in - "how busy is the room at eight" is a question about occupancy, not about
    /// arrivals, and counting arrivals makes a restaurant look empty at exactly its busiest hour.
    /// An open sitting is counted to the end of the range.
    /// </remarks>
    private static IReadOnlyList<HourBucket> ByHour(IReadOnlyList<SessionRow> sessions, string timeZoneId)
    {
        var counts = new int[24];

        foreach (var session in sessions)
        {
            var from = BranchTime.ToLocal(session.SeatedAtUtc, timeZoneId);
            var to = session.ClosedAtUtc is { } closed ? BranchTime.ToLocal(closed, timeZoneId) : from;

            // Capped so an open sitting somebody forgot to close does not paint the whole clock.
            var hours = Math.Clamp((int)Math.Floor((to - from).TotalHours), 0, 23);

            for (var offset = 0; offset <= hours; offset++)
            {
                counts[(from.Hour + offset) % 24]++;
            }
        }

        return [.. Enumerable.Range(0, 24).Select(h => new HourBucket(h, counts[h]))];
    }

    private static IReadOnlyList<WeekdayBucket> ByWeekday(IReadOnlyList<SessionRow> sessions, string timeZoneId)
    {
        var counts = new int[7];

        foreach (var session in sessions)
        {
            counts[(int)BranchTime.ToLocal(session.SeatedAtUtc, timeZoneId).DayOfWeek]++;
        }

        return
        [
            .. Enumerable.Range(0, 7).Select(d => new WeekdayBucket((DayOfWeek)d, counts[d])),
        ];
    }

    private static TurnTimeDistribution TurnTime(IReadOnlyList<SessionRow> sessions, int policyMinutes)
    {
        var durations = sessions
            .Where(s => s.ClosedAtUtc is not null)
            .Select(s => (int)(s.ClosedAtUtc!.Value - s.SeatedAtUtc).TotalMinutes)
            .Where(m => m >= 0)
            .OrderBy(m => m)
            .ToList();

        var buckets = TurnTimeBuckets
            .Select((top, index) => new DurationBucket(
                top,
                durations.Count(d => d <= top && (index == 0 || d > TurnTimeBuckets[index - 1]))))
            .ToList();

        return new TurnTimeDistribution(
            policyMinutes,
            Percentile(durations, 0.50m),
            Percentile(durations, 0.90m),
            buckets,
            durations.Count == 0 ? 0m : (decimal)durations.Count(d => d > policyMinutes) / durations.Count,
            durations.Count);
    }

    /// <summary>The nearest-rank percentile of an already-sorted list.</summary>
    /// <remarks>
    /// Nearest-rank rather than interpolated: these are minutes somebody sat at a table, and an
    /// interpolated 137.4 implies a precision the underlying clock does not have.
    /// </remarks>
    private static int? Percentile(IReadOnlyList<int> sorted, decimal fraction)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;

        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    // ------------------------------------------------------------ reservations

    public async Task<ReservationReport> GetReservationsAsync(
        ReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(request, cancellationToken);

        var current = await ReservationsAsync(scope, scope.Range, cancellationToken);
        var previous = await ReservationsAsync(scope, scope.Range.Previous(), cancellationToken);

        var cancellationDeadline = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == request.BranchId)
            .Select(b => b.ReservationPolicy.CancellationDeadlineMinutes)
            .FirstAsync(cancellationToken);

        return new ReservationReport(
            scope.AsScope(),
            Compare(current.Count, previous.Count),
            Compare(Seated(current), Seated(previous)),
            Compare(Cancelled(current), Cancelled(previous)),
            Compare(NoShows(current), NoShows(previous)),
            Compare(Rate(NoShows(current), current.Count), Rate(NoShows(previous), previous.Count)),
            Compare(Rate(Cancelled(current), current.Count), Rate(Cancelled(previous), previous.Count)),
            Compare(LateCancellations(current, cancellationDeadline), LateCancellations(previous, cancellationDeadline)),
            LeadTime(current),
            Compare(Unreachable(current), Unreachable(previous)));

        static int Seated(List<ReservationRow> rows) => rows.Count(r => r.Status == ReservationStatus.Seated
                                                                        || r.Status == ReservationStatus.Completed);

        static int Cancelled(List<ReservationRow> rows) => rows.Count(r =>
            r.Status is ReservationStatus.CancelledByDiner or ReservationStatus.CancelledByVenue);

        static int NoShows(List<ReservationRow> rows) => rows.Count(r => r.Status == ReservationStatus.NoShow);

        // Called off inside the branch's own deadline. A table given up in time is resold; one given
        // up at 19:50 for a 20:00 sitting is not, and lumping the two together hides which is which.
        static int LateCancellations(List<ReservationRow> rows, int deadlineMinutes) => rows.Count(r =>
            r.Status is ReservationStatus.CancelledByDiner or ReservationStatus.CancelledByVenue
            && r.CancelledAtUtc is { } cancelled
            && (r.StartUtc - cancelled).TotalMinutes < deadlineMinutes);

        // Booked from the web by somebody with no registered device: the reminder cannot reach
        // them. See ReservationChannel and docs/reports.md.
        static int Unreachable(List<ReservationRow> rows) =>
            rows.Count(r => r.Channel == ReservationChannel.Web && !r.HasLiveDevice);
    }

    private sealed record ReservationRow(
        DateTime StartUtc,
        DateTime CreatedAtUtc,
        DateTime? CancelledAtUtc,
        ReservationStatus Status,
        ReservationChannel Channel,
        bool HasLiveDevice);

    private async Task<List<ReservationRow>> ReservationsAsync(
        Scope scope,
        ReportRange range,
        CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(range.FromLocalDate, range.ToLocalDate, scope.TimeZoneId);

        return await db.Reservations
            .AsNoTracking()
            .Where(r => scope.BranchIds.Contains(r.BranchId) && r.StartUtc >= fromUtc && r.StartUtc < toUtc)
            .Select(r => new ReservationRow(
                r.StartUtc,
                r.CreatedAtUtc,
                r.CancelledAtUtc,
                r.Status,
                r.Channel,

                // Whether the diner can be pushed to at all. Checked now rather than stored at
                // booking time, so somebody who installs the app later stops being counted as
                // unreachable - which is the behaviour change the number exists to detect.
                r.DinerUserId != null
                && db.DinerDevices.Any(d => d.DinerUserId == r.DinerUserId && d.RevokedAtUtc == null)))
            .ToListAsync(cancellationToken);
    }

    private static IReadOnlyList<LeadTimeBucket> LeadTime(IReadOnlyList<ReservationRow> rows)
    {
        var hours = rows
            .Select(r => (int)Math.Max(0d, (r.StartUtc - r.CreatedAtUtc).TotalHours))
            .ToList();

        return
        [
            .. LeadTimeBuckets.Select((top, index) => new LeadTimeBucket(
                top,
                hours.Count(h => h <= top && (index == 0 || h > LeadTimeBuckets[index - 1])))),
        ];
    }

    // ------------------------------------------------------------ revenue

    public async Task<RevenueReport> GetRevenueAsync(
        ReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(request, cancellationToken);

        var current = await TabsAsync(scope, scope.Range, cancellationToken);
        var previous = await TabsAsync(scope, scope.Range.Previous(), cancellationToken);
        var adjustments = await AdjustmentsAsync(scope, scope.Range, cancellationToken);
        var previousAdjustments = await AdjustmentsAsync(scope, scope.Range.Previous(), cancellationToken);

        return new RevenueReport(
            scope.AsScope(),
            Compare(current.Sum(t => t.TotalAmd), previous.Sum(t => t.TotalAmd)),
            Compare(current.Sum(t => t.ServiceChargeAmd), previous.Sum(t => t.ServiceChargeAmd)),
            Compare(Average(current.Select(t => t.TotalAmd)), Average(previous.Select(t => t.TotalAmd))),

            // Per head, not per tab. A table of six spending 30,000 and a couple spending 10,000 are
            // the same average tab and completely different rooms, and a menu is priced per head.
            Compare(PerHead(current), PerHead(previous)),
            ByDay(current, scope.TimeZoneId),
            ByHourRevenue(current, scope.TimeZoneId),
            Compare(current.Sum(t => t.CashAmd), previous.Sum(t => t.CashAmd)),
            Compare(current.Sum(t => t.InAppAmd), previous.Sum(t => t.InAppAmd)),
            adjustments,
            Compare(adjustments.Sum(a => a.TotalAmd), previousAdjustments.Sum(a => a.TotalAmd)));

        static decimal Average(IEnumerable<long> values)
        {
            var list = values.ToList();

            return list.Count == 0 ? 0m : Math.Round((decimal)list.Sum() / list.Count, 0);
        }

        static decimal PerHead(List<TabRow> rows)
        {
            var heads = rows.Sum(t => t.PartySize);

            return heads == 0 ? 0m : Math.Round((decimal)rows.Sum(t => t.TotalAmd) / heads, 0);
        }
    }

    private sealed record TabRow(
        DateTime ClosedAtUtc,
        long TotalAmd,
        long ServiceChargeAmd,
        long CashAmd,
        long InAppAmd,
        int PartySize);

    /// <summary>
    /// Tabs that closed inside the range.
    /// </summary>
    /// <remarks>
    /// Counted on close rather than on open, because a tab is revenue when it settles and a sitting
    /// that starts at 23:30 and pays at 00:40 belongs to the night it was taken - which is the day
    /// the venue counted its drawer.
    /// </remarks>
    private async Task<List<TabRow>> TabsAsync(Scope scope, ReportRange range, CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(range.FromLocalDate, range.ToLocalDate, scope.TimeZoneId);

        return await db.Tabs
            .AsNoTracking()
            .Where(t => scope.BranchIds.Contains(t.BranchId)
                        && t.ClosedAtUtc != null
                        && t.ClosedAtUtc >= fromUtc
                        && t.ClosedAtUtc < toUtc
                        && t.Status == TabStatus.Closed)
            .Select(t => new TabRow(
                t.ClosedAtUtc!.Value,
                t.TotalAmd,
                t.ServiceChargeAmd,
                t.Payments
                    .Where(p => p.Status == PaymentStatus.Succeeded && p.Method == PaymentMethod.Cash)
                    .Sum(p => (long?)p.AmountAmd) ?? 0L,

                // Everything that is not cash. Zero until there is a wallet rail, and the column is
                // here so the day one exists no report has to change shape.
                t.Payments
                    .Where(p => p.Status == PaymentStatus.Succeeded && p.Method != PaymentMethod.Cash)
                    .Sum(p => (long?)p.AmountAmd) ?? 0L,
                t.TableSession.PartySize))
            .ToListAsync(cancellationToken);
    }

    private static IReadOnlyList<DailyRevenue> ByDay(IReadOnlyList<TabRow> tabs, string timeZoneId) =>
    [
        .. tabs
            .GroupBy(t => DateOnly.FromDateTime(BranchTime.ToLocal(t.ClosedAtUtc, timeZoneId)))
            .OrderBy(g => g.Key)
            .Select(g => new DailyRevenue(g.Key, g.Sum(t => t.TotalAmd), g.Count())),
    ];

    private static IReadOnlyList<HourlyRevenue> ByHourRevenue(IReadOnlyList<TabRow> tabs, string timeZoneId)
    {
        var totals = new long[24];

        foreach (var tab in tabs)
        {
            totals[BranchTime.ToLocal(tab.ClosedAtUtc, timeZoneId).Hour] += tab.TotalAmd;
        }

        return [.. Enumerable.Range(0, 24).Select(h => new HourlyRevenue(h, totals[h]))];
    }

    private async Task<List<AdjustmentLine>> AdjustmentsAsync(
        Scope scope,
        ReportRange range,
        CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(range.FromLocalDate, range.ToLocalDate, scope.TimeZoneId);

        // Grouped by who authorised it and why. An owner asking "what did we give away last month"
        // is asking both halves, and the manager-only rule on adjustments exists so this report can
        // answer the first.
        var rows = await db.TabAdjustments
            .AsNoTracking()
            .Where(a => scope.BranchIds.Contains(a.Tab.BranchId)
                        && a.CreatedAtUtc >= fromUtc
                        && a.CreatedAtUtc < toUtc
                        && a.VoidedAtUtc == null)
            .Select(a => new
            {
                a.Reason,
                a.Kind,
                a.CreatedByStaffId,
                StaffName = a.CreatedByStaff.FullName,
                a.Percent,
                a.AmountAmd,
                LineTotal = a.TabOrderLineId == null
                    ? a.Tab.SubtotalAmd
                    : a.TabOrderLine!.UnitPriceAmdSnapshot * a.TabOrderLine.Quantity,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .GroupBy(a => (a.Reason, a.Kind, a.CreatedByStaffId, a.StaffName))
                .Select(g => new AdjustmentLine(
                    g.Key.Reason,
                    g.Key.Kind,
                    g.Key.CreatedByStaffId,
                    g.Key.StaffName,
                    g.Count(),

                    // The same clamp and rounding the bill itself used, so what the report says was
                    // given away matches what the diner was actually charged.
                    g.Sum(a => Domain.Tabs.TabAdjustment.ReductionFor(a.Percent, a.AmountAmd, a.LineTotal))))
                .OrderByDescending(a => a.TotalAmd),
        ];
    }

    // ------------------------------------------------------------ menu

    public async Task<MenuReport> GetMenuAsync(
        ReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(request, cancellationToken);
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(
            scope.Range.FromLocalDate, scope.Range.ToLocalDate, scope.TimeZoneId);

        var sold = await db.TabOrderLines
            .AsNoTracking()
            .Where(l => scope.BranchIds.Contains(l.TabOrder.Tab.BranchId)
                        && l.TabOrder.PlacedAtUtc >= fromUtc
                        && l.TabOrder.PlacedAtUtc < toUtc
                        && l.VoidedAtUtc == null)
            .GroupBy(l => new { l.MenuItemId, l.MenuItem.Name, CategoryName = l.MenuItem.MenuCategory.Name })
            .Select(g => new MenuItemPerformance(
                g.Key.MenuItemId,
                g.Key.Name,
                g.Key.CategoryName,
                g.Sum(l => l.Quantity),
                g.Sum(l => (long)l.Quantity * l.UnitPriceAmdSnapshot)))
            .ToListAsync(cancellationToken);

        var soldIds = sold.Select(s => s.MenuItemId).ToHashSet();

        // The report that changes behaviour. Every item on the branch's menu with no unvoided line
        // in the period - which is not the same as "no line ever", and is why this is computed as
        // the difference rather than by a NOT EXISTS over all of history.
        var everything = await db.MenuItems
            .AsNoTracking()
            .Where(i => scope.BranchIds.Contains(i.MenuCategory.BranchId))
            .Select(i => new MenuItemPerformance(
                i.Id, i.Name, i.MenuCategory.Name, 0, 0L))
            .ToListAsync(cancellationToken);

        var voids = await db.TabOrderLines
            .AsNoTracking()
            .Where(l => scope.BranchIds.Contains(l.TabOrder.Tab.BranchId)
                        && l.TabOrder.PlacedAtUtc >= fromUtc
                        && l.TabOrder.PlacedAtUtc < toUtc
                        && l.VoidedAtUtc != null)
            .GroupBy(l => new { l.MenuItemId, l.MenuItem.Name, l.VoidReason })
            .Select(g => new VoidLine(
                g.Key.MenuItemId,
                g.Key.Name,
                g.Key.VoidReason ?? "(no reason given)",
                g.Count()))
            .ToListAsync(cancellationToken);

        return new MenuReport(
            scope.AsScope(),
            [.. sold.OrderByDescending(i => i.Quantity).ThenBy(i => i.Name).Take(TopItems)],
            [.. sold.OrderByDescending(i => i.RevenueAmd).ThenBy(i => i.Name).Take(TopItems)],
            [.. everything.Where(i => !soldIds.Contains(i.MenuItemId)).OrderBy(i => i.CategoryName).ThenBy(i => i.Name)],
            [.. voids.OrderByDescending(v => v.Count).ThenBy(v => v.Name)]);
    }

    public string GetMenuReportSql(ReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var range = request.Range();

        // The zone is not known without a round trip, and this is a diagnostic: UTC boundaries give
        // the same plan shape, which is what the caller is here to read.
        var fromUtc = range.FromLocalDate.ToDateTime(TimeOnly.MinValue);
        var toUtc = range.ToLocalDate.AddDays(1).ToDateTime(TimeOnly.MinValue);

        return db.TabOrderLines
            .AsNoTracking()
            .Where(l => l.TabOrder.Tab.BranchId == request.BranchId
                        && l.TabOrder.PlacedAtUtc >= fromUtc
                        && l.TabOrder.PlacedAtUtc < toUtc
                        && l.VoidedAtUtc == null)
            .GroupBy(l => new { l.MenuItemId, l.MenuItem.Name, CategoryName = l.MenuItem.MenuCategory.Name })
            .Select(g => new
            {
                g.Key.MenuItemId,
                g.Key.Name,
                g.Key.CategoryName,
                Quantity = g.Sum(l => l.Quantity),
                RevenueAmd = g.Sum(l => (long)l.Quantity * l.UnitPriceAmdSnapshot),
            })
            .ToQueryString();
    }

    // ------------------------------------------------------------ staff

    public async Task<StaffReport> GetStaffAsync(
        ReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveAsync(request, cancellationToken);

        var orders = await OrdersEnteredAsync(scope, scope.Range, cancellationToken);
        var previousOrders = await OrdersEnteredAsync(scope, scope.Range.Previous(), cancellationToken);
        var turned = await TablesTurnedAsync(scope, scope.Range, cancellationToken);
        var previousTurned = await TablesTurnedAsync(scope, scope.Range.Previous(), cancellationToken);

        var activeStaff = await db.StaffMembers
            .AsNoTracking()
            .CountAsync(
                s => s.IsActive
                     && s.Role != StaffRole.PlatformAdmin
                     && s.BranchId != null
                     && scope.BranchIds.Contains(s.BranchId.Value),
                cancellationToken);

        return new StaffReport(
            scope.AsScope(),
            Compare(orders, previousOrders),
            Compare(turned, previousTurned),
            activeStaff);
    }

    private async Task<int> OrdersEnteredAsync(Scope scope, ReportRange range, CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(range.FromLocalDate, range.ToLocalDate, scope.TimeZoneId);

        // Aggregated, and grouped by nothing. The staff member who keyed each order is on the row and
        // is deliberately not read - see StaffReport.
        return await db.TabOrders
            .AsNoTracking()
            .CountAsync(
                o => scope.BranchIds.Contains(o.Tab.BranchId)
                     && o.PlacedByStaffId != null
                     && o.PlacedAtUtc >= fromUtc
                     && o.PlacedAtUtc < toUtc,
                cancellationToken);
    }

    private async Task<int> TablesTurnedAsync(Scope scope, ReportRange range, CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = BranchTime.RangeToUtc(range.FromLocalDate, range.ToLocalDate, scope.TimeZoneId);

        return await db.TableSessions
            .AsNoTracking()
            .CountAsync(
                s => scope.BranchIds.Contains(s.BranchId)
                     && s.ClosedAtUtc != null
                     && s.ClosedAtUtc >= fromUtc
                     && s.ClosedAtUtc < toUtc,
                cancellationToken);
    }

    // ------------------------------------------------------------ scope and arithmetic

    private sealed record Scope(IReadOnlyList<Guid> BranchIds, ReportRange Range, string TimeZoneId)
    {
        public ReportScope AsScope() => new(BranchIds, Range, TimeZoneId);
    }

    /// <summary>
    /// Which branches this request covers, and in whose clock.
    /// </summary>
    /// <remarks>
    /// A rollup uses the <i>addressed</i> branch's zone for the whole venue. A chain with branches
    /// in two zones would need a decision about which day boundary a rollup uses, and picking the
    /// one the caller named is the only answer that is not arbitrary. Every Yalla venue is in
    /// Yerevan today, so the case is theoretical - but a silent choice would not stay visible.
    /// </remarks>
    private async Task<Scope> ResolveAsync(ReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var range = request.Range();

        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == request.BranchId)
            .Select(b => new { b.Id, b.VenueId, b.TimeZoneId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {request.BranchId} was not found.");

        if (!request.RollUpVenue)
        {
            return new Scope([branch.Id], range, branch.TimeZoneId);
        }

        // A rollup crosses branches, so the branch-scoped policy at the endpoint no longer covers
        // it: a manager scoped to one branch would otherwise widen their own reach by setting a
        // query parameter. An owner is venue-scoped by construction and a platform admin passes
        // every scope check by role, so those two are the whole answer.
        if (actor.Role is not (StaffRole.Owner or StaffRole.PlatformAdmin))
        {
            throw new Domain.Staff.StaffPermissionException(
                "Reporting across a venue's branches", actor.Role, StaffRole.Owner);
        }

        var siblings = await db.Branches
            .AsNoTracking()
            .Where(b => b.VenueId == branch.VenueId)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        return new Scope(siblings, range, branch.TimeZoneId);
    }

    private static Compared Compare(decimal value, decimal? previous) => new(value, previous);

    /// <summary>A rate, or zero when there is nothing to take a rate of.</summary>
    private static decimal Rate(int part, int whole) =>
        whole == 0 ? 0m : Math.Round((decimal)part / whole, 4);
}
