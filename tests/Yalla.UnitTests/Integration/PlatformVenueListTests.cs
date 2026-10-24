using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Application.Platform;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The venue list as the console's table reads it: rating and review count over published reviews,
/// active tables, open tabs, the first readiness issue, the cover thumbnail, every branch's tier, the
/// one-word state - and the status and tier filters, the district search and the sorts.
/// </summary>
/// <remarks>
/// Each test names its venues with a marker of its own and searches for it, so the shared database's
/// other venues are not in its pages.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class PlatformVenueListTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_row_carries_what_the_venue_table_shows()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var marker = Marker();

        // Branch one: ready but for its cover, four tables of which one is retired, a listing.
        var main = await TestBranchBuilder.CreateAsync(db, tableCount: 4, subscriptionTier: SubscriptionTier.Paid);
        await ConsoleTestData.RenameVenueAsync(db, main.VenueId, $"Cascade {marker}");
        await TestMenuBuilder.CreateAsync(db, main.BranchId);
        await ReviewTestData.SavePolicyAsync(db, main.BranchId, clock.UtcNow);
        db.StaffDevices.Add(new StaffDevice(main.VenueId, main.BranchId, "Tablet", $"client-{Guid.NewGuid():N}", clock.UtcNow));
        var retired = await db.DiningTables.FirstAsync(t => t.Id == main.TableIds[3]);
        retired.SetActive(false);
        var mainRow = await db.Branches.FirstAsync(b => b.Id == main.BranchId);
        mainRow.UpdateListing("Armenian", null, null, null, null, "Kentron");
        await db.SaveChangesAsync();

        // Branch two: free, with a cover and no menu.
        var secondId = await ConsoleTestData.AddBranchAsync(db, main.VenueId, "Zz Arabkir", SubscriptionTier.Free);
        var cover = await TestMenuBuilder.AddPhotoAsync(db, secondId);
        (await db.Branches.FirstAsync(b => b.Id == secondId)).SetCoverPhoto(cover);
        await db.SaveChangesAsync();
        var coverUrl = await db.Photos.Where(p => p.Id == cover).Select(p => p.ThumbnailPath).FirstAsync();

        // Reviews: 5, 4 and 3 published, a hidden 1 that does not count.
        foreach (var (rating, hidden) in new[] { (5, false), (4, false), (3, false), (1, true) })
        {
            var diner = await ReviewTestData.SeedDinerAsync(db, "Reviewer", clock.UtcNow);
            var reviewId = await ReviewTestData.SeedReviewAsync(db, rating == 3 ? secondId : main.BranchId, diner, rating, null, clock.UtcNow);

            if (hidden)
            {
                var review = await db.BranchReviews.FirstAsync(r => r.Id == reviewId);
                review.Hide(admin.StaffMemberId, byPlatform: true, "Spam", clock.UtcNow);
                await db.SaveChangesAsync();
            }
        }

        // Two tabs live, one settled.
        await ConsoleTestData.AddTabAsync(db, main, 0, clock.UtcNow, TabStatus.Open);
        await ConsoleTestData.AddTabAsync(db, main, 1, clock.UtcNow, TabStatus.Closing);
        await ConsoleTestData.AddTabAsync(db, main, 2, clock.UtcNow, TabStatus.Closed);

        var row = Assert.Single((await platform.ListVenuesAsync(new VenueListQuery(Search: marker))).Items);

        Assert.Equal(main.VenueId, row.VenueId);
        Assert.Equal(VenueStatus.Active, row.Status);
        Assert.Equal(2, row.BranchCount);
        Assert.Equal(3, row.TableCount);
        Assert.Equal(1, row.PaidBranchCount);
        Assert.Equal(TierMix.Mixed, row.TierMix);
        Assert.Equal(SubscriptionTier.Free, row.SubscriptionTier);
        Assert.Equal(4.0, row.AverageRating);
        Assert.Equal(3, row.ReviewCount);
        Assert.Equal(2, row.OpenTabCount);
        Assert.Equal("Kentron", row.District);
        Assert.Equal("Armenian", row.Cuisine);
        Assert.Equal(await db.Venues.Where(v => v.Id == main.VenueId).Select(v => v.CreatedAtUtc).FirstAsync(), row.CreatedAtUtc);

        // Every branch with its own tier and what a guest would find missing, active ones first, then
        // by name. The main branch's checklist is done but for its cover; the second has nothing yet -
        // and a missing tablet or policy is the venue's own work, not something a guest sees.
        Assert.Equal([main.BranchId, secondId], row.Branches.Select(b => b.BranchId));

        // Each branch's own district, so the console can search every branch's; absent when it has none.
        Assert.Equal(["Kentron", null], row.Branches.Select(b => b.District));
        Assert.Equal([SubscriptionTier.Paid, SubscriptionTier.Free], row.Branches.Select(b => b.SubscriptionTier));
        Assert.Equal([ReadinessIssueCodes.NoPhoto], row.Branches[0].Issues.Select(i => i.Code));
        Assert.Equal(
            [ReadinessIssueCodes.NoTables, ReadinessIssueCodes.NoMenu, ReadinessIssueCodes.NoHours],
            row.Branches[1].Issues.Select(i => i.Code));

        // The first issue of the first branch that has one, and where; both branches have one.
        var mainName = await db.Branches.Where(b => b.Id == main.BranchId).Select(b => b.Name).FirstAsync();
        Assert.Equal(new VenueReadinessIssue(ReadinessIssueCodes.NoPhoto, null, mainName), row.ReadinessIssue);
        Assert.Equal(2, row.NotReadyBranchCount);

        // The thumbnail is the first branch that has a cover.
        Assert.Equal(coverUrl, row.CoverThumbnailUrl);

        // The detail carries the same row, and its branches their open tabs and active tables.
        var detail = await platform.GetVenueAsync(main.VenueId);
        Assert.Equal(row, detail.Venue with { Branches = row.Branches });
        Assert.Equal(row.Branches.Select(b => b.BranchId), detail.Venue.Branches.Select(b => b.BranchId));
        var mainDetail = Assert.Single(detail.Branches, b => b.BranchId == main.BranchId);
        Assert.Equal(3, mainDetail.TableCount);
        Assert.Equal(2, mainDetail.OpenTabCount);
        Assert.Equal("Kentron", mainDetail.District);
        Assert.Equal(0, Assert.Single(detail.Branches, b => b.BranchId == secondId).OpenTabCount);
    }

    [SkippableFact]
    public async Task An_unfinished_menu_is_the_issue_with_its_count_and_a_deleted_venue_has_none()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var marker = Marker();

        var branch = await TestBranchBuilder.CreateAsync(db);
        await ConsoleTestData.RenameVenueAsync(db, branch.VenueId, $"Riverside {marker}");
        var category = new Yalla.Domain.Menus.MenuCategory(branch.BranchId, "Mains", 0);
        db.MenuCategories.Add(category);
        db.MenuItems.Add(new Yalla.Domain.Menus.MenuItem(category.Id, "Trout", null, 4_000L, null, null, null, null, null));
        db.MenuItems.Add(new Yalla.Domain.Menus.MenuItem(category.Id, "Salad", null, 1_500L, null, null, null, null, null));
        db.MenuItems.Add(new Yalla.Domain.Menus.MenuItem(category.Id, "Bread", null, 300L, null, null, null, null, null));
        await db.SaveChangesAsync();

        var row = Assert.Single((await platform.ListVenuesAsync(new VenueListQuery(Search: marker))).Items);
        var branchName = await db.Branches.Where(b => b.Id == branch.BranchId).Select(b => b.Name).FirstAsync();
        Assert.Equal(new VenueReadinessIssue(ReadinessIssueCodes.IncompleteDishes, 3, branchName), row.ReadinessIssue);

        var deleted = await TestBranchBuilder.CreateAsync(db);
        await ConsoleTestData.RenameVenueAsync(db, deleted.VenueId, $"Old Town {marker}");
        await platform.DeleteVenueAsync(deleted.VenueId);

        var gone = Assert.Single((await platform.ListVenuesAsync(new VenueListQuery(Search: $"Old Town {marker}", Status: "deleted"))).Items);
        Assert.Equal(VenueStatus.Deleted, gone.Status);
        Assert.Null(gone.ReadinessIssue);
        Assert.Equal(0, gone.NotReadyBranchCount);
        Assert.All(gone.Branches, b => Assert.Empty(b.Issues));
    }

    [SkippableFact]
    public async Task The_list_never_names_a_switched_off_branch_but_still_counts_a_suspended_venues_setup()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var marker = Marker();

        // A finished branch beside a switched-off one that has nothing: nothing to name.
        var ready = await ConsoleTestData.ReadyBranchAsync(db, clock.UtcNow, $"Ready {marker}");
        var off = await ConsoleTestData.AddBranchAsync(db, ready.VenueId, "Aaa Closed for works", SubscriptionTier.Free, isActive: false);

        var row = Assert.Single((await platform.ListVenuesAsync(new VenueListQuery(Search: $"Ready {marker}"))).Items);
        Assert.Null(row.ReadinessIssue);
        Assert.Equal(0, row.NotReadyBranchCount);
        Assert.Empty(Assert.Single(row.Branches, b => b.BranchId == off).Issues);

        // A suspended venue is still being set up: the list shows what is left, Home does not.
        var suspended = await TestBranchBuilder.CreateAsync(db);
        await ConsoleTestData.RenameVenueAsync(db, suspended.VenueId, $"Paused {marker}");
        await platform.SuspendVenueAsync(suspended.VenueId);

        var paused = Assert.Single((await platform.ListVenuesAsync(new VenueListQuery(Search: $"Paused {marker}"))).Items);
        Assert.Equal(ReadinessIssueCodes.NoMenu, paused.ReadinessIssue!.Code);
    }

    [SkippableFact]
    public async Task The_status_filter_hides_deleted_venues_by_default_and_selects_each_state()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var marker = Marker();

        var active = await Named(db, marker, "Active");
        var suspended = await Named(db, marker, "Suspended");
        var off = await Named(db, marker, "Off");
        var deleted = await Named(db, marker, "Deleted");

        await platform.SuspendVenueAsync(suspended.VenueId);
        await platform.UpdateVenueAsync(off.VenueId, new UpdateVenueCommand(IsActive: false));
        await platform.DeleteVenueAsync(deleted.VenueId);

        async Task<Guid[]> Ids(string? status = null, bool includeDeleted = false) =>
            [.. (await platform.ListVenuesAsync(new VenueListQuery(Search: marker, IncludeDeleted: includeDeleted, Status: status))).Items.Select(v => v.VenueId)];

        Assert.Equal([active.VenueId, off.VenueId, suspended.VenueId], await Ids());
        Assert.Equal([active.VenueId, off.VenueId, suspended.VenueId], await Ids("live"));
        Assert.Equal([active.VenueId], await Ids("active"));
        Assert.Equal([suspended.VenueId], await Ids("SUSPENDED"));
        Assert.Equal([off.VenueId], await Ids("inactive"));
        Assert.Equal([deleted.VenueId], await Ids("deleted"));
        Assert.Equal([active.VenueId, deleted.VenueId, off.VenueId, suspended.VenueId], await Ids("all"));
        Assert.Equal([active.VenueId, deleted.VenueId, off.VenueId, suspended.VenueId], await Ids(includeDeleted: true));

        // A status sent beside the old switch wins.
        Assert.Equal([deleted.VenueId], await Ids("deleted", includeDeleted: false));
        Assert.Equal([active.VenueId], await Ids("active", includeDeleted: true));

        var page = await platform.ListVenuesAsync(new VenueListQuery(Search: marker, Status: "all"));
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(VenueStatus.Inactive, page.Items.Single(v => v.VenueId == off.VenueId).Status);
        Assert.Equal(VenueStatus.Suspended, page.Items.Single(v => v.VenueId == suspended.VenueId).Status);
    }

    [SkippableFact]
    public async Task The_tier_filter_reads_every_branch_and_the_search_reaches_branch_names_and_districts()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var marker = Marker();

        var paid = await Named(db, marker, "Paid", SubscriptionTier.Paid);
        var free = await Named(db, marker, "Free", SubscriptionTier.Free);
        var mixed = await Named(db, marker, "Mixed", SubscriptionTier.Paid);
        await ConsoleTestData.AddBranchAsync(db, mixed.VenueId, "Second", SubscriptionTier.Free);

        async Task<Guid[]> Ids(string tier) =>
            [.. (await platform.ListVenuesAsync(new VenueListQuery(Search: marker, Tier: tier))).Items.Select(v => v.VenueId)];

        Assert.Equal([paid.VenueId], await Ids("paid"));
        Assert.Equal([free.VenueId], await Ids("free"));
        Assert.Equal([mixed.VenueId], await Ids("Mixed"));

        // Blank is every tier - the empty select - not the first value the parameter takes.
        Assert.Equal([free.VenueId, mixed.VenueId, paid.VenueId], await Ids(""));
        Assert.Equal([free.VenueId, mixed.VenueId, paid.VenueId], await Ids("  "));

        // The search finds a venue by a branch's name and by a branch's district.
        var branchName = $"Terrace {Marker()}";
        await ConsoleTestData.AddBranchAsync(db, free.VenueId, branchName, SubscriptionTier.Free);
        var district = $"Arabkir{Marker()}";
        (await db.Branches.FirstAsync(b => b.Id == paid.BranchId)).UpdateListing(null, null, null, null, null, district);
        await db.SaveChangesAsync();

        Assert.Equal(free.VenueId, Assert.Single((await platform.ListVenuesAsync(new VenueListQuery(Search: branchName))).Items).VenueId);
        Assert.Equal(paid.VenueId, Assert.Single((await platform.ListVenuesAsync(new VenueListQuery(Search: district))).Items).VenueId);
    }

    [SkippableFact]
    public async Task The_list_sorts_by_rating_tables_and_creation_either_way_with_unrated_venues_lowest()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var marker = Marker();

        var high = await Named(db, marker, "C High", tables: 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        var low = await Named(db, marker, "A Low", tables: 5);
        clock.Advance(TimeSpan.FromMinutes(1));
        var unrated = await Named(db, marker, "B Unrated", tables: 3);

        await ReviewAsync(db, high.BranchId, 5, clock);
        await ReviewAsync(db, low.BranchId, 2, clock);

        async Task<Guid[]> Ids(string sort, string? dir = null) =>
            [.. (await platform.ListVenuesAsync(new VenueListQuery(Search: marker, Sort: sort, Dir: dir))).Items.Select(v => v.VenueId)];

        Assert.Equal([low.VenueId, unrated.VenueId, high.VenueId], await Ids("name"));
        Assert.Equal([high.VenueId, unrated.VenueId, low.VenueId], await Ids("name", "desc"));
        Assert.Equal([high.VenueId, low.VenueId, unrated.VenueId], await Ids("rating", "desc"));
        Assert.Equal([unrated.VenueId, low.VenueId, high.VenueId], await Ids("rating", "asc"));
        Assert.Equal([low.VenueId, unrated.VenueId, high.VenueId], await Ids("tables", "DESC"));
        Assert.Equal([high.VenueId, unrated.VenueId, low.VenueId], await Ids("tables"));
        Assert.Equal([unrated.VenueId, low.VenueId, high.VenueId], await Ids("created", "desc"));
    }

    [SkippableTheory]
    [InlineData("status", "gone")]
    [InlineData("tier", "gold")]
    [InlineData("sort", "popularity")]
    [InlineData("dir", "up")]
    public async Task A_filter_or_sort_it_does_not_know_is_refused_naming_the_parameter(string parameter, string value)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var query = parameter switch
        {
            "status" => new VenueListQuery(Status: value),
            "tier" => new VenueListQuery(Tier: value),
            "sort" => new VenueListQuery(Sort: value),
            _ => new VenueListQuery(Dir: value),
        };

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => platform.ListVenuesAsync(query));
        Assert.Equal(parameter, refused.ParamName);
    }

    private static string Marker() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<TestBranch> Named(
        YallaDbContext db,
        string marker,
        string name,
        SubscriptionTier tier = SubscriptionTier.Free,
        int tables = 2)
    {
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: tables, subscriptionTier: tier);
        await ConsoleTestData.RenameVenueAsync(db, branch.VenueId, $"{name} {marker}");

        return branch;
    }

    private static async Task ReviewAsync(YallaDbContext db, Guid branchId, int rating, TestClock clock)
    {
        var diner = await ReviewTestData.SeedDinerAsync(db, "Reviewer", clock.UtcNow);
        await ReviewTestData.SeedReviewAsync(db, branchId, diner, rating, null, clock.UtcNow);
    }
}
