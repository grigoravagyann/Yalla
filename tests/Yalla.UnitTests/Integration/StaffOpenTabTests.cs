using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>POST /api/branches/{branchId}/tables/{tableId}/open-tab</c>: staff open the tab for a
/// walk-in who is not going to scan.
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes: a waiter could seat a walk-in, but a tab only ever opened from a phone - the
/// QR scan or a booking code. A party with no phone, or no wish to use one, could be sat down and
/// then not served through the system at all, because "take an order" needs a tab.
/// </para>
/// <para>
/// The tab opens with nobody on it. The rule that makes that safe is the second half of these
/// tests: somebody who scans afterwards becomes the host rather than waiting, pending, for a host
/// who does not exist.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class StaffOpenTabTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Seated, opened, ordered, paid: the whole walk-in visit with no phone on the tab, and the floor
    /// showing the tab in between.
    /// </summary>
    [SkippableFact]
    public async Task A_waiter_opens_a_tab_for_a_seated_walk_in_keys_the_order_and_takes_the_cash()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu) = await ArrangeAsync(factory);
        var tableId = branch.FirstTableId;

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        await SeatWalkInAsync(waiter, branch, tableId);

        var response = await OpenAsync(waiter, branch, tableId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tab = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tabId = tab.GetProperty("tabId").GetGuid();

        Assert.Equal(tableId, tab.GetProperty("diningTableId").GetGuid());
        Assert.Equal(TabStatus.Open, (TabStatus)tab.GetProperty("status").GetInt32());
        Assert.Equal(0, tab.GetProperty("participants").GetArrayLength());
        Assert.False(
            tab.TryGetProperty("hostParticipantId", out var host) && host.ValueKind != JsonValueKind.Null,
            "A tab staff opened has nobody to host it.");

        // The floor is how the tablet finds the tab again: the table now names it.
        var floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        var row = floor.GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("tableId").GetGuid() == tableId);

        Assert.Equal(tabId, row.GetProperty("openTabId").GetGuid());

        // An order with nobody on the tab lands on the table, and cash for all of it closes the bill.
        var ordered = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/staff-orders",
            new { items = new[] { new { menuItemId = menu.Khachapuri, quantity = 2 } }, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Created, ordered.StatusCode);

        var owed = (await ReadAsync(waiter, $"/api/tabs/{tabId}/participants"))
            .GetProperty("totals").GetProperty("remainingAmd").GetInt64();

        Assert.True(owed > 0, "Nothing was ordered, so there is nothing to pay.");

        var paid = await waiter.PostAsJsonAsync(
            $"/api/tabs/{tabId}/payments/cash",
            new { amountAmd = owed, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Created, paid.StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);

        Assert.Equal(TabStatus.Closed, (await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId)).Status);
    }

    /// <summary>
    /// A party that has paid and sits on can be given a new tab without freeing the table first.
    /// </summary>
    /// <remarks>
    /// Paying in full closes the sitting but leaves the table Occupied, because a party that has
    /// paid usually stays for another twenty minutes. The tablet then offered "Start a tab" and
    /// the server answered "nobody is seated" about a table the screen showed occupied - and
    /// seating it again was refused as Occupied to Occupied, so the waiter was stuck (QA
    /// 2026-10-17).
    /// </remarks>
    [SkippableFact]
    public async Task A_party_that_paid_and_sits_on_gets_a_new_tab_on_a_new_sitting()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu) = await ArrangeAsync(factory);
        var tableId = branch.FirstTableId;

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        await SeatWalkInAsync(waiter, branch, tableId);
        var first = await TabIdOfAsync(await OpenAsync(waiter, branch, tableId));

        var ordered = await waiter.PostAsJsonAsync(
            $"/api/tabs/{first}/staff-orders",
            new { items = new[] { new { menuItemId = menu.Khachapuri, quantity = 1 } }, clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Created, ordered.StatusCode);

        var owed = (await ReadAsync(waiter, $"/api/tabs/{first}/participants"))
            .GetProperty("totals").GetProperty("remainingAmd").GetInt64();
        var paid = await waiter.PostAsJsonAsync(
            $"/api/tabs/{first}/payments/cash",
            new { amountAmd = owed, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Created, paid.StatusCode);

        // Paid, sitting closed, table still Occupied: the case the tablet could not get out of.
        var second = await TabIdOfAsync(await OpenAsync(waiter, branch, tableId));

        Assert.NotEqual(first, second);

        var floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        var row = floor.GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("tableId").GetGuid() == tableId);

        Assert.Equal(second, row.GetProperty("openTabId").GetGuid());
        Assert.Equal(TableStatus.Occupied, (TableStatus)row.GetProperty("physicalStatus").GetInt32());

        // A sitting of its own, for the same party: the paid bill's sitting stays closed.
        await using var db = fixture.CreateContext(factory.Clock);

        var sittings = await db.TableSessions.AsNoTracking()
            .Where(s => s.DiningTableId == tableId)
            .ToListAsync();

        // The test clock does not move, so both sittings share a start: closed one first.
        sittings = [.. sittings.OrderBy(s => s.ClosedAtUtc is null)];

        Assert.Equal(2, sittings.Count);
        Assert.NotNull(sittings[0].ClosedAtUtc);
        Assert.Null(sittings[1].ClosedAtUtc);
        Assert.Equal(sittings[0].PartySize, sittings[1].PartySize);
        Assert.Equal(sittings[1].Id, (await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == second)).TableSessionId);
    }

    /// <summary>
    /// A second tap, a retry, and a guest who scanned first all end on one tab.
    /// </summary>
    [SkippableFact]
    public async Task Opening_twice_or_after_a_guest_scanned_answers_with_the_one_tab_already_there()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // The same tap twice, and then a second tap with its own id.
        var first = branch.TableIds[0];
        await SeatWalkInAsync(waiter, branch, first);

        var commandId = Guid.CreateVersion7();
        var original = await TabIdOfAsync(await OpenAsync(waiter, branch, first, commandId));
        var retried = await TabIdOfAsync(await OpenAsync(waiter, branch, first, commandId));
        var again = await TabIdOfAsync(await OpenAsync(waiter, branch, first));

        Assert.Equal(original, retried);
        Assert.Equal(original, again);

        // A guest scanned the second table before the waiter got to it: the waiter gets that tab.
        var second = branch.TableIds[1];
        var scanned = await ScanAsync(factory, await QrTokenAsync(factory, second), "phone-first");
        var scannedTabId = scanned.GetProperty("tab").GetProperty("tabId").GetGuid();

        Assert.Equal(scannedTabId, await TabIdOfAsync(await OpenAsync(waiter, branch, second)));

        await using var db = fixture.CreateContext(factory.Clock);

        Assert.Equal(1, await db.Tabs.CountAsync(t => t.DiningTableId == first));
        Assert.Equal(1, await db.Tabs.CountAsync(t => t.DiningTableId == second));

        // The same command id at another table is a client bug, not a way onto that tab.
        var reused = await OpenAsync(waiter, branch, second, commandId);

        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
    }

    /// <summary>
    /// Nobody sitting there: refused with its own code, and nothing seated or opened behind it.
    /// </summary>
    [SkippableFact]
    public async Task A_free_table_is_refused_as_not_seated_and_nothing_is_written()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var response = await OpenAsync(waiter, branch, branch.FirstTableId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("table-not-seated", problem.GetProperty("code").GetString());
        Assert.Equal(branch.FirstTableId, problem.GetProperty("context").GetProperty("tableId").GetGuid());
        Assert.Equal((int)TableStatus.Free, problem.GetProperty("context").GetProperty("status").GetInt32());

        await using var db = fixture.CreateContext(factory.Clock);

        Assert.False(await db.Tabs.AnyAsync(t => t.DiningTableId == branch.FirstTableId));
        Assert.False(await db.TableSessions.AnyAsync(s => s.DiningTableId == branch.FirstTableId));
    }

    /// <summary>
    /// A waiter at another branch cannot open a tab here, and a table at another branch is not found
    /// through this branch's route.
    /// </summary>
    [SkippableFact]
    public async Task Staff_at_another_branch_cannot_open_a_tab_here()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);

        AuthBranch elsewhere;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            elsewhere = await AuthTestData.CreateBranchAsync(db);
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        await SeatWalkInAsync(waiter, branch, branch.FirstTableId);

        using var outsider = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, elsewhere));

        Assert.Equal(HttpStatusCode.Forbidden, (await OpenAsync(outsider, branch, branch.FirstTableId)).StatusCode);

        // Their own branch in the route, this branch's table in it.
        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(outsider, elsewhere, branch.FirstTableId)).StatusCode);

        await using var verify = fixture.CreateContext(factory.Clock);

        Assert.False(await verify.Tabs.AnyAsync(t => t.DiningTableId == branch.FirstTableId));
    }

    /// <summary>
    /// The first phone to scan a tab staff opened hosts it, approved on the spot; the next one waits
    /// for that host as usual.
    /// </summary>
    /// <remarks>
    /// Without this a scan landed pending on a tab with no host, and nobody could ever approve it.
    /// </remarks>
    [SkippableFact]
    public async Task The_first_scan_on_a_tab_staff_opened_becomes_its_host_and_the_next_waits_for_them()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        var tableId = branch.FirstTableId;

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        await SeatWalkInAsync(waiter, branch, tableId);
        var tabId = await TabIdOfAsync(await OpenAsync(waiter, branch, tableId));

        var qr = await QrTokenAsync(factory, tableId);

        var first = await ScanAsync(factory, qr, "phone-ani");
        var firstMe = first.GetProperty("tab").GetProperty("me");

        Assert.Equal(tabId, first.GetProperty("tab").GetProperty("tabId").GetGuid());
        Assert.Equal(TabOpenOutcome.JoinedExistingTab, (TabOpenOutcome)first.GetProperty("outcome").GetInt32());
        Assert.Equal(ParticipantRole.Host, (ParticipantRole)firstMe.GetProperty("role").GetInt32());
        Assert.Equal(ParticipantStatus.Approved, (ParticipantStatus)firstMe.GetProperty("status").GetInt32());

        var second = await ScanAsync(factory, qr, "phone-gor");
        var secondMe = second.GetProperty("tab").GetProperty("me");

        Assert.Equal(ParticipantRole.Guest, (ParticipantRole)secondMe.GetProperty("role").GetInt32());
        Assert.Equal(ParticipantStatus.PendingApproval, (ParticipantStatus)secondMe.GetProperty("status").GetInt32());

        // The staff view agrees on who hosts.
        var view = await ReadAsync(waiter, $"/api/tabs/{tabId}/participants");

        Assert.Equal(
            firstMe.GetProperty("participantId").GetGuid(),
            view.GetProperty("hostParticipantId").GetGuid());
        Assert.Equal(2, view.GetProperty("participants").GetArrayLength());
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<(AuthBranch Branch, TestMenu Menu)> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await AuthTestData.CreateBranchAsync(db);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        return (branch, menu);
    }

    private static async Task SeatWalkInAsync(HttpClient staff, AuthBranch branch, Guid tableId)
    {
        var response = await staff.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{tableId}/seat-walk-in",
            new { partySize = 2, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> OpenAsync(
        HttpClient staff,
        AuthBranch branch,
        Guid tableId,
        Guid? clientCommandId = null) =>
        staff.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{tableId}/open-tab",
            new { clientCommandId = clientCommandId ?? Guid.CreateVersion7() });

    private static async Task<Guid> TabIdOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tabId").GetGuid();
    }

    private async Task<string> QrTokenAsync(YallaApiFactory factory, Guid tableId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.DiningTables.AsNoTracking().Where(t => t.Id == tableId).Select(t => t.QrToken).SingleAsync();
    }

    private static async Task<JsonElement> ScanAsync(YallaApiFactory factory, string qrToken, string deviceId)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/tabs/open",
            new { qrToken, deviceId, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
