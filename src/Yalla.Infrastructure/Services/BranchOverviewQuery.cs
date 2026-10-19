using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Overview;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The venue console's Overview: one branch, today, in a fixed handful of statements.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four reads after the guard, however busy the day.</b> The branch with its room and its open tabs
/// (one statement: the table, sitting and tab aggregates are scalar subqueries beside the branch row), its
/// opening hours, today's bookings, and the tabs closed today and on the same day last week. Everything
/// else - the counts by status, the load per hour, the late ones - is worked out from those rows in memory,
/// so a busy Saturday costs what a quiet Tuesday does.
/// </para>
/// <para>
/// <b>Today is the branch's calendar day.</b> A booking's <c>LocalDate</c> is already on that calendar, so
/// today's bookings are an equality on it. The money is counted as the revenue report counts it - a closed
/// tab's total on the day it closed - from the branch's local midnight.
/// </para>
/// <para>
/// <b>Today's bookings are read whole, the list is capped.</b> The counts and the chart are about every
/// booking of the day; the list the screen shows is the first <see cref="BranchReservationList.MaxRows"/>,
/// like every other booking list in the console. A branch's day is small enough to read whole.
/// </para>
/// </remarks>
internal sealed class BranchOverviewQuery(YallaDbContext db, IClock clock, IStaffBranchGuard branchGuard) : IBranchOverviewQuery
{
    public async Task<BranchOverview> GetAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        // From the stored row: an owner or general manager anywhere in their venue, a branch manager at
        // their own branch only, a platform admin anywhere. The route's BranchScoped said the same from
        // the token; this holds for a manager moved since it was issued.
        await branchGuard.RequireAtBranchAsync(branchId, "Read a branch's overview", cancellationToken);

        var nowUtc = clock.UtcNow;

        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new BranchRow
            {
                TimeZoneId = b.TimeZoneId,
                LateNudgeAfterMinutes = b.ReservationPolicy.LateNudgeAfterMinutes,
                Tables = db.DiningTables.Count(t => t.BranchId == b.Id && t.IsActive),
                OccupiedTables = db.DiningTables.Count(t => t.BranchId == b.Id && t.IsActive && t.Status == TableStatus.Occupied),
                OutOfServiceTables = db.DiningTables.Count(
                    t => t.BranchId == b.Id && t.IsActive && t.Status == TableStatus.OutOfService),
                Seats = db.DiningTables.Where(t => t.BranchId == b.Id && t.IsActive).Sum(t => (int?)t.Seats) ?? 0,
                PeopleInRoom = db.TableSessions
                    .Where(s => s.BranchId == b.Id && s.ClosedAtUtc == null)
                    .Sum(s => (int?)s.PartySize) ?? 0,
                OpenTabs = db.Tabs.Count(t => t.BranchId == b.Id && (t.Status == TabStatus.Open || t.Status == TabStatus.Closing)),
                UnpaidAmd = db.Tabs
                    .Where(t => t.BranchId == b.Id && (t.Status == TabStatus.Open || t.Status == TabStatus.Closing))
                    .Sum(t => (long?)t.RemainingAmd) ?? 0L,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var zone = BranchZone.For(branch.TimeZoneId);
        var localNow = zone.ToLocal(nowUtc);
        var today = DateOnly.FromDateTime(localNow);

        // Whole seconds: the screen says 18:42, and a fraction of a tick is not a time anybody reads.
        var timeNow = new TimeOnly(localNow.Hour, localNow.Minute, localNow.Second);

        var hours = await db.OpeningHours
            .AsNoTracking()
            .Where(h => h.BranchId == branchId)
            .Select(h => new HoursRow(h.Day, h.OpensAt, h.ClosesAt, h.ClosesNextDay))
            .ToListAsync(cancellationToken);

        var bookings = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branchId && r.LocalDate == today)
            .OrderBy(r => r.LocalStartTime)
            .ThenBy(r => r.StartUtc)
            .ThenBy(r => r.Id)
            .Select(r => new BookingRow
            {
                Id = r.Id,
                Code = r.Code,
                Status = r.Status,
                StartUtc = r.StartUtc,
                EndUtc = r.EndUtc,
                LocalStartTime = r.LocalStartTime,
                PartySize = r.PartySize,
                GuestName = r.GuestName,
                TableId = r.DiningTableId,
                TableLabel = r.DiningTable.Label,
                TableStatus = r.DiningTable.Status,
                RoomName = r.DiningTable.FloorArea == null ? null : r.DiningTable.FloorArea.Name,
                Channel = r.Channel,
                DeclinedBecause = r.DeclineReason,
                HoldExpiresAtUtc = r.HoldExpiresAtUtc,
                HasAccount = r.DinerUserId != null,
            })
            .ToListAsync(cancellationToken);

        return new BranchOverview(
            branchId,
            branch.TimeZoneId,
            nowUtc,
            today,
            timeNow,
            HoursNow(hours, localNow),
            Counts(bookings, nowUtc),
            new OverviewRoom(branch.PeopleInRoom, branch.OccupiedTables, branch.Tables, branch.OutOfServiceTables),
            new OverviewOpenTabs(branch.OpenTabs, branch.UnpaidAmd),
            await RevenueAsync(branchId, zone, today, timeNow, nowUtc, cancellationToken),
            new OverviewLoad(branch.Seats, Load(bookings, hours, zone, today)),
            [.. bookings
                .Take(BranchReservationList.MaxRows)
                .Select(b => new OverviewBooking(
                    b.Id,
                    b.Code,
                    b.Status,
                    b.StartUtc,
                    b.EndUtc,
                    b.LocalStartTime,
                    b.PartySize,
                    b.GuestName,
                    b.TableId,
                    b.TableLabel,
                    b.TableStatus,
                    b.RoomName,
                    b.Channel,
                    b.DeclinedBecause,
                    b.HoldExpiresAtUtc,

                    // The nudge is a push to the app, so only somebody with an account gets one - and only
                    // while the booking is confirmed: the handler drops it for anybody already seated.
                    b.HasAccount && b.Status == ReservationStatus.Confirmed
                        ? b.StartUtc.AddMinutes(branch.LateNudgeAfterMinutes)
                        : null))]);
    }

    // ---------------------------------------------------------------- the pieces

    private static OverviewBookingCounts Counts(List<BookingRow> bookings, DateTime nowUtc)
    {
        int Of(ReservationStatus status) => bookings.Count(b => b.Status == status);

        return new OverviewBookingCounts(
            Total: bookings.Count(b => !IsCancelled(b.Status)),
            PendingApproval: Of(ReservationStatus.PendingApproval),
            Confirmed: Of(ReservationStatus.Confirmed),
            Late: bookings.Count(b => b.Status == ReservationStatus.Confirmed && b.StartUtc < nowUtc),
            Seated: Of(ReservationStatus.Seated),
            Completed: Of(ReservationStatus.Completed),
            NoShow: Of(ReservationStatus.NoShow),
            Cancelled: bookings.Count(b => IsCancelled(b.Status)),
            People: bookings
                .Where(b => IsBooked(b.Status) || b.Status == ReservationStatus.PendingApproval)
                .Sum(b => b.PartySize));
    }

    /// <summary>
    /// Open now and until when, or shut and opening when - the same reading of the weekly hours as
    /// <c>BranchOpenNow</c>, which the diner's listing uses, so the console and the app agree.
    /// </summary>
    private static OverviewHours HoursNow(List<HoursRow> hours, DateTime localNow)
    {
        var day = localNow.DayOfWeek;
        var yesterday = day == DayOfWeek.Sunday ? DayOfWeek.Saturday : day - 1;
        var time = TimeOnly.FromDateTime(localNow);

        // Today's block that has opened and not closed - one past midnight has not closed today at all.
        var todays = hours.FirstOrDefault(h =>
            h.Day == day && time >= h.OpensAt && (h.ClosesNextDay || time < h.ClosesAt));

        if (todays is not null)
        {
            return new OverviewHours(true, todays.ClosesAt, todays.ClosesNextDay, null);
        }

        // Last night's block, still running: it closes today, so not "the next day".
        var lastNight = hours.FirstOrDefault(h => h.Day == yesterday && h.ClosesNextDay && time < h.ClosesAt);

        if (lastNight is not null)
        {
            return new OverviewHours(true, lastNight.ClosesAt, false, null);
        }

        var next = hours
            .Where(h => h.Day == day && h.OpensAt > time)
            .Select(h => (TimeOnly?)h.OpensAt)
            .Min();

        return new OverviewHours(false, null, false, next);
    }

    /// <summary>
    /// Booked people in each local hour of today. A sitting counts in every hour it touches - "how full is
    /// the room at eight" is about who is in it, not who arrives, as the occupancy report counts it.
    /// </summary>
    private static List<OverviewLoadHour> Load(
        List<BookingRow> bookings,
        List<HoursRow> hours,
        BranchZone zone,
        DateOnly today)
    {
        var people = new int[24];
        var pending = new int[24];
        var midnight = today.ToDateTime(TimeOnly.MinValue);
        int? first = null;
        int? last = null;

        void Touch(int hour)
        {
            first = first is { } f ? Math.Min(f, hour) : hour;
            last = last is { } l ? Math.Max(l, hour) : hour;
        }

        foreach (var booking in bookings)
        {
            var booked = IsBooked(booking.Status);

            if (!booked && booking.Status != ReservationStatus.PendingApproval)
            {
                continue;
            }

            var bars = booked ? people : pending;
            var to = zone.ToLocal(booking.EndUtc);

            // From the start's own hour while the sitting still runs, to the end of the day: the small
            // hours of a sitting past midnight are tomorrow's.
            for (var hour = booking.LocalStartTime.Hour; hour < 24 && midnight.AddHours(hour) < to; hour++)
            {
                bars[hour] += booking.PartySize;
                Touch(hour);
            }
        }

        // The hours the branch is open today: from each block's opening to its last hour - the end of the
        // day for one that runs past midnight.
        foreach (var block in hours.Where(h => h.Day == today.DayOfWeek))
        {
            var closesMinute = (block.ClosesAt.Hour * 60) + block.ClosesAt.Minute;

            Touch(block.OpensAt.Hour);
            Touch(block.ClosesNextDay || closesMinute == 0 ? 23 : (closesMinute - 1) / 60);
        }

        if (first is not { } start || last is not { } end)
        {
            return [];
        }

        return [.. Enumerable.Range(start, end - start + 1).Select(h => new OverviewLoadHour(h, people[h], pending[h]))];
    }

    /// <summary>
    /// What closed today until now, and the same weekday a week ago until the same wall-clock time.
    /// </summary>
    private async Task<OverviewRevenue> RevenueAsync(
        Guid branchId,
        BranchZone zone,
        DateOnly today,
        TimeOnly timeNow,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var lastWeek = today.AddDays(-7);
        var (todayFromUtc, _) = BranchTime.RangeToUtc(today, today, zone.TimeZoneId);
        var (lastWeekFromUtc, _) = BranchTime.RangeToUtc(lastWeek, lastWeek, zone.TimeZoneId);

        // The same time on the clock; across a clock change that skipped it, as long after midnight as now is.
        var lastWeekToUtc = zone.TryToUtc(lastWeek, timeNow, out var sameTime)
            ? sameTime
            : lastWeekFromUtc + (nowUtc - todayFromUtc);

        var closed = await db.Tabs
            .AsNoTracking()
            .Where(t => t.BranchId == branchId
                        && t.Status == TabStatus.Closed
                        && t.ClosedAtUtc != null
                        && ((t.ClosedAtUtc >= todayFromUtc && t.ClosedAtUtc <= nowUtc)
                            || (t.ClosedAtUtc >= lastWeekFromUtc && t.ClosedAtUtc <= lastWeekToUtc)))
            .Select(t => new { ClosedAtUtc = t.ClosedAtUtc!.Value, t.TotalAmd })
            .ToListAsync(cancellationToken);

        var todayAmd = closed.Where(t => t.ClosedAtUtc >= todayFromUtc).Sum(t => t.TotalAmd);
        var lastWeekAmd = closed.Where(t => t.ClosedAtUtc < todayFromUtc).Sum(t => t.TotalAmd);

        return new OverviewRevenue(
            todayAmd,
            lastWeekAmd,
            lastWeek,
            lastWeekAmd <= 0
                ? null
                : Math.Round((todayAmd - lastWeekAmd) * 100.0 / lastWeekAmd, 1, MidpointRounding.AwayFromZero));
    }

    /// <summary>A booking the venue has promised a table to and that has not fallen through.</summary>
    private static bool IsBooked(ReservationStatus status) =>
        status is ReservationStatus.Confirmed or ReservationStatus.Seated or ReservationStatus.Completed;

    private static bool IsCancelled(ReservationStatus status) =>
        status is ReservationStatus.CancelledByDiner or ReservationStatus.CancelledByVenue;

    private sealed record HoursRow(DayOfWeek Day, TimeOnly OpensAt, TimeOnly ClosesAt, bool ClosesNextDay);

    private sealed class BranchRow
    {
        public string TimeZoneId { get; init; } = null!;

        public int LateNudgeAfterMinutes { get; init; }

        public int Tables { get; init; }

        public int OccupiedTables { get; init; }

        public int OutOfServiceTables { get; init; }

        public int Seats { get; init; }

        public int PeopleInRoom { get; init; }

        public int OpenTabs { get; init; }

        public long UnpaidAmd { get; init; }
    }

    private sealed class BookingRow
    {
        public Guid Id { get; init; }

        public string Code { get; init; } = null!;

        public ReservationStatus Status { get; init; }

        public DateTime StartUtc { get; init; }

        public DateTime EndUtc { get; init; }

        public TimeOnly LocalStartTime { get; init; }

        public int PartySize { get; init; }

        public string GuestName { get; init; } = null!;

        public Guid TableId { get; init; }

        public string TableLabel { get; init; } = null!;

        public TableStatus TableStatus { get; init; }

        public string? RoomName { get; init; }

        public ReservationChannel Channel { get; init; }

        public DeclineReason? DeclinedBecause { get; init; }

        public DateTime? HoldExpiresAtUtc { get; init; }

        public bool HasAccount { get; init; }
    }
}
