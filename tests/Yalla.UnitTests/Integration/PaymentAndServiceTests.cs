using Microsoft.EntityFrameworkCore;
using Yalla.Application.Ordering;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Cash, the kitchen rail, calling a waiter, and the tab's event stream.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PaymentAndServiceTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 6, 20, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ 13. paying more than is owed

    [SkippableFact]
    public async Task Cash_for_more_than_is_owed_is_refused_with_the_current_balance()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 2);
        await using var db = world.Db;

        // 2 x 1,200 = 2,400, service 240, total 2,640.
        var payments = fixture.CreatePaymentService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var refused = await Assert.ThrowsAsync<PaymentExceedsRemainingException>(
            () => payments.RecordCashAsync(new RecordCashPaymentCommand(
                world.TabId, 5_000L, null, 0L, Guid.CreateVersion7())));

        // The waiter is standing at the table holding notes, so the number is in the exception.
        Assert.Equal(2_640L, refused.RemainingAmd);
        Assert.Equal(5_000L, refused.RequestedAmd);
        Assert.Contains("2640 AMD", refused.Message);

        await using var verify = fixture.CreateContext(world.Clock);
        Assert.False(await verify.Payments.AnyAsync(p => p.TabId == world.TabId));
    }

    // ------------------------------------------------------------ 14. two waiters, one balance

    /// <summary>
    /// <b>Test 14.</b> Two cash payments for the whole balance, genuinely racing: one succeeds and
    /// the other is refused.
    /// </summary>
    /// <remarks>
    /// Real commits over separate connections, not a mocked exception - the guarantee under test is
    /// the row version's, and a stubbed throw would prove only that the catch block compiles.
    /// Payments deliberately do <b>not</b> retry: reserving twice against the same remaining dram is
    /// exactly the failure the reserve exists to prevent.
    /// </remarks>
    [SkippableFact]
    public async Task Two_cash_payments_racing_for_the_same_balance_leave_one_of_them_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 2);
        await using var db = world.Db;

        await using var firstDb = fixture.CreateContext(world.Clock);
        await using var secondDb = fixture.CreateContext(world.Clock);

        var first = fixture.CreatePaymentService(firstDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));
        var second = fixture.CreatePaymentService(secondDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> Pay(ITabPaymentService service)
        {
            await gate.Task;

            try
            {
                await service.RecordCashAsync(new RecordCashPaymentCommand(
                    world.TabId, 2_640L, null, 0L, Guid.CreateVersion7()));

                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var a = Task.Run(() => Pay(first));
        var b = Task.Run(() => Pay(second));

        gate.SetResult();

        var outcomes = await Task.WhenAll(a, b);

        Assert.Equal(1, outcomes.Count(o => o is null));

        var loser = Assert.IsType<PaymentExceedsRemainingException>(outcomes.Single(o => o is not null));

        // Nothing is left to pay, and the loser is told so rather than being told to retry.
        Assert.Equal(0L, loser.RemainingAmd);

        await using var verify = fixture.CreateContext(world.Clock);

        // Exactly one payment landed. The bill is not paid twice.
        Assert.Equal(1, await verify.Payments.CountAsync(p => p.TabId == world.TabId));

        var tab = await verify.Tabs.AsNoTracking().FirstAsync(t => t.Id == world.TabId);

        Assert.Equal(2_640L, tab.PaidAmd);
        Assert.Equal(0L, tab.RemainingAmd);
    }

    // ------------------------------------------------------------ 16. reaching zero

    [SkippableFact]
    public async Task Settling_the_balance_closes_the_tab_and_its_session_but_leaves_the_table_occupied()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 1);
        await using var db = world.Db;

        var payments = fixture.CreatePaymentService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        // Half first: partial cash is normal and must work.
        var partial = await payments.RecordCashAsync(new RecordCashPaymentCommand(
            world.TabId, 600L, null, 0L, Guid.CreateVersion7()));

        Assert.False(partial.TabClosed);
        Assert.Equal(720L, partial.Totals.RemainingAmd);

        var final = await payments.RecordCashAsync(new RecordCashPaymentCommand(
            world.TabId, 720L, null, 0L, Guid.CreateVersion7()));

        Assert.True(final.TabClosed);
        Assert.True(final.TableSessionClosed);
        Assert.Equal(0L, final.Totals.RemainingAmd);

        await using var verify = fixture.CreateContext(world.Clock);

        var tab = await verify.Tabs.AsNoTracking().FirstAsync(t => t.Id == world.TabId);
        Assert.Equal(TabStatus.Closed, tab.Status);
        Assert.NotNull(tab.ClosedAtUtc);

        var session = await verify.TableSessions.AsNoTracking().FirstAsync(s => s.Id == tab.TableSessionId);
        Assert.NotNull(session.ClosedAtUtc);

        // But the table stays occupied. A party that has paid usually sits on for another twenty
        // minutes, and a floor plan that frees their table is lying to whoever is seating walk-ins.
        var table = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == world.Branch.FirstTableId);
        Assert.Equal(TableStatus.Occupied, table.Status);
    }

    // ------------------------------------------------------------ 17. tips

    [SkippableFact]
    public async Task A_tip_is_recorded_and_moves_neither_paid_nor_remaining()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 1);
        await using var db = world.Db;

        var payments = fixture.CreatePaymentService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        // 1,320 owed. The guest hands over 1,320 plus 2,000 for the staff.
        var paid = await payments.RecordCashAsync(new RecordCashPaymentCommand(
            world.TabId, 1_320L, null, TipAmd: 2_000L, Guid.CreateVersion7()));

        Assert.Equal(2_000L, paid.TipAmd);
        Assert.Equal(1_320L, paid.Totals.PaidAmd);
        Assert.Equal(0L, paid.Totals.RemainingAmd);
        Assert.Equal(1_320L, paid.Totals.TotalAmd);

        await using var verify = fixture.CreateContext(world.Clock);

        // Recorded, because the drawer has to reconcile against it - and outside the balance,
        // because folding it in would make the bill look overpaid by 2,000.
        var payment = await verify.Payments.AsNoTracking().FirstAsync(p => p.TabId == world.TabId);

        Assert.Equal(2_000L, payment.TipAmd);
        Assert.Equal(1_320L, payment.AmountAmd);

        var tab = await verify.Tabs.AsNoTracking().FirstAsync(t => t.Id == world.TabId);

        Assert.Equal(1_320L, tab.PaidAmd);
        Assert.Equal(0L, tab.RemainingAmd);
    }

    [SkippableFact]
    public async Task A_replayed_cash_command_returns_the_original_and_takes_no_more_money()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 2);
        await using var db = world.Db;

        var payments = fixture.CreatePaymentService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));
        var commandId = Guid.CreateVersion7();

        var first = await payments.RecordCashAsync(
            new RecordCashPaymentCommand(world.TabId, 1_000L, null, 0L, commandId));

        await using var againDb = fixture.CreateContext(world.Clock);
        var again = fixture.CreatePaymentService(againDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var second = await again.RecordCashAsync(
            new RecordCashPaymentCommand(world.TabId, 1_000L, null, 0L, commandId));

        Assert.True(second.WasReplay);
        Assert.Equal(first.PaymentId, second.PaymentId);

        await using var verify = fixture.CreateContext(world.Clock);

        Assert.Equal(1, await verify.Payments.CountAsync(p => p.TabId == world.TabId));
        Assert.Equal(1_000L, (await verify.Tabs.AsNoTracking().FirstAsync(t => t.Id == world.TabId)).PaidAmd);
    }

    [SkippableFact]
    public async Task A_manager_can_write_off_a_tab_and_a_waiter_cannot()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 2);
        await using var db = world.Db;

        var waiter = fixture.CreatePaymentService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => waiter.AbandonAsync(world.TabId, "they walked out"));

        await using var managerDb = fixture.CreateContext(world.Clock);
        var manager = fixture.CreatePaymentService(
            managerDb, world.Clock, TestActor.Manager(world.Branch.ManagerId));

        await manager.AbandonAsync(world.TabId, "they walked out");

        await using var verify = fixture.CreateContext(world.Clock);

        Assert.Equal(
            TabStatus.Abandoned,
            (await verify.Tabs.AsNoTracking().FirstAsync(t => t.Id == world.TabId)).Status);
    }

    // ------------------------------------------------------------ 18. the kitchen rail

    [SkippableFact]
    public async Task The_kitchen_role_may_mark_food_ready_and_may_not_serve_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 1);
        await using var db = world.Db;

        var orderId = await db.TabOrders.Where(o => o.TabId == world.TabId).Select(o => o.Id).FirstAsync();

        // A waiter sends it to the kitchen.
        await using (var waiterDb = fixture.CreateContext(world.Clock))
        {
            var waiter = fixture.CreateOrderService(
                waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

            var sent = await waiter.MoveOrderStatusAsync(orderId, TabOrderStatus.InKitchen);
            Assert.Equal(TabOrderStatus.InKitchen, sent.Status);
        }

        var kitchenId = await SeedKitchenStaffAsync(world);

        // The kitchen marks it ready. That is the whole of what the role does.
        await using (var kitchenDb = fixture.CreateContext(world.Clock))
        {
            var kitchen = fixture.CreateOrderService(
                kitchenDb, world.Clock, new TestActor(ActorType.Staff, kitchenId, StaffRole.Kitchen));

            var ready = await kitchen.MoveOrderStatusAsync(orderId, TabOrderStatus.Ready);
            Assert.Equal(TabOrderStatus.Ready, ready.Status);

            // Serving is a floor action and belongs to whoever carried the plate.
            var refused = await Assert.ThrowsAsync<StaffPermissionException>(
                () => kitchen.MoveOrderStatusAsync(orderId, TabOrderStatus.Served));

            Assert.Contains("Ready to Served", refused.Message);
        }

        // And a waiter can.
        await using (var waiterDb = fixture.CreateContext(world.Clock))
        {
            var waiter = fixture.CreateOrderService(
                waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

            var served = await waiter.MoveOrderStatusAsync(orderId, TabOrderStatus.Served);
            Assert.Equal(TabOrderStatus.Served, served.Status);
        }
    }

    [SkippableFact]
    public async Task An_order_cannot_go_backwards_along_the_rail()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 1);
        await using var db = world.Db;

        var orderId = await db.TabOrders.Where(o => o.TabId == world.TabId).Select(o => o.Id).FirstAsync();

        await using var waiterDb = fixture.CreateContext(world.Clock);
        var waiter = fixture.CreateOrderService(waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        await waiter.MoveOrderStatusAsync(orderId, TabOrderStatus.InKitchen);
        await waiter.MoveOrderStatusAsync(orderId, TabOrderStatus.Ready);

        // A Ready order tapped back to InKitchen loses the fact that it was ever cooked, and the
        // kitchen screen and the floor then disagree about whether food exists.
        await Assert.ThrowsAsync<Yalla.Domain.DomainStateException>(
            () => waiter.MoveOrderStatusAsync(orderId, TabOrderStatus.InKitchen));
    }

    [SkippableFact]
    public async Task The_branch_queue_shows_what_is_outstanding_with_its_table_and_its_notes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 0);
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [new OrderItemInput(world.Menu.Khachapuri, 1, Note: "no butter, please")],
            Guid.CreateVersion7()));

        await using var waiterDb = fixture.CreateContext(world.Clock);
        var waiter = fixture.CreateOrderService(waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var queue = await waiter.GetBranchOrdersAsync(world.Branch.BranchId, null);

        var order = Assert.Single(queue);

        Assert.Equal("1", order.TableLabel);
        Assert.Equal(TabOrderStatus.New, order.Status);
        Assert.Equal("no butter, please", Assert.Single(order.Lines).Note);

        // The longest prep on the order, not the sum.
        Assert.Equal(
            world.Clock.UtcNow.AddMinutes(TestMenu.LongestPrepMinutes), order.EstimatedReadyAtUtc);
    }

    // ------------------------------------------------------------ 19. calling a waiter

    [SkippableFact]
    public async Task Service_requests_are_rate_limited_per_tab_and_a_waiter_can_acknowledge_one()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 0);
        await using var db = world.Db;

        var requests = fixture.CreateServiceRequests(db, world.Clock, TestActor.Participant(world.HostId));

        ServiceRequestView? firstRequest = null;

        // Five is the limit; the sixth is refused. Per tab, so it does not matter that the guest
        // raises some of them - one bored person must not bury another table's request.
        for (var i = 0; i < 5; i++)
        {
            firstRequest ??= await requests.RaiseAsync(
                world.TabId, world.HostId, ServiceRequestPreset.Napkins, null);

            if (i > 0)
            {
                await requests.RaiseAsync(world.TabId, world.GuestId, ServiceRequestPreset.Water, null);
            }
        }

        var refused = await Assert.ThrowsAsync<ServiceRequestRateLimitedException>(
            () => requests.RaiseAsync(world.TabId, world.HostId, ServiceRequestPreset.TheBill, null));

        Assert.Equal(5, refused.Limit);
        Assert.Equal(10, refused.WindowMinutes);

        // The floor screen sees them, newest first, with the table and the wait.
        await using var staffDb = fixture.CreateContext(world.Clock);
        var staff = fixture.CreateServiceRequests(staffDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var open = await staff.GetOpenAsync(world.Branch.BranchId);

        Assert.Equal(5, open.Count);
        Assert.All(open, r => Assert.Equal("1", r.TableLabel));

        var acknowledged = await staff.AcknowledgeAsync(firstRequest!.ServiceRequestId);

        Assert.NotNull(acknowledged.AcknowledgedAtUtc);

        // Twice is a no-op, not an error: two waiters tapping the same one is the normal case.
        var again = await staff.AcknowledgeAsync(firstRequest.ServiceRequestId);
        Assert.Equal(acknowledged.AcknowledgedAtUtc, again.AcknowledgedAtUtc);

        Assert.Equal(4, (await staff.GetOpenAsync(world.Branch.BranchId)).Count);
    }

    // ------------------------------------------------------------ 20. the event stream

    /// <summary>
    /// <b>Test 20.</b> Every mutation writes exactly one event, and <c>afterSequence</c> returns
    /// them in order with nothing missing.
    /// </summary>
    [SkippableFact]
    public async Task Every_mutation_writes_exactly_one_event_and_the_stream_catches_up_in_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync(coffees: 0);
        await using var db = world.Db;

        // The host approving the guest happened during setup and is itself a mutation, so the
        // stream starts with it. Listing it here rather than filtering it out is the point: the
        // assertion is that EVERY mutation writes exactly one event, including the ones a test was
        // not thinking about.
        var expected = new List<TabEventType> { TabEventType.ParticipantApproved };

        // An order.
        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var order = await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 2)], Guid.CreateVersion7()));

        expected.Add(TabEventType.OrderPlaced);

        // A void.
        await using (var waiterDb = fixture.CreateContext(world.Clock))
        {
            var waiter = fixture.CreateOrderService(
                waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

            await waiter.VoidLineAsync(new VoidLineCommand(
                world.TabId, order.Lines[0].LineId, "wrong table", Guid.CreateVersion7()));

            expected.Add(TabEventType.LineVoided);
        }

        // A second order, so there is something left to pay for.
        await using (var againDb = fixture.CreateContext(world.Clock))
        {
            var again = fixture.CreateOrderService(againDb, world.Clock, TestActor.Participant(world.HostId));

            await again.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));

            expected.Add(TabEventType.OrderPlaced);
        }

        // A comp.
        await using (var managerDb = fixture.CreateContext(world.Clock))
        {
            var manager = fixture.CreateOrderService(
                managerDb, world.Clock, TestActor.Manager(world.Branch.ManagerId));

            await manager.AddAdjustmentAsync(new AddAdjustmentCommand(
                world.TabId, null, AdjustmentKind.Discount, 10m, null, "a regular", Guid.CreateVersion7()));

            expected.Add(TabEventType.AdjustmentAdded);
        }

        // A permission change.
        await using (var hostDb = fixture.CreateContext(world.Clock))
        {
            var tabs = fixture.CreateTabService(hostDb, world.Clock, TestActor.Participant(world.HostId));

            await tabs.SetPermissionsAsync(
                world.TabId, world.HostId, world.GuestId,
                new SetParticipantPermissionsCommand(true, false, false));

            expected.Add(TabEventType.ParticipantPermissionsChanged);
        }

        // A service request.
        await using (var requestDb = fixture.CreateContext(world.Clock))
        {
            var requests = fixture.CreateServiceRequests(
                requestDb, world.Clock, TestActor.Participant(world.HostId));

            await requests.RaiseAsync(world.TabId, world.HostId, ServiceRequestPreset.TheBill, null);

            expected.Add(TabEventType.ServiceRequested);
        }

        // And a payment that settles it, which closes the tab too.
        await using (var payDb = fixture.CreateContext(world.Clock))
        {
            var ledger = fixture.CreateLedger(payDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));
            var tab = await ledger.LoadForWriteAsync(world.TabId, default);
            var owed = ledger.Compute(tab).RemainingAmd;

            var payments = fixture.CreatePaymentService(
                payDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

            await payments.RecordCashAsync(
                new RecordCashPaymentCommand(world.TabId, owed, null, 0L, Guid.CreateVersion7()));

            expected.Add(TabEventType.PaymentRecorded);
            expected.Add(TabEventType.TabClosed);
        }

        await using var readDb = fixture.CreateContext(world.Clock);
        var billing = fixture.CreateBillingQuery(readDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var page = await billing.GetEventsAsync(world.TabId, afterSequence: 0L, limit: 0);

        Assert.Equal(expected, page.Events.Select(e => e.Type));

        // Strictly increasing, which is the contract - not contiguous, which is not.
        var sequences = page.Events.Select(e => e.Sequence).ToList();

        Assert.Equal(sequences.OrderBy(x => x), sequences);
        Assert.Equal(sequences.Distinct().Count(), sequences.Count);
        Assert.Equal(sequences[^1], page.MaxSequence);

        // Catching up from the middle returns the rest and nothing else - the phone that was in a
        // lift when the wine was ordered.
        var middle = sequences[2];
        var rest = await billing.GetEventsAsync(world.TabId, middle, 0);

        Assert.Equal(expected.Skip(3), rest.Events.Select(e => e.Type));
        Assert.All(rest.Events, e => Assert.True(e.Sequence > middle));

        // And the payload really is the change, not a summary of it.
        var placed = page.Events.First(e => e.Type == TabEventType.OrderPlaced);

        Assert.Equal(order.OrderId, placed.Payload.GetProperty("orderId").GetGuid());
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

    /// <summary>A seated table, a tab, a host, an approved guest, and optionally some coffee on it.</summary>
    private async Task<World> ArrangeAsync(int coffees)
    {
        var clock = new TestClock(Now);
        var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        var qr = await db.DiningTables
            .Where(t => t.Id == branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        Guid tabId;
        Guid hostId;
        Guid guestId;

        await using (var tabDb = fixture.CreateContext(clock))
        {
            var tabs = fixture.CreateTabService(tabDb, clock, new TestActor(ActorType.Diner, null, null, null));

            var opened = await tabs.OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7(), "Aram"));
            var joined = await tabs.OpenAsync(new OpenTabCommand(qr, "phone-guest", Guid.CreateVersion7(), "Nune"));

            await tabs.ApproveParticipantAsync(
                opened.Tab.TabId, opened.Tab.Me.ParticipantId, joined.Tab.Me.ParticipantId);

            tabId = opened.Tab.TabId;
            hostId = opened.Tab.Me.ParticipantId;
            guestId = joined.Tab.Me.ParticipantId;
        }

        if (coffees > 0)
        {
            await using var orderDb = fixture.CreateContext(clock);
            var orders = fixture.CreateOrderService(orderDb, clock, TestActor.Participant(hostId));

            await orders.PlaceOrderAsync(new PlaceOrderCommand(
                tabId, [new OrderItemInput(menu.Coffee, coffees)], Guid.CreateVersion7()));
        }

        return new World(db, clock, branch, menu, tabId, hostId, guestId);
    }

    /// <summary>A kitchen hand at this branch. The seeded branch has a waiter and a manager only.</summary>
    private async Task<Guid> SeedKitchenStaffAsync(World world)
    {
        await using var db = fixture.CreateContext(world.Clock);

        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == world.Branch.BranchId);

        var cook = new StaffMember(
            branch.VenueId,
            "Test Cook",
            $"+3742{Guid.NewGuid().ToString("N")[..7]}",
            StaffRole.Kitchen,
            "hash",
            branch.Id);

        db.StaffMembers.Add(cook);
        await db.SaveChangesAsync();

        return cook.Id;
    }
}
