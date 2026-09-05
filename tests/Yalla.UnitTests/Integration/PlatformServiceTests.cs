using Microsoft.EntityFrameworkCore;
using Yalla.Application.Platform;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The platform tier against a real database: the audit log lands in the same transaction as
/// the change, venue creation is atomic, and deletion is refused while people are still eating.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PlatformServiceTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 5, 14, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ 3. every action is audited

    [SkippableFact]
    public async Task Every_platform_action_writes_an_audit_row_in_the_same_transaction()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var service = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var slug = $"audit-{Guid.NewGuid():N}"[..20];

        var created = await service.CreateVenueAsync(new CreateVenueCommand("Audit Cafe", VenueType.Cafe, slug, Branch("main")));
        var venueId = created.Venue.VenueId;
        var branchId = created.Branches[0].BranchId;

        // Two changes, two rows, one commit.
        await AssertAuditAsync(db, admin.StaffMemberId, venueId, "venue.create");
        await AssertAuditAsync(db, admin.StaffMemberId, branchId, "branch.create");

        await service.UpdateVenueAsync(venueId, new UpdateVenueCommand(Name: "Audit Cafe Renamed"));
        await AssertAuditAsync(db, admin.StaffMemberId, venueId, "venue.update");

        await service.SuspendVenueAsync(venueId);
        await AssertAuditAsync(db, admin.StaffMemberId, venueId, "venue.suspend");

        await service.ReactivateVenueAsync(venueId);
        await AssertAuditAsync(db, admin.StaffMemberId, venueId, "venue.reactivate");

        var second = await service.AddBranchAsync(venueId, Branch("second"));
        await AssertAuditAsync(db, admin.StaffMemberId, second.BranchId, "branch.create");

        await service.UpdateBranchAsync(second.BranchId, new UpdateBranchCommand(SubscriptionTier: SubscriptionTier.Paid));
        await AssertAuditAsync(db, admin.StaffMemberId, second.BranchId, "branch.update");

        await service.DeleteVenueAsync(venueId);
        await AssertAuditAsync(db, admin.StaffMemberId, venueId, "venue.delete");

        // Eight changes; eight rows; nothing landed without its entry.
        await using var verify = fixture.CreateContext(clock);
        var rows = await verify.PlatformAuditLogs.AsNoTracking()
            .Where(l => l.TargetId == venueId || l.TargetId == branchId || l.TargetId == second.BranchId)
            .ToListAsync();

        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Equal(admin.StaffMemberId, r.ActorStaffMemberId));
        Assert.All(rows, r => Assert.StartsWith("{", r.ChangesJson));
    }

    // ------------------------------------------------------------ 4. atomic creation

    [SkippableFact]
    public async Task Creating_a_venue_creates_its_first_branch_atomically_and_a_failure_creates_neither()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var service = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var slug = $"atomic-{Guid.NewGuid():N}"[..20];

        var created = await service.CreateVenueAsync(new CreateVenueCommand("Atomic", VenueType.Restaurant, slug, Branch("first")));

        Assert.Single(created.Branches);
        Assert.Equal(1, created.Venue.BranchCount);

        await using var verify = fixture.CreateContext(clock);
        var auditBefore = await verify.PlatformAuditLogs.CountAsync();
        var branchesBefore = await verify.Branches.CountAsync();
        var venuesBefore = await verify.Venues.CountAsync();

        // The same slug again. The unique index refuses the venue row, and the whole unit of work
        // - the second venue, its branch, and both audit rows - rolls back with it.
        await using var retryDb = fixture.CreateContext(clock);
        var retry = fixture.CreatePlatformService(retryDb, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var orphanBranchSlug = $"orphan-{Guid.NewGuid():N}"[..20];

        var refused = await Assert.ThrowsAsync<DomainStateException>(
            () => retry.CreateVenueAsync(new CreateVenueCommand("Atomic Again", VenueType.Cafe, slug, Branch(orphanBranchSlug))));

        Assert.Contains(slug, refused.Message);

        Assert.Equal(venuesBefore, await verify.Venues.CountAsync());
        Assert.Equal(branchesBefore, await verify.Branches.CountAsync());
        Assert.Equal(auditBefore, await verify.PlatformAuditLogs.CountAsync());
        Assert.False(await verify.Branches.AnyAsync(b => b.Slug == orphanBranchSlug));
    }

    // ------------------------------------------------------------ 5. deletion is refused with blockers named

    [SkippableFact]
    public async Task Deleting_a_venue_with_an_open_tab_is_rejected_and_names_the_blocker()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);

        // A party is eating at table 1, and somebody has booked table 2 for the weekend.
        var table = await db.DiningTables.FirstAsync(t => t.Id == branch.FirstTableId);
        var session = TableSession.SeatWalkIn(branch.BranchId, table.Id, 2, Now, branch.WaiterId);
        db.TableSessions.Add(session);
        table.Occupy(session.Id);
        var tab = new Tab(branch.BranchId, table.Id, session.Id, Now, 10m);
        db.Tabs.Add(tab);
        session.AttachTab(tab.Id);
        await db.SaveChangesAsync();

        var booking = await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, branch.TableIds[1], Now.AddDays(2));

        var service = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var refused = await Assert.ThrowsAsync<VenueDeletionBlockedException>(() => service.DeleteVenueAsync(branch.VenueId));

        Assert.Equal([table.Label], refused.OpenTabs);
        Assert.Equal([booking.Code], refused.FutureReservations);
        Assert.Contains(table.Label, refused.Message);
        Assert.Contains(booking.Code, refused.Message);

        await using var verify = fixture.CreateContext(clock);
        var venue = await verify.Venues.AsNoTracking().FirstAsync(v => v.Id == branch.VenueId);
        Assert.Null(venue.DeletedAtUtc);
        Assert.True(venue.IsActive);

        // No audit row for a refused deletion: nothing changed.
        Assert.False(await verify.PlatformAuditLogs.AnyAsync(l => l.TargetId == branch.VenueId && l.Action == "venue.delete"));
    }

    [SkippableFact]
    public async Task A_venue_is_never_hard_deleted()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var deleted = await service.DeleteVenueAsync(branch.VenueId);

        Assert.True(deleted.Venue.IsDeleted);
        Assert.False(deleted.Venue.IsActive);

        await using var verify = fixture.CreateContext(clock);
        Assert.True(await verify.Venues.AnyAsync(v => v.Id == branch.VenueId));
        Assert.Equal(3, await verify.DiningTables.CountAsync(t => t.BranchId == branch.BranchId));

        // And it stays deleted: nothing more can be done to it.
        await using var laterDb = fixture.CreateContext(clock);
        var later = fixture.CreatePlatformService(laterDb, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        await Assert.ThrowsAsync<DomainStateException>(() => later.SuspendVenueAsync(branch.VenueId));
    }

    [SkippableFact]
    public async Task Only_a_platform_admin_may_act_on_the_platform_tier()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var asManager = fixture.CreatePlatformService(db, clock, TestActor.Manager(branch.ManagerId));

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.CreateVenueAsync(new CreateVenueCommand("Nope", VenueType.Cafe, "nope-" + Guid.NewGuid().ToString("N")[..8], Branch("x"))));

        await Assert.ThrowsAsync<StaffPermissionException>(() => asManager.SuspendVenueAsync(branch.VenueId));
    }

    [SkippableFact]
    public async Task The_venue_list_searches_pages_and_rolls_up_the_tier()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var service = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var marker = Guid.NewGuid().ToString("N")[..8];

        var venue = await service.CreateVenueAsync(new CreateVenueCommand($"Rollup {marker}", VenueType.Cafe, $"rollup-{marker}", Branch("a")));
        var paid = await service.AddBranchAsync(venue.Venue.VenueId, Branch("b") with { SubscriptionTier = SubscriptionTier.Paid });

        var page = await service.ListVenuesAsync(new VenueListQuery(Search: marker, Page: 1, PageSize: 10));

        var row = Assert.Single(page.Items);
        Assert.Equal(2, row.BranchCount);
        Assert.Equal(1, row.PaidBranchCount);
        Assert.Equal(SubscriptionTier.Free, row.SubscriptionTier); // not every branch is paid

        await service.UpdateBranchAsync(venue.Branches[0].BranchId, new UpdateBranchCommand(SubscriptionTier: SubscriptionTier.Paid));

        var after = await service.GetVenueAsync(venue.Venue.VenueId);
        Assert.Equal(SubscriptionTier.Paid, after.Venue.SubscriptionTier);
        Assert.Equal(SubscriptionTier.Paid, after.Branches.Single(b => b.BranchId == paid.BranchId).SubscriptionTier);
    }

    // ------------------------------------------------------------ helpers

    private static CreateBranchCommand Branch(string slugPart) => new(
        Name: $"Branch {slugPart}",
        Slug: $"{slugPart}-{Guid.NewGuid():N}"[..24],
        Address: "1 Test Street, Yerevan",
        Latitude: 40.18,
        Longitude: 44.51,
        TimeZoneId: "Asia/Yerevan",
        FloorWidth: 1000,
        FloorHeight: 700);

    private async Task AssertAuditAsync(YallaDbContext _, Guid actorId, Guid targetId, string action)
    {
        await using var verify = fixture.CreateContext(new TestClock(Now));

        var row = await verify.PlatformAuditLogs.AsNoTracking()
            .Where(l => l.TargetId == targetId && l.Action == action)
            .OrderByDescending(l => l.AtUtc)
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        Assert.Equal(actorId, row!.ActorStaffMemberId);
    }
}
