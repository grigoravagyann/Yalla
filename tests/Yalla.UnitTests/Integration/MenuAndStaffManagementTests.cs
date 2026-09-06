using Microsoft.EntityFrameworkCore;
using Yalla.Application.Menus;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests.Integration;

/// <summary>Menu management and staff management against a real database.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class MenuAndStaffManagementTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 5, 14, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ 1. a name and a price, no more

    /// <summary>
    /// <b>Test 1.</b> An item saved with nothing but a name and a price succeeds, and says it is
    /// not complete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test used to assert the opposite. Prompt 6 required a photo, ingredients, allergens, a
    /// portion size and a prep time on create, and its reasoning was right - optional fields stay
    /// blank and the feature is worthless. The enforcement point was wrong: it meant an eighty-dish
    /// menu could not be entered without eighty photo uploads first, in order, before a single name
    /// or price could be typed, and somebody sitting in a cafe with the owner could not do the
    /// obvious thing and shoot the photographs the following week.
    /// </para>
    /// <para>
    /// So the rule moved rather than went away. The two tests below it are where it now lives.
    /// What is still refused here is what was never a partially entered item.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_item_with_only_a_name_and_a_price_is_saved_and_reports_itself_incomplete()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Mains"));

        var bare = await menu.CreateItemAsync(
            branch.BranchId, category.Id, new CreateMenuItemCommand("Khachapuri", 3_200L));

        Assert.Equal("Khachapuri", bare.Name);
        Assert.Equal(3_200L, bare.PriceAmd);
        Assert.True(bare.IsAvailable);

        // The whole point: it saved, and it says it is not fit to show anyone.
        Assert.False(bare.IsComplete);
        Assert.Null(bare.Photo);
        Assert.Null(bare.Allergens);
        Assert.Null(bare.PrepMinutes);

        // Half-entered is fine too, and filling the rest in later completes it - which is the
        // workflow the change exists for.
        var partial = await menu.CreateItemAsync(
            branch.BranchId,
            category.Id,
            new CreateMenuItemCommand("Lahmajun", 900L, Ingredients: "flour, lamb, tomato"));

        Assert.False(partial.IsComplete);

        var photoId = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);

        var finished = await menu.UpdateItemAsync(branch.BranchId, bare.Id, new UpdateMenuItemCommand(
            Description: "House khachapuri",
            PhotoId: photoId,
            Ingredients: "flour, cheese, egg",
            Allergens: "gluten, dairy, egg",
            PortionSize: "350 g",
            PrepMinutes: 15));

        Assert.True(finished.IsComplete);
        Assert.NotNull(finished.Photo);

        // Still refused: a thing with no name and a thing with a negative price are not items.
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => menu.CreateItemAsync(branch.BranchId, category.Id, new CreateMenuItemCommand("  ", 1_000L)));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => menu.CreateItemAsync(branch.BranchId, category.Id, new CreateMenuItemCommand("Free lunch", -1L)));

        // Optional is not unchecked. An explicit zero prep time is a typo rather than "no prep
        // time", and an empty guid is not a photo id.
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => menu.CreateItemAsync(
                branch.BranchId, category.Id, new CreateMenuItemCommand("Soup", 800L, PrepMinutes: 0)));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => menu.CreateItemAsync(
                branch.BranchId, category.Id, new CreateMenuItemCommand("Soup", 800L, PhotoId: Guid.Empty)));
    }

    /// <summary>
    /// <b>Test 2.</b> An incomplete item is absent from the diner-facing menu and present in the
    /// console's.
    /// </summary>
    /// <remarks>
    /// The two reads disagree on purpose, and this is the pair of assertions that says so. A
    /// manager has to see the dish that still needs a photo - that is what saving it half-entered
    /// is for - and a diner must never see it, because somebody reading an empty allergen list
    /// reasonably concludes there are none.
    /// </remarks>
    [SkippableFact]
    public async Task An_incomplete_item_is_hidden_from_diners_and_shown_to_the_console()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Mains"));
        var photoId = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);

        var finished = await menu.CreateItemAsync(branch.BranchId, category.Id, Item("Khachapuri", photoId));

        var unfinished = await menu.CreateItemAsync(
            branch.BranchId, category.Id, Item("Lamb kebab", photoId) with { PhotoId = null });

        // A sold-out but complete dish, to pin the contrast: unavailable is shown, unfinished is not.
        var soldOut = await menu.CreateItemAsync(branch.BranchId, category.Id, Item("Areni red", photoId));
        await menu.SetItemAvailabilityAsync(branch.BranchId, soldOut.Id, isAvailable: false);

        var console = (await menu.GetMenuAsync(branch.BranchId)).Single().Items;

        Assert.Equal(3, console.Count);
        Assert.Contains(console, i => i.Id == unfinished.Id && !i.IsComplete);
        Assert.Contains(console, i => i.Id == finished.Id && i.IsComplete);

        await using var readDb = fixture.CreateContext(clock);
        var diner = await SqlServerFixture.CreateMenuQuery(readDb).GetBranchMenuAsync(branch.BranchId);
        var dinerItems = diner.Categories.SelectMany(c => c.Items).ToList();

        Assert.DoesNotContain(dinerItems, i => i.Id == unfinished.Id);
        Assert.Contains(dinerItems, i => i.Id == finished.Id);

        // Sold out is present and flagged, because "we are out of it tonight" is an answer.
        Assert.Contains(dinerItems, i => i.Id == soldOut.Id && !i.IsAvailable);

        // And everything a diner does see is complete, which is what makes those fields safe to read.
        Assert.All(dinerItems, i =>
        {
            Assert.True(i.IsComplete);
            Assert.NotNull(i.Photo);
            Assert.False(string.IsNullOrWhiteSpace(i.Description));
            Assert.False(string.IsNullOrWhiteSpace(i.Ingredients));
            Assert.False(string.IsNullOrWhiteSpace(i.Allergens));
            Assert.False(string.IsNullOrWhiteSpace(i.PortionSize));
            Assert.True(i.PrepMinutes > 0);
        });
    }

    // ------------------------------------------------------------ 5 and 6. moving an item

    /// <summary>
    /// <b>Tests 5 and 6.</b> An item moves between categories of the same branch and lands last;
    /// a category on another branch is refused; the order lines that reference it are untouched.
    /// </summary>
    [SkippableFact]
    public async Task An_item_moves_category_within_a_branch_lands_last_and_leaves_order_lines_alone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);
        var photoId = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);

        var drinks = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Drinks"));
        var desserts = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Desserts", 1));

        // Two desserts already in a deliberate order, so "last" is a real claim and not "first".
        await menu.CreateItemAsync(branch.BranchId, desserts.Id, Item("Pakhlava", photoId) with { DisplayOrder = 0 });
        await menu.CreateItemAsync(branch.BranchId, desserts.Id, Item("Gata", photoId) with { DisplayOrder = 7 });

        var affogato = await menu.CreateItemAsync(
            branch.BranchId, drinks.Id, Item("Affogato", photoId) with { PriceAmd = 1_600L, DisplayOrder = 0 });

        var lineId = await PlaceOrderLineAsync(db, branch, affogato.Id, affogato.Name, affogato.PriceAmd);

        var moved = await menu.UpdateItemAsync(
            branch.BranchId, affogato.Id, new UpdateMenuItemCommand(CategoryId: desserts.Id));

        Assert.Equal(desserts.Id, moved.CategoryId);
        Assert.Equal(8, moved.DisplayOrder);

        var categories = await menu.GetMenuAsync(branch.BranchId);

        Assert.Empty(categories.Single(c => c.Id == drinks.Id).Items);
        Assert.Equal(
            ["Pakhlava", "Gata", "Affogato"],
            categories.Single(c => c.Id == desserts.Id).Items.Select(i => i.Name));

        // Test 6: the line snapshotted a name and a price and never referenced a category, so
        // reorganising the menu cannot reach back and change a bill.
        await using var verify = fixture.CreateContext(clock);
        var line = await verify.TabOrderLines.AsNoTracking().FirstAsync(l => l.Id == lineId);

        Assert.Equal("Affogato", line.NameSnapshot);
        Assert.Equal(1_600L, line.UnitPriceAmdSnapshot);
        Assert.Equal(affogato.Id, line.MenuItemId);

        // A category on another branch is refused, and the refusal names the field, so the console
        // highlights the category picker rather than the whole form.
        var otherBranch = await TestBranchBuilder.CreateAsync(db);
        var elsewhere = await menu.CreateCategoryAsync(otherBranch.BranchId, new CreateMenuCategoryCommand("Sides"));

        var refused = await Assert.ThrowsAsync<FieldValidationException>(
            () => menu.UpdateItemAsync(branch.BranchId, affogato.Id, new UpdateMenuItemCommand(CategoryId: elsewhere.Id)));

        Assert.Equal("categoryId", refused.Field);
        Assert.Equal(elsewhere.Id, Assert.Single(refused.Violations).Value);

        await using var unchanged = fixture.CreateContext(clock);
        Assert.Equal(
            desserts.Id,
            (await unchanged.MenuItems.AsNoTracking().FirstAsync(i => i.Id == affogato.Id)).MenuCategoryId);
    }

    // ------------------------------------------------------------ 15. prices are snapshotted

    [SkippableFact]
    public async Task Changing_an_items_price_leaves_existing_order_lines_untouched()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Drinks"));
        var item = await menu.CreateItemAsync(branch.BranchId, category.Id, Item("Flat white", await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId)) with { PriceAmd = 1_400L });

        var lineId = await PlaceOrderLineAsync(db, branch, item.Id, item.Name, item.PriceAmd);

        var repriced = await menu.UpdateItemAsync(branch.BranchId, item.Id, new UpdateMenuItemCommand(PriceAmd: 1_900L));
        Assert.Equal(1_900L, repriced.PriceAmd);

        await using var verify = fixture.CreateContext(clock);
        var line = await verify.TabOrderLines.AsNoTracking().FirstAsync(l => l.Id == lineId);
        Assert.Equal(1_400L, line.UnitPriceAmdSnapshot);
        Assert.Equal("Flat white", line.NameSnapshot);

        // And the referenced item cannot be deleted - it is deactivated, and the caller is told.
        var deletion = await menu.DeleteItemAsync(branch.BranchId, item.Id);
        Assert.False(deletion.Deleted);
        Assert.True(deletion.Deactivated);
        Assert.False((await verify.MenuItems.AsNoTracking().FirstAsync(i => i.Id == item.Id)).IsAvailable);
    }

    [SkippableFact]
    public async Task Availability_is_a_toggle_and_an_unreferenced_item_is_removed_outright()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Mains"));
        var item = await menu.CreateItemAsync(branch.BranchId, category.Id, Item("Khachapuri", await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId)));

        var out86 = await menu.SetItemAvailabilityAsync(branch.BranchId, item.Id, isAvailable: false);
        Assert.False(out86.IsAvailable);

        var back = await menu.SetItemAvailabilityAsync(branch.BranchId, item.Id, isAvailable: true);
        Assert.True(back.IsAvailable);

        var deletion = await menu.DeleteItemAsync(branch.BranchId, item.Id);
        Assert.True(deletion.Deleted);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.MenuItems.AnyAsync(i => i.Id == item.Id));
    }

    // ------------------------------------------------------------ 16. role escalation guards

    [SkippableFact]
    public async Task A_manager_cannot_create_an_owner_and_cannot_change_their_own_role()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var asManager = fixture.CreateStaffManagementService(db, clock, TestActor.Manager(branch.ManagerId));

        // Above their own role: refused.
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.CreateAsync(branch.VenueId, Staff("Would-be Owner", StaffRole.Owner)));

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.CreateAsync(branch.VenueId, Staff("Would-be Manager", StaffRole.Manager)));

        // Their own role: refused, whatever direction.
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(Role: StaffRole.Owner)));

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(Role: StaffRole.Waiter)));

        // Waiters and kitchen staff: allowed.
        var waiter = await asManager.CreateAsync(branch.VenueId, Staff("New Waiter", StaffRole.Waiter, branch.BranchId));
        Assert.Equal(StaffRole.Waiter, waiter.Role);
        Assert.Equal(branch.BranchId, waiter.BranchId);

        var renamed = await asManager.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(FullName: "Nune Renamed"));
        Assert.Equal("Nune Renamed", renamed.FullName);
        Assert.Equal(StaffRole.Manager, renamed.Role);

        // An owner may create a manager and another owner.
        var owner = new StaffMember(branch.VenueId, "Founder", "+37499000001", StaffRole.Owner, "hash");
        db.StaffMembers.Add(owner);
        await db.SaveChangesAsync();

        var asOwner = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id));
        var manager = await asOwner.CreateAsync(branch.VenueId, Staff("Second Manager", StaffRole.Manager));
        Assert.Equal(StaffRole.Manager, manager.Role);

        var coOwner = await asOwner.CreateAsync(branch.VenueId, Staff("Co-owner", StaffRole.Owner) with
        {
            Email = $"co-{Guid.NewGuid():N}@example.test",
            Password = "a-perfectly-long-password",
        });
        Assert.True(coOwner.HasPasswordSignIn);

        // And nobody mints a platform admin through a venue's staff list.
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asOwner.CreateAsync(branch.VenueId, Staff("Sneaky", StaffRole.PlatformAdmin)));

        // A manager cannot touch the owner at all.
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.UpdateAsync(branch.VenueId, owner.Id, new UpdateStaffCommand(IsActive: false)));
    }

    [SkippableFact]
    public async Task A_manager_of_another_venue_cannot_manage_this_ones_staff()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var venueA = await TestBranchBuilder.CreateAsync(db);
        var venueB = await TestBranchBuilder.CreateAsync(db);

        var outsider = fixture.CreateStaffManagementService(db, clock, TestActor.Manager(venueB.ManagerId));

        await Assert.ThrowsAsync<StaffPermissionException>(() => outsider.ListAsync(venueA.VenueId));

        // A platform admin belongs to no venue and may act in any.
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreateStaffManagementService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var staff = await platform.ListAsync(venueA.VenueId);
        Assert.Equal(2, staff.Count);
    }

    /// <summary>
    /// A password mints a venue-scoped token. Giving one to a waiter would hand them, off a browser
    /// with no enrolled tablet, the branches their PIN is deliberately refused at.
    /// </summary>
    [SkippableFact]
    public async Task Only_owners_and_managers_can_be_given_an_admin_panel_sign_in()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var asManager = fixture.CreateStaffManagementService(db, clock, TestActor.Manager(branch.ManagerId));

        var withCredentials = Staff("Waiter With Password", StaffRole.Waiter, branch.BranchId) with
        {
            Email = $"waiter-{Guid.NewGuid():N}@example.test",
            Password = "a-perfectly-long-password",
        };

        var refused = await Assert.ThrowsAsync<DomainStateException>(
            () => asManager.CreateAsync(branch.VenueId, withCredentials));

        // The message is about the person being created, not the caller's rank - a platform admin
        // reading "requires the Manager role" would go looking in the wrong place.
        Assert.Contains("tapping a PIN", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the caller is", refused.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<DomainStateException>(
            () => asManager.CreateAsync(branch.VenueId, withCredentials with { Role = StaffRole.Kitchen }));

        // The same waiter without credentials is fine - the refusal is the password, not the role.
        var waiter = await asManager.CreateAsync(branch.VenueId, Staff("Plain Waiter", StaffRole.Waiter, branch.BranchId));
        Assert.False(waiter.HasPasswordSignIn);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(0, await verify.StaffMembers.CountAsync(s => s.Role == StaffRole.Waiter && s.Email != null && s.VenueId == branch.VenueId));
    }

    /// <summary>
    /// A branch-confined manager who could clear their own branch would sign in on every branch's
    /// tablets - the same escalation the own-role rule exists to stop.
    /// </summary>
    [SkippableFact]
    public async Task Nobody_widens_their_own_branch_assignment()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var asManager = fixture.CreateStaffManagementService(db, clock, TestActor.Manager(branch.ManagerId));

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => asManager.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(SetBranch: true, BranchId: null)));

        // An owner may do it for them, and the manager may still edit their own other fields.
        var owner = new StaffMember(branch.VenueId, "Founder", "+37499000002", StaffRole.Owner, "hash");
        db.StaffMembers.Add(owner);
        await db.SaveChangesAsync();

        var widened = await fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id))
            .UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(SetBranch: true, BranchId: null));

        Assert.Null(widened.BranchId);

        var renamed = await asManager.UpdateAsync(branch.VenueId, branch.ManagerId, new UpdateStaffCommand(FullName: "Still Me"));
        Assert.Equal("Still Me", renamed.FullName);
    }

    /// <summary>One address, one account - and the collision is an answer, not a fault.</summary>
    [SkippableFact]
    public async Task A_duplicate_email_is_refused_with_a_message_rather_than_a_fault()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var owner = new StaffMember(branch.VenueId, "Founder", "+37499000003", StaffRole.Owner, "hash");
        db.StaffMembers.Add(owner);
        await db.SaveChangesAsync();

        var service = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.Id));
        var email = $"taken-{Guid.NewGuid():N}@example.test";

        var first = Staff("First Manager", StaffRole.Manager) with { Email = email, Password = "a-perfectly-long-password" };
        await service.CreateAsync(branch.VenueId, first);

        var clash = Staff("Second Manager", StaffRole.Manager) with { Email = email, Password = "a-perfectly-long-password" };

        var refused = await Assert.ThrowsAsync<DomainStateException>(() => service.CreateAsync(branch.VenueId, clash));

        Assert.Contains("already has an account", refused.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, await verify.StaffMembers.CountAsync(s => s.Email == email));
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A complete item - every field completeness is measured on is filled in.</summary>
    private static CreateMenuItemCommand Item(string name, Guid photoId) => new(
        Name: name,
        PriceAmd: 2_500L,
        Description: $"House {name.ToLowerInvariant()}",
        PhotoId: photoId,
        Ingredients: "flour, cheese, egg",
        Allergens: "gluten, dairy, egg",
        PortionSize: "350 g",
        PrepMinutes: 15);

    private static CreateStaffCommand Staff(string name, StaffRole role, Guid? branchId = null) => new(
        FullName: name,
        Phone: $"+374{Random.Shared.Next(10_000_000, 99_999_999)}",
        Role: role,
        Pin: "4321",
        BranchId: branchId);

    /// <summary>Seats a party, opens a tab, and places one line for the item at its current price.</summary>
    private static async Task<Guid> PlaceOrderLineAsync(
        Yalla.Infrastructure.Persistence.YallaDbContext db,
        TestBranch branch,
        Guid menuItemId,
        string name,
        long priceAmd)
    {
        var table = await db.DiningTables.FirstAsync(t => t.Id == branch.FirstTableId);
        var session = TableSession.SeatWalkIn(branch.BranchId, table.Id, 2, Now, branch.WaiterId);
        db.TableSessions.Add(session);
        table.Occupy(session.Id);

        var tab = new Tab(branch.BranchId, table.Id, session.Id, Now, 10m);
        db.Tabs.Add(tab);
        session.AttachTab(tab.Id);

        var host = TabParticipant.Host(tab.Id, "Host", $"device-{Guid.NewGuid():N}", Now);
        db.TabParticipants.Add(host);
        tab.SetHostParticipant(host.Id);

        var order = TabOrder.PlacedByDiner(tab.Id, host.Id, Now);
        var line = order.AddLine(menuItemId, name, priceAmd, quantity: 1);
        db.TabOrders.Add(order);

        await db.SaveChangesAsync();

        return line.Id;
    }
}
