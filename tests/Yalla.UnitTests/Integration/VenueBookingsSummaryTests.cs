using System.Text.Json;
using Yalla.Application.Platform;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// One venue's bookings as counts, for the venue page: by outcome, by branch and by weekday, the
/// average party, today per branch and the requests still waiting - for 7 and 30 days, and never a
/// guest's name or number.
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

        // Ahead: two requests waiting and one confirmed - none of them in a window that ends today.
        await ConsoleTestData.AddBookingAsync(db, main, today.AddDays(1), ReservationStatus.PendingApproval, tableIndex: 3);
        await ConsoleTestData.AddBookingAsync(db, second, today.AddDays(5), ReservationStatus.PendingApproval);
        await ConsoleTestData.AddBookingAsync(db, main, today.AddDays(2), ReservationStatus.Confirmed);

        // Another venue's booking today is not this venue's.
        var elsewhere = await TestBranchBuilder.CreateAsync(db);
        await ConsoleTestData.AddBookingAsync(db, elsewhere, today, ReservationStatus.Confirmed);

        var summary = await query.GetVenueBookingsSummaryAsync(main.VenueId);

        Assert.Equal(main.VenueId, summary.VenueId);
        Assert.Equal(2, summary.PendingApprovalUpcoming);

        // Today, per branch, every branch listed.
        var todayMain = Assert.Single(summary.Today, t => t.BranchId == main.BranchId);
        Assert.Equal(2, todayMain.Bookings);
        Assert.Equal(today, todayMain.LocalDate);
        Assert.Equal(0, Assert.Single(summary.Today, t => t.BranchId == secondId).Bookings);
        Assert.Equal(0, Assert.Single(summary.Today, t => t.BranchId == quiet).Bookings);

        // ---- seven days: today back to today-6.
        var week = summary.Last7Days;
        Assert.Equal(7, week.Days);
        Assert.Equal(6, week.Total);
        Assert.Equal(new BookingStatusCounts(Completed: 2, Confirmed: 1, PendingApproval: 1, Cancelled: 1, NoShow: 1), week.ByStatus);

        // Not cancelled: 4, 2, 3, 5, 2 -> 16 / 5.
        Assert.Equal(3.2, week.AveragePartySize);

        Assert.Equal([main.BranchId, secondId, quiet], week.ByBranch.Select(b => b.BranchId));
        Assert.Equal(4, week.ByBranch[0].Total);
        Assert.Equal(new BookingStatusCounts(1, 1, 0, 1, 1), week.ByBranch[0].ByStatus);
        Assert.Equal(2, week.ByBranch[1].Total);
        Assert.Equal(new BookingStatusCounts(1, 0, 1, 0, 0), week.ByBranch[1].ByStatus);
        Assert.Equal(0, week.ByBranch[2].Total);
        Assert.Equal(week.Total, week.ByBranch.Sum(b => b.Total));

        Assert.Equal([1, 2, 3, 4, 5, 6, 7], week.ByWeekday.Select(w => w.IsoDay));
        Assert.Equal(week.Total, week.ByWeekday.Sum(w => w.Count));
        Assert.Equal(3, week.ByWeekday.Single(w => w.IsoDay == PlatformRollups.IsoDay(today.DayOfWeek)).Count);
        Assert.Equal(2, week.ByWeekday.Single(w => w.IsoDay == PlatformRollups.IsoDay(today.AddDays(-6).DayOfWeek)).Count);

        // ---- thirty days: adds today-7, today-20 and today-29, not today-30.
        var month = summary.Last30Days;
        Assert.Equal(30, month.Days);
        Assert.Equal(9, month.Total);
        Assert.Equal(new BookingStatusCounts(Completed: 4, Confirmed: 1, PendingApproval: 1, Cancelled: 2, NoShow: 1), month.ByStatus);

        // Not cancelled: 4, 2, 3, 5, 2, 2, 4 -> 22 / 7.
        Assert.Equal(3.1, month.AveragePartySize);
        Assert.Equal(month.Total, month.ByWeekday.Sum(w => w.Count));
        Assert.Equal(month.Total, month.ByBranch.Sum(b => b.Total));
    }

    [SkippableFact]
    public async Task A_venue_with_no_bookings_has_zeroes_and_no_average()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db);

        var summary = await query.GetVenueBookingsSummaryAsync(branch.VenueId);

        Assert.Equal(0, summary.PendingApprovalUpcoming);
        Assert.Equal(0, summary.Last7Days.Total);
        Assert.Equal(0, summary.Last30Days.Total);
        Assert.Null(summary.Last7Days.AveragePartySize);
        Assert.Equal(7, summary.Last30Days.ByWeekday.Count);
        Assert.All(summary.Last30Days.ByWeekday, w => Assert.Equal(0, w.Count));
        Assert.Equal(0, Assert.Single(summary.Today).Bookings);
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
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(1), ReservationStatus.PendingApproval, tableIndex: 1);

        var json = JsonSerializer.Serialize(
            await query.GetVenueBookingsSummaryAsync(branch.VenueId),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("Secretguest", json, StringComparison.Ordinal);
        Assert.DoesNotContain("555044", json, StringComparison.Ordinal);
        Assert.DoesNotContain(booking.Code, json, StringComparison.Ordinal);
    }

    private static async Task<Guid> AddTableAsync(Yalla.Infrastructure.Persistence.YallaDbContext db, TestBranch branch)
    {
        var table = await TestBranchBuilder.AddTableAsync(db, branch, "1", seats: 6, pinned: false);

        return table.Id;
    }
}
