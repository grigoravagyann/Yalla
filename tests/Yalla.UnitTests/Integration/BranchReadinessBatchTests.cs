using Yalla.Application.BranchSettings;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The readiness checklist for many branches at once is the checklist for each one: the batch the
/// platform's venue list and Home read gives every branch the view a manager's own read gives it, and
/// the issue codes say what the sentences say, line for line.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class BranchReadinessBatchTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task The_batch_gives_each_branch_its_own_checklist_and_the_codes_follow_the_sentences()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var readiness = SqlServerFixture.CreateReadinessQuery(db);

        var ready = await ConsoleTestData.ReadyBranchAsync(db, clock.UtcNow);
        var bare = await TestBranchBuilder.CreateAsync(db);
        var empty = await ConsoleTestData.AddBranchAsync(db, bare.VenueId, "Nothing yet", SubscriptionTier.Free);

        // A venue-wide owner counts as staff at every branch of the venue, the empty one included.
        await AuthTestData.SeedOwnerAsync(db, bare.VenueId);

        var unknown = Guid.NewGuid();
        var views = await readiness.GetManyAsync([ready.BranchId, bare.BranchId, empty, unknown, bare.BranchId]);

        Assert.Equal(3, views.Count);
        Assert.False(views.ContainsKey(unknown));

        foreach (var id in new[] { ready.BranchId, bare.BranchId, empty })
        {
            var one = await readiness.GetAsync(id);
            var batched = views[id];

            Assert.Equal(one with { IncompleteMenuItemIds = [], Blockers = [], Issues = [] }, batched with { IncompleteMenuItemIds = [], Blockers = [], Issues = [] });
            Assert.Equal(one.IncompleteMenuItemIds, batched.IncompleteMenuItemIds);
            Assert.Equal(one.Blockers, batched.Blockers);
            Assert.Equal(one.Issues, batched.Issues);
            Assert.Equal(batched.Blockers.Count, batched.Issues.Count);
        }

        Assert.True(views[ready.BranchId].IsReadyForDiners);
        Assert.Empty(views[ready.BranchId].Issues);

        Assert.Equal(
            [ReadinessIssueCodes.NoMenu, ReadinessIssueCodes.PolicyNotReviewed, ReadinessIssueCodes.NoDevice],
            views[bare.BranchId].Issues.Select(i => i.Code));

        Assert.Equal(
            [
                ReadinessIssueCodes.NoTables,
                ReadinessIssueCodes.NoMenu,
                ReadinessIssueCodes.NoHours,
                ReadinessIssueCodes.PolicyNotReviewed,
                ReadinessIssueCodes.NoDevice,
            ],
            views[empty].Issues.Select(i => i.Code));

        Assert.True(views[empty].StaffEnrolled);
        Assert.Equal(1, views[empty].StaffCount);

        // An empty request asks nothing of the database and answers nothing.
        Assert.Empty(await readiness.GetManyAsync([]));
    }

    [SkippableFact]
    public async Task Unfinished_dishes_are_an_issue_with_their_count()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(TestNow.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var category = new Yalla.Domain.Menus.MenuCategory(branch.BranchId, "Mains", 0);
        db.MenuCategories.Add(category);
        db.MenuItems.Add(new Yalla.Domain.Menus.MenuItem(category.Id, "Trout", null, 4_000L, null, null, null, null, null));
        db.MenuItems.Add(new Yalla.Domain.Menus.MenuItem(category.Id, "Salad", null, 1_500L, null, null, null, null, null));
        await db.SaveChangesAsync();

        var view = await SqlServerFixture.CreateReadinessQuery(db).GetAsync(branch.BranchId);

        Assert.Equal(new ReadinessIssue(ReadinessIssueCodes.IncompleteDishes, 2), view.Issues[0]);
        Assert.Contains("2 menu item(s)", view.Blockers[0], StringComparison.Ordinal);
    }
}
