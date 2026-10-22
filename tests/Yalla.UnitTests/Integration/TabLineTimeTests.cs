using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// When each line on a tab was ordered: <c>orderedAtUtc</c>, the <c>placedAtUtc</c> of the order the
/// line came in on, on every read that lists the tab's lines.
/// </summary>
/// <remarks>
/// The diner's bill reads "Ani · 19:08" under each dish. The time is the order's, not the line's: a
/// round is placed at once, and a pre-order that arrives on the tab when the party sits keeps the
/// time the waiter sent it to the kitchen (<c>PreOrderTests</c> pins that one).
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TabLineTimeTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Every_line_carries_its_orders_placed_time_on_the_tab_the_staff_view_and_the_stream()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);

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

        var host = await ScanAsync(factory, qrToken, "Ani");
        using var phone = factory.CreateClientWithToken(host.AccessToken);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync($"/api/tabs/{host.TabId}/admit", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        // A coffee from Ani's own phone.
        var dinerAt = factory.Clock.UtcNow;
        var dinersOrder = await CreatedAsync(await phone.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/orders",
            new
            {
                items = new[] { new { menuItemId = menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            }));

        // Three minutes later, a waiter's round: a khachapuri for Ani and a bottle for the table.
        factory.Clock.UtcNow = dinerAt.AddMinutes(3);
        var waiterAt = factory.Clock.UtcNow;
        var waitersOrder = await CreatedAsync(await waiter.PostAsJsonAsync(
            $"/api/tabs/{host.TabId}/staff-orders",
            new
            {
                items = new object[]
                {
                    new { menuItemId = menu.Khachapuri, quantity = 1, participantId = host.ParticipantId },
                    new { menuItemId = menu.Wine, quantity = 1, isShared = true },
                },
                clientCommandId = Guid.CreateVersion7(),
            }));

        var dinersOrderId = dinersOrder.GetProperty("orderId").GetGuid();
        var waitersOrderId = waitersOrder.GetProperty("orderId").GetGuid();

        // What the rows say, which is what every read has to agree with.
        Dictionary<Guid, DateTime> placedAt;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            placedAt = await db.TabOrders.AsNoTracking()
                .Where(o => o.TabId == host.TabId)
                .ToDictionaryAsync(o => o.Id, o => o.PlacedAtUtc);
        }

        Assert.Equal(dinerAt, placedAt[dinersOrderId]);
        Assert.Equal(waiterAt, placedAt[waitersOrderId]);

        // ---- The diner's tab: their own lines and the table's, each with its order's time.
        var view = await ReadAsync(phone, $"/api/tabs/{host.TabId}");
        var myLines = view.GetProperty("myLines").EnumerateArray().ToList();
        var tableLines = view.GetProperty("tableLines").EnumerateArray().ToList();

        Assert.Equal(3, myLines.Count);
        Assert.Equal(3, tableLines.Count);

        foreach (var line in myLines.Concat(tableLines))
        {
            Assert.Equal(placedAt[line.GetProperty("orderId").GetGuid()], OrderedAt(line));
        }

        Assert.Equal(dinerAt, OrderedAt(myLines.Single(l => l.GetProperty("menuItemId").GetGuid() == menu.Coffee)));
        Assert.Equal(waiterAt, OrderedAt(myLines.Single(l => l.GetProperty("menuItemId").GetGuid() == menu.Khachapuri)));
        Assert.Equal(waiterAt, OrderedAt(myLines.Single(l => l.GetProperty("menuItemId").GetGuid() == menu.Wine)));

        // ---- The staff view of the same tab.
        var staffLines = (await ReadAsync(waiter, $"/api/tabs/{host.TabId}/participants"))
            .GetProperty("lines").EnumerateArray().ToList();

        Assert.Equal(3, staffLines.Count);
        Assert.All(staffLines, l => Assert.Equal(placedAt[l.GetProperty("orderId").GetGuid()], OrderedAt(l)));

        // ---- The stream: each OrderPlaced line says when, so a phone catching up needs no refetch.
        var placedEvents = (await ReadAsync(phone, $"/api/tabs/{host.TabId}/events?afterSequence=0"))
            .GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("type").GetInt32() == (int)TabEventType.OrderPlaced)
            .Select(e => e.GetProperty("payload"))
            .ToList();

        Assert.Equal(2, placedEvents.Count);

        foreach (var payload in placedEvents)
        {
            var orderId = payload.GetProperty("orderId").GetGuid();
            Assert.All(
                payload.GetProperty("lines").EnumerateArray(),
                l => Assert.Equal(placedAt[orderId], OrderedAt(l)));
        }
    }

    private static DateTime OrderedAt(JsonElement line)
    {
        Assert.True(line.TryGetProperty("orderedAtUtc", out var value), "A line came back without orderedAtUtc.");

        return value.GetDateTime();
    }

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed record Scanned(Guid TabId, Guid ParticipantId, string AccessToken);

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
}
