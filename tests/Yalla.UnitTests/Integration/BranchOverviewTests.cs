using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Application.Overview;
using Yalla.Domain.Enums;
using Yalla.Domain.Messaging;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The venue console's Overview (<c>GET /api/branches/{branchId}/overview</c>) against a real database:
/// today's bookings by status, who is in the room, the open tabs and what they still owe, today's takings
/// against the same weekday last week, booked people per hour against the room's seats, and today's
/// bookings one by one - all on the branch's own calendar.
/// </summary>
/// <remarks>
/// Each test builds its own branch, so nothing another test leaves in the shared database is counted.
/// Asia/Yerevan is four hours ahead of UTC all year, so the instants below read as Yerevan wall clock
/// plus four.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class BranchOverviewTests(SqlServerFixture fixture)
{
    /// <summary>18:42 at the branch on Saturday 17 October 2026.</summary>
    private static readonly DateTime Now = new(2026, 10, 17, 14, 42, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 17);

    [SkippableFact]
    public async Task Today_is_counted_on_the_branch_calendar_from_its_bookings_room_tabs_and_takings()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        // Six tables of four, open 10:00 to 23:00 every day, a restaurant's 90-minute sitting.
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 6);
        var broken = await TestBranchBuilder.AddTableAsync(db, branch, "7", seats: 4);
        broken.MarkOutOfService();
        await db.SaveChangesAsync();

        var diner = await ReviewTestData.SeedDinerAsync(db, "Narine Overview", Now.AddDays(-30));

        // Today, every status. The late one started at 18:15 and nobody has come.
        await AddBookingAsync(db, branch, Today, new TimeOnly(12, 0), ReservationStatus.Completed, 2, 0);
        var seated = await AddBookingAsync(db, branch, Today, new TimeOnly(18, 0), ReservationStatus.Seated, 4, 1);
        var late = await AddBookingAsync(
            db, branch, Today, new TimeOnly(18, 15), ReservationStatus.Confirmed, 3, 2, ReservationChannel.App, diner);
        await AddBookingAsync(db, branch, Today, new TimeOnly(20, 0), ReservationStatus.Confirmed, 2, 3, ReservationChannel.Web);
        await AddBookingAsync(db, branch, Today, new TimeOnly(20, 30), ReservationStatus.PendingApproval, 6, 4);
        await AddBookingAsync(db, branch, Today, new TimeOnly(19, 0), ReservationStatus.CancelledByDiner, 2, 5);
        await AddBookingAsync(db, branch, Today, new TimeOnly(13, 0), ReservationStatus.NoShow, 2, 0);

        // Yesterday and tomorrow are not today, however close.
        await AddBookingAsync(db, branch, Today.AddDays(-1), new TimeOnly(23, 30), ReservationStatus.Completed, 2, 3);
        await AddBookingAsync(db, branch, Today.AddDays(1), new TimeOnly(0, 30), ReservationStatus.Confirmed, 2, 3);

        // The room now: the seated booking at table 2 with 15 000 still owed, a walk-in at table 6 owing
        // 8 000, and a party settling up at table 5 that owes nothing more.
        await SeatAsync(db, branch, 1, partySize: 4, Now.AddMinutes(-40), remainingAmd: 15_000, seated.Id);
        await SeatAsync(db, branch, 5, partySize: 2, Now.AddMinutes(-20), remainingAmd: 8_000);
        await SeatAsync(db, branch, 4, partySize: 2, Now.AddMinutes(-90), remainingAmd: 0, closing: true);

        // Takings: two tabs closed today, one yesterday; last Saturday one before 18:42 and one after.
        await ClosedTabAsync(db, branch, Local(Today, 13, 0), 30_000);
        await ClosedTabAsync(db, branch, Local(Today, 18, 0), 20_000);
        await ClosedTabAsync(db, branch, Local(Today.AddDays(-1), 22, 0), 99_999);
        await ClosedTabAsync(db, branch, Local(Today.AddDays(-7), 12, 0), 25_000);
        await ClosedTabAsync(db, branch, Local(Today.AddDays(-7), 20, 0), 40_000);

        var overview = await fixture
            .CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId))
            .GetAsync(branch.BranchId);

        Assert.Equal(branch.BranchId, overview.BranchId);
        Assert.Equal("Asia/Yerevan", overview.TimeZoneId);
        Assert.Equal(Now, overview.AsOfUtc);
        Assert.Equal(Today, overview.LocalDate);
        Assert.Equal(new TimeOnly(18, 42), overview.LocalTime);
        Assert.Equal(Today, overview.ServiceDate);
        Assert.Equal(15, overview.GraceMinutes);

        Assert.True(overview.Hours.IsOpen);
        Assert.Equal(new TimeOnly(23, 0), overview.Hours.ClosesAt);
        Assert.False(overview.Hours.ClosesNextDay);
        Assert.Null(overview.Hours.OpensAt);

        var bookings = overview.Bookings;
        Assert.Equal(6, bookings.Total);
        Assert.Equal(1, bookings.PendingApproval);
        Assert.Equal(2, bookings.Confirmed);
        Assert.Equal(1, bookings.Late);
        Assert.Equal(1, bookings.Seated);
        Assert.Equal(1, bookings.Completed);
        Assert.Equal(1, bookings.NoShow);
        Assert.Equal(1, bookings.Cancelled);
        Assert.Equal(2 + 4 + 3 + 2 + 6, bookings.People);
        Assert.Equal(0, bookings.PendingChanges);

        Assert.Equal(new OverviewRoom(People: 8, OccupiedTables: 3, Tables: 7, OutOfServiceTables: 1), overview.InRoom);
        Assert.Equal(new OverviewOpenTabs(Count: 3, UnpaidAmd: 23_000), overview.OpenTabs);

        Assert.Equal(50_000, overview.Revenue.TodayAmd);
        Assert.Equal(25_000, overview.Revenue.LastWeekAmd);
        Assert.Equal(Today.AddDays(-7), overview.Revenue.LastWeekDate);
        Assert.Equal(100.0, overview.Revenue.ChangePercent);

        // From the day's first opening, 10:00, last week's to the same 18:42.
        Assert.Equal(Local(Today, 10, 0), overview.Revenue.SinceUtc);

        // Six tables of four: the broken one seats nobody, so the room is full at 24.
        Assert.Equal(24, overview.Load.Capacity);

        // Every hour the branch is open today, 10:00 to the 22:00 hour. A sitting counts in every hour it
        // touches; the request counts apart, and the cancelled and the no-show not at all.
        Assert.Equal(Enumerable.Range(10, 13), overview.Load.Hours.Select(h => h.Hour));

        int People(int hour) => overview.Load.Hours.Single(h => h.Hour == hour).People;
        int Pending(int hour) => overview.Load.Hours.Single(h => h.Hour == hour).PendingPeople;

        Assert.Equal(0, People(10));
        Assert.Equal(2, People(12));
        Assert.Equal(2, People(13));
        Assert.Equal(0, People(14));
        Assert.Equal(7, People(18));
        Assert.Equal(7, People(19));
        Assert.Equal(2, People(20));
        Assert.Equal(6, Pending(20));
        Assert.Equal(6, Pending(21));
        Assert.Equal(0, Pending(22));
        Assert.Equal(0, People(22));

        // Today's bookings, one by one, earliest first - not the cancelled one, nor yesterday's or tomorrow's.
        var rows = overview.TodayBookings;
        Assert.Equal(6, rows.Count);
        Assert.False(overview.TodayBookingsTruncated);
        Assert.Equal(
            new[] { "12:00", "13:00", "18:00", "18:15", "20:00", "20:30" },
            rows.Select(r => r.LocalStartTime.ToString("HH:mm")));

        var lateRow = rows.Single(r => r.Id == late.Id);
        Assert.Equal(ReservationStatus.Confirmed, lateRow.Status);
        Assert.Equal(late.Code, lateRow.Code);
        Assert.Equal(3, lateRow.PartySize);
        Assert.Equal("Ani Overview", lateRow.GuestName);
        Assert.Equal(branch.TableIds[2], lateRow.TableId);
        Assert.Equal("3", lateRow.TableLabel);
        Assert.Equal("Windows", lateRow.RoomName);
        Assert.Equal(TableStatus.Free, lateRow.TableStatus);
        Assert.Equal(ReservationChannel.App, lateRow.Channel);
        Assert.Equal(Today, lateRow.LocalDate);
        Assert.False(lateRow.ChangeRequested);

        // Nothing in the outbox for it, so nothing is said about a nudge.
        Assert.Null(lateRow.LateNudgeAtUtc);
        Assert.Null(lateRow.LateNudgeSent);

        var seatedRow = rows.Single(r => r.Id == seated.Id);
        Assert.Equal(ReservationStatus.Seated, seatedRow.Status);
        Assert.Equal(TableStatus.Occupied, seatedRow.TableStatus);
    }

    /// <summary>
    /// A bar open 20:00 to 02:00: its Saturday runs until 02:00 on Sunday. At 22:00 on Saturday and at 01:00
    /// on Sunday the Overview is about the same service day - the same bookings, the same chart running past
    /// midnight, the same takings - while the calendar has turned.
    /// </summary>
    [SkippableFact]
    public async Task An_overnight_branch_counts_its_service_day_until_the_block_closes_after_midnight()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(
            db, tableCount: 6, openingHours: [(new TimeOnly(20, 0), new TimeOnly(2, 0), true)]);

        var query = fixture.CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId));

        // 18:42, before the doors open: shut until 20:00, and the chart already spans the night to the 01:00 hour.
        var evening = await query.GetAsync(branch.BranchId);

        Assert.Equal(Today, evening.ServiceDate);
        Assert.False(evening.Hours.IsOpen);
        Assert.Equal(new TimeOnly(20, 0), evening.Hours.OpensAt);
        Assert.Null(evening.Hours.ClosesAt);
        Assert.Equal(Enumerable.Range(20, 6), evening.Load.Hours.Select(h => h.Hour));
        Assert.Equal(Local(Today, 20, 0), evening.Revenue.SinceUtc);

        // Saturday's night: a party seated at 21:00, and two bookings after midnight on Sunday's calendar.
        var seated = await AddBookingAsync(db, branch, Today, new TimeOnly(21, 0), ReservationStatus.Seated, 2, 0);
        await SeatAsync(db, branch, 0, partySize: 2, Local(Today, 21, 5), remainingAmd: 0, seated.Id);
        var halfPast = await AddBookingAsync(db, branch, Today.AddDays(1), new TimeOnly(0, 30), ReservationStatus.Confirmed, 3, 1);
        var one = await AddBookingAsync(db, branch, Today.AddDays(1), new TimeOnly(1, 0), ReservationStatus.Confirmed, 4, 2);

        // Not Saturday's: 00:30 on Saturday's calendar was Friday night, and Sunday at 21:00 is Sunday's.
        await AddBookingAsync(db, branch, Today, new TimeOnly(0, 30), ReservationStatus.Completed, 5, 3);
        await AddBookingAsync(db, branch, Today.AddDays(1), new TimeOnly(21, 0), ReservationStatus.Confirmed, 6, 4);

        // Takings: Friday night's last tab at 00:40 on Saturday is not Saturday's; Saturday's are at 21:30 and,
        // after midnight, 00:45. The Saturday before: 21:00 and 00:30, and 01:30 is past the point compared.
        await ClosedTabAsync(db, branch, Local(Today, 0, 40), 11_000);
        await ClosedTabAsync(db, branch, Local(Today, 21, 30), 30_000);
        await ClosedTabAsync(db, branch, Local(Today.AddDays(1), 0, 45), 20_000);
        await ClosedTabAsync(db, branch, Local(Today.AddDays(-7), 21, 0), 25_000);
        await ClosedTabAsync(db, branch, Local(Today.AddDays(-6), 0, 30), 10_000);
        await ClosedTabAsync(db, branch, Local(Today.AddDays(-6), 1, 30), 99_000);

        void SameNight(BranchOverview overview)
        {
            Assert.Equal(Today, overview.ServiceDate);
            Assert.Equal(new[] { seated.Id, halfPast.Id, one.Id }, overview.TodayBookings.Select(r => r.Id));
            Assert.Equal(Today.AddDays(1), overview.TodayBookings[1].LocalDate);
            Assert.Equal(3, overview.Bookings.Total);
            Assert.Equal(1, overview.Bookings.Seated);
            Assert.Equal(2, overview.Bookings.Confirmed);

            // Hours past midnight count on: 24 is 00:00-01:00 on Sunday, 25 is 01:00, 26 is 02:00.
            Assert.Equal(Enumerable.Range(20, 7), overview.Load.Hours.Select(h => h.Hour));
            Assert.Equal(
                new[] { 0, 2, 2, 0, 3, 7, 4 },
                overview.Load.Hours.Select(h => h.People));
            Assert.Equal(Local(Today, 20, 0), overview.Revenue.SinceUtc);
            Assert.Equal(Today.AddDays(-7), overview.Revenue.LastWeekDate);
        }

        // 22:00 on Saturday.
        clock.UtcNow = Local(Today, 22, 0);
        var late = await query.GetAsync(branch.BranchId);

        SameNight(late);
        Assert.Equal(Today, late.LocalDate);
        Assert.True(late.Hours.IsOpen);
        Assert.Equal(new TimeOnly(2, 0), late.Hours.ClosesAt);
        Assert.True(late.Hours.ClosesNextDay);
        Assert.Equal(0, late.Bookings.Late);
        Assert.Equal(30_000, late.Revenue.TodayAmd);
        Assert.Equal(25_000, late.Revenue.LastWeekAmd);
        Assert.Equal(20.0, late.Revenue.ChangePercent);

        // 01:00 on Sunday: the calendar has turned, the service day has not.
        clock.UtcNow = Local(Today.AddDays(1), 1, 0);
        var night = await query.GetAsync(branch.BranchId);

        SameNight(night);
        Assert.Equal(Today.AddDays(1), night.LocalDate);
        Assert.True(night.Hours.IsOpen);
        Assert.Equal(new TimeOnly(2, 0), night.Hours.ClosesAt);

        // It closes today, so not "the next day".
        Assert.False(night.Hours.ClosesNextDay);
        Assert.Null(night.Hours.OpensAt);

        // The 00:30 party is late; the seated one is the one at the table.
        Assert.Equal(1, night.Bookings.Late);
        Assert.Equal(night.Bookings.Seated, night.InRoom.OccupiedTables);
        Assert.Equal(2, night.InRoom.People);

        Assert.Equal(50_000, night.Revenue.TodayAmd);
        Assert.Equal(35_000, night.Revenue.LastWeekAmd);
        Assert.Equal(42.9, night.Revenue.ChangePercent);
    }

    /// <summary>A block closing at midnight reads "until 00:00", and once it has closed tomorrow is today.</summary>
    [SkippableFact]
    public async Task A_branch_closing_at_midnight_is_open_until_00_00_and_not_the_next_day()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(
            db, openingHours: [(new TimeOnly(12, 0), new TimeOnly(0, 0), true)]);

        var query = fixture.CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId));

        var evening = await query.GetAsync(branch.BranchId);

        Assert.True(evening.Hours.IsOpen);
        Assert.Equal(new TimeOnly(0, 0), evening.Hours.ClosesAt);
        Assert.False(evening.Hours.ClosesNextDay);
        Assert.Equal(Enumerable.Range(12, 12), evening.Load.Hours.Select(h => h.Hour));

        clock.UtcNow = Local(Today.AddDays(1), 0, 30);
        var after = await query.GetAsync(branch.BranchId);

        Assert.Equal(Today.AddDays(1), after.ServiceDate);
        Assert.False(after.Hours.IsOpen);
        Assert.Equal(new TimeOnly(12, 0), after.Hours.OpensAt);
    }

    /// <summary>
    /// A booking whose change is waiting counts at the slot still promised; one whose change was declined is
    /// back where it was, confirmed, and listed like any other - though it carries the reason its change failed.
    /// </summary>
    [SkippableFact]
    public async Task A_change_waiting_counts_at_the_promised_slot_and_a_declined_change_leaves_a_live_booking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 4);
        var policy = await db.Branches.AsNoTracking().Where(b => b.Id == branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();

        // Confirmed for 19:00 at table 1, asking to move to 21:00 at table 2 for four.
        var moving = await AddBookingAsync(db, branch, Today, new TimeOnly(19, 0), ReservationStatus.Confirmed, 2, 0);
        moving.Reschedule(
            branch.TableIds[1], Local(Today, 21, 0), Today, new TimeOnly(21, 0), 4, policy,
            ReservationStatus.PendingApproval, Now, ApprovalTrigger.ShortNotice, Now.AddMinutes(15));

        // Confirmed for 20:00; its move to 21:30 was declined, so it is confirmed at 20:00 again.
        var kept = await AddBookingAsync(db, branch, Today, new TimeOnly(20, 0), ReservationStatus.Confirmed, 2, 2);
        kept.Reschedule(
            branch.TableIds[2], Local(Today, 21, 30), Today, new TimeOnly(21, 30), 2, policy,
            ReservationStatus.PendingApproval, Now, ApprovalTrigger.ShortNotice, Now.AddMinutes(15));
        kept.KeepConfirmedSlot(Now, DeclineReason.StaffDeclined);

        // Confirmed for the day after tomorrow, asking to come tonight at 20:30 instead: tonight it is a request.
        var tonight = await AddBookingAsync(db, branch, Today.AddDays(2), new TimeOnly(19, 0), ReservationStatus.Confirmed, 2, 3);
        tonight.Reschedule(
            branch.TableIds[3], Local(Today, 20, 30), Today, new TimeOnly(20, 30), 2, policy,
            ReservationStatus.PendingApproval, Now, ApprovalTrigger.ShortNotice, Now.AddMinutes(15));

        await db.SaveChangesAsync();
        Assert.Equal(ReservationStatus.Confirmed, kept.Status);
        Assert.NotNull(kept.DeclineReason);

        var overview = await fixture
            .CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId))
            .GetAsync(branch.BranchId);

        var rows = overview.TodayBookings;
        Assert.Equal(new[] { moving.Id, kept.Id, tonight.Id }, rows.Select(r => r.Id));

        var promised = rows[0];
        Assert.Equal(ReservationStatus.PendingApproval, promised.Status);
        Assert.True(promised.ChangeRequested);
        Assert.Equal(new TimeOnly(19, 0), promised.LocalStartTime);
        Assert.Equal(Local(Today, 19, 0), promised.StartUtc);
        Assert.Equal(branch.TableIds[0], promised.TableId);
        Assert.Equal("1", promised.TableLabel);
        Assert.Equal(2, promised.PartySize);

        Assert.Equal(ReservationStatus.Confirmed, rows[1].Status);
        Assert.False(rows[1].ChangeRequested);
        Assert.Equal(new TimeOnly(20, 0), rows[1].LocalStartTime);

        Assert.Equal(ReservationStatus.PendingApproval, rows[2].Status);
        Assert.False(rows[2].ChangeRequested);
        Assert.Equal(new TimeOnly(20, 30), rows[2].LocalStartTime);

        Assert.Equal(3, overview.Bookings.Total);
        Assert.Equal(2, overview.Bookings.Confirmed);
        Assert.Equal(1, overview.Bookings.PendingChanges);
        Assert.Equal(1, overview.Bookings.PendingApproval);
        Assert.Equal(6, overview.Bookings.People);

        int People(int hour) => overview.Load.Hours.Single(h => h.Hour == hour).People;
        int Pending(int hour) => overview.Load.Hours.Single(h => h.Hour == hour).PendingPeople;

        // Promised: 19:00-20:30 for two and 20:00-21:30 for two. Asked for: 21:00-22:30 for four, 20:30-22:00 for two.
        Assert.Equal(2, People(19));
        Assert.Equal(4, People(20));
        Assert.Equal(2, People(21));
        Assert.Equal(0, People(22));
        Assert.Equal(2, Pending(20));
        Assert.Equal(6, Pending(21));
        Assert.Equal(4, Pending(22));
    }

    /// <summary>
    /// A held table and the late nudge are said only of a confirmed booking - the nudge as the outbox has it,
    /// sent or still to go.
    /// </summary>
    [SkippableFact]
    public async Task The_hold_and_the_late_nudge_are_shown_for_a_confirmed_booking_from_what_is_stored()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // Years ahead, so no other test's outbox dispatcher finds these messages due.
        var now = new DateTime(2031, 5, 17, 14, 42, 0, DateTimeKind.Utc);
        var day = DateOnly.FromDateTime(now.AddHours(4));
        var clock = new TestClock(now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 4);
        var diner = await ReviewTestData.SeedDinerAsync(db, "Nudged Overview", now.AddDays(-30));

        var sent = await AddBookingAsync(
            db, branch, day, new TimeOnly(18, 15), ReservationStatus.Confirmed, 2, 0, ReservationChannel.App, diner,
            holdExpiresAtUtc: now.AddMinutes(5));
        var waiting = await AddBookingAsync(
            db, branch, day, new TimeOnly(18, 30), ReservationStatus.Confirmed, 2, 1, ReservationChannel.App, diner);
        var seated = await AddBookingAsync(
            db, branch, day, new TimeOnly(18, 0), ReservationStatus.Seated, 2, 2, ReservationChannel.App, diner,
            holdExpiresAtUtc: now.AddMinutes(5));
        var quiet = await AddBookingAsync(
            db, branch, day, new TimeOnly(18, 20), ReservationStatus.Confirmed, 2, 3, ReservationChannel.App, diner);

        var nudge = new OutboxMessage(
            OutboxMessageTypes.ReservationLateNudge, "{}", sent.StartUtc.AddMinutes(10),
            OutboxMessageTypes.KeyFor("reservation", sent.Id, "late-nudge"), now.AddDays(-1));
        nudge.MarkSent(sent.StartUtc.AddMinutes(11));

        // A moved booking's nudge carries the start it is for in its key.
        var later = new OutboxMessage(
            OutboxMessageTypes.ReservationLateNudge, "{}", waiting.StartUtc.AddMinutes(10),
            OutboxMessageTypes.KeyFor("reservation", waiting.Id, "late-nudge:20310517T1830"), now.AddDays(-1));

        var seatedNudge = new OutboxMessage(
            OutboxMessageTypes.ReservationLateNudge, "{}", seated.StartUtc.AddMinutes(10),
            OutboxMessageTypes.KeyFor("reservation", seated.Id, "late-nudge"), now.AddDays(-1));

        // A reminder is not a nudge.
        var reminder = new OutboxMessage(
            OutboxMessageTypes.ReservationReminder, "{}", quiet.StartUtc.AddHours(-3),
            OutboxMessageTypes.KeyFor("reservation", quiet.Id, "reminder"), now.AddDays(-1));

        db.OutboxMessages.AddRange(nudge, later, seatedNudge, reminder);
        await db.SaveChangesAsync();

        try
        {
            var rows = (await fixture
                    .CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId))
                    .GetAsync(branch.BranchId))
                .TodayBookings
                .ToDictionary(r => r.Id);

            Assert.Equal(now.AddMinutes(5), rows[sent.Id].HoldExpiresAtUtc);
            Assert.Equal(sent.StartUtc.AddMinutes(11), rows[sent.Id].LateNudgeAtUtc);
            Assert.True(rows[sent.Id].LateNudgeSent);

            Assert.Equal(waiting.StartUtc.AddMinutes(10), rows[waiting.Id].LateNudgeAtUtc);
            Assert.False(rows[waiting.Id].LateNudgeSent);

            Assert.Null(rows[seated.Id].HoldExpiresAtUtc);
            Assert.Null(rows[seated.Id].LateNudgeAtUtc);
            Assert.Null(rows[seated.Id].LateNudgeSent);

            Assert.Null(rows[quiet.Id].LateNudgeAtUtc);
            Assert.Null(rows[quiet.Id].LateNudgeSent);
        }
        finally
        {
            db.OutboxMessages.RemoveRange(nudge, later, seatedNudge, reminder);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>The list leaves out what was called off before it caps, and says when it capped.</summary>
    [SkippableFact]
    public async Task The_list_drops_the_cancelled_before_its_cap_and_says_when_it_was_cut()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);
        var policy = await db.Branches.AsNoTracking().Where(b => b.Id == branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();

        // Four called off first thing, then 203 confirmed a minute apart from 10:00.
        for (var i = 0; i < 4; i++)
        {
            db.Reservations.Add(NewBooking(branch, policy, Today, new TimeOnly(9, i), ReservationStatus.CancelledByDiner, 2, 0));
        }

        for (var i = 0; i < 203; i++)
        {
            db.Reservations.Add(NewBooking(branch, policy, Today, new TimeOnly(10, 0).AddMinutes(i), ReservationStatus.Confirmed, 2, 0));
        }

        await db.SaveChangesAsync();

        var overview = await fixture
            .CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId))
            .GetAsync(branch.BranchId);

        Assert.Equal(200, overview.TodayBookings.Count);
        Assert.True(overview.TodayBookingsTruncated);
        Assert.DoesNotContain(overview.TodayBookings, r => r.Status == ReservationStatus.CancelledByDiner);
        Assert.Equal(new TimeOnly(10, 0), overview.TodayBookings[0].LocalStartTime);

        // The counts are the whole day's.
        Assert.Equal(203, overview.Bookings.Confirmed);
        Assert.Equal(203, overview.Bookings.Total);
        Assert.Equal(4, overview.Bookings.Cancelled);
    }

    [SkippableFact]
    public async Task A_platform_admin_reads_any_branch_and_an_unknown_one_is_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreateBranchOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        Assert.Equal(branch.BranchId, (await query.GetAsync(branch.BranchId)).BranchId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => query.GetAsync(Guid.NewGuid()));
    }

    [SkippableFact]
    public async Task A_branch_with_no_hours_and_no_bookings_draws_no_bars_and_a_last_week_of_nothing_has_no_change()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db, openingHours: []);
        await ClosedTabAsync(db, branch, Local(Today, 13, 0), 12_000);

        var overview = await fixture
            .CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId))
            .GetAsync(branch.BranchId);

        Assert.False(overview.Hours.IsOpen);
        Assert.Null(overview.Hours.OpensAt);
        Assert.Empty(overview.Load.Hours);
        Assert.Empty(overview.TodayBookings);
        Assert.Equal(0, overview.Bookings.Total);
        Assert.Equal(12_000, overview.Revenue.TodayAmd);
        Assert.Equal(0, overview.Revenue.LastWeekAmd);
        Assert.Null(overview.Revenue.ChangePercent);

        // No hours: the day's takings start at local midnight.
        Assert.Equal(Local(Today, 0, 0), overview.Revenue.SinceUtc);
    }

    /// <summary>
    /// A fixed handful of statements however busy the day: one booking or twenty cost the same.
    /// </summary>
    [SkippableFact]
    public async Task The_overview_costs_the_same_statements_for_one_booking_as_for_twenty()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);

        async Task<int> CountFor(int bookings)
        {
            TestBranch branch;

            await using (var db = fixture.CreateContext(clock))
            {
                branch = await TestBranchBuilder.CreateAsync(db, tableCount: 10);

                for (var i = 0; i < bookings; i++)
                {
                    await AddBookingAsync(
                        db, branch, Today, new TimeOnly(12 + (i % 10), 0), ReservationStatus.Confirmed, 2, i % 10);
                }

                for (var i = 0; i < Math.Min(bookings, 5); i++)
                {
                    await SeatAsync(db, branch, i, partySize: 2, Now.AddMinutes(-30), remainingAmd: 1_000);
                }
            }

            var counter = new CommandCountingInterceptor();
            await using var readDb = fixture.CreateContext(clock, counter);

            var overview = await fixture
                .CreateBranchOverviewQuery(readDb, clock, TestActor.Manager(branch.ManagerId))
                .GetAsync(branch.BranchId);

            Assert.Equal(bookings, overview.TodayBookings.Count);

            return counter.Count;
        }

        var one = await CountFor(1);
        var twenty = await CountFor(20);

        Assert.Equal(one, twenty);
        // Two for the branch guard, four for the screen, and one for the late nudges when any guest has the app.
        Assert.True(one <= 7, $"The overview ran {one} statements; it is meant to be the guard's two and at most five of its own.");
    }

    [SkippableFact]
    public async Task A_branch_manager_reads_their_own_branch_and_no_other()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db);
        var sibling = await ConsoleTestData.AddBranchAsync(db, branch.VenueId, "Sibling", SubscriptionTier.Paid);
        var owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);

        var manager = fixture.CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId));

        await manager.GetAsync(branch.BranchId);
        await Assert.ThrowsAsync<StaffPermissionException>(() => manager.GetAsync(sibling));

        // The owner covers both, and a waiter neither.
        var asOwner = fixture.CreateBranchOverviewQuery(db, clock, TestActor.Owner(owner.StaffMemberId));
        Assert.Equal(sibling, (await asOwner.GetAsync(sibling)).BranchId);

        var waiter = fixture.CreateBranchOverviewQuery(db, clock, TestActor.Waiter(branch.WaiterId));
        await Assert.ThrowsAsync<StaffPermissionException>(() => waiter.GetAsync(branch.BranchId));
    }

    /// <summary>The wire the console codes to, through the real pipeline with the console's own sign-in.</summary>
    [SkippableFact]
    public async Task The_console_reads_the_overview_with_its_own_token_in_the_documented_shape()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);

        AuthBranch branch;
        PanelAccount owner;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);

            var asBuilt = new TestBranch(
                branch.VenueId, branch.BranchId, branch.WaiterId, branch.ManagerId, branch.TableIds, "Asia/Yerevan");
            var today = ConsoleTestData.YerevanDate(factory.Clock.UtcNow);

            await AddBookingAsync(db, asBuilt, today, new TimeOnly(23, 0), ReservationStatus.Confirmed, 2, 0);
        }

        using var client = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));

        var response = await client.GetAsync($"/api/branches/{branch.BranchId}/overview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var field in new[]
                 {
                     "branchId", "timeZoneId", "asOfUtc", "localDate", "localTime", "serviceDate", "graceMinutes", "hours",
                     "bookings", "inRoom", "openTabs", "revenue", "load", "todayBookings", "todayBookingsTruncated",
                 })
        {
            Assert.True(body.TryGetProperty(field, out _), $"The overview has no `{field}`.");
        }

        Assert.Equal(1, body.GetProperty("bookings").GetProperty("confirmed").GetInt32());
        Assert.Equal(0, body.GetProperty("openTabs").GetProperty("unpaidAmd").GetInt64());
        Assert.Equal(3, body.GetProperty("inRoom").GetProperty("tables").GetInt32());
        Assert.Equal(12, body.GetProperty("load").GetProperty("capacity").GetInt32());

        var row = body.GetProperty("todayBookings").EnumerateArray().Single();
        Assert.Equal("23:00:00", row.GetProperty("localStartTime").GetString());
        Assert.Equal(body.GetProperty("serviceDate").GetString(), row.GetProperty("localDate").GetString());
        Assert.False(row.GetProperty("changeRequested").GetBoolean());
        Assert.False(body.GetProperty("todayBookingsTruncated").GetBoolean());
        Assert.Equal((int)ReservationStatus.Confirmed, row.GetProperty("status").GetInt32());
        Assert.Equal("Ani Overview", row.GetProperty("guestName").GetString());

        // No guest number anywhere in it: the row menu acts on a booking, it does not call anybody.
        Assert.DoesNotContain(GuestPhone, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A null is left out, like everywhere in this API: nothing to compare against last week.
        Assert.False(body.GetProperty("revenue").TryGetProperty("changePercent", out _));

        // The platform admin passes every branch scope, so an unknown branch is a plain 404 for them.
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        Assert.Equal(HttpStatusCode.OK, (await platform.GetAsync($"/api/branches/{branch.BranchId}/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync($"/api/branches/{Guid.NewGuid()}/overview")).StatusCode);
    }

    /// <summary>
    /// The published schema the console's types are generated from: the operation, and which fields are
    /// always there and which may be absent - the frontend's gateway check reads those <c>required</c> lists.
    /// </summary>
    [Fact]
    public async Task The_published_overview_schema_marks_what_is_always_there_and_what_may_be_absent()
    {
        await using var factory = new YallaApiFactory().WithSwagger(enabled: true);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        var get = document.RootElement.GetProperty("paths").GetProperty("/api/branches/{branchId}/overview").GetProperty("get");
        Assert.Equal("getBranchOverview", get.GetProperty("operationId").GetString());

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        HashSet<string> Required(string name) =>
        [
            .. schemas.EnumerateObject()
                .Single(s => s.Name == name || s.Name.EndsWith("." + name, StringComparison.Ordinal))
                .Value.GetProperty("required").EnumerateArray().Select(r => r.GetString()!),
        ];

        Assert.Superset(
            new HashSet<string>
            {
                "branchId", "timeZoneId", "asOfUtc", "localDate", "localTime", "serviceDate", "graceMinutes", "hours",
                "bookings", "inRoom", "openTabs", "revenue", "load", "todayBookings", "todayBookingsTruncated",
            },
            Required(nameof(BranchOverview)));

        Assert.Superset(new HashSet<string> { "isOpen", "closesNextDay" }, Required(nameof(OverviewHours)));
        Assert.DoesNotContain("closesAt", Required(nameof(OverviewHours)));
        Assert.DoesNotContain("opensAt", Required(nameof(OverviewHours)));
        Assert.DoesNotContain("changePercent", Required(nameof(OverviewRevenue)));
        Assert.Contains("sinceUtc", Required(nameof(OverviewRevenue)));
        Assert.Contains("pendingChanges", Required(nameof(OverviewBookingCounts)));

        var booking = Required(nameof(OverviewBooking));
        Assert.Superset(
            new HashSet<string> { "id", "status", "localDate", "tableId", "tableStatus", "guestName", "channel", "changeRequested" },
            booking);

        // Gone: a declined booking is never listed, and a kept slot's leftover reason is not a decline.
        Assert.False(schemas.EnumerateObject().Single(s => s.Name.EndsWith(".OverviewBooking", StringComparison.Ordinal))
            .Value.GetProperty("properties").TryGetProperty("declinedBecause", out _));

        foreach (var optional in new[] { "roomName", "holdExpiresAtUtc", "lateNudgeAtUtc", "lateNudgeSent" })
        {
            Assert.DoesNotContain(optional, booking);
        }
    }

    // ------------------------------------------------------------ fixtures

    private const string GuestPhone = "+37499551177";

    /// <summary>The instant that is <paramref name="hour"/>:<paramref name="minute"/> at the branch on that date.</summary>
    private static DateTime Local(DateOnly date, int hour, int minute) =>
        DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Utc).AddHours(-4);

    private static async Task<Reservation> AddBookingAsync(
        YallaDbContext db,
        TestBranch branch,
        DateOnly date,
        TimeOnly time,
        ReservationStatus status,
        int partySize,
        int tableIndex,
        ReservationChannel channel = ReservationChannel.Staff,
        Guid? dinerUserId = null,
        DateTime? holdExpiresAtUtc = null)
    {
        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => b.ReservationPolicy)
            .FirstAsync();

        var booking = NewBooking(branch, policy, date, time, status, partySize, tableIndex, channel, dinerUserId, holdExpiresAtUtc);

        db.Reservations.Add(booking);
        await db.SaveChangesAsync();

        return booking;
    }

    /// <summary>A booking at the branch on that date and time, moved to <paramref name="status"/>, not yet added.</summary>
    private static Reservation NewBooking(
        TestBranch branch,
        ReservationPolicy policy,
        DateOnly date,
        TimeOnly time,
        ReservationStatus status,
        int partySize,
        int tableIndex,
        ReservationChannel channel = ReservationChannel.Staff,
        Guid? dinerUserId = null,
        DateTime? holdExpiresAtUtc = null)
    {
        var startUtc = Local(date, time.Hour, time.Minute);

        var booking = Reservation.Create(
            branch.BranchId,
            branch.TableIds[tableIndex],
            startUtc,
            date,
            time,
            partySize,
            "Ani Overview",
            GuestPhone,
            Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            policy,
            initialStatus: status == ReservationStatus.PendingApproval
                ? ReservationStatus.PendingApproval
                : ReservationStatus.Confirmed,
            dinerUserId: dinerUserId,
            holdExpiresAtUtc: holdExpiresAtUtc,
            channel: channel);

        switch (status)
        {
            case ReservationStatus.Seated:
                booking.MarkSeated();
                break;
            case ReservationStatus.Completed:
                booking.MarkSeated();
                booking.MarkCompleted();
                break;
            case ReservationStatus.CancelledByDiner:
                booking.CancelByDiner(startUtc.AddDays(-1), "Plans changed.", afterDeadline: false);
                break;
            case ReservationStatus.NoShow:
                booking.MarkNoShow(startUtc.AddMinutes(30));
                break;
        }

        return booking;
    }

    /// <summary>A party at the table now, with a tab still owing <paramref name="remainingAmd"/>.</summary>
    private static async Task SeatAsync(
        YallaDbContext db,
        TestBranch branch,
        int tableIndex,
        int partySize,
        DateTime seatedAtUtc,
        long remainingAmd,
        Guid? reservationId = null,
        bool closing = false)
    {
        var table = await db.DiningTables.FirstAsync(t => t.Id == branch.TableIds[tableIndex]);

        var session = reservationId is { } booked
            ? TableSession.SeatReservation(branch.BranchId, table.Id, booked, partySize, seatedAtUtc, branch.WaiterId)
            : TableSession.SeatWalkIn(branch.BranchId, table.Id, partySize, seatedAtUtc, branch.WaiterId);

        db.TableSessions.Add(session);
        table.Occupy(session.Id);
        await db.SaveChangesAsync();

        var tab = new Tab(branch.BranchId, table.Id, session.Id, seatedAtUtc, 0m, TabAdmissionReason.OpenedByStaff);
        tab.ApplyComputedTotals(remainingAmd + 5_000, 0L, 5_000);

        if (closing)
        {
            tab.BeginClosing();
        }

        db.Tabs.Add(tab);
        session.AttachTab(tab.Id);
        await db.SaveChangesAsync();
    }

    /// <summary>A sitting that has been and gone, its tab settled for <paramref name="totalAmd"/> at that instant.</summary>
    private static async Task ClosedTabAsync(YallaDbContext db, TestBranch branch, DateTime closedAtUtc, long totalAmd)
    {
        var tableId = branch.TableIds[0];

        var session = TableSession.SeatWalkIn(branch.BranchId, tableId, 2, closedAtUtc.AddHours(-1), branch.WaiterId);
        session.Close(closedAtUtc);
        db.TableSessions.Add(session);
        await db.SaveChangesAsync();

        var tab = new Tab(branch.BranchId, tableId, session.Id, closedAtUtc.AddHours(-1), 0m, TabAdmissionReason.OpenedByStaff);
        tab.ApplyComputedTotals(totalAmd, 0L, totalAmd);
        tab.Close(closedAtUtc);

        db.Tabs.Add(tab);
        session.AttachTab(tab.Id);
        await db.SaveChangesAsync();
    }
}
