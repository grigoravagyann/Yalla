using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Overview;
using Yalla.Domain.Enums;
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

        Assert.Equal(new OverviewRoom(People: 8, OccupiedTables: 3, Tables: 7, OutOfServiceTables: 1), overview.InRoom);
        Assert.Equal(new OverviewOpenTabs(Count: 3, UnpaidAmd: 23_000), overview.OpenTabs);

        Assert.Equal(50_000, overview.Revenue.TodayAmd);
        Assert.Equal(25_000, overview.Revenue.LastWeekAmd);
        Assert.Equal(Today.AddDays(-7), overview.Revenue.LastWeekDate);
        Assert.Equal(100.0, overview.Revenue.ChangePercent);

        // Seven tables of four, the broken one included: the line the room is drawn against.
        Assert.Equal(28, overview.Load.Capacity);

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

        // Today's bookings, one by one, earliest first - the cancelled one too; yesterday and tomorrow not.
        var rows = overview.TodayBookings;
        Assert.Equal(7, rows.Count);
        Assert.Equal(
            new[] { "12:00", "13:00", "18:00", "18:15", "19:00", "20:00", "20:30" },
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

        // An app account is nudged ten minutes after the start (the default policy); a guest with no
        // account is not nudged at all.
        Assert.Equal(late.StartUtc.AddMinutes(10), lateRow.LateNudgeAtUtc);
        Assert.Null(rows.Single(r => r.LocalStartTime == new TimeOnly(20, 0)).LateNudgeAtUtc);

        var seatedRow = rows.Single(r => r.Id == seated.Id);
        Assert.Equal(ReservationStatus.Seated, seatedRow.Status);
        Assert.Equal(TableStatus.Occupied, seatedRow.TableStatus);
    }

    [SkippableFact]
    public async Task A_branch_shut_until_tonight_says_when_it_opens_and_an_overnight_block_runs_into_the_next_day()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        // A bar: 20:00 to 02:00 the next morning, every day.
        var branch = await TestBranchBuilder.CreateAsync(
            db, openingHours: [(new TimeOnly(20, 0), new TimeOnly(2, 0), true)]);

        var query = fixture.CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId));

        var evening = await query.GetAsync(branch.BranchId);

        Assert.False(evening.Hours.IsOpen);
        Assert.Equal(new TimeOnly(20, 0), evening.Hours.OpensAt);
        Assert.Null(evening.Hours.ClosesAt);

        // The bars run from the opening to the end of the day: the hours after midnight are tomorrow's.
        Assert.Equal(Enumerable.Range(20, 4), evening.Load.Hours.Select(h => h.Hour));

        // 01:00 on Sunday: still Saturday's block, closing at 02:00 - and it is Sunday on the calendar.
        clock.UtcNow = Local(Today.AddDays(1), 1, 0);

        var night = await query.GetAsync(branch.BranchId);

        Assert.Equal(Today.AddDays(1), night.LocalDate);
        Assert.True(night.Hours.IsOpen);
        Assert.Equal(new TimeOnly(2, 0), night.Hours.ClosesAt);
        // It closes today, so not "the next day".
        Assert.False(night.Hours.ClosesNextDay);
        Assert.Null(night.Hours.OpensAt);
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
        // Two for the branch guard, four for the screen.
        Assert.True(one <= 6, $"The overview ran {one} statements; it is meant to be the guard's two and four of its own.");
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

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);

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
                     "branchId", "timeZoneId", "asOfUtc", "localDate", "localTime", "hours", "bookings", "inRoom",
                     "openTabs", "revenue", "load", "todayBookings",
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
        Assert.Equal((int)ReservationStatus.Confirmed, row.GetProperty("status").GetInt32());
        Assert.Equal("Ani Overview", row.GetProperty("guestName").GetString());

        // No guest number anywhere in it: the row menu acts on a booking, it does not call anybody.
        Assert.DoesNotContain(GuestPhone, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A null is left out, like everywhere in this API: nothing to compare against last week.
        Assert.False(body.GetProperty("revenue").TryGetProperty("changePercent", out _));
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
                "branchId", "timeZoneId", "asOfUtc", "localDate", "localTime", "hours", "bookings", "inRoom", "openTabs",
                "revenue", "load", "todayBookings",
            },
            Required(nameof(BranchOverview)));

        Assert.Superset(new HashSet<string> { "isOpen", "closesNextDay" }, Required(nameof(OverviewHours)));
        Assert.DoesNotContain("closesAt", Required(nameof(OverviewHours)));
        Assert.DoesNotContain("opensAt", Required(nameof(OverviewHours)));
        Assert.DoesNotContain("changePercent", Required(nameof(OverviewRevenue)));

        var booking = Required(nameof(OverviewBooking));
        Assert.Superset(new HashSet<string> { "id", "status", "tableId", "tableStatus", "guestName", "channel" }, booking);

        foreach (var optional in new[] { "roomName", "declinedBecause", "holdExpiresAtUtc", "lateNudgeAtUtc" })
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
        Guid? dinerUserId = null)
    {
        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => b.ReservationPolicy)
            .FirstAsync();

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

        db.Reservations.Add(booking);
        await db.SaveChangesAsync();

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

        var tab = new Tab(branch.BranchId, table.Id, session.Id, seatedAtUtc, 0m);
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

        var tab = new Tab(branch.BranchId, tableId, session.Id, closedAtUtc.AddHours(-1), 0m);
        tab.ApplyComputedTotals(totalAmd, 0L, totalAmd);
        tab.Close(closedAtUtc);

        db.Tabs.Add(tab);
        session.AttachTab(tab.Id);
        await db.SaveChangesAsync();
    }
}
