using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Which branches the managed-venue read hands a caller, decided from the caller's <i>stored</i>
/// staff row and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint test proves the route carries <c>VenueScoped</c>. This proves the query holds
/// its own lock as well, because removing either one on its own leaves the endpoint test green:
/// the policy refuses a neighbour before the query sees them, and the query refuses one after.
/// Two locks, each pinned separately.
/// </para>
/// <para>
/// The console used to work coverage out from the token's branch claim, which an owner does not
/// carry, and drew "no branch". The rule lives here now: an owner or a venue-wide manager covers
/// every branch, a manager whose row names a branch covers that one, the platform tier covers
/// everything, and everybody else is refused.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ManagedVenueQueryTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task An_owner_with_no_branch_covers_every_branch_of_the_venue()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var (venue, first, second) = await SeedVenueAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, venue);

        var view = await Query(db, TestActor.Owner(owner.StaffMemberId)).GetAsync(venue);

        Assert.Equal(venue, view.VenueId);
        Assert.Equal(new HashSet<Guid> { first, second }, view.Branches.Select(b => b.BranchId).ToHashSet());
        Assert.All(view.Branches, b => Assert.Equal(venue, b.VenueId));
    }

    /// <summary>An owner's row can name a branch; it is their home, not their limit.</summary>
    [SkippableFact]
    public async Task An_owner_with_a_home_branch_still_covers_every_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var (venue, first, second) = await SeedVenueAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, venue, branchId: first);

        var view = await Query(db, TestActor.Owner(owner.StaffMemberId)).GetAsync(venue);

        Assert.Equal(new HashSet<Guid> { first, second }, view.Branches.Select(b => b.BranchId).ToHashSet());
    }

    [SkippableFact]
    public async Task A_manager_with_a_home_branch_covers_that_branch_only()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var (venue, first, _) = await SeedVenueAsync(db);
        var manager = await AuthTestData.SeedManagerAsync(db, venue, branchId: first);

        var view = await Query(db, TestActor.Manager(manager.StaffMemberId)).GetAsync(venue);

        var only = Assert.Single(view.Branches);
        Assert.Equal(first, only.BranchId);
    }

    [SkippableFact]
    public async Task A_manager_with_no_branch_covers_every_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var (venue, first, second) = await SeedVenueAsync(db);
        var manager = await AuthTestData.SeedManagerAsync(db, venue);

        var view = await Query(db, TestActor.Manager(manager.StaffMemberId)).GetAsync(venue);

        Assert.Equal(new HashSet<Guid> { first, second }, view.Branches.Select(b => b.BranchId).ToHashSet());
    }

    [SkippableFact]
    public async Task The_platform_admin_covers_every_branch_of_any_venue()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var (venue, first, second) = await SeedVenueAsync(db);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);

        var view = await Query(db, TestActor.PlatformAdmin(admin.StaffMemberId)).GetAsync(venue);

        Assert.Equal(new HashSet<Guid> { first, second }, view.Branches.Select(b => b.BranchId).ToHashSet());
    }

    [SkippableFact]
    public async Task An_unknown_venue_is_not_found_rather_than_empty()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => Query(db, TestActor.PlatformAdmin(admin.StaffMemberId)).GetAsync(Guid.CreateVersion7()));
    }

    /// <summary>
    /// Active branches first, then by name, then by id - so the console's "first branch" is a live
    /// one, and an inactive branch is listed and flagged rather than hidden.
    /// </summary>
    [SkippableFact]
    public async Task Branches_are_listed_active_first_then_by_name_with_the_inactive_ones_flagged()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var branch = await TestBranchBuilder.CreateAsync(db);
        var venue = branch.VenueId;

        // Sorts first by name, so only the active flag can push it last.
        _ = await AuthTestData.AddBranchAsync(db, venue, "Asia/Yerevan", name: "Aaa Closed", isActive: false);
        // Three more active ones around the fixture's "Test Branch ...", created in an order that
        // is neither the name order nor its reverse, so a sort that fell back to the id (creation
        // order, roughly) cannot pass by luck.
        _ = await AuthTestData.AddBranchAsync(db, venue, "Asia/Yerevan", name: "Zzz Open");
        _ = await AuthTestData.AddBranchAsync(db, venue, "Asia/Yerevan", name: "Aaa Open");
        _ = await AuthTestData.AddBranchAsync(db, venue, "Asia/Yerevan", name: "Bbb Open");
        var owner = await AuthTestData.SeedOwnerAsync(db, venue);

        var view = await Query(db, TestActor.Owner(owner.StaffMemberId)).GetAsync(venue);

        var fixtureName = Assert.Single(view.Branches, b => b.BranchId == branch.BranchId).Name;
        Assert.StartsWith("Test Branch", fixtureName, StringComparison.Ordinal);

        var names = view.Branches.Select(b => b.Name).ToArray();
        Assert.Equal(["Aaa Open", "Bbb Open", fixtureName, "Zzz Open", "Aaa Closed"], names);
        Assert.Equal([true, true, true, true, false], view.Branches.Select(b => b.IsActive).ToArray());
    }

    // ------------------------------------------------------------ refusals

    [SkippableFact]
    public async Task A_deactivated_owner_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var (venue, _, _) = await SeedVenueAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, venue);

        (await db.StaffMembers.SingleAsync(s => s.Id == owner.StaffMemberId)).SetActive(false);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => Query(db, TestActor.Owner(owner.StaffMemberId)).GetAsync(venue));
    }

    /// <summary>The query's own venue lock, independent of the policy on the route.</summary>
    [SkippableFact]
    public async Task A_manager_of_another_venue_is_refused_by_the_query_itself()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var (venue, _, _) = await SeedVenueAsync(db);
        var theirs = await TestBranchBuilder.CreateAsync(db);

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => Query(db, TestActor.Manager(theirs.ManagerId)).GetAsync(venue));
    }

    [SkippableFact]
    public async Task A_waiter_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var branch = await TestBranchBuilder.CreateAsync(db);

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => Query(db, TestActor.Waiter(branch.WaiterId)).GetAsync(branch.VenueId));
    }

    // ------------------------------------------------------------ helpers

    private static ManagedVenueQuery Query(YallaDbContext db, ICurrentActor actor) => new(db, actor);

    /// <summary>A venue with two branches in different time zones, plus a neighbour to leak from.</summary>
    private static async Task<(Guid VenueId, Guid First, Guid Second)> SeedVenueAsync(YallaDbContext db)
    {
        var branch = await TestBranchBuilder.CreateAsync(db);
        var second = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Europe/London");
        _ = await TestBranchBuilder.CreateAsync(db);

        return (branch.VenueId, branch.BranchId, second);
    }
}
