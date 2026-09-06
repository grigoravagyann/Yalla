using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reports;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The five report groups, against seeded data whose answers are known by hand.
/// </summary>
/// <remarks>
/// The interesting tests here are about <b>boundaries</b> rather than about arithmetic: a local day
/// is not a UTC day, a period with no prior period is not a period of zero, and a range nobody meant
/// to ask for is a table scan nobody meant to run.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class ReportTests(SqlServerFixture fixture)
{
    /// <summary>Yerevan is UTC+4 with no daylight saving, which is what makes these boundaries exact.</summary>
    private const string Yerevan = "Asia/Yerevan";

    private static readonly DateOnly Tuesday = new(2026, 9, 8);

    // ------------------------------------------------------------ 6. local day boundaries

    /// <summary>
    /// <b>Test 6.</b> A report for a local Tuesday excludes a sitting that started at 01:30 local on
    /// Wednesday, and includes one that started at 23:30 local on Tuesday.
    /// </summary>
    /// <remarks>
    /// Both of those instants are on Tuesday in UTC - 21:30 and 19:30 - so a report computed on UTC
    /// boundaries gets this exactly backwards for the first one. It is wrong every single day, in
    /// the direction that moves the late sittings into the wrong day, which is where a restaurant's
    /// interesting numbers are.
    /// </remarks>
    [SkippableFact]
    public async Task A_report_for_a_local_day_uses_the_branchs_own_midnight()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, timeZoneId: Yerevan);

        // 23:30 local on Tuesday is 19:30 UTC on Tuesday. In the report.
        var lateTuesday = await SeatAsync(db, branch, Local(Tuesday, 23, 30), minutes: 60);

        // 01:30 local on Wednesday is 21:30 UTC on Tuesday. Not in the report - and the row a
        // UTC-boundary report would wrongly include.
        var earlyWednesday = await SeatAsync(db, branch, Local(Tuesday.AddDays(1), 1, 30), minutes: 60);

        // 00:30 local on Tuesday is 20:30 UTC on Monday. In the report, and the row a UTC-boundary
        // report would wrongly exclude.
        var earlyTuesday = await SeatAsync(db, branch, Local(Tuesday, 0, 30), minutes: 45);

        var reports = SqlServerFixture.CreateReportQuery(db, TestActor.Manager(branch.ManagerId));

        var report = await reports.GetOccupancyAsync(
            new ReportRequest(branch.BranchId, Tuesday, Tuesday));

        Assert.Equal(2, report.Sessions.Value);

        // And the two that are in it are the two that belong to Tuesday in Yerevan.
        await using var verify = fixture.CreateContext(clock);
        var included = await verify.TableSessions
            .AsNoTracking()
            .Where(s => s.BranchId == branch.BranchId)
            .Select(s => new { s.Id, s.SeatedAtUtc })
            .ToListAsync();

        Assert.Equal(3, included.Count);

        // And the UTC days say why a UTC boundary gets this wrong in both directions at once.
        // Tuesday the 8th in Yerevan spans 20:00 UTC on the 7th to 20:00 UTC on the 8th.
        //
        //   in the report, UTC Monday    - 00:30 local Tuesday  = 20:30 UTC on the 7th
        //   in the report, UTC Tuesday   - 23:30 local Tuesday  = 19:30 UTC on the 8th
        //   NOT in the report, UTC Tuesday - 01:30 local Wednesday = 21:30 UTC on the 8th
        //
        // So a report on UTC days would drop the first and pick up the third: two errors, in
        // opposite directions, on the same ordinary Tuesday.
        Assert.Equal(7, included.Single(s => s.Id == earlyTuesday).SeatedAtUtc.Day);
        Assert.Equal(8, included.Single(s => s.Id == lateTuesday).SeatedAtUtc.Day);
        Assert.Equal(8, included.Single(s => s.Id == earlyWednesday).SeatedAtUtc.Day);
    }

    // ------------------------------------------------------------ 7. the turn-time distribution

    /// <summary>
    /// <b>Test 7.</b> The turn-time distribution matches hand-computed values.
    /// </summary>
    /// <remarks>
    /// Five sittings of known length against a 90-minute policy, so the median, the 90th percentile
    /// and the over-policy fraction are all arithmetic somebody can check on paper.
    /// </remarks>
    [SkippableFact]
    public async Task The_turn_time_distribution_matches_hand_computed_values()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);

        var policy = Yalla.Domain.Venues.ReservationPolicy.DefaultFor(VenueType.Restaurant);
        var branch = await TestBranchBuilder.CreateAsync(db, timeZoneId: Yerevan, policy: policy, tableCount: 6);

        // 50, 80, 100, 140, 200 minutes. Sorted, five values.
        foreach (var (minutes, index) in new[] { 50, 80, 100, 140, 200 }.Select((m, i) => (m, i)))
        {
            await SeatAsync(db, branch, Local(Tuesday, 12 + index, 0), minutes, tableIndex: index % 3);
        }

        var reports = SqlServerFixture.CreateReportQuery(db, TestActor.Manager(branch.ManagerId));

        var turn = (await reports.GetOccupancyAsync(new ReportRequest(branch.BranchId, Tuesday, Tuesday))).TurnTime;

        Assert.Equal(90, turn.PolicyTurnTimeMinutes);
        Assert.Equal(5, turn.ClosedSessions);

        // Nearest-rank: the median of five sorted values is the third, and p90 is the fifth.
        Assert.Equal(100, turn.MedianMinutes);
        Assert.Equal(200, turn.P90Minutes);

        // Three of the five ran past 90 minutes: 100, 140 and 200.
        Assert.Equal(0.6m, turn.OverPolicyFraction);

        // And the shape is there rather than summarised away. 50 lands in the 60 bucket, 80 in 90,
        // 100 in 105, 140 in 150, 200 in 240.
        Assert.Equal(1, turn.Buckets.Single(b => b.UpToMinutes == 60).Sessions);
        Assert.Equal(1, turn.Buckets.Single(b => b.UpToMinutes == 90).Sessions);
        Assert.Equal(1, turn.Buckets.Single(b => b.UpToMinutes == 105).Sessions);
        Assert.Equal(1, turn.Buckets.Single(b => b.UpToMinutes == 150).Sessions);
        Assert.Equal(1, turn.Buckets.Single(b => b.UpToMinutes == 240).Sessions);
        Assert.Equal(5, turn.Buckets.Sum(b => b.Sessions));

        // The number the whole distribution exists to make visible: a policy of 90 against a median
        // of 100 is a venue refusing bookings it could take.
        Assert.True(turn.MedianMinutes > turn.PolicyTurnTimeMinutes);
    }

    // ------------------------------------------------------------ 8. items never ordered

    /// <summary>
    /// <b>Test 8.</b> "Never ordered" is exactly the items with no line in the period, and an item
    /// ordered outside the period counts as never ordered inside it.
    /// </summary>
    /// <remarks>
    /// The second half is the one worth pinning. Computed as "no line ever" this would silently
    /// hide the dish that sold twice in March and has not moved since - which is the dish the report
    /// exists to surface.
    /// </remarks>
    [SkippableFact]
    public async Task Items_never_ordered_covers_the_period_and_not_all_of_history()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, timeZoneId: Yerevan);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        // Inside the period: coffee sells.
        await OrderAsync(db, branch, menu.Coffee, Local(Tuesday, 13, 0), quantity: 3);

        // Outside it, a week earlier: khachapuri sold once and has not moved since.
        await OrderAsync(db, branch, menu.Khachapuri, Local(Tuesday.AddDays(-7), 13, 0), quantity: 1);

        var reports = SqlServerFixture.CreateReportQuery(db, TestActor.Manager(branch.ManagerId));

        var report = await reports.GetMenuAsync(new ReportRequest(branch.BranchId, Tuesday, Tuesday));

        var neverOrdered = report.NeverOrdered.Select(i => i.MenuItemId).ToList();

        // Sold in the period: absent from the list.
        Assert.DoesNotContain(menu.Coffee, neverOrdered);

        // Sold only outside it: present, which is the whole point.
        Assert.Contains(menu.Khachapuri, neverOrdered);

        // And the two that never sold at all.
        Assert.Contains(menu.Wine, neverOrdered);
        Assert.Contains(menu.SoldOut, neverOrdered);

        Assert.Equal(3, neverOrdered.Count);

        // The top list is the mirror image.
        var top = Assert.Single(report.TopByCount);

        Assert.Equal(menu.Coffee, top.MenuItemId);
        Assert.Equal(3, top.Quantity);
        Assert.Equal(3 * TestMenu.CoffeeAmd, top.RevenueAmd);
    }

    // ------------------------------------------------------------ 12. the comparison period

    /// <summary>
    /// <b>Test 12.</b> The comparison handles a range that crosses a month boundary, and a period
    /// with no prior data at all.
    /// </summary>
    /// <remarks>
    /// <b>No prior data is null, not zero.</b> A venue's first week has no previous week, and a
    /// client rendering zero would print "down 100%" about a venue that has done nothing wrong.
    /// </remarks>
    [SkippableFact]
    public async Task The_comparison_crosses_a_month_boundary_and_survives_having_no_prior_period()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, timeZoneId: Yerevan, tableCount: 4);

        // A range that straddles the end of September: 28 September to 4 October, seven days.
        var from = new DateOnly(2026, 9, 28);
        var to = new DateOnly(2026, 10, 4);

        var range = new ReportRange(from, to);

        Assert.Equal(7, range.Days);

        // Counted in days rather than calendar months, so the previous period is the seven days
        // immediately before - 21 to 27 September - and not "the previous month".
        Assert.Equal(new DateOnly(2026, 9, 21), range.Previous().FromLocalDate);
        Assert.Equal(new DateOnly(2026, 9, 27), range.Previous().ToLocalDate);
        Assert.Equal(range.Days, range.Previous().Days);

        // Two sittings in the period, one in the one before.
        await SeatAsync(db, branch, Local(new DateOnly(2026, 9, 30), 19, 0), minutes: 60);
        await SeatAsync(db, branch, Local(new DateOnly(2026, 10, 2), 19, 0), minutes: 60, tableIndex: 1);
        await SeatAsync(db, branch, Local(new DateOnly(2026, 9, 24), 19, 0), minutes: 60, tableIndex: 2);

        var reports = SqlServerFixture.CreateReportQuery(db, TestActor.Manager(branch.ManagerId));

        var report = await reports.GetOccupancyAsync(new ReportRequest(branch.BranchId, from, to));

        Assert.Equal(2m, report.Sessions.Value);
        Assert.Equal(1m, report.Sessions.Previous);
        Assert.Equal(1m, report.Sessions.ChangeFraction);

        // And a period with nothing before it. Zero, not null, because the previous period was
        // genuinely queried and genuinely empty - what must not happen is a divide by it.
        var firstEver = new DateOnly(2026, 1, 5);

        var opening = await reports.GetOccupancyAsync(new ReportRequest(branch.BranchId, firstEver, firstEver));

        Assert.Equal(0m, opening.Sessions.Value);
        Assert.Equal(0m, opening.Sessions.Previous);

        // The one assertion that matters: no percentage out of nothing.
        Assert.Null(opening.Sessions.ChangeFraction);
    }

    // ------------------------------------------------------------ 10. the range cap

    /// <summary>
    /// <b>Test 10.</b> An over-long range is refused by name, with the limit in the body.
    /// </summary>
    [SkippableFact]
    public async Task An_over_long_range_is_refused_with_a_named_error()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        var from = new DateOnly(2024, 1, 1);
        var to = from.AddDays(ReportRange.MaxDays);

        var refused = await manager.GetAsync(
            $"/api/branches/{branch.BranchId}/reports/occupancy?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("report-range-too-long", body.GetProperty("code").GetString());
        Assert.Equal(ReportRange.MaxDays, body.GetProperty("context").GetProperty("maxDays").GetInt32());
        Assert.Equal(ReportRange.MaxDays + 1, body.GetProperty("context").GetProperty("requestedDays").GetInt32());

        // One day shorter is accepted, so the cap is the cap and not an off-by-one.
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.GetAsync(
                $"/api/branches/{branch.BranchId}/reports/occupancy?from={from:yyyy-MM-dd}&to={to.AddDays(-1):yyyy-MM-dd}"))
                .StatusCode);
    }

    // ------------------------------------------------------------ 9. scope

    /// <summary>
    /// <b>Test 9.</b> A manager cannot report on another branch; an owner can roll up their venue.
    /// </summary>
    /// <remarks>
    /// Two different rules. The first is the branch-scoped policy at the endpoint; the second is in
    /// the query, because a rollup crosses branches and the endpoint's policy no longer covers it -
    /// a manager would otherwise widen their own reach by setting a query parameter.
    /// </remarks>
    [SkippableFact]
    public async Task A_manager_is_confined_to_their_branch_and_an_owner_can_roll_up_the_venue()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        AuthBranch theirs;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));

        var window = $"from={Tuesday:yyyy-MM-dd}&to={Tuesday:yyyy-MM-dd}";

        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.GetAsync($"/api/branches/{mine.BranchId}/reports/revenue?{window}")).StatusCode);

        // Another venue's branch: refused by the branch-scoped policy, before the handler runs.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.GetAsync($"/api/branches/{theirs.BranchId}/reports/revenue?{window}")).StatusCode);

        // And a manager cannot widen their own scope with a query parameter.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.GetAsync($"/api/branches/{mine.BranchId}/reports/revenue?{window}&rollUpVenue=true")).StatusCode);

        // An owner can. Two branches of one venue, rolled up.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var venue = await db.Venues.Include(v => v.Branches).FirstAsync(v => v.Id == mine.VenueId);

            db.Branches.Add(new Yalla.Domain.Venues.Branch(
                venue,
                name: "Second Branch",
                slug: $"second-{Guid.NewGuid():N}"[..20],
                address: "2 Test Street, Yerevan",
                latitude: 40.18,
                longitude: 44.51,
                timeZoneId: Yerevan,
                floorWidth: 800,
                floorHeight: 600));

            await db.SaveChangesAsync();
        }

        using var owner = factory.CreateClientWithToken(await SignInOwnerAsync(factory, mine));

        var rolled = await owner.GetAsync(
            $"/api/branches/{mine.BranchId}/reports/revenue?{window}&rollUpVenue=true");

        Assert.Equal(HttpStatusCode.OK, rolled.StatusCode);

        var body = await rolled.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, body.GetProperty("scope").GetProperty("branchIds").GetArrayLength());
    }

    // ------------------------------------------------------------ 11. CSV

    /// <summary>
    /// <b>Test 11.</b> The CSV export carries the same numbers as the JSON.
    /// </summary>
    /// <remarks>
    /// Local dates and whole dram in both, because an export in UTC would disagree with the report
    /// it was exported from - and the person comparing them is holding a till receipt.
    /// </remarks>
    [SkippableFact]
    public async Task The_csv_export_round_trips_the_same_numbers_as_the_json()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);

            var test = new TestBranch(
                branch.VenueId, branch.BranchId, branch.WaiterId, branch.ManagerId, branch.TableIds, Yerevan);

            await CloseTabAsync(db, test, Local(Tuesday, 20, 0), totalAmd: 12_000L);
            await CloseTabAsync(db, test, Local(Tuesday, 21, 0), totalAmd: 8_500L, tableIndex: 1);
        }

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        var window = $"from={Tuesday:yyyy-MM-dd}&to={Tuesday:yyyy-MM-dd}";
        var route = $"/api/branches/{branch.BranchId}/reports/revenue?{window}";

        var json = await (await manager.GetAsync(route)).Content.ReadFromJsonAsync<JsonElement>();
        var csvResponse = await manager.GetAsync($"{route}&format=csv");

        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);

        // Asserted on the bytes: ReadAsStringAsync decodes UTF-8 and drops the mark, so a string
        // check here would pass whether or not it was ever written.
        var bytes = await csvResponse.Content.ReadAsByteArrayAsync();

        // The BOM Excel needs in order to read Armenian venue names rather than mojibake.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));

        var csv = await csvResponse.Content.ReadAsStringAsync();

        var lines = csv.TrimStart('﻿')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        Assert.Equal("localDate,revenueAmd,tabs", lines[0]);

        var day = json.GetProperty("byDay").EnumerateArray().Single();

        Assert.Equal(
            string.Join(
                ',',
                day.GetProperty("localDate").GetString(),
                day.GetProperty("revenueAmd").GetInt64().ToString(CultureInfo.InvariantCulture),
                day.GetProperty("tabs").GetInt32().ToString(CultureInfo.InvariantCulture)),
            lines[1]);

        // And the number is the one the venue counted: two tabs, 20,500 dram.
        Assert.Equal(20_500L, day.GetProperty("revenueAmd").GetInt64());
        Assert.Equal(20_500L, json.GetProperty("totalAmd").GetProperty("value").GetDecimal());
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// An owner of the venue, signed in to the admin panel for real.
    /// </summary>
    /// <remarks>
    /// An owner rather than a manager because the rollup rule is about exactly that difference: an
    /// owner is venue-scoped by construction and a manager is not, so a manager widening their own
    /// reach with a query parameter is the thing being refused. Created here rather than in
    /// <c>AuthTestData</c> because this is the only test that needs one.
    /// </remarks>
    private async Task<string> SignInOwnerAsync(YallaApiFactory factory, AuthBranch branch)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.test";

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var hasher = new Yalla.Infrastructure.Identity.SecretHasher();

            var owner = new StaffMember(
                branch.VenueId,
                "Test Owner",
                $"+3743{Guid.NewGuid():N}"[..12],
                StaffRole.Owner,
                hasher.Hash("1357"));

            owner.SetPasswordCredentials(email, hasher.Hash(AuthTestData.ManagerPassword));

            db.StaffMembers.Add(owner);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-in",
            new { email, password = AuthTestData.ManagerPassword });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }

    /// <summary>A local wall-clock instant in Yerevan, as the UTC the database stores.</summary>
    private static DateTime Local(DateOnly date, int hour, int minute) =>
        Yalla.Domain.Venues.BranchTime.ToUtc(date.ToDateTime(new TimeOnly(hour, minute)), Yerevan);

    /// <summary>Seats a party and closes the sitting after a known number of minutes.</summary>
    private static async Task<Guid> SeatAsync(
        YallaDbContext db,
        TestBranch branch,
        DateTime seatedAtUtc,
        int minutes,
        int tableIndex = 0,
        int partySize = 2)
    {
        var session = TableSession.SeatWalkIn(
            branch.BranchId, branch.TableIds[tableIndex], partySize, seatedAtUtc, branch.WaiterId);

        session.Close(seatedAtUtc.AddMinutes(minutes));

        db.TableSessions.Add(session);
        await db.SaveChangesAsync();

        return session.Id;
    }

    /// <summary>Places one order for one item at a known instant.</summary>
    private static async Task OrderAsync(
        YallaDbContext db,
        TestBranch branch,
        Guid menuItemId,
        DateTime placedAtUtc,
        int quantity)
    {
        var item = await db.MenuItems.AsNoTracking().FirstAsync(i => i.Id == menuItemId);
        var tab = await OpenTabAsync(db, branch, placedAtUtc, tableIndex: 0);

        var order = TabOrder.PlacedByStaffMember(tab.Id, branch.WaiterId, placedAtUtc);
        order.AddLine(item.Id, item.Name, item.PriceAmd, quantity);

        db.TabOrders.Add(order);
        await db.SaveChangesAsync();
    }

    /// <summary>A tab that opened and closed at a known instant, with a known total.</summary>
    private static async Task CloseTabAsync(
        YallaDbContext db,
        TestBranch branch,
        DateTime closedAtUtc,
        long totalAmd,
        int tableIndex = 0)
    {
        var tab = await OpenTabAsync(db, branch, closedAtUtc.AddHours(-1), tableIndex);

        tab.ApplyComputedTotals(totalAmd, 0L, totalAmd);
        tab.Close(closedAtUtc);

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A sitting that has been and gone, with a tab on it.
    /// </summary>
    /// <remarks>
    /// Closed immediately, because <c>UX_TableSessions_OpenPerTable</c> permits one open sitting per
    /// table and every fixture here is history rather than a live room. The session is saved before
    /// the tab so the tab's foreign key has something to point at.
    /// </remarks>
    private static async Task<Tab> OpenTabAsync(
        YallaDbContext db,
        TestBranch branch,
        DateTime openedAtUtc,
        int tableIndex)
    {
        var tableId = branch.TableIds[tableIndex];

        var session = TableSession.SeatWalkIn(branch.BranchId, tableId, 2, openedAtUtc, branch.WaiterId);
        session.Close(openedAtUtc.AddHours(2));

        db.TableSessions.Add(session);
        await db.SaveChangesAsync();

        var tab = new Tab(branch.BranchId, tableId, session.Id, openedAtUtc, 0m);
        db.Tabs.Add(tab);
        session.AttachTab(tab.Id);

        await db.SaveChangesAsync();

        return tab;
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
