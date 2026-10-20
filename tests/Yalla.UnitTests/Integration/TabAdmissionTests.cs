using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;

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

        // Which waiter let the table in is the host's to read, not a pending joiner's (review N4).
        using var latePhone = factory.CreateClientWithToken(late.GetProperty("token").GetProperty("accessToken").GetString()!);

        var hostSees = AdmittedPayload(await ReadAsync(hostPhone, $"/api/tabs/{tabId}/events?afterSequence=0"));
        var pendingSees = AdmittedPayload(await ReadAsync(latePhone, $"/api/tabs/{tabId}/events?afterSequence=0"));

        Assert.Equal(branch.WaiterId, hostSees.GetProperty("byStaffId").GetGuid());
        Assert.False(pendingSees.TryGetProperty("byStaffId", out _));
        Assert.Equal((int)TabAdmissionReason.Waiter, pendingSees.GetProperty("admittedBecause").GetInt32());
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

        // The table is free, after a short pause in which a scan of it opens nothing: then the real party
        // scans and gets a new tab.
        var paused = await TryScanAsync(factory, await QrTokenAsync(factory, tableId), "phone-real");
        Assert.Equal(HttpStatusCode.Conflict, paused.StatusCode);
        Assert.Equal("scan-turned-away", (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await BackdateDecisionAsync(factory, tabId, ScanCooldown.Table + TimeSpan.FromSeconds(5));

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

        // After the table's short pause, the party's own scan opens a new tab on the same sitting.
        await BackdateDecisionAsync(factory, phantomTab, ScanCooldown.Table + TimeSpan.FromSeconds(5));

        var real = await ScanAsync(factory, await QrTokenAsync(factory, tableId), "phone-real");
        var realTab = real.GetProperty("tab");

        Assert.NotEqual(phantomTab, realTab.GetProperty("tabId").GetGuid());
        Assert.Equal(TabOpenOutcome.OpenedOnExistingSession, (TabOpenOutcome)real.GetProperty("outcome").GetInt32());
        Assert.Equal(TabAdmission.AwaitingAdmission, (TabAdmission)realTab.GetProperty("admission").GetProperty("status").GetInt32());
    }

    /// <summary>
    /// A scanner signed in with a proved number, and a tab staff opened, may order at once.
    /// </summary>
    [SkippableFact]
    public async Task A_verified_host_and_a_staff_tab_are_admitted_without_a_tap()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);

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
    }

    /// <summary>
    /// The phantom-host case (review S4): a waiter seats a party, a remote phone scans the table's photographed
    /// code first and hosts the waiting tab, and the waiter keys the party's order in. The order goes through -
    /// staff may always order - but it lets nobody in: the phantom's phone still cannot order. Only Admit does
    /// that.
    /// </summary>
    [SkippableFact]
    public async Task A_staff_order_on_a_waiting_tab_goes_through_but_admits_nobody()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu) = await ArrangeAsync(factory);
        var tableId = branch.FirstTableId;

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync(
                $"/api/branches/{branch.BranchId}/tables/{tableId}/seat-walk-in",
                new { partySize = 2, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var phantom = await ScanAsync(factory, await QrTokenAsync(factory, tableId), "phone-phantom");
        var tabId = phantom.GetProperty("tab").GetProperty("tabId").GetGuid();

        var staffOrder = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/staff-orders", NewOrder(menu));
        Assert.Equal(HttpStatusCode.Created, staffOrder.StatusCode);

        var staffView = await ReadAsync(waiter, $"/api/tabs/{tabId}/participants");
        Assert.Equal(
            TabAdmission.AwaitingAdmission,
            (TabAdmission)staffView.GetProperty("admission").GetProperty("status").GetInt32());

        using var phantomPhone = factory.CreateClientWithToken(phantom.GetProperty("token").GetProperty("accessToken").GetString()!);

        var refused = await phantomPhone.PostAsJsonAsync($"/api/tabs/{tabId}/orders", NewOrder(menu));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("tab-awaiting-admission", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.False((await ReadAsync(phantomPhone, $"/api/tabs/{tabId}")).GetProperty("me").GetProperty("canOrderNow").GetBoolean());

        // Still on the rail for the waiter to look at.
        var rail = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tabs/awaiting-admission");
        Assert.Equal(tabId, Assert.Single(rail.EnumerateArray()).GetProperty("tabId").GetGuid());

        // And not to be turned away with the kitchen's work on it.
        var reject = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/reject", new { clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Conflict, reject.StatusCode);
        Assert.Equal("conflicting-state", (await reject.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await db.TabEvents.CountAsync(e => e.TabId == tabId && e.Type == TabEventType.TabAdmitted));
    }

    /// <summary>
    /// The branch setting "unverified scans wait for a waiter" is on by default; switched off, a scan orders at
    /// once as before admission existed - admitted as NotRequired, with nothing on the rail.
    /// </summary>
    [SkippableFact]
    public async Task With_the_admission_setting_off_a_scan_orders_at_once_as_before()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, menu) = await ArrangeAsync(factory);
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        var policyUrl = $"/api/branches/{branch.BranchId}/reservation-policy";
        var current = await ReadAsync(manager, policyUrl);
        Assert.True(current.GetProperty("unverifiedScansNeedAdmission").GetBoolean());

        // An older console leaves it out: it stays on.
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(policyUrl, PolicyBody(current, scansNeedAdmission: null))).StatusCode);
        Assert.True((await ReadAsync(manager, policyUrl)).GetProperty("unverifiedScansNeedAdmission").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(policyUrl, PolicyBody(current, scansNeedAdmission: false))).StatusCode);
        Assert.False((await ReadAsync(manager, policyUrl)).GetProperty("unverifiedScansNeedAdmission").GetBoolean());

        var scan = await ScanAsync(factory, await QrTokenAsync(factory, branch.FirstTableId), "phone-off");
        var tab = scan.GetProperty("tab");
        var tabId = tab.GetProperty("tabId").GetGuid();

        Assert.Equal(TabAdmission.Admitted, (TabAdmission)tab.GetProperty("admission").GetProperty("status").GetInt32());
        Assert.Equal(
            TabAdmissionReason.NotRequired,
            (TabAdmissionReason)tab.GetProperty("admission").GetProperty("admittedBecause").GetInt32());
        Assert.True(tab.GetProperty("me").GetProperty("canOrderNow").GetBoolean());

        using var phone = factory.CreateClientWithToken(scan.GetProperty("token").GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.Created, (await phone.PostAsJsonAsync($"/api/tabs/{tabId}/orders", NewOrder(menu))).StatusCode);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        Assert.Equal(0, (await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tabs/awaiting-admission")).GetArrayLength());
    }

    /// <summary>
    /// After a reject (review S7) the phone that opened the turned-away tab opens nothing at the branch for
    /// half an hour, and the table opens nothing by an unverified scan for two minutes - each with when to try
    /// again. A real party that had joined the phantom's tab is not held back with it.
    /// </summary>
    [SkippableFact]
    public async Task After_a_reject_the_phone_and_the_table_pause_before_opening_again()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);

        var phantom = await ScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[0]), "phone-phantom");
        var tabId = phantom.GetProperty("tab").GetProperty("tabId").GetGuid();
        await ScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[0]), "phone-joined");

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/reject", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        // The phone, at another table of the branch.
        var byPhone = await TryScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[1]), "phone-phantom");
        Assert.Equal(HttpStatusCode.Conflict, byPhone.StatusCode);
        var phoneProblem = await byPhone.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scan-turned-away", phoneProblem.GetProperty("code").GetString());
        var phoneRetry = phoneProblem.GetProperty("context").GetProperty("retryAfterUtc").GetDateTime().ToUniversalTime();
        Assert.InRange(phoneRetry, factory.Clock.UtcNow.AddMinutes(29), factory.Clock.UtcNow.AddMinutes(31));

        // The table, from any phone.
        var byTable = await TryScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[0]), "phone-new");
        Assert.Equal(HttpStatusCode.Conflict, byTable.StatusCode);
        Assert.Equal("scan-turned-away", (await byTable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The phone that had only joined opens a tab elsewhere at once.
        Assert.Equal(HttpStatusCode.OK, (await TryScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[2]), "phone-joined")).StatusCode);

        // Past the table's pause: the table opens, the phone still does not.
        await BackdateDecisionAsync(factory, tabId, ScanCooldown.Table + TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, (await TryScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[0]), "phone-new")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await TryScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[1]), "phone-phantom")).StatusCode);

        // Past the phone's.
        await BackdateDecisionAsync(factory, tabId, ScanCooldown.Device + TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, (await TryScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[1]), "phone-phantom")).StatusCode);
    }

    /// <summary>
    /// Review S6: a scan that took a table held for a late booked party, turned away, gives the table back to
    /// Held - not to Free, where the next walk-in would take the late party's table.
    /// </summary>
    [SkippableFact]
    public async Task Rejecting_a_scan_that_took_a_held_table_puts_the_hold_back()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        var tableId = branch.FirstTableId;

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var held = await waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{tableId}/hold", new { clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);

        var phantom = await ScanAsync(factory, await QrTokenAsync(factory, tableId), "phone-phantom");
        var tabId = phantom.GetProperty("tab").GetProperty("tabId").GetGuid();

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(TableStatus.Occupied, (await db.DiningTables.AsNoTracking().SingleAsync(t => t.Id == tableId)).Status);
        }

        var rejected = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/reject", new { clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.True((await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("admission").GetProperty("tableFreed").GetBoolean());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(TableStatus.Held, (await db.DiningTables.AsNoTracking().SingleAsync(t => t.Id == tableId)).Status);
            Assert.Equal(0, await db.TableSessions.CountAsync(s => s.DiningTableId == tableId && s.ClosedAtUtc == null));
        }
    }

    /// <summary>
    /// Two waiters, one taps Admit and the other Reject on the same waiting tab at the same moment: one wins,
    /// the other is told it was decided (409), never a 500, and the tab ends in exactly one state.
    /// </summary>
    [SkippableFact]
    public async Task Admit_and_reject_racing_on_one_tab_answer_one_200_and_one_409()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);

        using var first = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var second = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        for (var round = 0; round < 3; round++)
        {
            var scan = await ScanAsync(factory, await QrTokenAsync(factory, branch.TableIds[round]), $"phone-race-{round}");
            var tabId = scan.GetProperty("tab").GetProperty("tabId").GetGuid();

            var answers = await Task.WhenAll(
                first.PostAsJsonAsync($"/api/tabs/{tabId}/admit", new { clientCommandId = Guid.CreateVersion7() }),
                second.PostAsJsonAsync($"/api/tabs/{tabId}/reject", new { clientCommandId = Guid.CreateVersion7() }));

            var codes = answers.Select(a => a.StatusCode).OrderBy(c => c).ToArray();
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], codes);

            var loser = answers.Single(a => a.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("tab-admission-decided", (await loser.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            await using var db = fixture.CreateContext(factory.Clock);
            var decided = await db.TabEvents.CountAsync(
                e => e.TabId == tabId && (e.Type == TabEventType.TabAdmitted || e.Type == TabEventType.TabAdmissionRejected));
            Assert.Equal(1, decided);
        }
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

    private static async Task<HttpResponseMessage> TryScanAsync(YallaApiFactory factory, string qrToken, string deviceId)
    {
        using var client = factory.CreateClient();

        return await client.PostAsJsonAsync("/api/tabs/open", new { qrToken, deviceId, clientCommandId = Guid.CreateVersion7() });
    }

    /// <summary>Moves a turned-away tab's decision back in time, as if the pause had run.</summary>
    private async Task BackdateDecisionAsync(YallaApiFactory factory, Guid tabId, TimeSpan by)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        DateTime? decidedAt = factory.Clock.UtcNow - by;

        await db.Tabs
            .Where(t => t.Id == tabId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AdmissionDecidedAtUtc, decidedAt));
    }

    private static object PolicyBody(JsonElement current, bool? scansNeedAdmission) =>
        new
        {
            turnTimeMinutes = current.GetProperty("turnTimeMinutes").GetInt32(),
            bufferMinutes = current.GetProperty("bufferMinutes").GetInt32(),
            graceMinutes = current.GetProperty("graceMinutes").GetInt32(),
            lateNudgeAfterMinutes = current.GetProperty("lateNudgeAfterMinutes").GetInt32(),
            graceExtensionMinutes = current.GetProperty("graceExtensionMinutes").GetInt32(),
            minLeadMinutes = current.GetProperty("minLeadMinutes").GetInt32(),
            bookingWindowDays = current.GetProperty("bookingWindowDays").GetInt32(),
            cancellationDeadlineMinutes = current.GetProperty("cancellationDeadlineMinutes").GetInt32(),
            autoConfirm = current.GetProperty("autoConfirm").GetBoolean(),
            serviceChargePercent = current.GetProperty("serviceChargePercent").GetDecimal(),
            pricesIncludeVat = current.GetProperty("pricesIncludeVat").GetBoolean(),
            maxSeatOverhang = current.TryGetProperty("maxSeatOverhang", out var o) ? o.GetInt32() : (int?)null,
            approvalRequiredAbovePartySize = current.TryGetProperty("approvalRequiredAbovePartySize", out var a)
                ? a.GetInt32()
                : (int?)null,
            walkInHoldbackMinutes = current.GetProperty("walkInHoldbackMinutes").GetInt32(),
            unverifiedScansNeedAdmission = scansNeedAdmission,
        };

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

    private static JsonElement AdmittedPayload(JsonElement page) =>
        page.GetProperty("events").EnumerateArray()
            .Single(e => e.GetProperty("type").GetInt32() == (int)TabEventType.TabAdmitted)
            .GetProperty("payload");

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
