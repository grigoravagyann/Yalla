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

    // ------------------------------------------------------------ 14. required descriptive fields

    [SkippableFact]
    public async Task Creating_a_menu_item_without_allergens_or_prep_time_is_rejected()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Mains"));

        var complete = Item("Khachapuri");

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => menu.CreateItemAsync(branch.BranchId, category.Id, complete with { Allergens = "" }));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => menu.CreateItemAsync(branch.BranchId, category.Id, complete with { PrepMinutes = 0 }));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => menu.CreateItemAsync(branch.BranchId, category.Id, complete with { PhotoUrl = " " }));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => menu.CreateItemAsync(branch.BranchId, category.Id, complete with { Ingredients = null! }));

        Assert.Empty((await menu.GetMenuAsync(branch.BranchId)).Single().Items);

        var created = await menu.CreateItemAsync(branch.BranchId, category.Id, complete);
        Assert.Equal("Khachapuri", created.Name);
        Assert.True(created.IsAvailable);
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
        var item = await menu.CreateItemAsync(branch.BranchId, category.Id, Item("Flat white") with { PriceAmd = 1_400L });

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
        var item = await menu.CreateItemAsync(branch.BranchId, category.Id, Item("Khachapuri"));

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

    private static CreateMenuItemCommand Item(string name) => new(
        Name: name,
        Description: $"House {name.ToLowerInvariant()}",
        PriceAmd: 2_500L,
        PhotoUrl: "https://cdn.example.test/item.jpg",
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
