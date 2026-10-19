using Microsoft.EntityFrameworkCore;
using Yalla.Application.Menus;
using Yalla.Domain;
using Yalla.Domain.Menus;

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

        // Two consoles, two connections, opposite orders at the same moment.
        await using var one = fixture.CreateContext(clock);
        await using var two = fixture.CreateContext(clock);

        var reversed = Enumerable.Reverse(ids).ToList();

        await Task.WhenAll(
            fixture.CreateMenuService(one).ReorderCategoriesAsync(branch.BranchId, new ReorderMenuCategoriesCommand(ids)),
            fixture.CreateMenuService(two).ReorderCategoriesAsync(branch.BranchId, new ReorderMenuCategoriesCommand(reversed)));

        await using var verify = fixture.CreateContext(clock);
        var stored = await verify.MenuCategories
            .Where(c => c.BranchId == branch.BranchId)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new { c.Id, c.DisplayOrder })
            .ToListAsync();

        // One of the two whole - never a mixture with two categories on one number.
        Assert.Equal([0, 1, 2, 3, 4], stored.Select(c => c.DisplayOrder));
        Assert.True(
            stored.Select(c => c.Id).SequenceEqual(ids) || stored.Select(c => c.Id).SequenceEqual(reversed),
            "The two reorders interleaved.");
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
