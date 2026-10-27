using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Application.Messaging;
using Yalla.Application.Platform;
using Yalla.Domain.Enums;
using Yalla.Domain.Messaging;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The platform console's Home, against a real database: venue counts by state, bookings per
/// day on the branch's calendar, pending requests and open tabs per venue, the branches not ready,
/// the newest venues and the outbox - and no guest's name or number anywhere in it.
/// </summary>
/// <remarks>
/// The database carries what earlier classes left in it, so Home's totals include their data too.
/// Each test reads Home before and after building its own and asserts the difference, which is
/// exact because nothing else writes to this class's database while it runs and its tests run one
/// at a time (<see cref="TestDatabasePool"/>). The two tests about which venues are newest
/// run their clocks decades ahead of every other test's, so their venues are the newest whatever ran
/// before - and the activity feed's test, further ahead still, makes its venue on today's clock.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class PlatformOverviewTests(SqlServerFixture fixture)
{
    private static TestClock NewClock() => new(TestNow.UtcNow.AddYears(1));

    [SkippableFact]
    public async Task Venues_are_counted_by_state_with_deleted_ones_apart()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = NewClock();
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var before = (await query.GetOverviewAsync()).Venues;

        var paid = await TestBranchBuilder.CreateAsync(db, features: BranchFeatures.Full);
        var free = await TestBranchBuilder.CreateAsync(db, features: BranchFeatures.Basic);
        var mixed = await TestBranchBuilder.CreateAsync(db, features: BranchFeatures.Full);
        await ConsoleTestData.AddBranchAsync(db, mixed.VenueId, "Second", BranchFeatures.Basic);
        var suspended = await TestBranchBuilder.CreateAsync(db, features: BranchFeatures.Basic);
        var deleted = await TestBranchBuilder.CreateAsync(db, features: BranchFeatures.Full);
        var switchedOff = await TestBranchBuilder.CreateAsync(db, features: BranchFeatures.Basic);

        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        await platform.SuspendVenueAsync(suspended.VenueId);
        await platform.DeleteVenueAsync(deleted.VenueId);
        await platform.UpdateVenueAsync(switchedOff.VenueId, new UpdateVenueCommand(IsActive: false));

        var after = (await query.GetOverviewAsync()).Venues;

        // Five venues not deleted, whatever their branches' features. The deleted one is only in Deleted.
        Assert.Equal(before.Total + 5, after.Total);
        Assert.Equal(before.Active + 3, after.Active);
        Assert.Equal(before.Suspended + 1, after.Suspended);
        Assert.Equal(before.Inactive + 1, after.Inactive);
        Assert.Equal(before.Deleted + 1, after.Deleted);
        Assert.Equal(after.Total, after.Active + after.Suspended + after.Inactive);

        // Six branches under those five venues; the deleted venue's branch is not counted.
        Assert.Equal(before.Branches + 6, after.Branches);

        _ = paid;
        _ = free;
    }

    [SkippableFact]
    public async Task Bookings_are_counted_per_day_on_the_branch_calendar_leaving_cancelled_ones_out()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = NewClock();
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db);
        var today = ConsoleTestData.YerevanDate(clock.UtcNow);

        var before = (await query.GetOverviewAsync()).Bookings;

        // Today: two that count and a cancellation that does not.
        await ConsoleTestData.AddBookingAsync(db, branch, today, ReservationStatus.Confirmed, tableIndex: 0);
        await ConsoleTestData.AddBookingAsync(db, branch, today, ReservationStatus.Confirmed, tableIndex: 1);
        await ConsoleTestData.AddBookingAsync(db, branch, today, ReservationStatus.CancelledByDiner, tableIndex: 2);

        // Yesterday: a visit, and a request nobody approved before its time - still a booking for the day.
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-1), ReservationStatus.Completed);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-1), ReservationStatus.PendingApproval, tableIndex: 1);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-1), ReservationStatus.CancelledByVenue, tableIndex: 2);

        // The last day of this week, the first of the one before, the end of the fortnight, the edge of
        // the month, and one day past it.
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-6), ReservationStatus.NoShow);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-7), ReservationStatus.Confirmed);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-7), ReservationStatus.Completed, tableIndex: 1);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-13), ReservationStatus.Completed);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-29), ReservationStatus.Completed);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-30), ReservationStatus.Completed);

        // Tomorrow is not in a window that ends today.
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(1), ReservationStatus.Confirmed);

        var after = (await query.GetOverviewAsync()).Bookings;

        Assert.Equal(30, after.PerDay.Count);

        // Consecutive days, oldest first, today last.
        for (var i = 1; i < after.PerDay.Count; i++)
        {
            Assert.Equal(after.PerDay[i - 1].Date.AddDays(1), after.PerDay[i].Date);
        }

        if (after.LabelTimeZoneId == "Asia/Yerevan")
        {
            Assert.Equal(today, after.PerDay[^1].Date);
        }

        int Added(int daysAgo) => after.PerDay[29 - daysAgo].Count - before.PerDay[29 - daysAgo].Count;

        Assert.Equal(2, Added(0));
        Assert.Equal(2, Added(1));
        Assert.Equal(1, Added(6));
        Assert.Equal(2, Added(7));
        Assert.Equal(1, Added(13));
        Assert.Equal(1, Added(29));

        Assert.Equal(before.Last7Days + 5, after.Last7Days);
        Assert.Equal(before.Previous7Days + 3, after.Previous7Days);
        Assert.Equal(after.PerDay.TakeLast(7).Sum(d => d.Count), after.Last7Days);
        Assert.Equal(after.PerDay.SkipLast(7).TakeLast(7).Sum(d => d.Count), after.Previous7Days);
        Assert.Equal(PlatformRollups.ChangePercent(after.Last7Days, after.Previous7Days), after.ChangePercent);
    }

    [SkippableFact]
    public async Task Pending_requests_ahead_and_open_tabs_are_counted_per_venue()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = NewClock();
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 4);
        var today = ConsoleTestData.YerevanDate(clock.UtcNow);

        var before = await query.GetOverviewAsync();

        // Two requests still waiting for a visit ahead; one whose time has gone; one already confirmed;
        // and one ahead whose answer window has closed - the sweep is about to decline it.
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(1), ReservationStatus.PendingApproval);
        var withDeadline = await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(2), ReservationStatus.PendingApproval, tableIndex: 1);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(-2), ReservationStatus.PendingApproval, tableIndex: 2);
        await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(1), ReservationStatus.Confirmed, tableIndex: 3);
        var lapsed = await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(3), ReservationStatus.PendingApproval, tableIndex: 2);

        var deadline = clock.UtcNow.AddHours(1);
        var gone = clock.UtcNow.AddMinutes(-1);
        await db.Database.ExecuteSqlAsync($"UPDATE dbo.Reservations SET ApprovalExpiresAtUtc = {deadline} WHERE Id = {withDeadline.Id}");
        await db.Database.ExecuteSqlAsync($"UPDATE dbo.Reservations SET ApprovalExpiresAtUtc = {gone} WHERE Id = {lapsed.Id}");

        // A tab open, one closing, one closed.
        await ConsoleTestData.AddTabAsync(db, branch, 0, clock.UtcNow, TabStatus.Open);
        await ConsoleTestData.AddTabAsync(db, branch, 1, clock.UtcNow, TabStatus.Closing);
        await ConsoleTestData.AddTabAsync(db, branch, 2, clock.UtcNow, TabStatus.Closed);

        var after = await query.GetOverviewAsync();

        Assert.Equal(before.PendingApprovals.Total + 2, after.PendingApprovals.Total);
        Assert.Equal(2, Assert.Single(after.PendingApprovals.Venues, v => v.VenueId == branch.VenueId).Count);
        Assert.Equal(after.PendingApprovals.Total, after.PendingApprovals.Venues.Sum(v => v.Count));

        Assert.Equal(before.OpenTabs.Total + 2, after.OpenTabs.Total);
        Assert.Equal(2, Assert.Single(after.OpenTabs.Venues, v => v.VenueId == branch.VenueId).Count);
        Assert.Equal(after.OpenTabs.Total, after.OpenTabs.Venues.Sum(v => v.Count));

        // The most first.
        Assert.Equal(
            after.OpenTabs.Venues.Select(v => v.Count).OrderByDescending(c => c),
            after.OpenTabs.Venues.Select(v => v.Count));
    }

    [SkippableFact]
    public async Task A_branch_diners_can_reach_that_is_not_finished_is_listed_with_its_issues_and_nothing_else_is()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow.AddYears(40));
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var actor = TestActor.PlatformAdmin(admin.StaffMemberId);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, actor);
        var platform = fixture.CreatePlatformService(db, clock, actor);

        var before = (await query.GetOverviewAsync()).NotReady;

        // Finished: every checklist line and a cover. Not listed.
        clock.Advance(TimeSpan.FromSeconds(1));
        await ConsoleTestData.ReadyBranchAsync(db, clock.UtcNow);

        // Unfinished, but nobody can reach them: a suspended venue, a deleted one, a switched-off branch.
        clock.Advance(TimeSpan.FromSeconds(1));
        var suspended = await TestBranchBuilder.CreateAsync(db);
        await platform.SuspendVenueAsync(suspended.VenueId);

        clock.Advance(TimeSpan.FromSeconds(1));
        var deleted = await TestBranchBuilder.CreateAsync(db);
        await platform.DeleteVenueAsync(deleted.VenueId);

        clock.Advance(TimeSpan.FromSeconds(1));
        var ready = await ConsoleTestData.ReadyBranchAsync(db, clock.UtcNow);
        var off = await ConsoleTestData.AddBranchAsync(db, ready.VenueId, "Closed for works", BranchFeatures.Basic, isActive: false);

        // Unfinished and reachable: the builder's branch has tables, hours and staff, and nothing else.
        clock.Advance(TimeSpan.FromSeconds(1));
        var bare = await TestBranchBuilder.CreateAsync(db);

        var after = (await query.GetOverviewAsync()).NotReady;

        Assert.Equal(before.Total + 1, after.Total);
        Assert.DoesNotContain(after.Branches, b => b.BranchId == off);

        // Newest venues first, so this test's are among the twenty listed.
        var listed = Assert.Single(after.Branches, b => b.BranchId == bare.BranchId);
        Assert.Equal(bare.VenueId, listed.VenueId);
        Assert.DoesNotContain(after.Branches, b => b.VenueId == suspended.VenueId || b.VenueId == deleted.VenueId);
        Assert.DoesNotContain(after.Branches, b => b.VenueId == ready.VenueId);

        var listedVenues = after.Branches.Select(b => b.VenueId).ToList();
        var created = await db.Venues
            .Where(v => listedVenues.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.CreatedAtUtc);
        var order = listedVenues.Select(id => created[id]).ToList();
        Assert.Equal(order.OrderByDescending(at => at), order);

        // What a guest would find unfinished: no menu and no picture. The missing tablet and the
        // unsaved policy are the venue's own work and are not on Home.
        Assert.Equal([ReadinessIssueCodes.NoMenu, ReadinessIssueCodes.NoPhoto], listed.Issues.Select(i => i.Code));

        Assert.True(after.Branches.Count <= 20);
    }

    [SkippableFact]
    public async Task The_newest_venues_come_first_and_a_deleted_one_is_not_among_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow.AddYears(100));
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var actor = TestActor.PlatformAdmin(admin.StaffMemberId);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, actor);

        clock.Advance(TimeSpan.FromMinutes(1));
        var older = await TestBranchBuilder.CreateAsync(db, features: BranchFeatures.Basic);
        clock.Advance(TimeSpan.FromMinutes(1));
        var newer = await TestBranchBuilder.CreateAsync(db, features: BranchFeatures.Full);
        clock.Advance(TimeSpan.FromMinutes(1));
        var gone = await TestBranchBuilder.CreateAsync(db);
        await fixture.CreatePlatformService(db, clock, actor).DeleteVenueAsync(gone.VenueId);

        var recent = (await query.GetOverviewAsync()).RecentVenues;

        Assert.True(recent.Count <= 5);
        Assert.DoesNotContain(recent, v => v.VenueId == gone.VenueId);
        Assert.Equal(newer.VenueId, recent[0].VenueId);
        Assert.Equal(older.VenueId, recent[1].VenueId);

        Assert.Equal(VenueStatus.Active, recent[0].Status);
        Assert.Equal(1, recent[0].BranchCount);
        Assert.Equal(
            await db.Venues.Where(v => v.Id == newer.VenueId).Select(v => v.CreatedAtUtc).FirstAsync(),
            recent[0].CreatedAtUtc);
    }

    [SkippableFact]
    public async Task The_outbox_numbers_are_the_outbox_endpoints_with_the_last_day_of_dead_letters()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = NewClock();
        await fixture.ClearOutboxAsync(clock);

        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        // Dead letters only: nothing a dispatcher in another test could pick up and send.
        var recent = new OutboxMessage(OutboxMessageTypes.ReservationReminder, "{}", clock.UtcNow.AddHours(-3), $"console:{Guid.NewGuid():N}", clock.UtcNow.AddHours(-3));
        recent.DeadLetter("The provider refused it.", clock.UtcNow.AddHours(-2));
        var old = new OutboxMessage(OutboxMessageTypes.ReservationReminder, "{}", clock.UtcNow.AddDays(-3), $"console:{Guid.NewGuid():N}", clock.UtcNow.AddDays(-3));
        old.DeadLetter("The provider refused it.", clock.UtcNow.AddDays(-2));
        db.OutboxMessages.AddRange(recent, old);
        await db.SaveChangesAsync();

        var health = await fixture.CreateOutbox(db, clock).GetHealthAsync();
        var outbox = (await query.GetOverviewAsync()).Outbox;

        Assert.Equal(2, health.DeadLettered);
        Assert.Equal(1, health.DeadLetteredLastDay);

        Assert.Equal(health.Pending, outbox.Pending);
        Assert.Equal(health.Due, outbox.Due);
        Assert.Equal(health.Failed, outbox.Failed);
        Assert.Equal(health.DeadLettered, outbox.DeadLettered);
        Assert.Equal(health.SentLastDay, outbox.SentLastDay);
        Assert.Equal(health.DeadLetteredLastDay, outbox.DeadLetteredLastDay);

        await fixture.ClearOutboxAsync(clock);
    }

    [SkippableFact]
    public async Task Home_names_no_guest_anywhere_in_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = NewClock();
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var query = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db);
        var today = ConsoleTestData.YerevanDate(clock.UtcNow);

        var booking = await ConsoleTestData.AddBookingAsync(db, branch, today.AddDays(1), ReservationStatus.PendingApproval);
        await ConsoleTestData.AddBookingAsync(db, branch, today, ReservationStatus.Confirmed, tableIndex: 1);

        var json = JsonSerializer.Serialize(await query.GetOverviewAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain(ConsoleTestData.GuestName, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secretguest", json, StringComparison.Ordinal);
        Assert.DoesNotContain(ConsoleTestData.GuestPhone, json, StringComparison.Ordinal);
        Assert.DoesNotContain("555044", json, StringComparison.Ordinal);
        Assert.DoesNotContain(booking.Code, json, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Only_a_platform_admin_reads_Home()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = NewClock();
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var asManager = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId));
        var asOwner = fixture.CreatePlatformOverviewQuery(db, clock, TestActor.Owner(branch.ManagerId));

        await Assert.ThrowsAsync<StaffPermissionException>(() => asManager.GetOverviewAsync());
        await Assert.ThrowsAsync<StaffPermissionException>(() => asOwner.GetOverviewAsync());
        await Assert.ThrowsAsync<StaffPermissionException>(() => asManager.GetVenueBookingsSummaryAsync(branch.VenueId));
        await Assert.ThrowsAsync<StaffPermissionException>(() => asManager.GetActivityAsync(new ActivityQuery()));
    }
}
