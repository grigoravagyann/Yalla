using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

public sealed partial class BranchFeatureEnforcementTests
{
    /// <summary>
    /// StaffPhone: blocked, a paired phone's every call - session or device token - is 409, its session does not renew,
    /// and neither a new pairing code nor one issued before is any good; the branch's tablets carry on, and the phone can
    /// still sign out.
    /// </summary>
    [SkippableFact]
    public async Task A_blocked_staff_phone_is_refused_every_call_and_renewal_while_the_tablet_carries_on()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var console = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        using var anonymous = factory.CreateClient();

        var paired = await OkAsync(await StaffPhoneTests.PairAsync(
            anonymous, await StaffPhoneTests.IssueAsync(console, branch, branch.WaiterId), branch.WaiterPin));
        var renewalToken = paired.GetProperty("session").GetProperty("renewalToken").GetString()!;

        using var phone = factory.CreateClientWithToken(paired.GetProperty("session").GetProperty("accessToken").GetString()!);
        using var phoneDevice = factory.CreateClientWithToken(paired.GetProperty("device").GetProperty("deviceToken").GetString()!);
        using var tablet = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // Working - and its standing cached - before the block. A code for the manager's phone, issued and not used.
        await OkAsync(await phone.GetAsync("/api/staff/device/notification-preferences"));
        var unused = await StaffPhoneTests.IssueAsync(console, branch, branch.ManagerId);

        await BlockByPlatformAsync(factory, branch.BranchId, BranchFeature.StaffPhone);

        // Every call the phone makes, with either token, on its very next request.
        await AssertRefusedAsync(
            await phone.GetAsync("/api/staff/device/notification-preferences"), BranchFeature.StaffPhone, branch.BranchId);
        await AssertRefusedAsync(
            await phone.GetAsync($"/api/branches/{branch.BranchId}/tables/floor"), BranchFeature.StaffPhone, branch.BranchId);
        await AssertRefusedAsync(await phoneDevice.GetAsync("/api/auth/staff/device"), BranchFeature.StaffPhone, branch.BranchId);

        // No renewal, no PIN sign-in, no pairing - with a new code or one issued before.
        await AssertRefusedAsync(
            await anonymous.PostAsJsonAsync("/api/auth/staff/renew", new { renewalToken }), BranchFeature.StaffPhone, branch.BranchId);
        await AssertRefusedAsync(
            await phoneDevice.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = branch.WaiterId, pin = branch.WaiterPin }),
            BranchFeature.StaffPhone,
            branch.BranchId);
        await AssertRefusedAsync(
            await console.PostAsJsonAsync($"/api/branches/{branch.BranchId}/staff/{branch.ManagerId}/phone-pairings", new { }),
            BranchFeature.StaffPhone,
            branch.BranchId);
        await AssertRefusedAsync(
            await anonymous.PostAsJsonAsync("/api/auth/staff/phone-pair/preview", new { code = unused }),
            BranchFeature.StaffPhone,
            branch.BranchId);
        await AssertRefusedAsync(
            await StaffPhoneTests.PairAsync(anonymous, unused, "9182"), BranchFeature.StaffPhone, branch.BranchId);

        // The tablet carries on, and the phone can still sign out.
        await OkAsync(await tablet.GetAsync($"/api/branches/{branch.BranchId}/tables/floor"));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await phone.PostAsJsonAsync("/api/auth/staff/sign-out", new { renewalToken })).StatusCode);
    }

    /// <summary>
    /// KitchenDisplay: blocked, the cook cannot sign in, and a kitchen session already open is refused the queue and the
    /// rail - while waiters, who use the same two routes, read the queue and carry the kitchen's tickets on to served.
    /// </summary>
    [SkippableFact]
    public async Task A_blocked_kitchen_display_refuses_the_kitchen_while_the_floor_finishes_its_tickets()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu) = await ArrangeAsync(factory, tableCount: 2);
        Guid cookId;
        AuthTab tab;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            cookId = await AuthTestData.SeedKitchenAsync(db, branch);
            tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.FirstTableId, factory.Clock.UtcNow);
        }

        using var cook = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, branch, cookId));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var order = await OkAsync(await waiter.PostAsJsonAsync($"/api/tabs/{tab.TabId}/staff-orders", new
        {
            clientCommandId = Guid.CreateVersion7(),
            items = new[] { new { menuItemId = menu.Coffee, quantity = 1 } },
        }));
        var orderId = order.GetProperty("orderId").GetGuid();

        // A waiter's order goes straight to the kitchen, which has it before the block.
        Assert.Equal((int)TabOrderStatus.InKitchen, order.GetProperty("status").GetInt32());
        await OkAsync(await cook.GetAsync($"/api/branches/{branch.BranchId}/orders"));

        await BlockByPlatformAsync(factory, branch.BranchId, BranchFeature.KitchenDisplay);

        // The open kitchen session: refused the queue and the rail.
        await AssertRefusedAsync(
            await cook.GetAsync($"/api/branches/{branch.BranchId}/orders"), BranchFeature.KitchenDisplay, branch.BranchId);
        await AssertRefusedAsync(
            await cook.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = TabOrderStatus.Ready }),
            BranchFeature.KitchenDisplay,
            branch.BranchId);

        // A new kitchen sign-in.
        using (var kitchenTablet = factory.CreateClientWithToken(await StaffAuthTests.EnrolDeviceAsync(factory, branch)))
        {
            await AssertRefusedAsync(
                await kitchenTablet.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = cookId, pin = AuthTestData.KitchenPin }),
                BranchFeature.KitchenDisplay,
                branch.BranchId);
        }

        // The floor reads the queue and finishes the ticket.
        var queue = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/orders");
        Assert.Contains(queue.EnumerateArray(), o => o.GetProperty("orderId").GetGuid() == orderId);

        await OkAsync(await waiter.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = TabOrderStatus.Ready }));
        var served = await OkAsync(await waiter.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = TabOrderStatus.Served }));

        Assert.Equal((int)TabOrderStatus.Served, served.GetProperty("status").GetInt32());
    }
}
