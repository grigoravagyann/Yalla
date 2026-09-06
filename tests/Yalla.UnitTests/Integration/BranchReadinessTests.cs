using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Application.Menus;
using Yalla.Application.Platform;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The onboarding checklist and the gate it feeds.
/// </summary>
/// <remarks>
/// Prompt 6 required a photo, ingredients, allergens, a portion size and a prep time before an item
/// could be saved at all. The reasoning was right and the enforcement point was wrong - it made an
/// eighty-dish menu unenterable. These are the tests for where the rule went instead: a projection
/// that says what is missing, and a refusal to put the branch in front of diners while anything is.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class BranchReadinessTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 6, 11, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ 3. the going-live gate

    /// <summary>
    /// <b>Test 3.</b> A branch whose menu has unfinished items cannot be switched to Paid, and the
    /// refusal says how many.
    /// </summary>
    /// <remarks>
    /// The count is the point. "Cannot upgrade" sends a manager to support; "eleven dishes still
    /// need a photo" is something they can finish this afternoon.
    /// </remarks>
    [SkippableFact]
    public async Task A_branch_with_incomplete_menu_items_cannot_be_switched_to_paid()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db, subscriptionTier: SubscriptionTier.Free);

        var menu = fixture.CreateMenuService(db);
        var photoId = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);
        var category = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Mains"));

        await menu.CreateItemAsync(branch.BranchId, category.Id, Complete("Khachapuri", photoId));

        // Three that a diner must never be shown, each missing something different.
        await menu.CreateItemAsync(branch.BranchId, category.Id, Complete("Lamb kebab", photoId) with { PhotoId = null });
        await menu.CreateItemAsync(branch.BranchId, category.Id, Complete("Dolma", photoId) with { Allergens = null });
        await menu.CreateItemAsync(branch.BranchId, category.Id, new CreateMenuItemCommand("Lavash", 300L));

        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var refused = await Assert.ThrowsAsync<BranchNotReadyForDinersException>(
            () => platform.UpdateBranchAsync(
                branch.BranchId, new UpdateBranchCommand(SubscriptionTier: SubscriptionTier.Paid)));

        Assert.Equal(3, refused.IncompleteMenuItemCount);
        Assert.Contains("3 menu item(s)", refused.Message);
        Assert.Equal(branch.BranchId, refused.BranchId);

        // And the tier really did not move - the refusal is not cosmetic.
        await using var stillFree = fixture.CreateContext(clock);
        Assert.Equal(
            SubscriptionTier.Free,
            (await stillFree.Branches.AsNoTracking().FirstAsync(b => b.Id == branch.BranchId)).SubscriptionTier);

        // Finish the three, and the same call goes through.
        var incomplete = await SqlServerFixture.CreateReadinessQuery(db).IncompleteMenuItemIdsAsync(branch.BranchId);

        foreach (var itemId in incomplete)
        {
            await menu.UpdateItemAsync(branch.BranchId, itemId, new UpdateMenuItemCommand(
                Description: "Finished later, which is the whole point",
                PhotoId: photoId,
                Ingredients: "flour, water, salt",
                Allergens: "gluten",
                PortionSize: "one serving",
                PrepMinutes: 10));
        }

        var upgraded = await platform.UpdateBranchAsync(
            branch.BranchId, new UpdateBranchCommand(SubscriptionTier: SubscriptionTier.Paid));

        Assert.Equal(SubscriptionTier.Paid, upgraded.SubscriptionTier);
    }

    /// <summary>
    /// A branch that is already Paid is not re-gated by an unrelated edit.
    /// </summary>
    /// <remarks>
    /// The gate is on <i>going</i> live, not on being live. A venue that is trading and adds a
    /// half-entered dish must still be able to change its own name; refusing that would make the
    /// rule a trap rather than a checklist. The dish is hidden from diners either way, which is the
    /// protection that actually matters.
    /// </remarks>
    [SkippableFact]
    public async Task An_already_paid_branch_can_still_be_edited_with_an_unfinished_menu()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var branch = await TestBranchBuilder.CreateAsync(db, subscriptionTier: SubscriptionTier.Paid);

        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(branch.BranchId, new CreateMenuCategoryCommand("Specials"));
        await menu.CreateItemAsync(branch.BranchId, category.Id, new CreateMenuItemCommand("Tonight's fish", 4_500L));

        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var renamed = await platform.UpdateBranchAsync(
            branch.BranchId, new UpdateBranchCommand(Name: "Renamed Branch", SubscriptionTier: SubscriptionTier.Paid));

        Assert.Equal("Renamed Branch", renamed.Name);
        Assert.Equal(SubscriptionTier.Paid, renamed.SubscriptionTier);
    }

    // ------------------------------------------------------------ 4. the checklist

    /// <summary>
    /// <b>Test 4.</b> Readiness reports every line, and flipping any input flips its line.
    /// </summary>
    /// <remarks>
    /// Written as one test rather than eight, deliberately: the thing worth proving is that each
    /// line answers to <i>its own</i> input and to nothing else, and that only shows up when the
    /// lines are satisfied one at a time and the rest are watched for movement.
    /// </remarks>
    [SkippableFact]
    public async Task Readiness_reports_each_line_and_each_input_flips_only_its_own()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        // A branch with nothing: no tables, no menu, no hours, no staff, no tablets, and a
        // reservation policy nobody has looked at.
        var venue = new Venue($"Bare Venue {Guid.NewGuid():N}"[..24], VenueType.Cafe, $"bare-{Guid.NewGuid():N}"[..20]);
        db.Venues.Add(venue);

        var branch = new Branch(
            venue,
            name: "Bare Branch",
            slug: $"bare-branch-{Guid.NewGuid():N}"[..24],
            address: "1 Test Street, Yerevan",
            latitude: 40.18,
            longitude: 44.51,
            timeZoneId: "Asia/Yerevan",
            floorWidth: 1000,
            floorHeight: 700);

        db.Branches.Add(branch);
        await db.SaveChangesAsync();

        var readiness = SqlServerFixture.CreateReadinessQuery(db);

        var bare = await readiness.GetAsync(branch.Id);

        Assert.False(bare.IsReadyForDiners);
        Assert.False(bare.FloorPlanDrawn);
        Assert.False(bare.TablesLabelled);
        Assert.False(bare.MenuCategoriesPresent);
        Assert.False(bare.MenuComplete);
        Assert.False(bare.OpeningHoursSet);
        Assert.False(bare.ReservationPolicyReviewed);
        Assert.False(bare.StaffEnrolled);
        Assert.False(bare.DeviceEnrolled);
        Assert.Equal(0, bare.IncompleteMenuItemCount);

        // Every unsatisfied line has said something a person can read. Six, not eight: "tables have
        // no label" only fires once there are tables, and "categories but no items" only once there
        // is a category, so each pair reports the first thing wrong rather than both at once.
        Assert.Equal(6, bare.Blockers.Count);

        // ---- the floor plan
        var table = await TestBranchBuilder.AddTableAsync(
            db, new TestBranch(venue.Id, branch.Id, Guid.Empty, Guid.Empty, [], "Asia/Yerevan"), "1", seats: 4);

        var withTables = await readiness.GetAsync(branch.Id);

        Assert.True(withTables.FloorPlanDrawn);
        Assert.True(withTables.TablesLabelled);
        Assert.Equal(1, withTables.TableCount);
        Assert.False(withTables.MenuCategoriesPresent);
        Assert.False(withTables.IsReadyForDiners);

        // ---- the menu, in the two steps the console actually shows: a category, then items
        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(branch.Id, new CreateMenuCategoryCommand("Coffee"));

        var withCategory = await readiness.GetAsync(branch.Id);

        Assert.True(withCategory.MenuCategoriesPresent);
        Assert.Equal(1, withCategory.MenuCategoryCount);

        // A category with no items is not a finished menu either.
        Assert.False(withCategory.MenuComplete);

        var unfinished = await menu.CreateItemAsync(branch.Id, category.Id, new CreateMenuItemCommand("Flat white", 1_200L));

        var withUnfinished = await readiness.GetAsync(branch.Id);

        Assert.Equal(1, withUnfinished.MenuItemCount);
        Assert.Equal(1, withUnfinished.IncompleteMenuItemCount);
        Assert.Equal(unfinished.Id, Assert.Single(withUnfinished.IncompleteMenuItemIds));
        Assert.False(withUnfinished.MenuComplete);

        var photoId = await TestMenuBuilder.AddPhotoAsync(db, branch.Id);

        await menu.UpdateItemAsync(branch.Id, unfinished.Id, new UpdateMenuItemCommand(
            Description: "House flat white",
            PhotoId: photoId,
            Ingredients: "espresso, milk",
            Allergens: "dairy",
            PortionSize: "250 ml",
            PrepMinutes: 4));

        var withMenu = await readiness.GetAsync(branch.Id);

        Assert.True(withMenu.MenuComplete);
        Assert.Equal(0, withMenu.IncompleteMenuItemCount);
        Assert.Empty(withMenu.IncompleteMenuItemIds);
        Assert.False(withMenu.OpeningHoursSet);

        // ---- opening hours
        var settings = fixture.CreateBranchSettingsService(db, clock, TestActor.PlatformAdmin(Guid.CreateVersion7()));

        await settings.ReplaceOpeningHoursAsync(
            branch.Id,
            [.. Enum.GetValues<DayOfWeek>().Select(d => new OpeningHoursBlock(d, new TimeOnly(9, 0), new TimeOnly(22, 0)))]);

        var withHours = await readiness.GetAsync(branch.Id);

        Assert.True(withHours.OpeningHoursSet);
        Assert.Equal(7, withHours.OpeningHoursDayCount);
        Assert.False(withHours.ReservationPolicyReviewed);

        // ---- the reservation policy. Saving the form is what counts as reviewing it: the branch
        // has had a policy since it was created, so its existence answers nothing.
        var policy = await settings.GetReservationPolicyAsync(branch.Id);
        await settings.UpdateReservationPolicyAsync(branch.Id, CommandFrom(policy));

        var withPolicy = await readiness.GetAsync(branch.Id);

        Assert.True(withPolicy.ReservationPolicyReviewed);
        Assert.False(withPolicy.StaffEnrolled);

        // ---- staff
        var waiter = new Yalla.Domain.Staff.StaffMember(
            venue.Id, "Test Waiter", $"+3749{Guid.NewGuid():N}"[..12], StaffRole.Waiter, "hash", branch.Id);

        db.StaffMembers.Add(waiter);
        await db.SaveChangesAsync();

        var withStaff = await readiness.GetAsync(branch.Id);

        Assert.True(withStaff.StaffEnrolled);
        Assert.Equal(1, withStaff.StaffCount);
        Assert.False(withStaff.DeviceEnrolled);
        Assert.False(withStaff.IsReadyForDiners);

        // ---- the last line: a tablet
        var device = new StaffDevice(venue.Id, branch.Id, "Bar tablet", $"client-{Guid.NewGuid():N}", Now);
        db.StaffDevices.Add(device);
        await db.SaveChangesAsync();

        var ready = await readiness.GetAsync(branch.Id);

        Assert.True(ready.DeviceEnrolled);
        Assert.Equal(1, ready.DeviceCount);
        Assert.True(ready.IsReadyForDiners);
        Assert.Empty(ready.Blockers);

        // And each line flips back when its own input goes away, so none of them is hard-coded to
        // true once something else has been satisfied.
        device.Revoke(Now);
        table.SetActive(false);
        await db.SaveChangesAsync();

        var undone = await readiness.GetAsync(branch.Id);

        Assert.False(undone.DeviceEnrolled);
        Assert.False(undone.FloorPlanDrawn);
        Assert.False(undone.IsReadyForDiners);

        // ...and nothing else moved with them.
        Assert.True(undone.MenuComplete);
        Assert.True(undone.OpeningHoursSet);
        Assert.True(undone.ReservationPolicyReviewed);
        Assert.True(undone.StaffEnrolled);
    }

    [SkippableFact]
    public async Task Readiness_for_an_unknown_branch_is_a_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => SqlServerFixture.CreateReadinessQuery(db).GetAsync(Guid.CreateVersion7()));
    }

    // ------------------------------------------------------------ helpers

    private static CreateMenuItemCommand Complete(string name, Guid photoId) => new(
        Name: name,
        PriceAmd: 2_500L,
        Description: $"House {name.ToLowerInvariant()}",
        PhotoId: photoId,
        Ingredients: "flour, cheese, egg",
        Allergens: "gluten, dairy, egg",
        PortionSize: "350 g",
        PrepMinutes: 15);

    /// <summary>Re-sends a policy unchanged, which is what "the owner reviewed it" looks like.</summary>
    private static ReservationPolicyCommand CommandFrom(ReservationPolicyView p) => new(
        p.TurnTimeMinutes,
        p.BufferMinutes,
        p.GraceMinutes,
        p.LateNudgeAfterMinutes,
        p.GraceExtensionMinutes,
        p.MinLeadMinutes,
        p.BookingWindowDays,
        p.CancellationDeadlineMinutes,
        p.AutoConfirm,
        p.ServiceChargePercent,
        p.PricesIncludeVat,
        p.MaxSeatOverhang,
        p.ApprovalRequiredAbovePartySize,
        p.WalkInHoldbackMinutes);
}
