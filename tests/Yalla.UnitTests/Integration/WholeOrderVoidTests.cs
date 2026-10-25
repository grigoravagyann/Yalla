using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Activity;
using Yalla.Application.Ordering;
using Yalla.Application.Reports;
using Yalla.Application.Tables;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Persistence.Migrations;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Voiding a whole order from the rail takes its lines off the bill - the cache, the staff view, the split, the balance
/// a payment is held against, the revenue and menu reports, Staff → Results and the timeline - by the one rule every
/// total already follows: a line counts until it is voided. Live QA found a voided cappuccino still on a bill of 3,762.
/// Also the guard (a tab already paid against is a refund, refused) and the backfill of rows voided the old way. See
/// <c>docs/billing.md</c>, "Voiding a whole order".
/// </summary>
[Collection(SqlServerCollection.Name)]
public class WholeOrderVoidTests(SqlServerFixture fixture)
{
    /// <summary>17:12 in Yerevan, a Saturday.</summary>
    private static readonly DateTime SeatedAt = new(2026, 10, 24, 13, 12, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 24);

    /// <summary>
    /// Lilit sends two coffees and a bottle of wine as two orders, then voids the wine's order on the rail. The bill is
    /// the coffees everywhere it is read; the old total, wine included, is refused as more than the tab owes; the coffees'
    /// total settles it. The reports count the coffees, the menu report lists the wine as voided with why, Lilit's
    /// results show the sale and one void, and the timeline has the one <c>order.voided</c> entry, at the wine's price.
    /// </summary>
    [SkippableFact]
    public async Task A_voided_order_comes_off_the_bill_the_balance_and_every_report()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(SeatedAt);
        var (branch, menu) = await ArrangeAsync(clock);
        var lilit = branch.WaiterId;
        var tabId = await SeatAndOpenAsync(clock, branch);

        await OrderAsync(clock, lilit, tabId, menu.Coffee, quantity: 2);
        var wine = await OrderAsync(clock, lilit, tabId, menu.Wine, quantity: 1);
        Assert.Equal(TabOrderStatus.InKitchen, wine.Status);

        long withWine;

        await using (var db = fixture.CreateContext(clock))
        {
            withWine = (await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId)).TotalAmd;
        }

        clock.UtcNow = SeatedAt.AddMinutes(6);
        var voidedAt = clock.UtcNow;

        var ticket = await AsAsync(clock, TestActor.Waiter(lilit), (db, actor) =>
            fixture.CreateOrderService(db, clock, actor).MoveOrderStatusAsync(wine.OrderId, TabOrderStatus.Voided));

        Assert.Equal(TabOrderStatus.Voided, ticket.Status);

        long expected;

        await using (var db = fixture.CreateContext(clock))
        {
            var tab = await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId);
            var subtotal = 2 * TestMenu.CoffeeAmd;
            expected = subtotal + Domain.Common.Money.PercentOf(subtotal, tab.ServiceChargePercentSnapshot);

            // The wine's line is voided as the order was: when, by whom, and why.
            var line = await db.TabOrderLines.AsNoTracking().SingleAsync(l => l.TabOrderId == wine.OrderId);
            Assert.Equal((voidedAt, lilit, TabOrder.WholeOrderVoidReason), (line.VoidedAtUtc, line.VoidedByStaffId, line.VoidReason));

            // The cache, written inline with the void, and the same five numbers recomputed from the rows.
            Assert.Equal((subtotal, expected, 0L, expected), (tab.SubtotalAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd));
            Assert.True(expected < withWine);

            var recomputed = await fixture.CreateLedger(db, clock, TestActor.Waiter(lilit)).RecomputeTotalsAsync(tabId, default);
            Assert.Equal(
                (tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd),
                (recomputed.SubtotalAmd, recomputed.ServiceChargeAmd, recomputed.TotalAmd, recomputed.PaidAmd, recomputed.RemainingAmd));

            // The stream says it once - the order moved to Voided - and not line by line.
            var events = await db.TabEvents.AsNoTracking().Where(e => e.TabId == tabId).OrderBy(e => e.Sequence).ToListAsync();
            // (A waiter's order went straight to the kitchen, which is the wine's other status change.)
            var moves = events
                .Where(e => e.Type == TabEventType.OrderStatusChanged
                            && e.PayloadJson.Contains(wine.OrderId.ToString(), StringComparison.Ordinal))
                .Select(e => JsonDocument.Parse(e.PayloadJson).RootElement.GetProperty("toStatus").GetInt32())
                .ToList();
            Assert.Equal([(int)TabOrderStatus.InKitchen, (int)TabOrderStatus.Voided], moves);
            Assert.DoesNotContain(events, e => e.Type == TabEventType.LineVoided);
        }

        // The staff view: the coffees' total, the wine's line kept, marked and at nothing.
        var staffView = await AsAsync(clock, TestActor.Waiter(lilit), async (db, _) =>
            (await fixture.CreateTabQuery(db).GetForStaffAsync(tabId))!);
        Assert.Equal(expected, staffView.Totals.TotalAmd);
        Assert.Equal(expected, staffView.Totals.RemainingAmd);
        var wineLine = Assert.Single(staffView.Lines, l => l.OrderId == wine.OrderId);
        Assert.True(wineLine.IsVoided);
        Assert.Equal((0L, TabOrder.WholeOrderVoidReason), (wineLine.LineTotalAmd, wineLine.VoidReason));

        // The split reads the same bill, and finds the cache already right.
        var shares = await AsAsync(clock, TestActor.Waiter(lilit), (db, actor) =>
            fixture.CreateBillingQuery(db, clock, actor).GetSharesAsync(tabId, null));
        Assert.Equal(expected, shares.Totals!.TotalAmd);
        Assert.Equal(expected, shares.Totals.RemainingAmd);

        // The balance a payment is held against: the old total, wine included, is more than the tab owes.
        clock.UtcNow = SeatedAt.AddMinutes(40);
        var refused = await AsAsync(clock, TestActor.Waiter(lilit), (db, actor) =>
            Assert.ThrowsAsync<PaymentExceedsRemainingException>(() =>
                fixture.CreatePaymentService(db, clock, actor)
                    .RecordCashAsync(new RecordCashPaymentCommand(tabId, withWine, null, 0L, Guid.CreateVersion7()))));
        Assert.Equal(expected, refused.RemainingAmd);

        await AsAsync(clock, TestActor.Waiter(lilit), async (db, actor) =>
            await fixture.CreatePaymentService(db, clock, actor)
                .RecordCashAsync(new RecordCashPaymentCommand(tabId, expected, null, 0L, Guid.CreateVersion7())));

        await using (var db = fixture.CreateContext(clock))
        {
            var closed = await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId);
            Assert.Equal((TabStatus.Closed, expected, expected, 0L), (closed.Status, closed.TotalAmd, closed.PaidAmd, closed.RemainingAmd));
        }

        var manager = TestActor.Manager(branch.ManagerId);

        // The revenue report: the coffees' bill.
        var revenue = await AsAsync(clock, manager, (db, actor) =>
            SqlServerFixture.CreateReportQuery(db, actor).GetRevenueAsync(new ReportRequest(branch.BranchId, Today, Today)));
        Assert.Equal(expected, revenue.TotalAmd.Value);
        Assert.Equal(expected, revenue.HallAmd.Value);
        Assert.Equal(expected, revenue.CashAmd.Value);

        // The menu report: the wine was not sold; it was voided, and says why.
        var menuReport = await AsAsync(clock, manager, (db, actor) =>
            SqlServerFixture.CreateReportQuery(db, actor).GetMenuAsync(new ReportRequest(branch.BranchId, Today, Today)));
        Assert.DoesNotContain(menuReport.TopByCount, i => i.MenuItemId == menu.Wine);
        Assert.Equal((2, 2 * TestMenu.CoffeeAmd), menuReport.TopByCount.Where(i => i.MenuItemId == menu.Coffee).Select(i => (i.Quantity, i.RevenueAmd)).Single());
        var voided = Assert.Single(menuReport.Voids);
        Assert.Equal((menu.Wine, TabOrder.WholeOrderVoidReason, 1), (voided.MenuItemId, voided.Reason, voided.Count));

        // Staff → Results: Lilit's sale is the bill, her one void is the wine, and the totals are the revenue report's.
        var results = await AsAsync(clock, manager, (db, actor) =>
            new StaffPerformanceReader(db, clock, actor, fixture.CreateBranchGuard(db, actor))
                .GetBranchAsync(branch.BranchId, new StaffPerformanceQuery()));
        var lilitRow = results.Staff.Single(r => r.StaffMemberId == lilit);
        Assert.Equal((expected, 1, expected, 1), (lilitRow.SalesAmd, lilitRow.Voids, lilitRow.CashAmd, lilitRow.CashPayments));
        Assert.Equal(revenue.HallAmd.Value, results.Totals.SalesAmd);
        Assert.Equal(1, results.Totals.Voids);

        // The timeline: the wine's line at nothing, voided by Lilit; the one order.voided entry at the wine's price.
        var timeline = await AsAsync(clock, manager, (db, actor) =>
            new ActivityHistoryQuery(db, clock, fixture.CreateBranchGuard(db, actor))
                .TimelineAsync(branch.BranchId, ActivitySubjects.Tab, tabId));
        Assert.Equal(expected, timeline.Tab!.TotalAmd);
        var timelineWine = Assert.Single(timeline.Lines, l => l.OrderId == wine.OrderId);
        Assert.Equal((0L, lilit, TabOrder.WholeOrderVoidReason), (timelineWine.LineTotalAmd, timelineWine.VoidedByStaffMemberId, timelineWine.VoidReason));
        Assert.Equal(2 * TestMenu.CoffeeAmd, timeline.Lines.Sum(l => l.LineTotalAmd));

        var orderVoided = Assert.Single(timeline.Entries, e => e.Action == ActivityActions.OrderVoided);
        Assert.Equal(TestMenu.WineAmd, orderVoided.AmountAmd);
        Assert.DoesNotContain(timeline.Entries, e => e.Action == ActivityActions.OrderLineVoided);
    }

    /// <summary>
    /// Once money has changed hands against a tab, voiding an order on it would be a refund - refused, as a line's void
    /// is, and nothing moves: the order is still in the kitchen, its line still charged, the totals and the stream as they
    /// were. An order whose every line was already voided before anybody paid can still be voided: nothing comes off.
    /// </summary>
    [SkippableFact]
    public async Task An_order_on_a_tab_already_paid_against_is_refused_and_nothing_moves()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(SeatedAt);
        var (branch, menu) = await ArrangeAsync(clock);
        var lilit = branch.WaiterId;
        var tabId = await SeatAndOpenAsync(clock, branch);

        await OrderAsync(clock, lilit, tabId, menu.Coffee, quantity: 2);
        var wine = await OrderAsync(clock, lilit, tabId, menu.Wine, quantity: 1);
        var cake = await OrderAsync(clock, lilit, tabId, menu.Khachapuri, quantity: 1);

        // The khachapuri's line goes on its own, before anybody pays.
        await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            fixture.CreateOrderService(db, clock, actor)
                .VoidLineAsync(new VoidLineCommand(tabId, cake.Lines[0].LineId, "Burnt", Guid.CreateVersion7())));

        // A first guest pays part.
        clock.UtcNow = SeatedAt.AddMinutes(20);
        await AsAsync(clock, TestActor.Waiter(lilit), async (db, actor) =>
            await fixture.CreatePaymentService(db, clock, actor)
                .RecordCashAsync(new RecordCashPaymentCommand(tabId, 1_000L, null, 0L, Guid.CreateVersion7())));

        (long Subtotal, long Total, long Paid, long Remaining) before;
        long eventsBefore;

        await using (var db = fixture.CreateContext(clock))
        {
            var tab = await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId);
            before = (tab.SubtotalAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd);
            eventsBefore = await db.TabEvents.CountAsync(e => e.TabId == tabId);
        }

        clock.UtcNow = SeatedAt.AddMinutes(22);
        var refused = await AsAsync(clock, TestActor.Waiter(lilit), (db, actor) =>
            Assert.ThrowsAsync<LineAlreadyPaidException>(() =>
                fixture.CreateOrderService(db, clock, actor).MoveOrderStatusAsync(wine.OrderId, TabOrderStatus.Voided)));
        Assert.Equal((tabId, wine.Lines[0].LineId), (refused.TabId, refused.LineId));
        Assert.Contains("voiding an order", refused.Message, StringComparison.Ordinal);

        await using (var db = fixture.CreateContext(clock))
        {
            var order = await db.TabOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == wine.OrderId);
            Assert.Equal(TabOrderStatus.InKitchen, order.Status);
            Assert.False(Assert.Single(order.Lines).IsVoided);

            var tab = await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId);
            Assert.Equal(before, (tab.SubtotalAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd));
            Assert.Equal(eventsBefore, await db.TabEvents.CountAsync(e => e.TabId == tabId));
            Assert.False(await db.ActivityEntries.AnyAsync(e => e.BranchId == branch.BranchId && e.Action == ActivityActions.OrderVoided));
        }

        // The khachapuri's order has nothing left on the bill, so voiding it moves no money and is not a refund.
        var cakeTicket = await AsAsync(clock, TestActor.Waiter(lilit), (db, actor) =>
            fixture.CreateOrderService(db, clock, actor).MoveOrderStatusAsync(cake.OrderId, TabOrderStatus.Voided));
        Assert.Equal(TabOrderStatus.Voided, cakeTicket.Status);

        await using (var db = fixture.CreateContext(clock))
        {
            var tab = await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId);
            Assert.Equal(before, (tab.SubtotalAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd));

            var cakeLine = await db.TabOrderLines.AsNoTracking().SingleAsync(l => l.TabOrderId == cake.OrderId);
            Assert.Equal("Burnt", cakeLine.VoidReason);
        }
    }

    /// <summary>
    /// The migration's backfill over two bills written the old way - a voided wine order whose line was left on the bill.
    /// On the unpaid tab the line is voided when the order was, by whom the event names, and the cache is the coffees';
    /// on the tab already paid in full nothing changes - the bill stays as it was paid. A second run changes nothing.
    /// </summary>
    [SkippableFact]
    public async Task The_backfill_takes_old_voided_orders_off_unpaid_bills_and_leaves_paid_ones_as_paid()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(SeatedAt);
        var (branch, menu) = await ArrangeAsync(clock);
        var lilit = branch.WaiterId;

        var unpaid = await SeatAndOpenAsync(clock, branch, branch.TableIds[0]);
        var paid = await SeatAndOpenAsync(clock, branch, branch.TableIds[1]);

        var voidedAt = SeatedAt.AddMinutes(5);
        var oldVoids = new Dictionary<Guid, Guid>();

        foreach (var tabId in new[] { unpaid, paid })
        {
            await OrderAsync(clock, lilit, tabId, menu.Coffee, quantity: 2);
            var wine = await OrderAsync(clock, lilit, tabId, menu.Wine, quantity: 1);
            oldVoids[tabId] = wine.OrderId;

            // The old void: the order moved to Voided and its event appended, its line left alone.
            await using var db = fixture.CreateContext(clock);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TabOrders SET Status = 5 WHERE Id = {wine.OrderId}");

            var next = await db.TabEvents.Where(e => e.TabId == tabId).MaxAsync(e => e.Sequence) + 1;
            var moved = new TabEvent(
                tabId,
                TabEventType.OrderStatusChanged,
                JsonSerializer.Serialize(new { orderId = wine.OrderId, fromStatus = 2, toStatus = 5, byStaffId = lilit }),
                ActorType.Staff,
                lilit,
                voidedAt);
            moved.PlaceAt(next);
            db.TabEvents.Add(moved);
            await db.SaveChangesAsync();
        }

        long withWine;

        await using (var db = fixture.CreateContext(clock))
        {
            withWine = (await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == paid)).TotalAmd;
        }

        // The paid tab was settled at the old total, the wine included.
        clock.UtcNow = SeatedAt.AddMinutes(30);
        await AsAsync(clock, TestActor.Waiter(lilit), async (db, actor) =>
            await fixture.CreatePaymentService(db, clock, actor)
                .RecordCashAsync(new RecordCashPaymentCommand(paid, withWine, null, 0L, Guid.CreateVersion7())));

        await RunBackfillAsync(clock);

        await using (var db = fixture.CreateContext(clock))
        {
            // The unpaid bill: the wine voided as the order was, and the cache the coffees' - what the lines now say.
            var line = await db.TabOrderLines.AsNoTracking().SingleAsync(l => l.TabOrderId == oldVoids[unpaid]);
            Assert.Equal((voidedAt, lilit, TabOrder.WholeOrderVoidReason), (line.VoidedAtUtc, line.VoidedByStaffId, line.VoidReason));

            var tab = await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == unpaid);
            var subtotal = 2 * TestMenu.CoffeeAmd;
            var expected = subtotal + Domain.Common.Money.PercentOf(subtotal, tab.ServiceChargePercentSnapshot);
            Assert.Equal((subtotal, expected, 0L, expected), (tab.SubtotalAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd));

            var ledger = fixture.CreateLedger(db, clock, TestActor.Waiter(lilit));
            var bill = ledger.Compute(await ledger.LoadForWriteAsync(unpaid, default));
            Assert.Equal((tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd), (bill.SubtotalAmd, bill.ServiceChargeAmd, bill.TotalAmd));

            // The paid bill: as it was paid.
            var paidLine = await db.TabOrderLines.AsNoTracking().SingleAsync(l => l.TabOrderId == oldVoids[paid]);
            Assert.False(paidLine.IsVoided);

            var settled = await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == paid);
            Assert.Equal((TabStatus.Closed, withWine, withWine, 0L), (settled.Status, settled.TotalAmd, settled.PaidAmd, settled.RemainingAmd));
        }

        // Idempotent.
        await RunBackfillAsync(clock);

        await using (var db = fixture.CreateContext(clock))
        {
            var line = await db.TabOrderLines.AsNoTracking().SingleAsync(l => l.TabOrderId == oldVoids[unpaid]);
            Assert.Equal(voidedAt, line.VoidedAtUtc);
            Assert.False((await db.TabOrderLines.AsNoTracking().SingleAsync(l => l.TabOrderId == oldVoids[paid])).IsVoided);
        }
    }

    // ------------------------------------------------------------ helpers

    private async Task RunBackfillAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        await db.Database.ExecuteSqlRawAsync(ActivityHistory.VoidedOrderLinesBackfillSql);
    }

    private async Task<(TestBranch Branch, TestMenu Menu)> ArrangeAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        return (branch, menu);
    }

    /// <summary>The branch's waiter seats two and opens the bill.</summary>
    private Task<Guid> SeatAndOpenAsync(TestClock clock, TestBranch branch, Guid? tableId = null) =>
        AsAsync(clock, TestActor.Waiter(branch.WaiterId), async (db, actor) =>
        {
            var table = tableId ?? branch.FirstTableId;

            await fixture.CreateService(db, clock, actor)
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, table, 2, Guid.CreateVersion7()));

            return (await fixture.CreateTabService(db, clock, actor)
                .OpenForStaffAsync(new OpenTabForStaffCommand(branch.BranchId, table, Guid.CreateVersion7()))).TabId;
        });

    /// <summary>One request: a fresh context, acting as <paramref name="actor"/>.</summary>
    private async Task<T> AsAsync<T>(TestClock clock, TestActor actor, Func<YallaDbContext, TestActor, Task<T>> act)
    {
        await using var db = fixture.CreateContext(clock);

        return await act(db, actor);
    }

    private async Task<OrderView> OrderAsync(TestClock clock, Guid staffId, Guid tabId, Guid menuItemId, int quantity) =>
        await AsAsync(clock, TestActor.Waiter(staffId), (db, actor) =>
            fixture.CreateOrderService(db, clock, actor)
                .PlaceOrderAsync(new PlaceOrderCommand(tabId, [new OrderItemInput(menuItemId, quantity)], Guid.CreateVersion7())));
}

/// <summary>
/// The same over HTTP, on the suite's clock (so <c>YALLA_TEST_NOW</c> moves it): a guest's coffee from the phone, the
/// waiter's wine and khachapuri, the wine's order voided from the tablet. The diner's bill, the split, the staff view
/// and the balance all leave the wine out; voiding the khachapuri's order once a guest has paid part is a 409
/// <c>line-already-paid</c>; and the revenue CSV and the Staff → Results CSV carry the bill that was paid.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class WholeOrderVoidEndpointTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_voided_order_is_off_the_diners_bill_the_split_the_balance_and_the_CSVs()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        AuthBranch branch;
        TestMenu menu;
        string qrToken;
        string timeZoneId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            qrToken = await db.DiningTables.AsNoTracking()
                .Where(t => t.BranchId == branch.BranchId).OrderBy(t => t.Label).Select(t => t.QrToken).FirstAsync();
            timeZoneId = await db.Branches.Where(b => b.Id == branch.BranchId).Select(b => b.TimeZoneId).SingleAsync();
        }

        using var anonymous = factory.CreateClient();
        var opened = await PreOrderTests.OkAsync(await anonymous.PostAsJsonAsync(
            "/api/tabs/open",
            new { qrToken, deviceId = "phone-host", clientCommandId = Guid.CreateVersion7(), displayName = "Host" }));
        var tabId = opened.GetProperty("tab").GetProperty("tabId").GetGuid();
        await fixture.AdmitTabAsync(factory.Clock, tabId);

        using var host = factory.CreateClientWithToken(opened.GetProperty("token").GetProperty("accessToken").GetString()!);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        Assert.Equal(
            HttpStatusCode.Created,
            (await host.PostAsJsonAsync($"/api/tabs/{tabId}/orders", new
            {
                items = new[] { new { menuItemId = menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            })).StatusCode);

        async Task<Guid> StaffOrderAsync(Guid menuItemId)
        {
            var placed = await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/staff-orders", new
            {
                items = new[] { new { menuItemId, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            });
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

            return (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        }

        var wineOrder = await StaffOrderAsync(menu.Wine);
        var khachapuriOrder = await StaffOrderAsync(menu.Khachapuri);

        var withWine = (await PreOrderTests.OkAsync(await host.GetAsync($"/api/tabs/{tabId}")))
            .GetProperty("tableTotal").GetProperty("totalAmd").GetInt64();

        // Voided from the tablet.
        var ticket = await PreOrderTests.OkAsync(await waiter.PostAsJsonAsync($"/api/orders/{wineOrder}/status", new { status = 5 }));
        var status = ticket.GetProperty("status");
        Assert.Equal(
            TabOrderStatus.Voided,
            status.ValueKind == JsonValueKind.Number
                ? (TabOrderStatus)status.GetInt32()
                : Enum.Parse<TabOrderStatus>(status.GetString()!, ignoreCase: true));

        // The diner's own bill.
        var dinerView = await PreOrderTests.OkAsync(await host.GetAsync($"/api/tabs/{tabId}"));
        var percent = dinerView.GetProperty("serviceChargePercent").GetDecimal();
        var subtotal = TestMenu.CoffeeAmd + TestMenu.KhachapuriAmd;
        var expected = subtotal + Domain.Common.Money.PercentOf(subtotal, percent);
        var tableTotal = dinerView.GetProperty("tableTotal");
        Assert.Equal((subtotal, expected, expected), (
            tableTotal.GetProperty("subtotalAmd").GetInt64(),
            tableTotal.GetProperty("totalAmd").GetInt64(),
            tableTotal.GetProperty("remainingAmd").GetInt64()));
        Assert.True(expected < withWine);

        // The split, to the dram.
        var shares = await PreOrderTests.OkAsync(await host.GetAsync($"/api/tabs/{tabId}/shares"));
        Assert.Equal(expected, shares.GetProperty("totals").GetProperty("totalAmd").GetInt64());
        Assert.Equal(expected, shares.GetProperty("shares").EnumerateArray().Sum(s => s.GetProperty("shareAmd").GetInt64()));

        // The staff view: the wine kept on the bill, marked, at nothing.
        var staffView = await PreOrderTests.OkAsync(await waiter.GetAsync($"/api/tabs/{tabId}/participants"));
        Assert.Equal(expected, staffView.GetProperty("totals").GetProperty("totalAmd").GetInt64());
        var wineLine = staffView.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("orderId").GetGuid() == wineOrder);
        Assert.True(wineLine.GetProperty("isVoided").GetBoolean());
        Assert.Equal(0L, wineLine.GetProperty("lineTotalAmd").GetInt64());
        Assert.Equal(TabOrder.WholeOrderVoidReason, wineLine.GetProperty("voidReason").GetString());

        // A guest pays part; voiding an order now is a refund.
        Assert.Equal(
            HttpStatusCode.Created,
            (await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/payments/cash", new { amountAmd = 1_000L, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var refund = await PreOrderTests.ProblemAsync(
            await waiter.PostAsJsonAsync($"/api/orders/{khachapuriOrder}/status", new { status = 5 }),
            HttpStatusCode.Conflict,
            "line-already-paid");
        Assert.Equal(tabId, refund.GetProperty("context").GetProperty("tabId").GetGuid());

        // The balance: what is left of the coffee and the khachapuri, not of the wine.
        await PreOrderTests.ProblemAsync(
            await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/payments/cash", new { amountAmd = withWine - 1_000L, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "payment-exceeds-remaining");
        Assert.Equal(
            HttpStatusCode.Created,
            (await waiter.PostAsJsonAsync($"/api/tabs/{tabId}/payments/cash", new { amountAmd = expected - 1_000L, tipAmd = 0L, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var closedView = await PreOrderTests.OkAsync(await waiter.GetAsync($"/api/tabs/{tabId}/participants"));
        Assert.Equal((expected, 0L), (
            closedView.GetProperty("totals").GetProperty("paidAmd").GetInt64(),
            closedView.GetProperty("totals").GetProperty("remainingAmd").GetInt64()));

        // The CSVs, on the branch's own date for the moment it closed.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(factory.Clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
        var window = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var b = $"/api/branches/{branch.BranchId}";

        var revenue = await CsvAsync(manager, $"{b}/reports/revenue?{window}&format=csv");
        Assert.Equal("localDate,revenueAmd,tabs,hallAmd,takeawayAmd,takeawayOrders", revenue[0]);
        var day = revenue.Single(r => r.StartsWith($"{today:yyyy-MM-dd},", StringComparison.Ordinal)).Split(',');
        Assert.Equal((expected.ToString(CultureInfo.InvariantCulture), "1", expected.ToString(CultureInfo.InvariantCulture)), (day[1], day[2], day[3]));

        var team = await CsvAsync(manager, $"{b}/staff-performance.csv?{window}");
        var header = team[0].Split(',');
        var totals = team.Single(r => r.StartsWith("totals,", StringComparison.Ordinal)).Split(',');
        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), totals[Array.IndexOf(header, "salesAmd")]);
        Assert.Equal("1", totals[Array.IndexOf(header, "voids")]);
    }

    private static async Task<List<string>> CsvAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.GetPreamble().Length));

        return [.. text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)];
    }
}
