using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Identity;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// "Who" per line on a waiter's order, through the real pipeline: the wire, the refusals the tablet
/// has to read, and the diner reads that follow a line to its guest.
/// </summary>
/// <remarks>
/// <c>OrderingTests</c> proves the attribution and the arithmetic against the service. These pin
/// what only a request shows: that <c>items[i].participantId</c> binds, that each refusal carries the
/// status and code the order-level field's does, and that the diner's Orders tab and the "order is
/// ready" notice follow each line rather than the order.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class LineAttributionEndpointTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_line_per_guest_binds_and_every_refusal_reads_like_the_order_level_ones()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        TestMenu menu;
        string[] codes;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            codes = await db.DiningTables.AsNoTracking()
                .Where(t => t.BranchId == branch.BranchId)
                .OrderBy(t => t.Label)
                .Select(t => t.QrToken)
                .ToArrayAsync();
        }

        var host = await ScanAsync(factory, codes[0], "Ani");
        var pending = await ScanAsync(factory, codes[0], "Vahe");
        var elsewhere = await ScanAsync(factory, codes[1], "Gor");

        Assert.Equal(host.TabId, pending.TabId);
        Assert.NotEqual(host.TabId, elsewhere.TabId);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // The round: a coffee for Ani, a bottle for the table. One order.
        var placed = await waiter.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/staff-orders",
            new
            {
                items = new object[]
                {
                    new { menuItemId = menu.Coffee, quantity = 1, participantId = host.ParticipantId },
                    new { menuItemId = menu.Wine, quantity = 1, isShared = true },
                },
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var lines = (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines").EnumerateArray().ToList();
        var coffee = lines.Single(l => l.GetProperty("menuItemId").GetGuid() == menu.Coffee);
        var wine = lines.Single(l => l.GetProperty("menuItemId").GetGuid() == menu.Wine);

        Assert.Equal(host.ParticipantId, coffee.GetProperty("onBehalfOfParticipantId").GetGuid());
        Assert.False(coffee.GetProperty("isTableAttributed").GetBoolean());
        Assert.False(
            wine.TryGetProperty("onBehalfOfParticipantId", out var wineOwner) && wineOwner.ValueKind != JsonValueKind.Null,
            "The table's bottle came back in somebody's name.");

        // A shared line naming a guest: 400, naming the line.
        var shared = await SendAsync(
            waiter,
            host.TabId,
            menu.Khachapuri,
            new { menuItemId = menu.Wine, quantity = 1, isShared = true, participantId = host.ParticipantId });
        Assert.Equal(HttpStatusCode.BadRequest, shared.Status);
        Assert.Equal("invalid-request", shared.Problem.GetProperty("code").GetString());
        Assert.Equal("items[1].participantId", shared.Problem.GetProperty("context").GetProperty("field").GetString());

        // Somebody who is not on this tab, made up or at the next table; somebody still waiting.
        foreach (var (participantId, status, code) in new[]
                 {
                     (Guid.CreateVersion7(), HttpStatusCode.NotFound, "not-found"),
                     (elsewhere.ParticipantId, HttpStatusCode.NotFound, "not-found"),
                     (pending.ParticipantId, HttpStatusCode.Conflict, "conflicting-state"),
                 })
        {
            var onTheLine = await SendAsync(
                waiter, host.TabId, menu.Khachapuri, new { menuItemId = menu.Coffee, quantity = 1, participantId });
            var onTheOrder = await SendOrderLevelAsync(waiter, host.TabId, menu.Coffee, participantId);

            Assert.Equal(status, onTheLine.Status);
            Assert.Equal(code, onTheLine.Problem.GetProperty("code").GetString());
            Assert.Equal(onTheOrder.Status, onTheLine.Status);
            Assert.Equal(
                onTheOrder.Problem.GetProperty("detail").GetString(),
                onTheLine.Problem.GetProperty("detail").GetString());
        }

        // From the diner's own phone, on the diner route: 403, as the order-level field is.
        using var phone = factory.CreateClientWithToken(host.AccessToken);
        var fromPhone = await phone.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/orders",
            new
            {
                items = new[] { new { menuItemId = menu.Coffee, quantity = 1, participantId = host.ParticipantId } },
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Forbidden, fromPhone.StatusCode);
        Assert.Equal("forbidden", (await fromPhone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Every refusal refused the whole send.
        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await verify.TabOrders.CountAsync(o => o.TabId == host.TabId));
    }

    /// <summary>
    /// One round with a line per guest reaches each guest's Orders tab with what is theirs, and
    /// tells each of them when it is ready.
    /// </summary>
    /// <remarks>
    /// Both followed the order: an order keyed in on somebody's behalf was theirs, whole. A round
    /// with a line per guest is several people's, so the list finds it by any line keyed in for the
    /// diner, and a guest the host hid the total from is shown their own lines of it - the rule the
    /// tab's <c>myLines</c> follows - not the next guest's dish with its price. "Ready" goes to every
    /// guest with a line on it.
    /// </remarks>
    [SkippableFact]
    public async Task A_round_with_a_line_per_guest_reaches_each_guests_orders_with_their_lines_and_tells_each_it_is_ready()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (aniToken, aniUserId) = await DinerFeedTestData.SignInDinerAsync(factory);
        var (bobToken, bobUserId) = await DinerFeedTestData.SignInDinerAsync(factory);

        AuthBranch branch;
        TestMenu menu;
        AuthTab tab;
        Guid ani;
        Guid bob;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.TableIds[0], now);

            // Ani: the host hid the table's total from her. Bob may see it.
            var hidden = TabParticipant.Guest(tab.TabId, "Ani", $"device-{Guid.NewGuid():N}", now, true, aniUserId);
            hidden.Approve(now);
            var seesAll = TabParticipant.Guest(tab.TabId, "Bob", $"device-{Guid.NewGuid():N}", now, false, bobUserId);
            seesAll.Approve(now);
            db.TabParticipants.AddRange(hidden, seesAll);

            await db.SaveChangesAsync();

            await db.Branches
                .Where(b => b.Id == branch.BranchId)
                .ExecuteUpdateAsync(set => set.SetProperty(b => b.NotifyOnOrderReady, true));

            (ani, bob) = (hidden.Id, seesAll.Id);
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var placed = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tab.TabId}/staff-orders",
            new
            {
                items = new object[]
                {
                    new { menuItemId = menu.Khachapuri, quantity = 1, participantId = ani },
                    new { menuItemId = menu.Coffee, quantity = 1, participantId = bob },
                    new { menuItemId = menu.Wine, quantity = 1, isShared = true },
                },
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var orderId = (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();

        // Ani: her khachapuri and the bottle she was at the table for - not Bob's coffee.
        using var aniClient = factory.CreateClientWithToken(aniToken);
        var hers = Assert.Single((await aniClient.GetFromJsonAsync<JsonElement>("/api/diner/orders")).EnumerateArray());

        Assert.Equal(orderId, hers.GetProperty("orderId").GetGuid());
        Assert.Equal(
            new[] { TestMenu.KhachapuriAmd, TestMenu.WineAmd }.Order(),
            hers.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("lineTotalAmd").GetInt64()).Order());
        Assert.Equal(TestMenu.KhachapuriAmd + TestMenu.WineAmd, hers.GetProperty("totalAmd").GetInt64());

        var single = await aniClient.GetFromJsonAsync<JsonElement>($"/api/diner/orders/{orderId}");
        Assert.Equal(2, single.GetProperty("items").GetArrayLength());

        // Bob may see the table's bill, so the round is shown whole.
        using var bobClient = factory.CreateClientWithToken(bobToken);
        var his = Assert.Single((await bobClient.GetFromJsonAsync<JsonElement>("/api/diner/orders")).EnumerateArray());

        Assert.Equal(orderId, his.GetProperty("orderId").GetGuid());
        Assert.Equal(3, his.GetProperty("items").GetArrayLength());

        // Ready: each of them is told.
        Assert.Equal(HttpStatusCode.OK, (await waiter.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await waiter.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = 3 })).StatusCode);

        foreach (var client in new[] { aniClient, bobClient })
        {
            var entry = Assert.Single(
                (await client.GetFromJsonAsync<JsonElement>("/api/diner/notifications")).GetProperty("items").EnumerateArray());

            Assert.Equal(DinerNotificationKinds.OrderReady, entry.GetProperty("kind").GetString());
            Assert.Equal(orderId, entry.GetProperty("orderId").GetGuid());
        }
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private sealed record Scanned(Guid TabId, Guid ParticipantId, string AccessToken);

    /// <summary>Scans a table's code as a phone does: the first opens the tab, the next waits on the host.</summary>
    private static async Task<Scanned> ScanAsync(YallaApiFactory factory, string qrToken, string displayName)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/tabs/open",
            new
            {
                qrToken,
                deviceId = $"phone-{Guid.NewGuid():N}",
                displayName,
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tab = body.GetProperty("tab");

        return new Scanned(
            tab.GetProperty("tabId").GetGuid(),
            tab.GetProperty("me").GetProperty("participantId").GetGuid(),
            body.GetProperty("token").GetProperty("accessToken").GetString()!);
    }

    /// <summary>A waiter's send of a plain line of <paramref name="first"/>, then <paramref name="line"/>.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Problem)> SendAsync(
        HttpClient waiter, Guid tabId, Guid first, object line)
    {
        var response = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/staff-orders",
            new
            {
                items = new object[] { new { menuItemId = first, quantity = 1 }, line },
                clientCommandId = Guid.CreateVersion7(),
            });

        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Problem)> SendOrderLevelAsync(
        HttpClient waiter, Guid tabId, Guid menuItemId, Guid onBehalfOfParticipantId)
    {
        var response = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/staff-orders",
            new
            {
                items = new[] { new { menuItemId, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
                onBehalfOfParticipantId,
            });

        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }
}
