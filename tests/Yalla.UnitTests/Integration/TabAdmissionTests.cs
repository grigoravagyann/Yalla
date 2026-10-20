using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A QR scan with no proved number behind it waits for a waiter's admission before anybody on it can order
/// (decided 2026-10-20). A photographed code can be scanned from anywhere; the waiter can see the table.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class TabAdmissionTests(SqlServerFixture fixture)
{
    /// <summary>
    /// An anonymous scan opens a tab that waits: the phone reads it and calls a waiter but cannot order, the
    /// floor and the rail show it, and once a waiter admits it the host orders - an order that still waits at
    /// New for a waiter.
    /// </summary>
    [SkippableFact]
    public async Task An_unverified_scan_waits_for_a_waiter_and_orders_once_admitted()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu) = await ArrangeAsync(factory);
        var tableId = branch.FirstTableId;

        var scan = await ScanAsync(factory, await QrTokenAsync(factory, tableId), "phone-host");
        var tab = scan.GetProperty("tab");
        var tabId = tab.GetProperty("tabId").GetGuid();

        Assert.Equal(TabAdmission.AwaitingAdmission, (TabAdmission)tab.GetProperty("admission").GetProperty("status").GetInt32());
        Assert.False(tab.GetProperty("me").GetProperty("canOrderNow").GetBoolean());

        using var phone = factory.CreateClientWithToken(scan.GetProperty("token").GetProperty("accessToken").GetString()!);

        var refused = await phone.PostAsJsonAsync($"/api/tabs/{tabId}/orders", NewOrder(menu));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("tab-awaiting-admission", problem.GetProperty("code").GetString());
        Assert.Equal(tabId, problem.GetProperty("context").GetProperty("tabId").GetGuid());

        // Calling a waiter stays open - it is how the table gets let in. The bill does not: there is none.
        var call = await phone.PostAsJsonAsync($"/api/tabs/{tabId}/service-requests", new { preset = ServiceRequestPreset.Water });
        Assert.Equal(HttpStatusCode.Created, call.StatusCode);

        var bill = await phone.PostAsJsonAsync(
            $"/api/tabs/{tabId}/service-requests", new { preset = ServiceRequestPreset.TheBill, billScope = BillScope.MyShare });
        Assert.Equal(HttpStatusCode.Conflict, bill.StatusCode);
        Assert.Equal("tab-awaiting-admission", (await bill.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The floor and the rail know.
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        var row = floor.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("tableId").GetGuid() == tableId);
        Assert.True(row.GetProperty("openTabAwaitingAdmission").GetBoolean());

        var rail = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tabs/awaiting-admission");
        var card = Assert.Single(rail.EnumerateArray());
        Assert.Equal(tabId, card.GetProperty("tabId").GetGuid());
        Assert.True(card.GetProperty("sittingSeatedByScan").GetBoolean());
        Assert.Equal(1, card.GetProperty("participantCount").GetInt32());

        // The change stream carried the signal.
        var changes = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/changes?afterSequence=0");
        Assert.Contains(
            changes.GetProperty("changes").EnumerateArray(),
            c => c.TryGetProperty("signal", out var s) && s.GetInt32() == (int)TableChangeSignal.TabAwaitingAdmission
                 && c.GetProperty("tabId").GetGuid() == tabId);

        // Admit.
        var admitted = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/admit", new { clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);

        var admission = (await admitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("admission");
        Assert.Equal(TabAdmission.Admitted, (TabAdmission)admission.GetProperty("status").GetInt32());
        Assert.Equal(TabAdmissionReason.Waiter, (TabAdmissionReason)admission.GetProperty("admittedBecause").GetInt32());
        Assert.Equal(branch.WaiterId, admission.GetProperty("decidedByStaffId").GetGuid());

        // The phone learns from its stream, and now orders - an order that waits at New for the waiter.
        var events = await ReadAsync(phone, $"/api/tabs/{tabId}/events?afterSequence=0");
        Assert.Contains(events.GetProperty("events").EnumerateArray(), e => e.GetProperty("type").GetInt32() == (int)TabEventType.TabAdmitted);

        var ordered = await phone.PostAsJsonAsync($"/api/tabs/{tabId}/orders", NewOrder(menu));
        Assert.Equal(HttpStatusCode.Created, ordered.StatusCode);
        Assert.Equal(
            TabOrderStatus.New,
            (TabOrderStatus)(await ordered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());

        Assert.Equal(0, (await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tabs/awaiting-admission")).GetArrayLength());
    }

    /// <summary>
    /// After admission the host lets the others order as before: a joiner is pending until the host approves
    /// them, then orders.
    /// </summary>
    [SkippableFact]
    public async Task After_admission_the_host_lets_the_others_order_as_before()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu) = await ArrangeAsync(factory);
        var qr = await QrTokenAsync(factory, branch.FirstTableId);

        var host = await ScanAsync(factory, qr, "phone-host");
        var tabId = host.GetProperty("tab").GetProperty("tabId").GetGuid();
        var guest = await ScanAsync(factory, qr, "phone-guest");
        var guestId = guest.GetProperty("tab").GetProperty("me").GetProperty("participantId").GetGuid();

        using var hostPhone = factory.CreateClientWithToken(host.GetProperty("token").GetProperty("accessToken").GetString()!);
        using var guestPhone = factory.CreateClientWithToken(guest.GetProperty("token").GetProperty("accessToken").GetString()!);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // The host approves the guest while the tab still waits: approved, but nobody orders yet.
        Assert.Equal(
            HttpStatusCode.OK,
            (await hostPhone.PostAsJsonAsync($"/api/tabs/{tabId}/participants/{guestId}/approve", new { })).StatusCode);

        var guestView = await ReadAsync(guestPhone, $"/api/tabs/{tabId}");
        Assert.False(guestView.GetProperty("me").GetProperty("canOrderNow").GetBoolean());

        await AdmitAsync(waiter, tabId);

        guestView = await ReadAsync(guestPhone, $"/api/tabs/{tabId}");
        Assert.True(guestView.GetProperty("me").GetProperty("canOrderNow").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, (await guestPhone.PostAsJsonAsync($"/api/tabs/{tabId}/orders", NewOrder(menu))).StatusCode);

        // And somebody joining the admitted tab waits for the host, as always.
        var late = await ScanAsync(factory, qr, "phone-late");
        Assert.Equal(
            ParticipantStatus.PendingApproval,
            (ParticipantStatus)late.GetProperty("tab").GetProperty("me").GetProperty("status").GetInt32());
    }

    /// <summary>
    /// Rejecting a tab the scan seated closes it and frees the table through the state machine, with who and
    /// why recorded; a second tap answers as the first, and admitting it afterwards is refused.
    /// </summary>
    [SkippableFact]
    public async Task Rejecting_a_scan_that_seated_the_table_closes_the_tab_and_frees_the_table()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        var tableId = branch.FirstTableId;

        var scan = await ScanAsync(factory, await QrTokenAsync(factory, tableId), "phone-phantom");
        var tabId = scan.GetProperty("tab").GetProperty("tabId").GetGuid();

        using var phone = factory.CreateClientWithToken(scan.GetProperty("token").GetProperty("accessToken").GetString()!);
        await phone.PostAsJsonAsync($"/api/tabs/{tabId}/service-requests", new { preset = ServiceRequestPreset.Napkins });

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var commandId = Guid.CreateVersion7();
        var rejected = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/reject", new { clientCommandId = commandId, reason = "nobody there" });

        Assert.True(rejected.StatusCode == HttpStatusCode.OK, await rejected.Content.ReadAsStringAsync());

        var view = await rejected.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(TabStatus.Closed, (TabStatus)view.GetProperty("status").GetInt32());
        Assert.Equal(TabAdmission.Rejected, (TabAdmission)view.GetProperty("admission").GetProperty("status").GetInt32());
        Assert.True(view.GetProperty("admission").GetProperty("tableFreed").GetBoolean());
        Assert.Equal(branch.WaiterId, view.GetProperty("admission").GetProperty("decidedByStaffId").GetGuid());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(TableStatus.Free, (await db.DiningTables.AsNoTracking().SingleAsync(t => t.Id == tableId)).Status);
            Assert.Equal(0, await db.TableSessions.CountAsync(s => s.DiningTableId == tableId && s.ClosedAtUtc == null));

            var freed = await db.TableStateChanges.AsNoTracking()
                .SingleAsync(c => c.TabId == tabId && c.Signal == TableChangeSignal.TabAdmissionRejected);

            Assert.Equal(TableStatus.Occupied, freed.FromStatus);
            Assert.Equal(TableStatus.Free, freed.ToStatus);
            Assert.Equal("nobody there", freed.Reason);
            Assert.Equal(branch.WaiterId, freed.ActorId);

            // Its call is nobody's to answer now.
            Assert.Equal(0, await db.ServiceRequests.CountAsync(r => r.TabId == tabId && r.SupersededAtUtc == null && r.AcknowledgedAtUtc == null));
        }

        // The phone's stream says so.
        var events = await ReadAsync(phone, $"/api/tabs/{tabId}/events?afterSequence=0");
        var rejection = events.GetProperty("events").EnumerateArray()
            .Single(e => e.GetProperty("type").GetInt32() == (int)TabEventType.TabAdmissionRejected);
        Assert.Contains("tableFreed", rejection.GetProperty("payload").GetRawText(), StringComparison.Ordinal);

        // The same tap again: the same answer, nothing more written.
        var again = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/reject", new { clientCommandId = commandId });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var admit = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/admit", new { clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Conflict, admit.StatusCode);
        Assert.Equal("tab-admission-decided", (await admit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The table is free: the real party scans and gets a new tab.
        var fresh = await ScanAsync(factory, await QrTokenAsync(factory, tableId), "phone-real");
        Assert.NotEqual(tabId, fresh.GetProperty("tab").GetProperty("tabId").GetGuid());
    }

    /// <summary>
    /// A phantom scan onto a table a waiter seated: rejecting closes only the tab, the party stays seated, and
    /// their own scan opens a new tab on the same sitting.
    /// </summary>
    [SkippableFact]
    public async Task Rejecting_a_scan_on_a_table_a_waiter_seated_keeps_the_party_seated_and_frees_the_sitting_for_a_new_tab()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        var tableId = branch.FirstTableId;

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var seated = await waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{tableId}/seat-walk-in",
            new { partySize = 2, clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);

        var phantom = await ScanAsync(factory, await QrTokenAsync(factory, tableId), "phone-phantom");
        var phantomTab = phantom.GetProperty("tab").GetProperty("tabId").GetGuid();

        var rail = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tabs/awaiting-admission");
        Assert.False(Assert.Single(rail.EnumerateArray()).GetProperty("sittingSeatedByScan").GetBoolean());

        var rejected = await waiter.PostAsJsonAsync($"/api/tabs/{phantomTab}/reject", new { clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.False((await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("admission").GetProperty("tableFreed").GetBoolean());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(TableStatus.Occupied, (await db.DiningTables.AsNoTracking().SingleAsync(t => t.Id == tableId)).Status);
            Assert.Null((await db.TableSessions.AsNoTracking().SingleAsync(s => s.DiningTableId == tableId && s.ClosedAtUtc == null)).TabId);
        }

        var real = await ScanAsync(factory, await QrTokenAsync(factory, tableId), "phone-real");
        var realTab = real.GetProperty("tab");

        Assert.NotEqual(phantomTab, realTab.GetProperty("tabId").GetGuid());
        Assert.Equal(TabOpenOutcome.OpenedOnExistingSession, (TabOpenOutcome)real.GetProperty("outcome").GetInt32());
        Assert.Equal(TabAdmission.AwaitingAdmission, (TabAdmission)realTab.GetProperty("admission").GetProperty("status").GetInt32());
    }

    /// <summary>
    /// A scanner signed in with a proved number, and a tab staff opened, may order at once. A waiter's own
    /// order on a waiting tab admits it.
    /// </summary>
    [SkippableFact]
    public async Task A_verified_host_a_staff_tab_and_a_waiters_order_are_admitted_without_a_tap()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu) = await ArrangeAsync(factory);

        // Verified host.
        using var verified = factory.CreateClientWithToken(await SignInVerifiedDinerAsync(factory));
        var scanned = await verified.PostAsJsonAsync(
            "/api/tabs/open",
            new { qrToken = await QrTokenAsync(factory, branch.TableIds[0]), deviceId = "phone-verified", clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, scanned.StatusCode);
        var verifiedTab = (await scanned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tab");

        Assert.Equal(TabAdmission.Admitted, (TabAdmission)verifiedTab.GetProperty("admission").GetProperty("status").GetInt32());
        Assert.Equal(TabAdmissionReason.VerifiedHost, (TabAdmissionReason)verifiedTab.GetProperty("admission").GetProperty("admittedBecause").GetInt32());
        Assert.True(verifiedTab.GetProperty("me").GetProperty("canOrderNow").GetBoolean());

        // Staff-opened tab.
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        await waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{branch.TableIds[1]}/seat-walk-in",
            new { partySize = 2, clientCommandId = Guid.CreateVersion7() });
        var opened = await waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{branch.TableIds[1]}/open-tab", new { clientCommandId = Guid.CreateVersion7() });
        var staffAdmission = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("admission");

        Assert.Equal(TabAdmissionReason.OpenedByStaff, (TabAdmissionReason)staffAdmission.GetProperty("admittedBecause").GetInt32());

        // A waiter's order on a waiting tab.
        var anonymous = await ScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[2]), "phone-anon");
        var waitingTab = anonymous.GetProperty("tab").GetProperty("tabId").GetGuid();

        var staffOrder = await waiter.PostAsJsonAsync($"/api/tabs/{waitingTab}/staff-orders", NewOrder(menu));
        Assert.Equal(HttpStatusCode.Created, staffOrder.StatusCode);

        var staffView = await ReadAsync(waiter, $"/api/tabs/{waitingTab}/participants");
        Assert.Equal(TabAdmission.Admitted, (TabAdmission)staffView.GetProperty("admission").GetProperty("status").GetInt32());
        Assert.Equal(TabAdmissionReason.WaiterOrder, (TabAdmissionReason)staffView.GetProperty("admission").GetProperty("admittedBecause").GetInt32());
    }

    /// <summary>
    /// Admission is staff work at the tab's own branch: a phone cannot admit, another branch's waiter is
    /// refused, a command id is not reusable on another tab, and admitting twice answers as the first time.
    /// </summary>
    [SkippableFact]
    public async Task Only_a_waiter_at_the_tabs_branch_admits_and_a_retry_answers_as_the_first_tap()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        var (elsewhere, _) = await ArrangeAsync(factory);

        var scan = await ScanAsync(factory, await QrTokenAsync(factory, branch.FirstTableId), "phone-host");
        var tabId = scan.GetProperty("tab").GetProperty("tabId").GetGuid();

        using var phone = factory.CreateClientWithToken(scan.GetProperty("token").GetProperty("accessToken").GetString()!);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await phone.PostAsJsonAsync($"/api/tabs/{tabId}/admit", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        using var stranger = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, elsewhere));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.PostAsJsonAsync($"/api/tabs/{tabId}/admit", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/api/branches/{branch.BranchId}/tabs/awaiting-admission")).StatusCode);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var missingId = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/admit", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missingId.StatusCode);

        var commandId = Guid.CreateVersion7();
        await AdmitAsync(waiter, tabId, commandId);
        await AdmitAsync(waiter, tabId, commandId);
        await AdmitAsync(waiter, tabId);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(1, await db.TabEvents.CountAsync(e => e.TabId == tabId && e.Type == TabEventType.TabAdmitted));
        }

        // The same id on another tab is a collision.
        var other = await ScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[1]), "phone-other");
        var otherTab = other.GetProperty("tab").GetProperty("tabId").GetGuid();

        var reused = await waiter.PostAsJsonAsync($"/api/tabs/{otherTab}/admit", new { clientCommandId = commandId });
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("client-command-id-in-use", (await reused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // And rejecting an admitted tab is refused.
        var reject = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/reject", new { clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Conflict, reject.StatusCode);
        Assert.Equal("tab-admission-decided", (await reject.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<(AuthBranch Branch, TestMenu Menu)> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        return (branch, menu);
    }

    private static object NewOrder(TestMenu menu) =>
        new { items = new[] { new { menuItemId = menu.Khachapuri, quantity = 1 } }, clientCommandId = Guid.CreateVersion7() };

    private static async Task AdmitAsync(HttpClient waiter, Guid tabId, Guid? commandId = null)
    {
        var response = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/admit", new { clientCommandId = commandId ?? Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> QrTokenAsync(YallaApiFactory factory, Guid tableId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.DiningTables.AsNoTracking().Where(t => t.Id == tableId).Select(t => t.QrToken).SingleAsync();
    }

    private static async Task<JsonElement> ScanAsync(YallaApiFactory factory, string qrToken, string deviceId)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/tabs/open", new { qrToken, deviceId, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> SignInVerifiedDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();
        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
