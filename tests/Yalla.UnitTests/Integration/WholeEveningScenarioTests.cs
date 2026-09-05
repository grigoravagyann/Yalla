using Microsoft.EntityFrameworkCore;
using Yalla.Application.Ordering;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// One evening in one branch, end to end.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this suite is tested per feature, and nothing tests across them. This runs a
/// whole service through the system against real SQL Server: a booking, a walk-in, a QR scan,
/// invitations, orders from three surfaces, a void, a comp, a late joiner, the split, two cash
/// payments, a service request, and freeing the table.
/// </para>
/// <para>
/// The interactions it exists to catch are the ones no single-feature test can see: whether a comp
/// applied by a manager on one connection is visible to a payment taken on another, whether a late
/// joiner lands on a bottle ordered before they arrived, whether closing the tab disturbs a booking
/// for the same evening at a different table.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class WholeEveningScenarioTests(SqlServerFixture fixture)
{
    /// <summary>Half past seven on a Sunday. The booking is for eight.</summary>
    private static readonly DateTime Now = new(2026, 9, 13, 15, 30, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task A_whole_evening_runs_through_the_system_and_the_tab_balances_to_zero()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);

        // ---------------------------------------------------------- 1. a branch, a floor, a menu
        await using var seedDb = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(seedDb, tableCount: 8);
        var menu = await TestMenuBuilder.CreateAsync(seedDb, branch.BranchId);

        var tableSeven = branch.TableIds[6];
        var tableThree = branch.TableIds[2];

        // ---------------------------------------------------------- 2. a diner books table 7 for 20:00
        Guid bookingId;

        await using (var db = fixture.CreateContext(clock))
        {
            var reservations = fixture.CreateReservationService(db, clock, TestActor.Diner());

            var booked = await reservations.CreateAsync(new CreateReservationCommand(
                branch.BranchId,
                tableSeven,
                new DateOnly(2026, 9, 13),
                new TimeOnly(20, 0),
                PartySize: 2,
                GuestName: "Tigran",
                GuestPhone: "+37411998877",
                ClientCommandId: Guid.CreateVersion7()));

            Assert.Equal(ReservationStatus.Confirmed, booked.Status);
            bookingId = booked.Id;
        }

        // ---------------------------------------------------------- 3. a walk-in at table 3, then a scan
        await using (var db = fixture.CreateContext(clock))
        {
            var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

            await machine.SeatWalkInAsync(new SeatWalkInCommand(
                branch.BranchId, tableThree, 2, Guid.CreateVersion7()));
        }

        var qr = await seedDb.DiningTables
            .Where(t => t.Id == tableThree)
            .Select(t => t.QrToken)
            .FirstAsync();

        Guid tabId;
        Guid hostId;

        await using (var db = fixture.CreateContext(clock))
        {
            var tabs = fixture.CreateTabService(db, clock, new TestActor(ActorType.Diner, null, null, null));

            var scanned = await tabs.OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7(), "Aram"));

            // A waiter had already seated them, so the scan attaches a tab to that sitting.
            Assert.Equal(TabOpenOutcome.OpenedOnExistingSession, scanned.Outcome);

            tabId = scanned.Tab.TabId;
            hostId = scanned.Tab.Me.ParticipantId;
        }

        // ---------------------------------------------------------- 4. two friends, one approved
        var approvedGuest = await JoinAsync(clock, branch, tableThree, "phone-nune", "Nune");
        var pendingGuest = await JoinAsync(clock, branch, tableThree, "phone-vahe", "Vahe");

        await using (var db = fixture.CreateContext(clock))
        {
            var tabs = fixture.CreateTabService(db, clock, TestActor.Participant(hostId));
            await tabs.ApproveParticipantAsync(tabId, hostId, approvedGuest);
        }

        // ---------------------------------------------------------- 5. the orders
        Guid doomedLineId;
        Guid khachapuriLineId;

        await using (var db = fixture.CreateContext(clock))
        {
            var host = fixture.CreateOrderService(db, clock, TestActor.Participant(hostId));

            // Two of the host's own, and a bottle for the table.
            var hostOrder = await host.PlaceOrderAsync(new PlaceOrderCommand(
                tabId,
                [
                    new OrderItemInput(menu.Coffee, 2),
                    new OrderItemInput(menu.Khachapuri, 1),
                    new OrderItemInput(menu.Wine, 1, IsShared: true),
                ],
                Guid.CreateVersion7()));

            doomedLineId = hostOrder.Lines.First(l => l.UnitPriceAmd == TestMenu.CoffeeAmd).LineId;
            khachapuriLineId = hostOrder.Lines.First(l => l.UnitPriceAmd == TestMenu.KhachapuriAmd).LineId;

            // The bottle was poured for the two people who were there.
            var bottle = hostOrder.Lines.Single(l => l.IsShared);
            Assert.Equal(2, bottle.SharedWithParticipantIds.Count);
            Assert.DoesNotContain(pendingGuest, bottle.SharedWithParticipantIds);
        }

        await using (var db = fixture.CreateContext(clock))
        {
            var waiter = fixture.CreateOrderService(db, clock, TestActor.Waiter(branch.WaiterId));

            // A spoken order the waiter could attribute...
            await waiter.PlaceOrderAsync(new PlaceOrderCommand(
                tabId,
                [new OrderItemInput(menu.Khachapuri, 1)],
                Guid.CreateVersion7(),
                OnBehalfOfParticipantId: approvedGuest));

            // ...and one they could not.
            var unattributed = await waiter.PlaceOrderAsync(new PlaceOrderCommand(
                tabId, [new OrderItemInput(menu.Coffee, 1)], Guid.CreateVersion7()));

            Assert.True(Assert.Single(unattributed.Lines).IsTableAttributed);
        }

        // ---------------------------------------------------------- 6. a void and a comp
        await using (var db = fixture.CreateContext(clock))
        {
            var waiter = fixture.CreateOrderService(db, clock, TestActor.Waiter(branch.WaiterId));

            await waiter.VoidLineAsync(new VoidLineCommand(
                tabId, doomedLineId, "ordered by mistake", Guid.CreateVersion7()));
        }

        await using (var db = fixture.CreateContext(clock))
        {
            var manager = fixture.CreateOrderService(db, clock, TestActor.Manager(branch.ManagerId));

            var comp = await manager.AddAdjustmentAsync(new AddAdjustmentCommand(
                tabId, khachapuriLineId, AdjustmentKind.Comp, null, TestMenu.KhachapuriAmd,
                "it arrived cold", Guid.CreateVersion7()));

            Assert.Equal(TestMenu.KhachapuriAmd, comp.ReductionAmd);
        }

        // ---------------------------------------------------------- 7. the late friend orders
        await using (var db = fixture.CreateContext(clock))
        {
            var tabs = fixture.CreateTabService(db, clock, TestActor.Participant(hostId));
            await tabs.ApproveParticipantAsync(tabId, hostId, pendingGuest);
        }

        await using (var db = fixture.CreateContext(clock))
        {
            var late = fixture.CreateOrderService(db, clock, TestActor.Participant(pendingGuest));

            await late.PlaceOrderAsync(new PlaceOrderCommand(
                tabId, [new OrderItemInput(menu.Coffee, 1)], Guid.CreateVersion7()));
        }

        // ---------------------------------------------------------- 8. the split
        TabSharesView shares;

        await using (var db = fixture.CreateContext(clock))
        {
            var billing = fixture.CreateBillingQuery(db, clock, TestActor.Waiter(branch.WaiterId));
            shares = await billing.GetSharesAsync(tabId, null);
        }

        // Coffee 1,200 x2 voided -> 0; khachapuri 3,200 comped -> 0; wine 9,500 shared two ways;
        // the guest's khachapuri 3,200; the table's coffee 1,200 split three ways... but the
        // table-attributed line was placed while only two were approved, so it splits two ways.
        // Subtotal: 9,500 + 3,200 + 1,200 + 1,200 (the late coffee) = 15,100.
        Assert.True(
            shares.Totals!.SubtotalAmd == 15_100L,
            $"Subtotal was {shares.Totals.SubtotalAmd}. Shares: "
            + string.Join(", ", shares.Shares!.Select(x => $"{x.DisplayName}={x.ShareAmd}")));
        Assert.Equal(1_510L, shares.Totals.ServiceChargeAmd);
        Assert.Equal(16_610L, shares.Totals.TotalAmd);

        // The whole point of the split: it adds up, to the dram.
        Assert.Equal(shares.Totals.TotalAmd, shares.Shares!.Sum(s => s.ShareAmd));

        // The late friend is on their own coffee and on nothing that came before them.
        var lateShare = shares.Shares!.Single(s => s.ParticipantId == pendingGuest);
        Assert.Equal(TestMenu.CoffeeAmd, lateShare.OwnItemsAmd);
        Assert.Equal(0L, lateShare.SharedItemsAmd);

        // ---------------------------------------------------------- 9. cash, a shout, and the rest
        await using (var db = fixture.CreateContext(clock))
        {
            var payments = fixture.CreatePaymentService(db, clock, TestActor.Waiter(branch.WaiterId));

            var half = await payments.RecordCashAsync(new RecordCashPaymentCommand(
                tabId, 8_000L, hostId, TipAmd: 1_000L, Guid.CreateVersion7()));

            Assert.False(half.TabClosed);
            Assert.Equal(8_610L, half.Totals.RemainingAmd);
        }

        Guid requestId;

        await using (var db = fixture.CreateContext(clock))
        {
            var requests = fixture.CreateServiceRequests(db, clock, TestActor.Participant(hostId));

            var raised = await requests.RaiseAsync(tabId, hostId, ServiceRequestPreset.TheBill, null);
            requestId = raised.ServiceRequestId;
        }

        await using (var db = fixture.CreateContext(clock))
        {
            var requests = fixture.CreateServiceRequests(db, clock, TestActor.Waiter(branch.WaiterId));

            var acknowledged = await requests.AcknowledgeAsync(requestId);
            Assert.NotNull(acknowledged.AcknowledgedAtUtc);
        }

        await using (var db = fixture.CreateContext(clock))
        {
            var payments = fixture.CreatePaymentService(db, clock, TestActor.Waiter(branch.WaiterId));

            var rest = await payments.RecordCashAsync(new RecordCashPaymentCommand(
                tabId, 8_610L, null, 0L, Guid.CreateVersion7()));

            Assert.True(rest.TabClosed);
            Assert.True(rest.TableSessionClosed);
            Assert.Equal(0L, rest.Totals.RemainingAmd);
        }

        // ---------------------------------------------------------- 10. the waiter frees the table
        await using (var db = fixture.CreateContext(clock))
        {
            var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

            var freed = await machine.FreeTableAsync(new TableStateCommand(
                branch.BranchId, tableThree, Guid.CreateVersion7()));

            Assert.Equal(TableStatus.Free, freed.ToStatus);
        }

        // ---------------------------------------------------------- and the assertions that matter
        await using var verify = fixture.CreateContext(clock);

        // The tab balances to zero.
        var tab = await verify.Tabs.AsNoTracking().FirstAsync(t => t.Id == tabId);

        Assert.Equal(TabStatus.Closed, tab.Status);
        Assert.Equal(16_610L, tab.TotalAmd);
        Assert.Equal(16_610L, tab.PaidAmd);
        Assert.Equal(0L, tab.RemainingAmd);

        // Recomputing from the lines matches the cache. The cache is a denormalisation and this is
        // the assertion that keeps it honest across every mutation the evening made.
        var ledger = fixture.CreateLedger(verify, clock, TestActor.Waiter(branch.WaiterId));
        var loaded = await ledger.LoadForWriteAsync(tabId, default);
        var recomputed = ledger.Compute(loaded);

        Assert.Equal(tab.SubtotalAmd, recomputed.SubtotalAmd);
        Assert.Equal(tab.ServiceChargeAmd, recomputed.ServiceChargeAmd);
        Assert.Equal(tab.TotalAmd, recomputed.TotalAmd);
        Assert.Equal(tab.PaidAmd, recomputed.PaidAmd);

        // The event sequence has no gaps of its own: strictly increasing, all present, and the last
        // one is the close.
        var events = await verify.TabEvents
            .AsNoTracking()
            .Where(e => e.TabId == tabId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        var trail = string.Join(", ", events.Select(e => $"{e.Sequence}:{e.Type}"));

        // Contiguous from 1, which is what the per-tab counter buys over a database identity.
        Assert.Equal(
            Enumerable.Range(1, events.Count).Select(i => (long)i),
            events.Select(e => e.Sequence));

        // And in the order things actually happened. This assertion found a real bug: with an
        // IDENTITY column the payment that settled the bill and the close it triggered were written
        // in one SaveChanges, and EF inserted them in whichever order it liked - intermittently
        // telling a catching-up phone that the tab closed BEFORE the payment that closed it.
        Assert.True(
            events[^1].Type == TabEventType.TabClosed,
            $"The stream should end with the close. It was: {trail}");

        Assert.True(
            events[^2].Type == TabEventType.PaymentRecorded,
            $"The close should follow the payment that caused it. It was: {trail}");

        // Every kind of thing that happened tonight is on the stream.
        Assert.Contains(TabEventType.OrderPlaced, events.Select(e => e.Type));
        Assert.Contains(TabEventType.LineVoided, events.Select(e => e.Type));
        Assert.Contains(TabEventType.AdjustmentAdded, events.Select(e => e.Type));
        Assert.Contains(TabEventType.PaymentRecorded, events.Select(e => e.Type));
        Assert.Contains(TabEventType.ServiceRequested, events.Select(e => e.Type));
        Assert.Contains(TabEventType.ServiceRequestAcknowledged, events.Select(e => e.Type));

        // Every state change has its audit row: the seating, the free, and the tab's own log.
        var stateChanges = await verify.TableStateChanges
            .AsNoTracking()
            .Where(c => c.DiningTableId == tableThree)
            .ToListAsync();

        Assert.True(
            stateChanges.Count == 2,
            "Table 3 should have exactly the seating and the free: "
            + string.Join(", ", stateChanges.Select(c => $"{c.FromStatus}->{c.ToStatus}")));
        Assert.All(stateChanges, c => Assert.NotEqual(Guid.Empty, c.ClientCommandId));

        // The voided line is still on the bill, labelled.
        var voided = await verify.TabOrderLines.AsNoTracking().FirstAsync(l => l.Id == doomedLineId);

        Assert.True(voided.IsVoided);
        Assert.Equal("ordered by mistake", voided.VoidReason);

        // The tip stayed out of the balance.
        Assert.Equal(1_000L, await verify.Payments.Where(p => p.TabId == tabId).SumAsync(p => p.TipAmd));

        // And the 20:00 booking at table 7 was never touched by any of it.
        var booking = await verify.Reservations.AsNoTracking().FirstAsync(r => r.Id == bookingId);

        Assert.Equal(ReservationStatus.Confirmed, booking.Status);
        Assert.Equal(tableSeven, booking.DiningTableId);

        var sevenNow = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableSeven);
        Assert.Equal(TableStatus.Free, sevenNow.Status);
    }

    /// <summary>A phone scanning a table that already has a tab: it lands on that tab, pending.</summary>
    private async Task<Guid> JoinAsync(
        TestClock clock,
        TestBranch branch,
        Guid tableId,
        string deviceId,
        string name)
    {
        await using var db = fixture.CreateContext(clock);

        var qr = await db.DiningTables.Where(t => t.Id == tableId).Select(t => t.QrToken).FirstAsync();
        var tabs = fixture.CreateTabService(db, clock, new TestActor(ActorType.Diner, null, null, null));

        var joined = await tabs.OpenAsync(new OpenTabCommand(qr, deviceId, Guid.CreateVersion7(), name));

        Assert.Equal(TabOpenOutcome.JoinedExistingTab, joined.Outcome);
        Assert.Equal(ParticipantStatus.PendingApproval, joined.Tab.Me.Status);

        return joined.Tab.Me.ParticipantId;
    }
}
