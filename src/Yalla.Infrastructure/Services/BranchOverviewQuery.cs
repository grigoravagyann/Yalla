using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Messaging;
using Yalla.Application.Overview;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The venue console's Overview: one branch, its service day, in a fixed handful of statements.
/// </summary>
/// <remarks>
/// <para>
/// <b>At most five reads after the guard, however busy the day.</b> The branch with its room and its open
/// tabs (one statement: the table, sitting and tab aggregates are scalar subqueries beside the branch row),
/// its opening hours with the special days around today, the service day's bookings, the tabs closed in the two windows compared, and - only
/// when a confirmed guest has the app - the late nudges the outbox holds for them. Everything else - the
/// counts by status, the load per hour, the late ones - is worked out from those rows in memory, so a busy
/// Saturday costs what a quiet Tuesday does.
/// </para>
/// <para>
/// <b>The service day, not the calendar's.</b> While last night's block is still open past midnight, the
/// branch is still in last night's service; otherwise it is in today's. A booking belongs to the service day
/// it starts in - 00:30 on Sunday inside Saturday's block is Saturday's - so the bookings are read for the
/// service date and the day after, and sorted into service days by the hours on those dates - the weekly ones,
/// or a special day's where one replaces them.
/// </para>
/// <para>
/// <b>A waiting change counts where it is still promised.</b> A confirmed booking moved at short notice is
/// <c>PendingApproval</c> with its confirmed slot kept beside it (<c>Previous*</c>); until the floor answers,
/// that slot is the diner's, so it is counted, charted and listed there, and the slot it asks for is charted
/// as pending.
/// </para>
/// <para>
/// <b>The list is capped, the counts are not.</b> The counts and the chart are about every booking of the
/// day; the list leaves out the cancelled and declined and then carries the first
/// <see cref="BranchReservationList.MaxRows"/>, saying when it was cut.
/// </para>
/// </remarks>
internal sealed class BranchOverviewQuery(YallaDbContext db, IClock clock, IStaffBranchGuard branchGuard) : IBranchOverviewQuery
{
    /// <summary>The chart's hours stop two days from the service date's midnight, whatever a sitting claims.</summary>
    private const int LastChartHour = 47;

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
                GraceMinutes = b.ReservationPolicy.GraceMinutes,
                Tables = db.DiningTables.Count(t => t.BranchId == b.Id && t.IsActive),
                OccupiedTables = db.DiningTables.Count(t => t.BranchId == b.Id && t.IsActive && t.Status == TableStatus.Occupied),
                OutOfServiceTables = db.DiningTables.Count(
                    t => t.BranchId == b.Id && t.IsActive && t.Status == TableStatus.OutOfService),

                // A table out of service seats nobody tonight, so the room is full without it.
                Seats = db.DiningTables
                    .Where(t => t.BranchId == b.Id && t.IsActive && t.Status != TableStatus.OutOfService)
                    .Sum(t => (int?)t.Seats) ?? 0,
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

        // The week and the special days from the day before yesterday to tomorrow: the service date is today
        // or yesterday, and sorting bookings into it asks about the day after and each day's evening before.
        var hours = await OpeningCalendars.ForBranchAsync(
            db, branchId, today.AddDays(-2), today.AddDays(1), cancellationToken);

        var serviceDate = hours.ServiceDateOf(localNow);
        var nextDate = serviceDate.AddDays(1);

        var rows = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branchId
                        && (r.LocalDate == serviceDate
                            || r.LocalDate == nextDate
                            || r.PreviousLocalDate == serviceDate
                            || r.PreviousLocalDate == nextDate))
            .Select(r => new BookingRow
            {
                Id = r.Id,
                Code = r.Code,
                Status = r.Status,
                StartUtc = r.StartUtc,
                EndUtc = r.EndUtc,
                LocalDate = r.LocalDate,
                LocalStartTime = r.LocalStartTime,
                PartySize = r.PartySize,
                GuestName = r.GuestName,
                Table = new TableFacts
                {
                    Id = r.DiningTableId,
                    Label = r.DiningTable.Label,
                    Status = r.DiningTable.Status,
                    RoomName = r.DiningTable.FloorArea == null ? null : r.DiningTable.FloorArea.Name,
                },
                Channel = r.Channel,
                HoldExpiresAtUtc = r.HoldExpiresAtUtc,
                HasAccount = r.DinerUserId != null,

                // The account whose record the venue sees beside the booking: one that proved its number.
                // The same test as the bookings list: an account whose number is proved now (review N2).
                RecordedAccount = r.PhoneUnverified
                                  && !db.DinerUsers.Any(d => d.Id == r.DinerUserId && d.PhoneVerifiedAtUtc != null)
                    ? null
                    : r.DinerUserId,
                CancelledBecause = r.CancelledBecause,
                PreviousStartUtc = r.PreviousStartUtc,
                PreviousEndUtc = r.PreviousEndUtc,
                PreviousLocalDate = r.PreviousLocalDate,
                PreviousLocalStartTime = r.PreviousLocalStartTime,
                PreviousPartySize = r.PreviousPartySize,
                PreviousTable = db.DiningTables
                    .Where(t => t.Id == r.PreviousDiningTableId)
                    .Select(t => new TableFacts
                    {
                        Id = t.Id,
                        Label = t.Label,
                        Status = t.Status,
                        RoomName = t.FloorArea == null ? null : t.FloorArea.Name,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var (entries, askedFor) = ServiceDayBookings(rows, hours, serviceDate);
        var nudges = await LateNudgesAsync(entries, cancellationToken);

        var listed = entries.Where(e => !IsCancelled(e.Status)).ToList();

        // Each guest's record, one grouped statement for the whole list (none when no listed guest has one).
        var records = await GuestRecords.ForAccountsAsync(
            db,
            listed.Take(BranchReservationList.MaxRows).Where(ShowsRecord).Select(e => e.Row.RecordedAccount!.Value),
            nowUtc,
            cancellationToken);

        return new BranchOverview(
            branchId,
            branch.TimeZoneId,
            nowUtc,
            today,
            timeNow,
            serviceDate,
            branch.GraceMinutes,
            HoursNow(hours, localNow, serviceDate),
            Counts(entries, nowUtc),
            new OverviewRoom(branch.PeopleInRoom, branch.OccupiedTables, branch.Tables, branch.OutOfServiceTables),
            new OverviewOpenTabs(branch.OpenTabs, branch.UnpaidAmd),
            await RevenueAsync(branchId, zone, hours, serviceDate, localNow, nowUtc, cancellationToken),
            new OverviewLoad(branch.Seats, Load(entries, askedFor, hours, zone, serviceDate)),
            [.. listed
                .Take(BranchReservationList.MaxRows)
                .Select(e =>
                {
                    var confirmed = e.Status == ReservationStatus.Confirmed;
                    var nudge = confirmed && nudges.TryGetValue(e.Id, out var n) ? n : null;

                    return new OverviewBooking(
                        e.Id,
                        e.Code,
                        e.Status,
                        e.StartUtc,
                        e.EndUtc,
                        e.LocalDate,
                        e.LocalStartTime,
                        e.PartySize,
                        e.GuestName,
                        e.Table.Id,
                        e.Table.Label,
                        e.Table.Status,
                        e.Table.RoomName,
                        e.Channel,
                        e.ChangeRequested,

                        // A hold is the floor keeping a confirmed party's table; once they sit down, or the
                        // booking is anything else, the column is history.
                        confirmed ? e.HoldExpiresAtUtc : null,
                        nudge?.AtUtc,
                        nudge?.Sent,
                        ShowsRecord(e) && records.TryGetValue(e.Row.RecordedAccount!.Value, out var record) ? record : null);
                })],
            listed.Count > BranchReservationList.MaxRows);
    }

    // ---------------------------------------------------------------- the service day

    /// <summary>
    /// Whether a booking starting at <paramref name="date"/> <paramref name="time"/> belongs to the service date:
    /// it starts on it, or after midnight on the next date inside its block running past midnight. A block closing
    /// at 00:00 runs into nothing.
    /// </summary>
    private static bool InServiceDay(OpeningCalendar hours, DateOnly date, TimeOnly time, DateOnly serviceDate) =>
        (date == serviceDate || date == serviceDate.AddDays(1))
        && hours.ServiceDateOf(date.ToDateTime(time)) == serviceDate;

    /// <summary>
    /// The service day's bookings as the screen counts them - each at the slot it holds - and the slots
    /// that confirmed bookings are asking to move to, which only the chart's pending bars show.
    /// </summary>
    private static (List<Entry> Entries, List<Entry> AskedFor) ServiceDayBookings(
        List<BookingRow> rows,
        OpeningCalendar hours,
        DateOnly serviceDate)
    {
        var entries = new List<Entry>();
        var askedFor = new List<Entry>();

        foreach (var row in rows)
        {
            var asked = InServiceDay(hours, row.LocalDate, row.LocalStartTime, serviceDate);

            if (row.Status == ReservationStatus.PendingApproval
                && row.PreviousTable is { } previousTable
                && row.PreviousStartUtc is { } previousStart
                && row.PreviousEndUtc is { } previousEnd
                && row.PreviousLocalDate is { } previousDate
                && row.PreviousLocalStartTime is { } previousTime
                && InServiceDay(hours, previousDate, previousTime, serviceDate))
            {
                // Still promised at its confirmed slot today: counted there, with the change it asks for apart.
                entries.Add(new Entry(
                    row, previousStart, previousEnd, previousDate, previousTime, row.PreviousPartySize ?? row.PartySize,
                    previousTable, ChangeRequested: true));

                if (asked)
                {
                    askedFor.Add(Entry.AsBooked(row));
                }

                continue;
            }

            if (asked)
            {
                entries.Add(Entry.AsBooked(row));
            }
        }

        entries.Sort((a, b) =>
        {
            var byDate = a.LocalDate.CompareTo(b.LocalDate);

            if (byDate != 0)
            {
                return byDate;
            }

            var byTime = a.LocalStartTime.CompareTo(b.LocalStartTime);

            if (byTime != 0)
            {
                return byTime;
            }

            return a.StartUtc != b.StartUtc ? a.StartUtc.CompareTo(b.StartUtc) : a.Id.CompareTo(b.Id);
        });

        return (entries, askedFor);
    }

    // ---------------------------------------------------------------- the pieces

    private static OverviewBookingCounts Counts(List<Entry> entries, DateTime nowUtc)
    {
        int Of(ReservationStatus status) => entries.Count(e => e.Counted == status);

        return new OverviewBookingCounts(
            Total: entries.Count(e => !IsCancelled(e.Status)),
            PendingApproval: Of(ReservationStatus.PendingApproval),
            Confirmed: Of(ReservationStatus.Confirmed),
            Late: entries.Count(e => e.Counted == ReservationStatus.Confirmed && e.StartUtc < nowUtc),
            Seated: Of(ReservationStatus.Seated),
            Completed: Of(ReservationStatus.Completed),
            NoShow: Of(ReservationStatus.NoShow),
            Cancelled: entries.Count(e => IsCancelled(e.Status)),
            CancelledBySystem: entries.Count(e => IsCancelled(e.Status) && e.Row.CancelledBecause is not null),
            People: entries
                .Where(e => IsBooked(e.Counted) || e.Counted == ReservationStatus.PendingApproval)
                .Sum(e => e.PartySize),
            PendingChanges: entries.Count(e => e.ChangeRequested));
    }

    /// <summary>
    /// Open now and until when, or shut and opening when - the same reading of the calendar as
    /// <c>BranchOpenNow</c>, which the diner's listing uses, so the console and the app agree. With the service
    /// date's special day, when it has one.
    /// </summary>
    private static OverviewHours HoursNow(OpeningCalendar hours, DateTime localNow, DateOnly serviceDate)
    {
        var today = DateOnly.FromDateTime(localNow);
        var special = hours.SpecialDayOn(serviceDate) is { } day ? SpecialOpeningDayView.From(day) : null;

        // Today's block that has opened and not closed first, then last night's still running; one that closes
        // on a later date than today reads "(next day)" - a midnight close is "until 00:00".
        if (hours.OpenAt(localNow) is { } open)
        {
            var closesLater = DateOnly.FromDateTime(open.Closes) > today && open.Block.RunsPastMidnight;

            return new OverviewHours(true, open.Block.ClosesAt, closesLater, null, special);
        }

        var next = hours.IntervalsOn(today)
            .Where(i => i.Opens > localNow)
            .Select(i => (TimeOnly?)i.Block.OpensAt)
            .Min();

        return new OverviewHours(false, null, false, next, special);
    }

    /// <summary>
    /// Booked people in each hour of the service day, numbered from the service date's midnight - 24 is
    /// 00:00 on the next date. A sitting counts in every hour it touches, as the occupancy report counts it.
    /// </summary>
    private static List<OverviewLoadHour> Load(
        List<Entry> entries,
        List<Entry> askedFor,
        OpeningCalendar hours,
        BranchZone zone,
        DateOnly serviceDate)
    {
        var people = new int[LastChartHour + 1];
        var pending = new int[LastChartHour + 1];
        var midnight = serviceDate.ToDateTime(TimeOnly.MinValue);
        int? first = null;
        int? last = null;

        void Touch(int hour)
        {
            first = first is { } f ? Math.Min(f, hour) : hour;
            last = last is { } l ? Math.Max(l, hour) : hour;
        }

        void Chart(Entry entry, int[] bars)
        {
            var to = zone.ToLocal(entry.EndUtc);
            var from = ((entry.LocalDate.DayNumber - serviceDate.DayNumber) * 24) + entry.LocalStartTime.Hour;

            for (var hour = Math.Max(from, 0); hour <= LastChartHour && midnight.AddHours(hour) < to; hour++)
            {
                bars[hour] += entry.PartySize;
                Touch(hour);
            }
        }

        foreach (var entry in entries)
        {
            if (IsBooked(entry.Counted))
            {
                Chart(entry, people);
            }
            else if (entry.Counted == ReservationStatus.PendingApproval)
            {
                Chart(entry, pending);
            }
        }

        foreach (var entry in askedFor)
        {
            Chart(entry, pending);
        }

        // The hours the branch is open on the service date: from each block's opening to its last hour,
        // past midnight for one that runs into the next day.
        foreach (var block in hours.BlocksOn(serviceDate))
        {
            var closesMinute = (block.ClosesAt.Hour * 60) + block.ClosesAt.Minute;

            Touch(block.OpensAt.Hour);
            Touch(!block.ClosesNextDay ? (closesMinute - 1) / 60 : closesMinute == 0 ? 23 : 24 + ((closesMinute - 1) / 60));
        }

        if (first is not { } start || last is not { } end)
        {
            return [];
        }

        return [.. Enumerable.Range(start, end - start + 1).Select(h => new OverviewLoadHour(h, people[h], pending[h]))];
    }

    /// <summary>
    /// What closed in the service day so far - from its first opening, or local midnight of the service date
    /// when it has no hours - and in the same service day a week before, for as long on the clock.
    /// </summary>
    private async Task<OverviewRevenue> RevenueAsync(
        Guid branchId,
        BranchZone zone,
        OpeningCalendar hours,
        DateOnly serviceDate,
        DateTime localNow,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var opens = hours.BlocksOn(serviceDate)
            .Select(b => (TimeOnly?)b.OpensAt)
            .Min() ?? TimeOnly.MinValue;

        var lastWeek = serviceDate.AddDays(-7);
        var sinceUtc = StartOf(zone, serviceDate, opens);
        var lastWeekFromUtc = StartOf(zone, lastWeek, opens);

        // As long after the opening on the clock as now is; across a clock change that skipped that time, as
        // long in real time.
        var lastWeekToLocal = lastWeek.ToDateTime(opens) + (localNow - serviceDate.ToDateTime(opens));
        var lastWeekToUtc = zone.TryToUtc(
            DateOnly.FromDateTime(lastWeekToLocal), TimeOnly.FromDateTime(lastWeekToLocal), out var sameTime)
            ? sameTime
            : lastWeekFromUtc + (nowUtc - sinceUtc);

        var closed = await db.Tabs
            .AsNoTracking()
            .Where(t => t.BranchId == branchId
                        && t.Status == TabStatus.Closed
                        && t.Admission == TabAdmission.Admitted
                        && t.ClosedAtUtc != null
                        && ((t.ClosedAtUtc >= sinceUtc && t.ClosedAtUtc <= nowUtc)
                            || (t.ClosedAtUtc >= lastWeekFromUtc && t.ClosedAtUtc <= lastWeekToUtc)))
            .Select(t => new { ClosedAtUtc = t.ClosedAtUtc!.Value, t.TotalAmd })
            .ToListAsync(cancellationToken);

        var todayAmd = closed.Where(t => t.ClosedAtUtc >= sinceUtc).Sum(t => t.TotalAmd);
        var lastWeekAmd = closed.Where(t => t.ClosedAtUtc < sinceUtc).Sum(t => t.TotalAmd);

        return new OverviewRevenue(
            todayAmd,
            lastWeekAmd,
            lastWeek,
            lastWeekAmd <= 0
                ? null
                : Math.Round((todayAmd - lastWeekAmd) * 100.0 / lastWeekAmd, 1, MidpointRounding.AwayFromZero),
            sinceUtc);
    }

    /// <summary>The instant a service day's takings start; local midnight when the opening is skipped by a clock change.</summary>
    private static DateTime StartOf(BranchZone zone, DateOnly date, TimeOnly opens) =>
        zone.TryToUtc(date, opens, out var utc) ? utc : BranchTime.RangeToUtc(date, date, zone.TimeZoneId).FromUtc;

    /// <summary>
    /// The late nudge each confirmed guest with the app has in the outbox: its send time once sent, else when
    /// it is due. A booking moved since carries its start in the key, so the latest one stands.
    /// </summary>
    private async Task<Dictionary<Guid, Nudge>> LateNudgesAsync(List<Entry> entries, CancellationToken cancellationToken)
    {
        var asked = entries.Where(e => e.Status == ReservationStatus.Confirmed && e.HasAccount).ToList();

        if (asked.Count == 0)
        {
            return [];
        }

        var ids = asked.Select(e => e.Id).ToHashSet();
        var fromUtc = asked.Min(e => e.StartUtc);
        var toUtc = asked.Max(e => e.StartUtc).AddDays(1);

        // By type and due time; the key names the booking.
        var messages = await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.Type == OutboxMessageTypes.ReservationLateNudge
                        && m.DeadLetteredAtUtc == null
                        && m.ScheduledForUtc >= fromUtc
                        && m.ScheduledForUtc <= toUtc)
            .Select(m => new { m.IdempotencyKey, m.ScheduledForUtc, m.SentAtUtc })
            .ToListAsync(cancellationToken);

        return messages
            .Select(m => (Id: BookingOf(m.IdempotencyKey), m.ScheduledForUtc, m.SentAtUtc))
            .Where(m => m.Id is { } id && ids.Contains(id))
            .GroupBy(m => m.Id!.Value)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latest = g.OrderByDescending(m => m.ScheduledForUtc).First();

                    return new Nudge(latest.SentAtUtc ?? latest.ScheduledForUtc, latest.SentAtUtc is not null);
                });
    }

    /// <summary>The booking a <c>reservation:{id}:…</c> key is about, or null for any other key.</summary>
    private static Guid? BookingOf(string key)
    {
        var parts = key.Split(':');

        return parts.Length >= 3
               && parts[0] == "reservation"
               && Guid.TryParse(parts[1], CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    /// <summary>A booking the venue has promised a table to and that has not fallen through.</summary>
    private static bool IsBooked(ReservationStatus status) =>
        status is ReservationStatus.Confirmed or ReservationStatus.Seated or ReservationStatus.Completed;

    /// <summary>
    /// Whether the list shows the guest's record beside a booking: a proved account behind it, and still pending or
    /// confirmed (review S3) - the day's sitting, never a finished one.
    /// </summary>
    private static bool ShowsRecord(Entry e) =>
        e.Row.RecordedAccount is not null
        && e.Row.Status is ReservationStatus.PendingApproval or ReservationStatus.Confirmed;

    private static bool IsCancelled(ReservationStatus status) =>
        status is ReservationStatus.CancelledByDiner or ReservationStatus.CancelledByVenue;

    private sealed record Nudge(DateTime AtUtc, bool Sent);

    /// <summary>A booking at the slot the service day counts it at.</summary>
    /// <param name="Row">The booking as read.</param>
    /// <param name="StartUtc">The slot's start.</param>
    /// <param name="EndUtc">The slot's end.</param>
    /// <param name="LocalDate">The slot's calendar date.</param>
    /// <param name="LocalStartTime">The slot's start on the clock.</param>
    /// <param name="PartySize">The party at that slot.</param>
    /// <param name="Table">The slot's table.</param>
    /// <param name="ChangeRequested">Counted at its confirmed slot while a change waits.</param>
    private sealed record Entry(
        BookingRow Row,
        DateTime StartUtc,
        DateTime EndUtc,
        DateOnly LocalDate,
        TimeOnly LocalStartTime,
        int PartySize,
        TableFacts Table,
        bool ChangeRequested)
    {
        public Guid Id => Row.Id;

        public string Code => Row.Code;

        public ReservationStatus Status => Row.Status;

        public string GuestName => Row.GuestName;

        public ReservationChannel Channel => Row.Channel;

        public DateTime? HoldExpiresAtUtc => Row.HoldExpiresAtUtc;

        public bool HasAccount => Row.HasAccount;

        /// <summary>How the counts see it: a waiting change of a confirmed booking is still confirmed.</summary>
        public ReservationStatus Counted => ChangeRequested ? ReservationStatus.Confirmed : Status;

        public static Entry AsBooked(BookingRow row) =>
            new(row, row.StartUtc, row.EndUtc, row.LocalDate, row.LocalStartTime, row.PartySize, row.Table, ChangeRequested: false);
    }

    private sealed class BranchRow
    {
        public string TimeZoneId { get; init; } = null!;

        public int GraceMinutes { get; init; }

        public int Tables { get; init; }

        public int OccupiedTables { get; init; }

        public int OutOfServiceTables { get; init; }

        public int Seats { get; init; }

        public int PeopleInRoom { get; init; }

        public int OpenTabs { get; init; }

        public long UnpaidAmd { get; init; }
    }

    private sealed class TableFacts
    {
        public Guid Id { get; init; }

        public string Label { get; init; } = null!;

        public TableStatus Status { get; init; }

        public string? RoomName { get; init; }
    }

    private sealed class BookingRow
    {
        public Guid Id { get; init; }

        public string Code { get; init; } = null!;

        public ReservationStatus Status { get; init; }

        public DateTime StartUtc { get; init; }

        public DateTime EndUtc { get; init; }

        public DateOnly LocalDate { get; init; }

        public TimeOnly LocalStartTime { get; init; }

        public int PartySize { get; init; }

        public string GuestName { get; init; } = null!;

        public TableFacts Table { get; init; } = null!;

        public ReservationChannel Channel { get; init; }

        public DateTime? HoldExpiresAtUtc { get; init; }

        public bool HasAccount { get; init; }

        /// <summary>The booking's account when it made the booking with a proved number - whose record is shown.</summary>
        public Guid? RecordedAccount { get; init; }

        /// <summary>Set when the platform cancelled it (a guest's no-show block).</summary>
        public CancellationCause? CancelledBecause { get; init; }

        public DateTime? PreviousStartUtc { get; init; }

        public DateTime? PreviousEndUtc { get; init; }

        public DateOnly? PreviousLocalDate { get; init; }

        public TimeOnly? PreviousLocalStartTime { get; init; }

        public int? PreviousPartySize { get; init; }

        public TableFacts? PreviousTable { get; init; }
    }
}
