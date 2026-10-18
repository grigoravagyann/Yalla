using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Application.Platform;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// One venue's bookings as counts, for the venue page: by outcome, by branch with today's count, by
/// weekday and the average party - for 7 or 30 days, and never a guest's name or number.
/// </summary>
/// <remarks>
/// Scoped to a venue this test builds, so the shared database's other bookings do not reach it and
/// every number is exact.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class VenueBookingsSummaryTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_venues_bookings_are_counted_by_outcome_branch_and_weekday_for_seven_and_thirty_days()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var main = await TestBranchBuilder.CreateAsync(db, tableCount: 4);
        await ConsoleTestData.RenameVenueAsync(db, main.VenueId, $"Cascade {Guid.NewGuid():N}"[..20]);
        var secondId = await ConsoleTestData.AddBranchAsync(db, main.VenueId, "Zz Second", SubscriptionTier.Free);
        var second = main with { BranchId = secondId, TableIds = [await AddTableAsync(db, main with { BranchId = secondId })] };
        var quiet = await ConsoleTestData.AddBranchAsync(db, main.VenueId, "Zzz Quiet", SubscriptionTier.Free);

        var today = ConsoleTestData.YerevanDate(clock.UtcNow);

        // Today at the main branch: two that count for today and a cancellation that does not.
        await ConsoleTestData.AddBookingAsync(db, main, today, ReservationStatus.Confirmed, partySize: 4, tableIndex: 0);
        await ConsoleTestData.AddBookingAsync(db, main, today, ReservationStatus.Completed, partySize: 2, tableIndex: 1);
        await ConsoleTestData.AddBookingAsync(db, main, today, ReservationStatus.CancelledByDiner, partySize: 6, tableIndex: 2);

        // Earlier this week, at both branches.
        await ConsoleTestData.AddBookingAsync(db, main, today.AddDays(-3), ReservationStatus.NoShow, partySize: 3);
        await ConsoleTestData.AddBookingAsync(db, second, today.AddDays(-6), ReservationStatus.Completed, partySize: 5);
        await ConsoleTestData.AddBookingAsync(db, second, today.AddDays(-6), ReservationStatus.PendingApproval, partySize: 2, localTime: new TimeOnly(12, 0));

        // In the month but not the week, and one day past the month.
        await ConsoleTestData.AddBookingAsync(db, main, today.AddDays(-7), ReservationStatus.CancelledByVenue, partySize: 8);
        await ConsoleTestData.AddBookingAsync(db, main, today.AddDays(-20), ReservationStatus.Completed, partySize: 2);
        await ConsoleTestData.AddBookingAsync(db, main, today.AddDays(-29), ReservationStatus.Completed, partySize: 4);
        await ConsoleTestData.AddBookingAsync(db, main, today.AddDays(-30), ReservationStatus.Completed, partySize: 9);

        // Ahead: none of them in a window that ends today.
        await ConsoleTestData.AddBookingAsync(db, main, today.AddDays(1), ReservationStatus.PendingApproval, tableIndex: 3);
        await ConsoleTestData.AddBookingAsync(db, main, today.AddDays(2), ReservationStatus.Confirmed);

        // Another venue's booking today is not this venue's.
        var elsewhere = await TestBranchBuilder.CreateAsync(db);
        await ConsoleTestData.AddBookingAsync(db, elsewhere, today, ReservationStatus.Confirmed);

        // ---- seven days: today back to today-6.
        var week = await query.GetVenueBookingsSummaryAsync(main.VenueId, 7);

        Assert.Equal(main.VenueId, week.VenueId);
        Assert.Equal(7, week.Days);
        Assert.Equal(6, week.Total);
        Assert.Equal(new BookingStatusCounts(Completed: 2, Upcoming: 1, PendingApproval: 1, Cancelled: 1, NoShow: 1), week.ByStatus);

        // Not cancelled: 4, 2, 3, 5, 2 -> 16 / 5.
        Assert.Equal(3.2, week.AveragePartySize);

        // Every branch, by name, flat, with today's bookings beside the window's.
        Assert.Equal([main.BranchId, secondId, quiet], week.ByBranch.Select(b => b.BranchId));
        Assert.Equal(new BranchBookingCounts(main.BranchId, week.ByBranch[0].Name, 4, 1, 1, 0, 1, 1, TodayCount: 2), week.ByBranch[0]);
        Assert.Equal(new BranchBookingCounts(secondId, "Zz Second", 2, 1, 0, 1, 0, 0, TodayCount: 0), week.ByBranch[1]);
        Assert.Equal(new BranchBookingCounts(quiet, "Zzz Quiet", 0, 0, 0, 0, 0, 0, TodayCount: 0), week.ByBranch[2]);
        Assert.Equal(week.Total, week.ByBranch.Sum(b => b.Total));

        // All seven weekdays, Sunday (0) to Saturday (6), by the weekday of the visit at the branch.
        Assert.Equal(Enum.GetValues<DayOfWeek>(), week.ByWeekday.Select(w => w.DayOfWeek));
        Assert.Equal(week.Total, week.ByWeekday.Sum(w => w.Count));
        Assert.Equal(3, week.ByWeekday.Single(w => w.DayOfWeek == today.DayOfWeek).Count);
        Assert.Equal(2, week.ByWeekday.Single(w => w.DayOfWeek == today.AddDays(-6).DayOfWeek).Count);
        Assert.Equal(1, week.ByWeekday.Single(w => w.DayOfWeek == today.AddDays(-3).DayOfWeek).Count);

        // ---- thirty days: adds today-7, today-20 and today-29, not today-30.
        var month = await query.GetVenueBookingsSummaryAsync(main.VenueId, 30);

        Assert.Equal(30, month.Days);
        Assert.Equal(9, month.Total);
        Assert.Equal(new BookingStatusCounts(Completed: 4, Upcoming: 1, PendingApproval: 1, Cancelled: 2, NoShow: 1), month.ByStatus);

        // Not cancelled: 4, 2, 3, 5, 2, 2, 4 -> 22 / 7.
        Assert.Equal(3.1, month.AveragePartySize);
        Assert.Equal(month.Total, month.ByWeekday.Sum(w => w.Count));
        Assert.Equal(month.Total, month.ByBranch.Sum(b => b.Total));
        Assert.Equal(2, month.ByBranch[0].TodayCount);

        // Thirty is what an unasked window is.
        var unasked = await query.GetVenueBookingsSummaryAsync(main.VenueId);
        Assert.Equal(30, unasked.Days);
        Assert.Equal(month.Total, unasked.Total);
        Assert.Equal(month.ByStatus, unasked.ByStatus);
    }

    [SkippableFact]
    public async Task A_venue_with_no_bookings_has_zeroes_every_weekday_and_no_average()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db);

        var summary = await query.GetVenueBookingsSummaryAsync(branch.VenueId, 7);

        Assert.Equal(0, summary.Total);
        Assert.Null(summary.AveragePartySize);
        Assert.Equal(7, summary.ByWeekday.Count);
        Assert.All(summary.ByWeekday, w => Assert.Equal(0, w.Count));
        Assert.Equal(0, Assert.Single(summary.ByBranch).TodayCount);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(31)]
    public async Task A_window_other_than_seven_or_thirty_days_is_refused_naming_it(int days)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db);

        var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.GetVenueBookingsSummaryAsync(branch.VenueId, days));
        Assert.Equal("days", refused.ParamName);
    }

    [SkippableFact]
    public async Task An_unknown_venue_is_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => query.GetVenueBookingsSummaryAsync(Guid.NewGuid()));
    }

    [SkippableFact]
    public async Task The_summary_names_no_guest()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db);
        var today = ConsoleTestData.YerevanDate(clock.UtcNow);

        var booking = await ConsoleTestData.AddBookingAsync(db, branch, today, ReservationStatus.Confirmed);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-1), ReservationStatus.PendingApproval, tableIndex: 1);

        var json = JsonSerializer.Serialize(
            await query.GetVenueBookingsSummaryAsync(branch.VenueId, 7),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("Secretguest", json, StringComparison.Ordinal);
        Assert.DoesNotContain("555044", json, StringComparison.Ordinal);
        Assert.DoesNotContain(booking.Code, json, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_route_serves_the_shape_the_venue_page_binds_to()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        TestBranch branch;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
            await ConsoleTestData.AddBookingAsync(db, branch, ConsoleTestData.YerevanDate(factory.Clock.UtcNow), ReservationStatus.Confirmed, partySize: 3);
        }

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        var body = await (await platform.GetAsync($"/api/platform/venues/{branch.VenueId}/bookings-summary?days=7"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            ["venueId", "days", "total", "byStatus", "byWeekday", "averagePartySize", "byBranch"],
            body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, body.GetProperty("days").GetInt32());
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(3.0, body.GetProperty("averagePartySize").GetDouble());

        Assert.Equal(
            ["completed", "upcoming", "pendingApproval", "cancelled", "noShow"],
            body.GetProperty("byStatus").EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, body.GetProperty("byStatus").GetProperty("upcoming").GetInt32());

        var weekdays = body.GetProperty("byWeekday").EnumerateArray().ToList();
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], weekdays.Select(w => w.GetProperty("dayOfWeek").GetInt32()));

        var row = Assert.Single(body.GetProperty("byBranch").EnumerateArray());
        Assert.Equal(
            ["branchId", "name", "total", "completed", "upcoming", "pendingApproval", "cancelled", "noShow", "todayCount"],
            row.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, row.GetProperty("todayCount").GetInt32());

        Assert.Equal(30, (await (await platform.GetAsync($"/api/platform/venues/{branch.VenueId}/bookings-summary"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("days").GetInt32());

        var refused = await platform.GetAsync($"/api/platform/venues/{branch.VenueId}/bookings-summary?days=14");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("days", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("field").GetString());
    }

    private static async Task<Guid> AddTableAsync(Yalla.Infrastructure.Persistence.YallaDbContext db, TestBranch branch)
    {
        var table = await TestBranchBuilder.AddTableAsync(db, branch, "1", seats: 6, pinned: false);

        return table.Id;
    }
}
