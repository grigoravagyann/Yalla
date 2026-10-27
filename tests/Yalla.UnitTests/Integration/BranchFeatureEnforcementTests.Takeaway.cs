using System.Net.Http.Json;
using Yalla.Domain.Enums;
using Yalla.Domain.Takeaway;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

public sealed partial class BranchFeatureEnforcementTests
{
    /// <summary>
    /// Takeaway: blocked, the diner's availability reads as switched off and a new order is refused; an order placed
    /// before the block is accepted, readied and collected as any other.
    /// </summary>
    [SkippableFact]
    public async Task Blocked_takeaway_refuses_new_orders_and_reads_off_while_a_placed_order_is_collected()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu) = await ArrangeAsync(factory);

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/takeaway-settings", new
        {
            enabled = true,
            autoAccept = false,
            prepMinutes = 20,
            lastOrderMinutes = 30,
            maxInProgress = 5,
        }));

        // Tomorrow at 14:00 in Yerevan, inside the branch's hours - after signing in, on the real clock.
        var afternoon = TestNow.UtcNow.Date.AddDays(1).AddHours(10);
        factory.Clock.UtcNow = afternoon;

        object Order() => new
        {
            branchId = branch.BranchId,
            pickup = new { asap = true },
            lines = new[] { new { menuItemId = menu.Khachapuri, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        };

        var placed = await OkAsync(await diner.PostAsJsonAsync("/api/diner/takeaway-orders", Order()));
        var orderId = placed.GetProperty("id").GetGuid();

        await BlockAsync(factory, branch.BranchId, BranchFeature.Takeaway);

        // The app asks whether to show takeaway: no - not an error. The owner's own switch is untouched.
        var availability = await ReadAsync(diner, $"/api/diner/branches/{branch.BranchId}/takeaway");
        Assert.False(availability.GetProperty("enabled").GetBoolean());
        Assert.False(availability.GetProperty("open").GetBoolean());
        Assert.True((await ReadAsync(manager, $"/api/branches/{branch.BranchId}/takeaway-settings")).GetProperty("enabled").GetBoolean());

        await AssertRefusedAsync(
            await diner.PostAsJsonAsync("/api/diner/takeaway-orders", Order()), BranchFeature.Takeaway, branch.BranchId);

        // The order already placed runs on: accepted, ready, collected.
        factory.Clock.UtcNow = afternoon.AddMinutes(2);
        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{orderId}/accept", new { readyAt = afternoon.AddMinutes(25) }));
        await OkAsync(await waiter.PostAsync($"/api/takeaway-orders/{orderId}/ready", null));
        var collected = await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{orderId}/collect", new { method = 1 }));

        Assert.Equal((int)TakeawayStatus.Collected, collected.GetProperty("status").GetInt32());
    }
}
