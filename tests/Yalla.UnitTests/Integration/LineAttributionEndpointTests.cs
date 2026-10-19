using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
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

        // Absent, not null: the wire omits a null member, and the generated client reads it as optional.
        Assert.False(
            wine.TryGetProperty("onBehalfOfParticipantId", out _),
            "The table's bottle came back with an onBehalfOfParticipantId member.");

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

        // One push each, under a key of their own: the round has no order-level guest, so neither
        // of them holds the order's own "ready" key.
        await using var verify = fixture.CreateContext(factory.Clock);
        var keys = await verify.OutboxMessages
            .AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith(OutboxMessageTypes.PrefixFor("order", orderId)))
            .Select(m => m.IdempotencyKey)
            .ToListAsync();

        Assert.Equal(
            new[]
            {
                OutboxMessageTypes.KeyFor("order", orderId, $"ready:{ani}"),
                OutboxMessageTypes.KeyFor("order", orderId, $"ready:{bob}"),
            }.Order(StringComparer.Ordinal),
            keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// An order "for" a guest with nothing of theirs left on it neither lists for them nor tells
    /// them it is ready; one with something of theirs shows them only that, and the bottle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order-level <c>onBehalfOfParticipantId</c> is a default for the lines that name nobody,
    /// not a claim on the order. A waiter can send it for Ani and put every line on Bob, and a line
    /// of Ani's can be voided before the food is up: either way nothing of hers is coming, and a
    /// "your order is ready" push - or the round on her Orders tab with Bob's dish on it - is wrong.
    /// </para>
    /// <para>
    /// Three rounds. A: for Ani, its one line put on Bob. B: for Ani, a coffee of hers that is then
    /// voided and a bottle put on Bob. C: for Cara, whom the host hid the total from - her
    /// khachapuri, Bob's coffee, and a shared bottle.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_order_for_a_guest_with_nothing_of_theirs_on_it_neither_lists_for_them_nor_tells_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (aniToken, aniUserId) = await DinerFeedTestData.SignInDinerAsync(factory);
        var (bobToken, bobUserId) = await DinerFeedTestData.SignInDinerAsync(factory);
        var (caraToken, caraUserId) = await DinerFeedTestData.SignInDinerAsync(factory);

        AuthBranch branch;
        TestMenu menu;
        AuthTab tab;
        Guid ani;
        Guid bob;
        Guid cara;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.TableIds[0], now);

            var guests = new[] { ("Ani", false, aniUserId), ("Bob", false, bobUserId), ("Cara", true, caraUserId) }
                .Select(g =>
                {
                    var participant = TabParticipant.Guest(tab.TabId, g.Item1, $"device-{Guid.NewGuid():N}", now, g.Item2, g.Item3);
                    participant.Approve(now);
                    return participant;
                })
                .ToList();

            db.TabParticipants.AddRange(guests);
            await db.SaveChangesAsync();

            await db.Branches
                .Where(b => b.Id == branch.BranchId)
                .ExecuteUpdateAsync(set => set.SetProperty(b => b.NotifyOnOrderReady, true));

            (ani, bob, cara) = (guests[0].Id, guests[1].Id, guests[2].Id);
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        async Task<JsonElement> SendAsync(Guid forWhom, params object[] items)
        {
            var response = await waiter.PostAsJsonAsync(
                $"/api/tabs/{tab.TabId}/staff-orders",
                new { items, clientCommandId = Guid.CreateVersion7(), onBehalfOfParticipantId = forWhom });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var allOnBob = await SendAsync(ani, new { menuItemId = menu.Khachapuri, quantity = 1, participantId = bob });
        var voidedForAni = await SendAsync(
            ani,
            new { menuItemId = menu.Coffee, quantity = 1 },
            new { menuItemId = menu.Wine, quantity = 1, participantId = bob });
        var forCara = await SendAsync(
            cara,
            new { menuItemId = menu.Khachapuri, quantity = 1 },
            new { menuItemId = menu.Coffee, quantity = 1, participantId = bob },
            new { menuItemId = menu.Wine, quantity = 1, isShared = true });

        var (a, b, c) = (
            allOnBob.GetProperty("orderId").GetGuid(),
            voidedForAni.GetProperty("orderId").GetGuid(),
            forCara.GetProperty("orderId").GetGuid());

        var anisCoffee = voidedForAni.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("menuItemId").GetGuid() == menu.Coffee)
            .GetProperty("lineId").GetGuid();

        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync(
                $"/api/tabs/{tab.TabId}/lines/{anisCoffee}/void",
                new { reason = "sent back", clientCommandId = Guid.CreateVersion7() })).StatusCode);

        foreach (var orderId in new[] { a, b })
        {
            Assert.Equal(HttpStatusCode.OK, (await waiter.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = 2 })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await waiter.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = 3 })).StatusCode);
        }

        // Ani: A had nothing of hers, so it is not on her list. B is - her coffee is on it, voided and
        // labelled, as a voided line always stayed - but neither is ready for her.
        using var aniClient = factory.CreateClientWithToken(aniToken);
        Assert.Equal(
            [b],
            (await aniClient.GetFromJsonAsync<JsonElement>("/api/diner/orders")).EnumerateArray()
                .Select(o => o.GetProperty("orderId").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await aniClient.GetAsync($"/api/diner/orders/{a}")).StatusCode);
        Assert.Equal(
            0,
            (await aniClient.GetFromJsonAsync<JsonElement>("/api/diner/notifications")).GetProperty("items").GetArrayLength());

        // Bob is told about both, each under his own key.
        using var bobClient = factory.CreateClientWithToken(bobToken);
        Assert.Equal(
            new[] { a, b }.Order(),
            (await bobClient.GetFromJsonAsync<JsonElement>("/api/diner/notifications")).GetProperty("items")
                .EnumerateArray().Select(e => e.GetProperty("orderId").GetGuid()).Order());

        await using (var verify = fixture.CreateContext(factory.Clock))
        {
            var keys = await verify.OutboxMessages.AsNoTracking()
                .Where(m => m.IdempotencyKey.StartsWith("order:"))
                .Where(m => m.IdempotencyKey.Contains(a.ToString()) || m.IdempotencyKey.Contains(b.ToString()))
                .Select(m => m.IdempotencyKey)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    OutboxMessageTypes.KeyFor("order", a, $"ready:{bob}"),
                    OutboxMessageTypes.KeyFor("order", b, $"ready:{bob}"),
                }.Order(StringComparer.Ordinal),
                keys.Order(StringComparer.Ordinal));
        }

        // Cara, without the total: her khachapuri and the bottle she shares - not Bob's coffee.
        using var caraClient = factory.CreateClientWithToken(caraToken);
        var hers = Assert.Single((await caraClient.GetFromJsonAsync<JsonElement>("/api/diner/orders")).EnumerateArray());

        Assert.Equal(c, hers.GetProperty("orderId").GetGuid());
        Assert.Equal(
            new[] { TestMenu.KhachapuriAmd, TestMenu.WineAmd }.Order(),
            hers.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("lineTotalAmd").GetInt64()).Order());
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
