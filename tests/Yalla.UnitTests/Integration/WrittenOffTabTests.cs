using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A tab written off - the party left without paying and a manager took the loss - leaves nothing live behind it on the
/// floor: its open calls drop off the rail, its orders leave the kitchen's queue and the waiter's to-send list, and
/// every tablet following the change stream hears it at once.
/// </summary>
/// <remarks>
/// Found in QA (2026-10-27): the "bill, please" of a table that walked out stayed on the floor's rail, and its unsent
/// and cooking orders stayed on the queue, after the manager had written the tab off. Nothing about the money changes
/// here: the orders keep the status they had (no void - the amount written off is what was on the bill), the calls are
/// answered rather than deleted, and the bill the manager wrote off is the bill that stays.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class WrittenOffTabTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_tab_written_off_takes_its_calls_and_orders_off_the_floor_and_tells_the_change_stream()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .StartingAtYerevan(new TimeOnly(15, 0));

        AuthBranch branch;
        TestMenu menu;
        string[] qrTokens;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            qrTokens = [.. await db.DiningTables.AsNoTracking()
                .Where(t => t.BranchId == branch.BranchId)
                .OrderBy(t => t.Label)
                .Select(t => t.QrToken)
                .ToListAsync()];
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        // The table that walks out, and a neighbour that stays.
        var walksOut = await ScanAndAdmitAsync(factory, qrTokens[0], "phone-walks-out");
        var stays = await ScanAndAdmitAsync(factory, qrTokens[1], "phone-stays");
        using var host = walksOut.Phone;
        using var neighbour = stays.Phone;
        var tabId = walksOut.TabId;
        var neighbourTabId = stays.TabId;

        // On the walking-out tab: a phone's order waiting for a waiter to send it (New), the waiter's round in the
        // kitchen (InKitchen), and a dish up at the pass (Ready).
        var toSend = await OrderIdAsync(await host.PostAsJsonAsync(
            $"/api/tabs/{tabId}/orders",
            new { items = new[] { new { menuItemId = menu.Coffee, quantity = 1 } }, clientCommandId = Guid.CreateVersion7() }));
        var cooking = await OrderIdAsync(await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/staff-orders",
            new { items = new[] { new { menuItemId = menu.Khachapuri, quantity = 1 } }, clientCommandId = Guid.CreateVersion7() }));
        var ready = await OrderIdAsync(await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/staff-orders",
            new { items = new[] { new { menuItemId = menu.Wine, quantity = 1 } }, clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(waiter, ready, TabOrderStatus.Ready)).StatusCode);

        // Water a waiter said "I'm on it" to, and the bill, asked for and not yet brought.
        var water = await CallAsync(host, tabId, new { preset = ServiceRequestPreset.Water });
        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsync($"/api/branches/{branch.BranchId}/calls/{water}/claim", content: null)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var bill = await CallAsync(host, tabId, new { preset = ServiceRequestPreset.TheBill, billScope = BillScope.WholeBill });

        // The neighbour's call and order, which nothing here may touch.
        var neighbourCall = await CallAsync(neighbour, neighbourTabId, new { preset = ServiceRequestPreset.Napkins });
        var neighbourOrder = await OrderIdAsync(await waiter.PostAsJsonAsync(
            $"/api/tabs/{neighbourTabId}/staff-orders",
            new { items = new[] { new { menuItemId = menu.Coffee, quantity = 2 } }, clientCommandId = Guid.CreateVersion7() }));

        // Both live on the floor before the write-off.
        var callsBefore = await IdsAsync(waiter, $"/api/branches/{branch.BranchId}/service-requests", "serviceRequestId");
        Assert.Superset(new HashSet<Guid> { water, bill, neighbourCall }, callsBefore);
        var queueBefore = await IdsAsync(waiter, $"/api/branches/{branch.BranchId}/orders", "orderId");
        Assert.Superset(new HashSet<Guid> { toSend, cooking, ready, neighbourOrder }, queueBefore);

        var owed = await RemainingAsync(waiter, tabId);
        var streamFrom = await MaxSequenceAsync(waiter, branch.BranchId);

        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var writtenOffAt = factory.Clock.UtcNow;
        var written = await manager.PostAsJsonAsync($"/api/tabs/{tabId}/abandon", new { reason = "they walked out" });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        // The rail: the table's calls are gone, the neighbour's stays.
        var callsAfter = await IdsAsync(waiter, $"/api/branches/{branch.BranchId}/service-requests", "serviceRequestId");
        Assert.DoesNotContain(water, callsAfter);
        Assert.DoesNotContain(bill, callsAfter);
        Assert.Contains(neighbourCall, callsAfter);

        // The queue - the kitchen's tickets and the waiter's to-send list read from it: the table's orders are gone,
        // the neighbour's stays.
        var queueAfter = await IdsAsync(waiter, $"/api/branches/{branch.BranchId}/orders", "orderId");
        Assert.DoesNotContain(toSend, queueAfter);
        Assert.DoesNotContain(cooking, queueAfter);
        Assert.DoesNotContain(ready, queueAfter);
        Assert.Contains(neighbourOrder, queueAfter);

        // A tablet that still shows the ticket cannot send, cook, serve or void it: the party has gone and the bill is
        // written off. Nothing moves.
        Assert.Equal(HttpStatusCode.Conflict, (await MoveAsync(waiter, toSend, TabOrderStatus.InKitchen)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await MoveAsync(waiter, cooking, TabOrderStatus.Ready)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await MoveAsync(waiter, ready, TabOrderStatus.Served)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await MoveAsync(manager, cooking, TabOrderStatus.Voided)).StatusCode);

        // Every tablet following the stream hears it: one same-status row on the table, naming the tab and the manager.
        var page = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/changes?afterSequence={streamFrom}&limit=500");
        var signalled = page.GetProperty("changes").EnumerateArray()
            .Where(c => c.TryGetProperty("signal", out var signal) && signal.GetInt32() == (int)TableChangeSignal.TabWrittenOff)
            .ToList();
        var row = Assert.Single(signalled);
        Assert.Equal(tabId, row.GetProperty("tabId").GetGuid());
        Assert.Equal(branch.FirstTableId, row.GetProperty("diningTableId").GetGuid());
        Assert.Equal(row.GetProperty("fromStatus").GetInt32(), row.GetProperty("toStatus").GetInt32());
        Assert.Equal((int)TableStatus.Occupied, row.GetProperty("toStatus").GetInt32());
        Assert.Equal((int)ActorType.Staff, row.GetProperty("actorType").GetInt32());
        Assert.Equal(branch.ManagerId, row.GetProperty("actorId").GetGuid());

        await using var verify = fixture.CreateContext(factory.Clock);

        // The calls are answered by the manager who wrote the tab off, at that moment - not deleted, and not a claim:
        // nobody walked over, so no waiter's response time is made of it. The water keeps the claim it had.
        var calls = await verify.ServiceRequests.AsNoTracking()
            .Where(r => r.TabId == tabId)
            .ToDictionaryAsync(r => r.Id);
        Assert.Equal(2, calls.Count);
        Assert.All(calls.Values, call =>
        {
            Assert.Equal(writtenOffAt, call.AcknowledgedAtUtc!.Value, TimeSpan.FromMilliseconds(10));
            Assert.Equal(branch.ManagerId, call.AcknowledgedByStaffId);
            Assert.Null(call.SupersededAtUtc);
        });
        Assert.Equal(branch.WaiterId, calls[water].ClaimedByStaffId);
        Assert.Null(calls[bill].ClaimedAtUtc);
        Assert.Null(calls[bill].ClaimedByStaffId);

        // The orders keep the status they had: no void, nothing off the bill the manager wrote off.
        var statuses = await verify.TabOrders.AsNoTracking()
            .Where(o => o.TabId == tabId)
            .ToDictionaryAsync(o => o.Id, o => o.Status);
        Assert.Equal(TabOrderStatus.New, statuses[toSend]);
        Assert.Equal(TabOrderStatus.InKitchen, statuses[cooking]);
        Assert.Equal(TabOrderStatus.Ready, statuses[ready]);
        Assert.False(await verify.TabOrderLines.AnyAsync(l => l.TabOrder.TabId == tabId && l.VoidedAtUtc != null));

        var tab = await verify.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId);
        Assert.Equal(TabStatus.Abandoned, tab.Status);
        Assert.Equal(owed, tab.RemainingAmd);
        Assert.Equal(owed, (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("remainingAmd").GetInt64());

        // The neighbour's call is still nobody's.
        Assert.Null((await verify.ServiceRequests.AsNoTracking().SingleAsync(r => r.Id == neighbourCall)).AcknowledgedAtUtc);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A phone scans the table's code; the waiter lets the tab in. Answers the tab and the phone.</summary>
    private async Task<(Guid TabId, HttpClient Phone)> ScanAndAdmitAsync(YallaApiFactory factory, string qrToken, string deviceId)
    {
        using var anonymous = factory.CreateClient();
        var opened = await anonymous.PostAsJsonAsync(
            "/api/tabs/open",
            new { qrToken, deviceId, clientCommandId = Guid.CreateVersion7(), displayName = "Host" });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

        var body = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var tabId = body.GetProperty("tab").GetProperty("tabId").GetGuid();

        await fixture.AdmitTabAsync(factory.Clock, tabId);

        return (tabId, factory.CreateClientWithToken(body.GetProperty("token").GetProperty("accessToken").GetString()!));
    }

    private static async Task<Guid> OrderIdAsync(HttpResponseMessage placed)
    {
        Assert.True(placed.StatusCode == HttpStatusCode.Created, await placed.Content.ReadAsStringAsync());

        return (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
    }

    private static async Task<Guid> CallAsync(HttpClient phone, Guid tabId, object body)
    {
        var response = await phone.PostAsJsonAsync($"/api/tabs/{tabId}/service-requests", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("serviceRequestId").GetGuid();
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, Guid orderId, TabOrderStatus status) =>
        client.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status });

    private static async Task<HashSet<Guid>> IdsAsync(HttpClient client, string url, string idMember) =>
        [.. (await ReadAsync(client, url)).EnumerateArray().Select(e => e.GetProperty(idMember).GetGuid())];

    private static async Task<long> RemainingAsync(HttpClient staff, Guid tabId) =>
        (await ReadAsync(staff, $"/api/tabs/{tabId}/participants")).GetProperty("totals").GetProperty("remainingAmd").GetInt64();

    private static async Task<long> MaxSequenceAsync(HttpClient staff, Guid branchId) =>
        (await ReadAsync(staff, $"/api/branches/{branchId}/tables/changes?afterSequence=0&limit=1"))
            .GetProperty("maxSequence").GetInt64();

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} answered {(int)response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
