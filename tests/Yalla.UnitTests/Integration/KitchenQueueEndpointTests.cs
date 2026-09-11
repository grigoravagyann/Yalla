using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The kitchen display, signed in as the Kitchen role through the real pipeline.
/// </summary>
/// <remarks>
/// The service has always known what a cook may do - exactly <c>InKitchen -> Ready</c> - but the
/// two kitchen routes once carried <c>WaiterOrAbove</c>, so a Kitchen account was refused at the
/// door and the service's rule never ran. These tests pin both halves: the door is open to the
/// kitchen on those two routes and no others, and past the door the service still decides.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class KitchenQueueEndpointTests(SqlServerFixture fixture)
{
    private const string CookPin = "5830";

    [SkippableFact]
    public async Task A_kitchen_account_at_the_branch_reads_the_queue()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var kitchen = await ArrangeAsync(factory);

        using var cook = factory.CreateClientWithToken(await SignInCookAsync(factory, kitchen.Branch));
        var response = await cook.GetAsync($"/api/branches/{kitchen.Branch.BranchId}/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var orders = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            orders.EnumerateArray(),
            o => o.GetProperty("orderId").GetGuid() == kitchen.OrderId);
    }

    [SkippableFact]
    public async Task A_kitchen_account_moves_an_order_from_InKitchen_to_Ready()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var kitchen = await ArrangeAsync(factory);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, kitchen.Branch));
        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(waiter, kitchen.OrderId, TabOrderStatus.InKitchen)).StatusCode);

        using var cook = factory.CreateClientWithToken(await SignInCookAsync(factory, kitchen.Branch));
        var response = await MoveAsync(cook, kitchen.OrderId, TabOrderStatus.Ready);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            TabOrderStatus.Ready,
            EnumOf<TabOrderStatus>((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status")));
    }

    [SkippableFact]
    public async Task A_kitchen_account_is_refused_every_other_move_by_the_service()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var kitchen = await ArrangeAsync(factory);

        using var cook = factory.CreateClientWithToken(await SignInCookAsync(factory, kitchen.Branch));
        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, kitchen.Branch));

        // New -> InKitchen is the waiter's "send to the kitchen", not the cook's.
        var send = await MoveAsync(cook, kitchen.OrderId, TabOrderStatus.InKitchen);
        await AssertServiceRefusalAsync(send);

        (await MoveAsync(waiter, kitchen.OrderId, TabOrderStatus.InKitchen)).EnsureSuccessStatusCode();
        (await MoveAsync(waiter, kitchen.OrderId, TabOrderStatus.Ready)).EnsureSuccessStatusCode();

        // Ready -> Served is a floor action: whoever carried the plate.
        var serve = await MoveAsync(cook, kitchen.OrderId, TabOrderStatus.Served);
        await AssertServiceRefusalAsync(serve);

        // And neither refusal moved anything.
        var queue = await ReadAsync(cook, $"/api/branches/{kitchen.Branch.BranchId}/orders");
        var order = queue.EnumerateArray().Single(o => o.GetProperty("orderId").GetGuid() == kitchen.OrderId);
        Assert.Equal(TabOrderStatus.Ready, EnumOf<TabOrderStatus>(order.GetProperty("status")));
    }

    [SkippableFact]
    public async Task A_kitchen_account_at_another_branch_is_refused_the_queue()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var kitchen = await ArrangeAsync(factory);

        AuthBranch elsewhere;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            elsewhere = await AuthTestData.CreateBranchAsync(db);
        }

        using var cook = factory.CreateClientWithToken(await SignInCookAsync(factory, elsewhere));

        // The cook's own queue opens, so the refusal below is about the branch and nothing else.
        Assert.Equal(
            HttpStatusCode.OK,
            (await cook.GetAsync($"/api/branches/{elsewhere.BranchId}/orders")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await cook.GetAsync($"/api/branches/{kitchen.Branch.BranchId}/orders")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await MoveAsync(cook, kitchen.OrderId, TabOrderStatus.InKitchen)).StatusCode);
    }

    [SkippableFact]
    public async Task A_kitchen_account_is_still_refused_the_floor_and_the_service_requests()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var kitchen = await ArrangeAsync(factory);

        using var cook = factory.CreateClientWithToken(await SignInCookAsync(factory, kitchen.Branch));

        // Proves the token is a working kitchen token, so the refusals below are the policy's.
        Assert.Equal(
            HttpStatusCode.OK,
            (await cook.GetAsync($"/api/branches/{kitchen.Branch.BranchId}/orders")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await cook.GetAsync($"/api/branches/{kitchen.Branch.BranchId}/tables/floor")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await cook.GetAsync($"/api/branches/{kitchen.Branch.BranchId}/service-requests")).StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private sealed record Kitchen(AuthBranch Branch, Guid OrderId);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>A branch with a cook on its staff and one New order on an open tab.</summary>
    private async Task<Kitchen> ArrangeAsync(YallaApiFactory factory)
    {
        AuthBranch branch;
        TestMenu menu;
        string qrToken;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

            qrToken = await db.DiningTables.AsNoTracking()
                .Where(t => t.BranchId == branch.BranchId)
                .OrderBy(t => t.Label)
                .Select(t => t.QrToken)
                .FirstAsync();
        }

        using var anonymous = factory.CreateClient();
        var opened = await anonymous.PostAsJsonAsync(
            "/api/tabs/open",
            new { qrToken, deviceId = "phone-host", clientCommandId = Guid.CreateVersion7(), displayName = "Host" });
        opened.EnsureSuccessStatusCode();

        var body = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var tabId = body.GetProperty("tab").GetProperty("tabId").GetGuid();

        using var host = factory.CreateClientWithToken(
            body.GetProperty("token").GetProperty("accessToken").GetString()!);

        var placed = await host.PostAsJsonAsync(
            $"/api/tabs/{tabId}/orders",
            new
            {
                items = new[] { new { menuItemId = menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            });
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var orderId = (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();

        return new Kitchen(branch, orderId);
    }

    /// <summary>
    /// Seeds a cook at the branch with a real PIN and signs them in on an enrolled tablet - the
    /// kitchen display is the staff app in another mode, so it signs in the same way.
    /// </summary>
    private async Task<string> SignInCookAsync(YallaApiFactory factory, AuthBranch branch)
    {
        Guid cookId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var cook = new StaffMember(
                branch.VenueId,
                "Test Cook",
                $"+3742{Guid.NewGuid().ToString("N")[..7]}",
                StaffRole.Kitchen,
                "hash",
                branch.BranchId);
            cook.SetPinHash(new SecretHasher().Hash(CookPin));

            db.StaffMembers.Add(cook);
            await db.SaveChangesAsync();
            cookId = cook.Id;
        }

        using var tablet = factory.CreateClientWithToken(await StaffAuthTests.EnrolDeviceAsync(factory, branch));
        var response = await tablet.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = cookId, pin = CookPin });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, Guid orderId, TabOrderStatus status) =>
        client.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status });

    /// <summary>
    /// A 403 that came from the service's kitchen rule, not from the door. The policy refuses with
    /// an empty body; the service refuses with a problem naming the move.
    /// </summary>
    private static async Task AssertServiceRefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Moving an order from", text, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static T EnumOf<T>(JsonElement element) where T : struct, Enum =>
        element.ValueKind == JsonValueKind.Number
            ? (T)Enum.ToObject(typeof(T), element.GetInt32())
            : Enum.Parse<T>(element.GetString()!, ignoreCase: true);
}
