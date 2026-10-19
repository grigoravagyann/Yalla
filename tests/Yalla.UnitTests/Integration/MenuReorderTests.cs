using Microsoft.EntityFrameworkCore;
using Yalla.Application.Menus;
using Yalla.Domain;
using Yalla.Domain.Menus;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The console's Menu screen: categories and dishes moved up and down in one request each, new ones landing
/// last, and each dish saying which of its fields it still lacks.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class MenuReorderTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 12, 30, 8, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task Categories_take_the_order_they_are_sent_in_and_repair_old_ties()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);

        // A menu typed in before reordering existed: every category on 0, sorted by name.
        var coffee = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Coffee", 0));
        var desserts = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Desserts", 0));
        var mains = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Mains", 0));

        // Mains up to the top: one request, whatever the old numbers were.
        var reordered = await menu.ReorderCategoriesAsync(
            branch.BranchId, new ReorderMenuCategoriesCommand([mains.Id, coffee.Id, desserts.Id]));

        Assert.Equal([mains.Id, coffee.Id, desserts.Id], reordered.Select(c => c.Id));
        Assert.Equal([0, 1, 2], reordered.Select(c => c.DisplayOrder));

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(
            [mains.Id, coffee.Id, desserts.Id],
            (await fixture.CreateMenuService(verify).GetMenuAsync(branch.BranchId)).Select(c => c.Id));
    }

    [SkippableFact]
    public async Task A_stale_or_foreign_list_is_refused_whole_and_nothing_moves()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var other = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);

        var first = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("First"));
        var second = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Second"));
        var theirs = await menu.CreateCategoryAsync(other.BranchId, new CreateMenuCategoryCommand("Theirs"));

        foreach (var stale in new Guid[][] { [second.Id], [second.Id, second.Id], [second.Id, theirs.Id], [second.Id, first.Id, theirs.Id] })
        {
            var refused = await Assert.ThrowsAsync<FieldValidationException>(
                () => menu.ReorderCategoriesAsync(branch.BranchId, new ReorderMenuCategoriesCommand(stale)));

            Assert.Equal("categoryIds", refused.Field);
            Assert.Equal(FieldBounds.Conflict, refused.Violations.Single().Bound);
        }

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(
            [first.Id, second.Id],
            (await fixture.CreateMenuService(verify).GetMenuAsync(branch.BranchId)).Select(c => c.Id));
    }

    [SkippableFact]
    public async Task Dishes_move_within_their_category_and_new_ones_land_last()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var other = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);

        var coffee = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Coffee"));
        var tea = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Tea"));

        // Added without a position: after the ones before, not on top of them.
        Assert.Equal([0, 1], new[] { coffee.DisplayOrder, tea.DisplayOrder });

        var latte = await menu.CreateItemAsync(branch.BranchId, coffee.Id, new CreateMenuItemCommand("Latte", 1_400L));
        var espresso = await menu.CreateItemAsync(branch.BranchId, coffee.Id, new CreateMenuItemCommand("Espresso", 800L));
        var flat = await menu.CreateItemAsync(branch.BranchId, coffee.Id, new CreateMenuItemCommand("Flat white", 1_500L));

        Assert.Equal([0, 1, 2], new[] { latte.DisplayOrder, espresso.DisplayOrder, flat.DisplayOrder });

        // Flat white up one.
        var moved = await menu.ReorderItemsAsync(
            branch.BranchId, coffee.Id, new ReorderMenuItemsCommand([latte.Id, flat.Id, espresso.Id]));

        Assert.Equal([latte.Id, flat.Id, espresso.Id], moved.Items.Select(i => i.Id));
        Assert.Equal([0, 1, 2], moved.Items.Select(i => i.DisplayOrder));

        // Not every item of the category, or a category of another branch.
        var refused = await Assert.ThrowsAsync<FieldValidationException>(
            () => menu.ReorderItemsAsync(branch.BranchId, coffee.Id, new ReorderMenuItemsCommand([latte.Id, flat.Id])));
        Assert.Equal("itemIds", refused.Field);

        var theirs = await menu.CreateCategoryAsync(other.BranchId, new CreateMenuCategoryCommand("Theirs"));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => menu.ReorderItemsAsync(branch.BranchId, theirs.Id, new ReorderMenuItemsCommand([])));
    }

    [SkippableFact]
    public async Task Two_reorders_at_once_leave_every_category_on_its_own_position()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        TestBranch branch;
        List<Guid> ids;

        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
            var menu = fixture.CreateMenuService(db);
            ids = [];

            foreach (var name in new[] { "A", "B", "C", "D", "E" })
            {
                ids.Add((await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand(name))).Id);
            }
        }

        var shuffled = new List<Guid> { ids[2], ids[0], ids[4], ids[1], ids[3] };
        var reversed = Enumerable.Reverse(ids).ToList();

        // The second console, on its own connection - its session id is what the server reports it waiting under.
        await using var two = fixture.CreateContext(clock);
        var twoSession = await SessionIdAsync(two);
        Task<IReadOnlyList<MenuCategoryView>>? second = null;
        var secondWaited = false;

        // The first console has read the categories inside its lock and is about to write them: right then, the
        // second one starts - every run, not when the timing happens to line up - and must be made to wait.
        var hook = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDATE [MenuCategories]", StringComparison.Ordinal),
            async () =>
            {
                second = fixture.CreateMenuService(two)
                    .ReorderCategoriesAsync(branch.BranchId, new ReorderMenuCategoriesCommand(reversed));
                secondWaited = await WaitsOnALockAsync(clock, twoSession);
            });

        await using var one = fixture.CreateContext(clock, hook);
        await fixture.CreateMenuService(one).ReorderCategoriesAsync(branch.BranchId, new ReorderMenuCategoriesCommand(shuffled));

        Assert.True(hook.Fired);
        Assert.NotNull(second);
        await second;

        // It waited for the first to commit, then read the first's order and replaced it whole with its own.
        Assert.True(secondWaited, "The second reorder ran while the first held the categories it had read.");

        await using var verify = fixture.CreateContext(clock);
        var stored = await verify.MenuCategories
            .Where(c => c.BranchId == branch.BranchId)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new { c.Id, c.DisplayOrder })
            .ToListAsync();

        Assert.Equal([0, 1, 2, 3, 4], stored.Select(c => c.DisplayOrder));
        Assert.Equal(reversed, stored.Select(c => c.Id));
    }

    [SkippableFact]
    public async Task Two_categories_added_at_once_land_on_two_positions()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        TestBranch branch;

        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
        }

        await using var two = fixture.CreateContext(clock);
        var twoSession = await SessionIdAsync(two);
        Task<MenuCategoryView>? second = null;
        var secondWaited = false;

        // The first has read the last position and is about to take it; the second asks for the last position now.
        var hook = new BeforeFirstCommandMatching(
            sql => sql.Contains("INSERT INTO [MenuCategories]", StringComparison.Ordinal),
            async () =>
            {
                second = fixture.CreateMenuService(two).CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Tea"));
                secondWaited = await WaitsOnALockAsync(clock, twoSession);
            });

        await using var one = fixture.CreateContext(clock, hook);
        var first = await fixture.CreateMenuService(one).CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Coffee"));

        Assert.True(hook.Fired);
        Assert.NotNull(second);
        var tea = await second;

        Assert.True(secondWaited, "The second add read the last position while the first was taking it.");
        Assert.Equal([0, 1], new[] { first.DisplayOrder, tea.DisplayOrder });
    }

    [SkippableFact]
    public async Task Each_dish_names_the_fields_it_still_lacks_from_the_one_rule()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Mains"));

        var bare = await menu.CreateItemAsync(branch.BranchId, category.Id, new CreateMenuItemCommand("Khachapuri", 3_200L));

        Assert.False(bare.IsComplete);
        Assert.Equal(["photo", "description", "ingredients", "allergens", "portionSize", "prepMinutes"], bare.MissingFields);

        var half = await menu.UpdateItemAsync(
            branch.BranchId,
            bare.Id,
            new UpdateMenuItemCommand(
                Description: "Adjarian, with an egg", Ingredients: "flour, sulguni, egg", Allergens: "gluten, dairy, egg",
                PortionSize: "450 g"));

        Assert.Equal(["photo", "prepMinutes"], half.MissingFields);

        var listed = (await menu.GetMenuAsync(branch.BranchId)).Single().Items.Single();
        Assert.Equal(half.MissingFields, listed.MissingFields);
    }

    [SkippableFact]
    public async Task Removing_a_dishs_photo_makes_it_incomplete_hides_it_from_diners_and_leaves_the_picture_to_the_sweep()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        // Uploaded long ago, so once nothing uses it the sweep's grace is already over.
        var photo = TestBranchBuilder.RoomPhoto(branch.BranchId);
        db.Photos.Add(photo);
        await db.SaveChangesAsync();

        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Mains"));
        var dish = await menu.CreateItemAsync(
            branch.BranchId,
            category.Id,
            new CreateMenuItemCommand(
                "Khachapuri", 3_200L, "Adjarian, with an egg", photo.Id, "flour, sulguni, egg", "gluten, dairy, egg",
                "450 g", 20));

        Assert.True(dish.IsComplete);

        var removed = await menu.RemoveItemPhotoAsync(branch.BranchId, dish.Id);

        Assert.Null(removed.Photo);
        Assert.False(removed.IsComplete);
        Assert.Equal(["photo"], removed.MissingFields);

        // Saying it twice is the same answer, not a 404.
        Assert.Equal(["photo"], (await menu.RemoveItemPhotoAsync(branch.BranchId, dish.Id)).MissingFields);

        // Gone from the diner's menu until it has a photo again.
        await using var read = fixture.CreateContext(clock);
        var dinerMenu = await SqlServerFixture.CreateMenuQuery(read).GetBranchMenuAsync(branch.BranchId);
        Assert.DoesNotContain(dish.Id, dinerMenu.Categories.SelectMany(c => c.Items).Select(i => i.Id));

        // Nothing uses the picture now, so the sweep takes it like any other detached photo.
        var photos = new PhotoService(
            read,
            new Yalla.Infrastructure.Media.LocalDiskPhotoStorage(
                new Yalla.Infrastructure.Media.PhotoStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "yalla-sweep-" + Guid.NewGuid().ToString("N")) },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Yalla.Infrastructure.Media.LocalDiskPhotoStorage>.Instance),
            clock,
            fixture.CreateBranchGuard(read, TestActor.Manager(branch.ManagerId)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PhotoService>.Instance);

        await photos.SweepOrphansAsync();

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.Photos.AnyAsync(p => p.Id == photo.Id));

        // Another branch's dish is not this branch's to change.
        var other = await TestBranchBuilder.CreateAsync(db);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => menu.RemoveItemPhotoAsync(other.BranchId, dish.Id));
    }

    /// <summary>The server's id for the session on this context's connection, which it keeps open.</summary>
    private static async Task<short> SessionIdAsync(Yalla.Infrastructure.Persistence.YallaDbContext db)
    {
        await db.Database.OpenConnectionAsync();

        return await db.Database.SqlQueryRaw<short>("SELECT @@SPID AS Value").SingleAsync();
    }

    /// <summary>
    /// Whether the session is seen waiting on a lock within four seconds - less than the five a branch save waits
    /// before giving up. Asked of the server, so it is a fact about the lock and not about how fast this machine is.
    /// </summary>
    private async Task<bool> WaitsOnALockAsync(TestClock clock, short session)
    {
        await using var watcher = fixture.CreateContext(clock);
        var until = DateTime.UtcNow.AddSeconds(4);

        while (DateTime.UtcNow < until)
        {
            var waiting = await watcher.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys.dm_os_waiting_tasks WHERE session_id = {session} AND wait_type LIKE 'LCK%'")
                .SingleAsync();

            if (waiting > 0)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    [Fact]
    public void Missing_fields_are_empty_exactly_when_the_rule_says_complete()
    {
        var photoId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();

        // Every combination of the six fields present or absent.
        for (var mask = 0; mask < 64; mask++)
        {
            bool Has(int bit) => (mask & (1 << bit)) != 0;

            var item = new MenuItem(
                categoryId,
                "Dish",
                Has(1) ? "A description" : null,
                1_000L,
                Has(0) ? photoId : null,
                Has(2) ? "flour" : null,
                Has(3) ? "gluten" : null,
                Has(4) ? "200 g" : null,
                Has(5) ? 10 : null);

            var missing = MenuItemCompleteness.MissingFields(item);

            Assert.Equal(MenuItemCompleteness.IsComplete(item), missing.Count == 0);
            Assert.Equal(6 - Enumerable.Range(0, 6).Count(Has), missing.Count);
        }
    }
}
