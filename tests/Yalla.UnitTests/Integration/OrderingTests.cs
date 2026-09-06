using Microsoft.EntityFrameworkCore;
using Yalla.Application.Ordering;
using Yalla.Application.Tabs;
using Yalla.Domain.Billing;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Ordering from both surfaces, voids, comps, and the totals that come out.
/// </summary>
/// <remarks>
/// Against real SQL Server, because most of what is under test is the database's: the unique index
/// that makes a double-tap idempotent, the row version that makes two simultaneous orders safe, and
/// the identity column that gives the event stream its order.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class OrderingTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 6, 18, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ the menu

    /// <summary>
    /// A pending participant reads the whole menu, prices included, and can do nothing else.
    /// </summary>
    /// <remarks>
    /// The rule was set in Prompt 5 and only starts to bite now that there is a menu to read. It is
    /// the one thing a guest waiting on the host's approval must be able to do: they are sitting at
    /// the table, and a menu they cannot open is the friction the product exists to remove. Prices
    /// stay visible even to somebody the host has hidden the table total from, so they can always
    /// work out what their own order costs.
    /// </remarks>
    [SkippableFact]
    public async Task A_pending_participant_reads_the_menu_with_prices_and_cannot_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        // Somebody who scanned the code and has not been let on yet.
        var pending = await JoinWithoutApprovalAsync(world, "phone-pending", "Vahe");

        var menu = await SqlServerFixture.CreateMenuQuery(db).GetBranchMenuAsync(world.Branch.BranchId);

        var items = menu.Categories.SelectMany(c => c.Items).ToList();

        Assert.Equal(4, items.Count);
        Assert.All(items, i => Assert.True(i.PriceAmd > 0L, $"{i.Name} came back without a price."));

        // Every descriptive field is present, because the whole argument for making them required
        // was that the diner stops needing to ask a waiter.
        Assert.All(items, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Description));
            Assert.False(string.IsNullOrWhiteSpace(i.Ingredients));
            Assert.False(string.IsNullOrWhiteSpace(i.Allergens));
            Assert.False(string.IsNullOrWhiteSpace(i.PortionSize));
            // Asserted rather than dereferenced: a photo is required on a menu
            // item, so a null one is the failure this line is here to catch,
            // and `i.Photo.CardUrl` on its own reports it as an NRE instead.
            Assert.NotNull(i.Photo);
            Assert.False(string.IsNullOrWhiteSpace(i.Photo.CardUrl));
            Assert.NotEqual(Guid.Empty, i.Photo.PhotoId);
            Assert.True(i.PrepMinutes > 0);
        });

        // The sold-out dish is present and flagged, not hidden.
        var soldOut = items.Single(i => i.Id == world.Menu.SoldOut);

        Assert.False(soldOut.IsAvailable);
        Assert.Equal(2_000L, soldOut.PriceAmd);

        // And that is the whole of what they may do: ordering is refused until the host approves.
        await using var orderDb = fixture.CreateContext(world.Clock);
        var orders = fixture.CreateOrderService(orderDb, world.Clock, TestActor.Participant(pending));

        var refused = await Assert.ThrowsAsync<TabPermissionException>(
            () => orders.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7())));

        Assert.Contains("approved", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ 1. spoken orders and attribution

    [SkippableFact]
    public async Task A_spoken_order_on_a_named_guests_behalf_is_attributed_to_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiter = fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var placed = await waiter.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [new OrderItemInput(world.Menu.Coffee, 1)],
            Guid.CreateVersion7(),
            OnBehalfOfParticipantId: world.GuestId));

        Assert.Equal(world.Branch.WaiterId, placed.PlacedByStaffId);
        Assert.Equal(world.GuestId, placed.OnBehalfOfParticipantId);

        var line = Assert.Single(placed.Lines);
        Assert.False(line.IsTableAttributed);
        Assert.False(line.IsShared);
        Assert.Empty(line.SharedWithParticipantIds);

        // It lands on that guest alone.
        var shares = await SharesAsync(world);

        Assert.Equal(TestMenu.CoffeeAmd, Share(shares, world.GuestId).OwnItemsAmd);
        Assert.Equal(0L, Share(shares, world.HostId).OwnItemsAmd);
    }

    [SkippableFact]
    public async Task A_spoken_order_with_nobody_named_is_attributed_to_the_table_and_split_across_everyone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiter = fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        // The waiter took the order across a noisy table and could not say who asked for it.
        var placed = await waiter.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Wine, 1)], Guid.CreateVersion7()));

        var line = Assert.Single(placed.Lines);

        // Explicit in the data, not incidental: this is where paper-ordering habits show up.
        Assert.True(line.IsTableAttributed);
        Assert.Equal(2, line.SharedWithParticipantIds.Count);

        var shares = await SharesAsync(world);

        // 9,500 two ways is 4,750 each, and neither owns it.
        Assert.Equal(4_750L, Share(shares, world.HostId).SharedItemsAmd);
        Assert.Equal(4_750L, Share(shares, world.GuestId).SharedItemsAmd);
        Assert.Equal(0L, Share(shares, world.HostId).OwnItemsAmd);
    }

    // ------------------------------------------------------------ 2. a sold-out dish

    [SkippableFact]
    public async Task An_unavailable_item_is_refused_by_name_and_nothing_else_on_the_order_is_placed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var refused = await Assert.ThrowsAsync<MenuItemUnavailableException>(
            () => diner.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId,
                [
                    new OrderItemInput(world.Menu.Coffee, 2),
                    new OrderItemInput(world.Menu.SoldOut, 1),
                    new OrderItemInput(world.Menu.Khachapuri, 1),
                ],
                Guid.CreateVersion7())));

        // Named, so the client can say which dish rather than failing the order generically.
        Assert.Equal(world.Menu.SoldOut, refused.MenuItemId);
        Assert.Contains("Lamb kebab", refused.Message);

        // All-or-nothing. A partial order is a decision made on the diner's behalf that they find
        // out about when the food arrives.
        await using var verify = fixture.CreateContext(world.Clock);
        Assert.False(await verify.TabOrders.AnyAsync(o => o.TabId == world.TabId));
    }

    // ------------------------------------------------------------ 3. the shared-line snapshot

    [SkippableFact]
    public async Task A_shared_line_snapshots_who_was_there_and_a_later_joiner_is_absent_from_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var host = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var bottle = await host.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [new OrderItemInput(world.Menu.Wine, 1, IsShared: true)],
            Guid.CreateVersion7()));

        var shared = Assert.Single(bottle.Lines);
        Assert.Equal(2, shared.SharedWithParticipantIds.Count);

        // The friend who was fifteen minutes late.
        var latecomer = await JoinAndApproveAsync(world, "phone-late", "Nare");

        Assert.DoesNotContain(latecomer, shared.SharedWithParticipantIds);

        var shares = await SharesAsync(world);

        // Still two ways, not three: they were not there when it was poured.
        Assert.Equal(4_750L, Share(shares, world.HostId).SharedItemsAmd);
        Assert.Equal(4_750L, Share(shares, world.GuestId).SharedItemsAmd);
        Assert.Equal(0L, Share(shares, latecomer).SharedItemsAmd);
    }

    // ------------------------------------------------------------ 4. a tab that is closing

    [SkippableFact]
    public async Task Ordering_on_a_closing_tab_is_refused_with_its_own_error()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        await using (var staffDb = fixture.CreateContext(world.Clock))
        {
            var tabs = fixture.CreateTabService(staffDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));
            await tabs.BeginClosingAsync(world.TabId);
        }

        await using var ordererDb = fixture.CreateContext(world.Clock);
        var diner = fixture.CreateOrderService(ordererDb, world.Clock, TestActor.Participant(world.HostId));

        var refused = await Assert.ThrowsAsync<TabNotAcceptingOrdersException>(
            () => diner.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7())));

        Assert.Equal(TabStatus.Closing, refused.Status);
        Assert.Contains("bill has been asked for", refused.Message);
    }

    // ------------------------------------------------------------ 5. the double tap

    [SkippableFact]
    public async Task A_replayed_order_command_returns_the_original_and_creates_no_second_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));
        var commandId = Guid.CreateVersion7();

        var first = await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 2)], commandId));

        Assert.False(first.WasReplay);

        // The diner saw nothing happen and tapped again, on a fresh request.
        await using var secondDb = fixture.CreateContext(world.Clock);
        var again = fixture.CreateOrderService(secondDb, world.Clock, TestActor.Participant(world.HostId));

        var second = await again.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 2)], commandId));

        Assert.True(second.WasReplay);
        Assert.Equal(first.OrderId, second.OrderId);

        await using var verify = fixture.CreateContext(world.Clock);

        Assert.Equal(1, await verify.TabOrders.CountAsync(o => o.TabId == world.TabId));
        Assert.Equal(2 * TestMenu.KhachapuriAmd, (await TabAsync(verify, world.TabId)).SubtotalAmd);
    }

    // ------------------------------------------------------------ 6 and 7. voids

    [SkippableFact]
    public async Task A_voided_line_stays_on_the_bill_labelled_and_the_total_drops()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var order = await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [new OrderItemInput(world.Menu.Coffee, 1), new OrderItemInput(world.Menu.Khachapuri, 1)],
            Guid.CreateVersion7()));

        var doomed = order.Lines.First(l => l.Name.Contains("khachapuri", StringComparison.OrdinalIgnoreCase));

        await using var waiterDb = fixture.CreateContext(world.Clock);
        var waiter = fixture.CreateOrderService(waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var after = await waiter.VoidLineAsync(
            new VoidLineCommand(world.TabId, doomed.LineId, "sent to the wrong table", Guid.CreateVersion7()));

        // Still there, and the diner can see it - nothing silently disappears from a bill somebody
        // is watching on their phone.
        var voided = after.Lines.Single(l => l.LineId == doomed.LineId);

        Assert.Equal(0L, voided.LineTotalAmd);
        Assert.Equal(2, after.Lines.Count);

        await using var verify = fixture.CreateContext(world.Clock);

        var row = await verify.TabOrderLines.AsNoTracking().FirstAsync(l => l.Id == doomed.LineId);

        Assert.True(row.IsVoided);
        Assert.Equal("sent to the wrong table", row.VoidReason);
        Assert.Equal(world.Branch.WaiterId, row.VoidedByStaffId);

        // And the money is right: only the coffee is left.
        var tab = await TabAsync(verify, world.TabId);

        Assert.Equal(TestMenu.CoffeeAmd, tab.SubtotalAmd);
        Assert.Equal(120L, tab.ServiceChargeAmd);
        Assert.Equal(1_320L, tab.TotalAmd);
    }

    [SkippableFact]
    public async Task Voiding_a_line_on_a_tab_that_has_been_paid_against_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var order = await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Coffee, 2)], Guid.CreateVersion7()));

        await using (var payDb = fixture.CreateContext(world.Clock))
        {
            var payments = fixture.CreatePaymentService(
                payDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

            await payments.RecordCashAsync(
                new RecordCashPaymentCommand(world.TabId, 1_000L, null, 0L, Guid.CreateVersion7()));
        }

        await using var waiterDb = fixture.CreateContext(world.Clock);
        var waiter = fixture.CreateOrderService(waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var refused = await Assert.ThrowsAsync<LineAlreadyPaidException>(
            () => waiter.VoidLineAsync(new VoidLineCommand(
                world.TabId, order.Lines[0].LineId, "changed their mind", Guid.CreateVersion7())));

        Assert.Contains("refund", refused.Message);
    }

    // ------------------------------------------------------------ 8. a comp, end to end

    [SkippableFact]
    public async Task A_comp_reduces_the_service_charge_with_the_dish_it_takes_off()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var order = await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [new OrderItemInput(world.Menu.Coffee, 1), new OrderItemInput(world.Menu.Khachapuri, 1)],
            Guid.CreateVersion7()));

        // 1,200 + 3,200 = 4,400, service 440.
        await using var verifyBefore = fixture.CreateContext(world.Clock);
        var before = await TabAsync(verifyBefore, world.TabId);

        Assert.Equal(4_400L, before.SubtotalAmd);
        Assert.Equal(440L, before.ServiceChargeAmd);

        var khachapuri = order.Lines.First(l => l.UnitPriceAmd == TestMenu.KhachapuriAmd);

        await using var managerDb = fixture.CreateContext(world.Clock);
        var manager = fixture.CreateOrderService(
            managerDb, world.Clock, TestActor.Manager(world.Branch.ManagerId));

        var comp = await manager.AddAdjustmentAsync(new AddAdjustmentCommand(
            world.TabId, khachapuri.LineId, AdjustmentKind.Comp, null, TestMenu.KhachapuriAmd,
            "it arrived cold", Guid.CreateVersion7()));

        Assert.Equal(TestMenu.KhachapuriAmd, comp.ReductionAmd);

        await using var verifyAfter = fixture.CreateContext(world.Clock);
        var after = await TabAsync(verifyAfter, world.TabId);

        // The service charge came off with it. Charging service on a dish you have just apologised
        // for is the opposite of an apology.
        Assert.Equal(1_200L, after.SubtotalAmd);
        Assert.Equal(120L, after.ServiceChargeAmd);
        Assert.Equal(1_320L, after.TotalAmd);
    }

    [SkippableFact]
    public async Task A_waiter_cannot_comp_a_dish()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiter = fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var refused = await Assert.ThrowsAsync<StaffPermissionException>(
            () => waiter.AddAdjustmentAsync(new AddAdjustmentCommand(
                world.TabId, null, AdjustmentKind.Discount, 10m, null, "a regular", Guid.CreateVersion7())));

        Assert.Equal(StaffRole.Manager, refused.RequiredRole);
        Assert.Equal(StaffRole.Waiter, refused.ActualRole);
    }

    // ------------------------------------------------------------ 11. the cache agrees with the lines

    /// <summary>
    /// <b>Test 11.</b> A complex tab, recomputed from its rows in a fresh context, equals the
    /// cached columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cached totals are a denormalisation, exactly like <c>DiningTable.Status</c>: the
    /// authoritative number is the sum of the lines. This is the test that says so, and the one
    /// that fails if a future mutation forgets to recompute.
    /// </para>
    /// <para>
    /// It matters more since the cache moved out of the order-insert transaction, not less. Orders
    /// now refresh it in a second transaction afterwards while voids and adjustments still write it
    /// inline, so this exercises a tab built by both kinds of write and insists they end up
    /// agreeing.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Recomputing_a_complex_tab_from_its_lines_equals_the_cached_totals()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var host = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var first = await host.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [
                new OrderItemInput(world.Menu.Coffee, 3),
                new OrderItemInput(world.Menu.Wine, 1, IsShared: true),
            ],
            Guid.CreateVersion7()));

        await using (var guestDb = fixture.CreateContext(world.Clock))
        {
            var guest = fixture.CreateOrderService(guestDb, world.Clock, TestActor.Participant(world.GuestId));

            await guest.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 2)], Guid.CreateVersion7()));
        }

        await using (var waiterDb = fixture.CreateContext(world.Clock))
        {
            var waiter = fixture.CreateOrderService(
                waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

            await waiter.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));

            await waiter.VoidLineAsync(new VoidLineCommand(
                world.TabId, first.Lines[0].LineId, "wrong order", Guid.CreateVersion7()));
        }

        await using (var managerDb = fixture.CreateContext(world.Clock))
        {
            var manager = fixture.CreateOrderService(
                managerDb, world.Clock, TestActor.Manager(world.Branch.ManagerId));

            await manager.AddAdjustmentAsync(new AddAdjustmentCommand(
                world.TabId, null, AdjustmentKind.Discount, 12.5m, null, "a regular",
                Guid.CreateVersion7()));
        }

        // Recompute from the rows, in a context that has never seen the cache written.
        await using var fresh = fixture.CreateContext(world.Clock);

        var ledger = fixture.CreateLedger(fresh, world.Clock, TestActor.Waiter(world.Branch.WaiterId));
        var tab = await ledger.LoadForWriteAsync(world.TabId, default);
        var recomputed = ledger.Compute(tab);

        Assert.Equal(tab.SubtotalAmd, recomputed.SubtotalAmd);
        Assert.Equal(tab.ServiceChargeAmd, recomputed.ServiceChargeAmd);
        Assert.Equal(tab.TotalAmd, recomputed.TotalAmd);
        Assert.Equal(tab.PaidAmd, recomputed.PaidAmd);
        Assert.Equal(tab.RemainingAmd, recomputed.RemainingAmd);

        // And it is a real bill, not an accidentally empty one.
        Assert.True(recomputed.TotalAmd > 0L);
        Assert.Equal(recomputed.TotalAmd, recomputed.Shares.Sum(s => s.ShareAmd));
    }

    // ------------------------------------------------------------ 11. a guest without the total

    [SkippableFact]
    public async Task A_guest_without_the_table_total_gets_their_own_share_and_no_aggregate()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var host = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        await host.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 2)], Guid.CreateVersion7()));

        await using (var guestDb = fixture.CreateContext(world.Clock))
        {
            var guest = fixture.CreateOrderService(guestDb, world.Clock, TestActor.Participant(world.GuestId));

            await guest.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));
        }

        // The host turns the total off for their guest.
        await using (var hostDb = fixture.CreateContext(world.Clock))
        {
            var tabs = fixture.CreateTabService(hostDb, world.Clock, TestActor.Participant(world.HostId));

            await tabs.SetPermissionsAsync(
                world.TabId, world.HostId, world.GuestId,
                new SetParticipantPermissionsCommand(CanOrder: true, CanSeeTableTotal: false, CanPay: false));
        }

        await using var readDb = fixture.CreateContext(world.Clock);
        var billing = fixture.CreateBillingQuery(readDb, world.Clock, TestActor.Participant(world.GuestId));

        var view = await billing.GetSharesAsync(world.TabId, world.GuestId);

        // Their own number is always there - "I didn't order that" is settled by them being able to
        // see what they did order.
        Assert.NotNull(view.MyShare);
        Assert.Equal(TestMenu.CoffeeAmd, view.MyShare!.OwnItemsAmd);

        // And the aggregate is ABSENT, not zeroed. A zero reads as "nothing owed".
        Assert.False(view.TableTotalVisible);
        Assert.Null(view.Totals);
        Assert.Null(view.Shares);
    }

    // ------------------------------------------------------------ 12. a removed guest

    [SkippableFact]
    public async Task A_removed_participants_items_stay_on_the_bill_and_fall_to_the_host()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        await using (var guestDb = fixture.CreateContext(world.Clock))
        {
            var guest = fixture.CreateOrderService(guestDb, world.Clock, TestActor.Participant(world.GuestId));

            await guest.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 1)], Guid.CreateVersion7()));
        }

        await using (var hostDb = fixture.CreateContext(world.Clock))
        {
            var tabs = fixture.CreateTabService(hostDb, world.Clock, TestActor.Participant(world.HostId));
            await tabs.RemoveParticipantAsync(world.TabId, world.HostId, world.GuestId);
        }

        var shares = await SharesAsync(world);

        // Still on the bill: the food was eaten.
        Assert.Equal(TestMenu.KhachapuriAmd, shares.Totals!.SubtotalAmd);

        // And it falls to the host, stated rather than folded in silently.
        Assert.Equal(TestMenu.KhachapuriAmd, Share(shares, world.HostId).AbsorbedFromRemovedAmd);
        Assert.Equal(TestMenu.KhachapuriAmd, shares.AbsorbedFromRemovedAmd);
        Assert.Equal(0L, Share(shares, world.GuestId).ShareAmd);
        Assert.Equal(shares.Totals.TotalAmd, shares.Shares!.Sum(s => s.ShareAmd));
    }

    // ------------------------------------------------------------ 9 and 10. ten orders at once

    /// <summary>How many orders race in the concurrency test. Above the old retry ceiling of three.</summary>
    private const int SimultaneousOrders = 10;

    /// <summary>
    /// <b>Tests 9 and 10.</b> Ten genuinely simultaneous orders on one tab all succeed, and the
    /// totals cache converges on the right number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be two orders, and two was the number the old design could survive. The order
    /// insert wrote the tab's totals cache in the same transaction, so every concurrent order
    /// contended for one row and lost the row-version race; three attempts absorbed a couple of
    /// writers and six exhausted them, surfacing a concurrency error <b>to a diner</b> for adding a
    /// coffee. That is the worst-looking failure in the product, because the fault is invisible and
    /// the app simply appears broken.
    /// </para>
    /// <para>
    /// Raising the ceiling would have moved it. The fix was to stop writing the shared row: the
    /// stored totals are a cache, the authoritative total is the sum of the lines, and the insert
    /// does not need to touch the tab to be correct. Ten writers now have nothing in common to
    /// collide on, so the number here could be a hundred - ten is simply comfortably past the
    /// ceiling that used to exist.
    /// </para>
    /// <para>
    /// Ten separate contexts, and therefore ten connections, released by one gate. Anything sharing
    /// a context would serialise on the context itself and prove nothing.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Ten_simultaneous_orders_all_succeed_and_the_totals_cache_converges()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var contexts = new List<YallaDbContext>();
        var services = new List<Yalla.Infrastructure.Services.TabOrderService>();

        try
        {
            for (var i = 0; i < SimultaneousOrders; i++)
            {
                var context = fixture.CreateContext(world.Clock);
                contexts.Add(context);

                // Alternating between the two people on the tab. Which of them orders is beside the
                // point - what is under test is what the writers share, and they share the tab.
                var participant = i % 2 == 0 ? world.HostId : world.GuestId;
                services.Add(fixture.CreateOrderService(context, world.Clock, TestActor.Participant(participant)));
            }

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var placements = services
                .Select(service => Task.Run(async () =>
                {
                    await gate.Task;

                    return await service.PlaceOrderAsync(new PlaceOrderCommand(
                        world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));
                }))
                .ToList();

            gate.SetResult();

            // Test 9. Task.WhenAll rethrows, so a concurrency error reaching any one of these
            // diners fails the test here rather than showing up as a wrong total below.
            var results = await Task.WhenAll(placements);

            Assert.All(results, r => Assert.False(r.WasReplay));
            Assert.Equal(SimultaneousOrders, results.Select(r => r.OrderId).Distinct().Count());

            // Test 10. The cache converges: every writer recomputes after its own insert, and the
            // row version on the tab is what makes the last one to commit the one that saw
            // everything.
            var expectedSubtotal = SimultaneousOrders * TestMenu.CoffeeAmd;

            await using var verify = fixture.CreateContext(world.Clock);
            var tab = await TabAsync(verify, world.TabId);

            Assert.Equal(expectedSubtotal, tab.SubtotalAmd);
            Assert.Equal(expectedSubtotal / 10L, tab.ServiceChargeAmd);
            Assert.Equal(expectedSubtotal + (expectedSubtotal / 10L), tab.TotalAmd);

            // And the cache agrees with the lines, which are the truth.
            await using var recomputeDb = fixture.CreateContext(world.Clock);
            var ledger = fixture.CreateLedger(recomputeDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));
            var recomputed = ledger.Compute(await ledger.LoadForWriteAsync(world.TabId, default));

            Assert.Equal(tab.SubtotalAmd, recomputed.SubtotalAmd);
            Assert.Equal(tab.TotalAmd, recomputed.TotalAmd);

            // The event stream numbered all ten, contiguously. Renumbering after a lost place is
            // the other race ten simultaneous writers provoke, and a gap or a duplicate here would
            // mean a catching-up phone silently missing an order.
            var sequences = await verify.TabEvents
                .AsNoTracking()
                .Where(e => e.TabId == world.TabId && e.Type == TabEventType.OrderPlaced)
                .Select(e => e.Sequence)
                .OrderBy(s => s)
                .ToListAsync();

            Assert.Equal(SimultaneousOrders, sequences.Count);
            Assert.Equal(sequences.Count, sequences.Distinct().Count());
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    // ------------------------------------------------------------ helpers

    private sealed record World(
        YallaDbContext Db,
        TestClock Clock,
        TestBranch Branch,
        TestMenu Menu,
        Guid TabId,
        Guid HostId,
        Guid GuestId);

    /// <summary>A seated table with a tab, a host, one approved guest, and a menu.</summary>
    private async Task<World> ArrangeAsync()
    {
        var clock = new TestClock(Now);
        var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        var qr = await db.DiningTables
            .Where(t => t.Id == branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        await using var tabDb = fixture.CreateContext(clock);
        var tabs = fixture.CreateTabService(tabDb, clock, new TestActor(ActorType.Diner, null, null, null));

        var opened = await tabs.OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7(), "Aram"));
        var joined = await tabs.OpenAsync(new OpenTabCommand(qr, "phone-guest", Guid.CreateVersion7(), "Nune"));

        await tabs.ApproveParticipantAsync(
            opened.Tab.TabId, opened.Tab.Me.ParticipantId, joined.Tab.Me.ParticipantId);

        return new World(
            db, clock, branch, menu, opened.Tab.TabId, opened.Tab.Me.ParticipantId, joined.Tab.Me.ParticipantId);
    }

    private async Task<Guid> JoinAndApproveAsync(World world, string deviceId, string name)
    {
        await using var db = fixture.CreateContext(world.Clock);
        var tabs = fixture.CreateTabService(db, world.Clock, new TestActor(ActorType.Diner, null, null, null));

        var qr = await db.DiningTables
            .Where(t => t.Id == world.Branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        var joined = await tabs.OpenAsync(new OpenTabCommand(qr, deviceId, Guid.CreateVersion7(), name));

        await tabs.ApproveParticipantAsync(world.TabId, world.HostId, joined.Tab.Me.ParticipantId);

        return joined.Tab.Me.ParticipantId;
    }

    /// <summary>A phone that scanned the code and is waiting on the host.</summary>
    private async Task<Guid> JoinWithoutApprovalAsync(World world, string deviceId, string name)
    {
        await using var db = fixture.CreateContext(world.Clock);
        var tabs = fixture.CreateTabService(db, world.Clock, new TestActor(ActorType.Diner, null, null, null));

        var qr = await db.DiningTables
            .Where(t => t.Id == world.Branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        var joined = await tabs.OpenAsync(new OpenTabCommand(qr, deviceId, Guid.CreateVersion7(), name));

        Assert.Equal(ParticipantStatus.PendingApproval, joined.Tab.Me.Status);

        return joined.Tab.Me.ParticipantId;
    }

    private async Task<TabSharesView> SharesAsync(World world)
    {
        await using var db = fixture.CreateContext(world.Clock);
        var billing = fixture.CreateBillingQuery(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        return await billing.GetSharesAsync(world.TabId, null);
    }

    private static ParticipantShareView Share(TabSharesView view, Guid participantId) =>
        view.Shares!.Single(s => s.ParticipantId == participantId);

    private static async Task<Tab> TabAsync(YallaDbContext db, Guid tabId) =>
        await db.Tabs.AsNoTracking().FirstAsync(t => t.Id == tabId);
}
