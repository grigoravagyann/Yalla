using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Ordering;
using Yalla.Application.Reports;
using Yalla.Application.Tables;
using Yalla.Application.Tabs;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The activity history and the staff results against the real services: who did what, a sale that follows the table
/// across a transfer, medians, the money matching the revenue report, a bill's timeline, and the retention sweep. The
/// HTTP surface - who may read, the device, the feed's pages - is <see cref="ActivityEndpointTests"/>. See
/// <c>docs/activity.md</c>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class ActivityHistoryTests(SqlServerFixture fixture)
{
    /// <summary>17:12 in Yerevan, a Saturday.</summary>
    private static readonly DateTime SeatedAt = new(2026, 10, 24, 13, 12, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 24);

    /// <summary>
    /// The Check board, end to end: Lilit seats four and sends two khachapuri; at 17:40 she hands the table to Aram; Aram
    /// sends a bottle of wine and takes the cash with a 2,000 tip. Lilit's share of the sale is her lines', Aram's his,
    /// the shares add up to the bill, the tip and the cash are Aram's - and the totals are the revenue report's to the
    /// dram. The timeline names the two waiters in turn and who added each line.
    /// </summary>
    [SkippableFact]
    public async Task A_transfer_moves_the_sale_from_then_on_and_the_totals_match_the_revenue_report()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(SeatedAt);
        var (branch, menu, aram, _) = await ArrangeAsync(clock);
        var lilit = branch.WaiterId;
        var tableId = branch.FirstTableId;

        // 17:12 Lilit seats four, a minute later opens the bill; 17:15 two khachapuri.
        var tabId = await AsAsync(clock, TestActor.Waiter(lilit), async (db, actor) =>
        {
            await fixture.CreateService(db, clock, actor)
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 4, Guid.CreateVersion7()));

            clock.UtcNow = SeatedAt.AddMinutes(1);

            return (await fixture.CreateTabService(db, clock, actor)
                .OpenForStaffAsync(new OpenTabForStaffCommand(branch.BranchId, tableId, Guid.CreateVersion7()))).TabId;
        });

        clock.UtcNow = SeatedAt.AddMinutes(3);
        await OrderAsync(clock, lilit, tabId, menu.Khachapuri, quantity: 2);

        // Aram may not take Lilit's table: it is not his, and he is not a manager.
        clock.UtcNow = SeatedAt.AddMinutes(20);
        await AsAsync(clock, TestActor.Waiter(aram), async (db, actor) =>
        {
            await Assert.ThrowsAsync<StaffPermissionException>(() =>
                Transfer(db, clock, actor).TransferAsync(new TransferTableCommand(tabId, aram)));

            return 0;
        });

        // 17:40 Lilit hands it to Aram.
        clock.UtcNow = SeatedAt.AddMinutes(28);
        var transfer = await AsAsync(clock, TestActor.Waiter(lilit), (db, actor) =>
            Transfer(db, clock, actor).TransferAsync(new TransferTableCommand(tabId, aram)));

        Assert.Equal(lilit, transfer.PreviousServerStaffMemberId);
        Assert.Equal(aram, transfer.ServerStaffMemberId);
        Assert.False(transfer.WasReplay);

        // Again: already his, nothing written.
        var again = await AsAsync(clock, TestActor.Waiter(aram), (db, actor) =>
            Transfer(db, clock, actor).TransferAsync(new TransferTableCommand(tabId, aram)));
        Assert.True(again.WasReplay);

        // 17:44 Aram sends the wine; 18:20 he takes the cash, and a tip.
        clock.UtcNow = SeatedAt.AddMinutes(32);
        await OrderAsync(clock, aram, tabId, menu.Wine, quantity: 1);

        clock.UtcNow = SeatedAt.AddMinutes(68);
        long total;

        await using (var db = fixture.CreateContext(clock))
        {
            total = await fixture.CreateLedger(db, clock, TestActor.Waiter(aram)).RecomputeTotalsAsync(tabId, default)
                is { } bill ? bill.TotalAmd : 0L;
        }

        await AsAsync(clock, TestActor.Waiter(aram), async (db, actor) =>
            await fixture.CreatePaymentService(db, clock, actor)
                .RecordCashAsync(new RecordCashPaymentCommand(tabId, total, null, 2_000L, Guid.CreateVersion7())));

        // Whose each part was, as stored.
        await using (var db = fixture.CreateContext(clock))
        {
            var orders = await db.TabOrders.AsNoTracking().Where(o => o.TabId == tabId).OrderBy(o => o.PlacedAtUtc).ToListAsync();
            Assert.Equal([lilit, aram], orders.Select(o => o.ServerStaffMemberId!.Value).ToArray());

            var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.TabId == tabId);
            Assert.Equal(aram, payment.RecordedByStaffId);
            Assert.Equal(aram, payment.ServerStaffMemberId);

            // The History: who did each, in order, all from the console (no device on these test actors).
            var entries = await db.ActivityEntries.AsNoTracking()
                .Where(e => e.BranchId == branch.BranchId)
                .OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id)
                .Select(e => new { e.Action, e.StaffMemberId, e.DeviceLabel, e.IsMoney, e.TableSessionId })
                .ToListAsync();

            Assert.Equal(
                [
                    ActivityActions.TableSeated, ActivityActions.TableTabOpened, ActivityActions.OrderSent,
                    ActivityActions.TableTransferred, ActivityActions.OrderSent, ActivityActions.PaymentCash,
                ],
                entries.Select(e => e.Action).ToArray());
            Assert.Equal([lilit, lilit, lilit, lilit, aram, aram], entries.Select(e => e.StaffMemberId!.Value).ToArray());
            Assert.All(entries, e => Assert.Equal(ActivityLog.ConsoleLabel, e.DeviceLabel));
            Assert.Equal([false, false, false, false, false, true], entries.Select(e => e.IsMoney).ToArray());
            Assert.Single(entries.Select(e => e.TableSessionId).Distinct());
        }

        // The results: the bill split by the lines' worth, the shares adding up to it.
        var khachapuri = 2 * TestMenu.KhachapuriAmd;
        var wine = TestMenu.WineAmd;
        var expected = StaffPerformanceRules.Split(total, [(lilit, khachapuri), (aram, wine)], aram);

        var report = await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            Performance(db, clock, actor).GetBranchAsync(branch.BranchId, new StaffPerformanceQuery()));

        var lilitRow = report.Staff.Single(r => r.StaffMemberId == lilit);
        var aramRow = report.Staff.Single(r => r.StaffMemberId == aram);

        Assert.Equal(expected.Single(s => s.Server == lilit).Amd, lilitRow.SalesAmd);
        Assert.Equal(expected.Single(s => s.Server == aram).Amd, aramRow.SalesAmd);
        Assert.Equal(total, lilitRow.SalesAmd + aramRow.SalesAmd);
        Assert.True(lilitRow.SalesAmd < aramRow.SalesAmd);
        Assert.Equal((1, 4L, 0L, 0L, 0), (lilitRow.Tables, lilitRow.Guests, lilitRow.TipsAmd, lilitRow.CashAmd, lilitRow.CashPayments));
        Assert.Equal((1, 4L, 2_000L, total, 1), (aramRow.Tables, aramRow.Guests, aramRow.TipsAmd, aramRow.CashAmd, aramRow.CashPayments));
        Assert.Equal(lilitRow.SalesAmd, lilitRow.AverageCheckAmd);

        // The branch, counted once - one table, four guests - and the revenue report's money to the dram.
        var revenue = await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            SqlServerFixture.CreateReportQuery(db, actor).GetRevenueAsync(new ReportRequest(branch.BranchId, Today, Today)));

        Assert.Equal(1, report.Totals.Tables);
        Assert.Equal(4L, report.Totals.Guests);
        Assert.Equal(revenue.Tabs.Value, report.Totals.Tables);
        Assert.Equal(revenue.HallAmd.Value, report.Totals.SalesAmd);
        Assert.Equal(revenue.AverageTabAmd.Value, report.Totals.AverageCheckAmd);
        Assert.Equal(revenue.CashAmd.Value, report.Totals.CashAmd);
        Assert.Equal(revenue.TakeawayAmd.Value, report.Totals.TakeawayAmd);
        Assert.Equal(revenue.AdjustmentsTotalAmd.Value, report.Totals.AdjustmentsAmd);
        Assert.Equal(0L, report.Totals.UnattributedSalesAmd);
        Assert.Equal(2_000L, report.Totals.TipsAmd);

        // The timeline: the two waiters in turn, who added each line, and every entry of the sitting.
        var timeline = await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            History(db, clock, actor).TimelineAsync(branch.BranchId, ActivitySubjects.Tab, tabId));

        Assert.Equal([lilit, aram], timeline.Tab!.Servers.Select(s => s.StaffMemberId).ToArray());
        Assert.Equal(SeatedAt.AddMinutes(28), timeline.Tab.Servers[1].FromUtc);
        Assert.Equal(2_000L, timeline.Tab.TipsAmd);
        Assert.Equal(
            [(lilit, lilit), (aram, aram)],
            timeline.Lines.Select(l => (l.AddedByStaffMemberId!.Value, l.ServerStaffMemberId!.Value)).ToArray());
        Assert.Equal(6, timeline.Entries.Count);
        Assert.Equal([lilit, aram], timeline.People.Select(p => p.StaffMemberId).ToArray());

        // Aram's own page: his share on today's bar of the week, and the bill he closed on his cash list.
        var mine = await AsAsync(clock, TestActor.Waiter(aram), (db, actor) =>
            Performance(db, clock, actor).GetMineAsync(new StaffPerformanceQuery(List: "cash")));

        Assert.Equal(aramRow, mine.Kpis);
        Assert.Equal(StaffPerformanceRules.ChartDays, mine.DailySales.Count);
        Assert.Equal(new DailySales(Today, aramRow.SalesAmd, 1), mine.DailySales[^1]);
        Assert.Equal(ActivityActions.PaymentCash, Assert.Single(mine.Recent.Items).Action);
    }

    /// <summary>
    /// A void and a discount after the transfer: still the bill's total that is split, the void counted for who voided
    /// it and the discount for the manager who gave it - and the totals still the report's.
    /// </summary>
    [SkippableFact]
    public async Task Voids_and_discounts_are_whoever_gave_them_and_the_totals_stay_the_reports()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(SeatedAt);
        var (branch, menu, aram, _) = await ArrangeAsync(clock);
        var lilit = branch.WaiterId;
        var tableId = branch.FirstTableId;

        var tabId = await AsAsync(clock, TestActor.Waiter(lilit), async (db, actor) =>
        {
            await fixture.CreateService(db, clock, actor)
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));

            return (await fixture.CreateTabService(db, clock, actor)
                .OpenForStaffAsync(new OpenTabForStaffCommand(branch.BranchId, tableId, Guid.CreateVersion7()))).TabId;
        });

        await OrderAsync(clock, lilit, tabId, menu.Coffee, quantity: 3);
        var wineOrder = await OrderAsync(clock, lilit, tabId, menu.Wine, quantity: 1);

        await AsAsync(clock, TestActor.Manager(branch.ManagerId), async (db, actor) =>
        {
            var orders = fixture.CreateOrderService(db, clock, actor);
            await orders.VoidLineAsync(new VoidLineCommand(tabId, wineOrder.Lines[0].LineId, "Guest changed their mind", Guid.CreateVersion7()));
            await orders.AddAdjustmentAsync(new AddAdjustmentCommand(
                tabId, null, AdjustmentKind.Discount, 10m, null, "Regular", Guid.CreateVersion7()));

            return 0;
        });

        long total;

        await using (var db = fixture.CreateContext(clock))
        {
            total = (await fixture.CreateLedger(db, clock, TestActor.Manager(branch.ManagerId)).RecomputeTotalsAsync(tabId, default)).TotalAmd;
        }

        await AsAsync(clock, TestActor.Waiter(lilit), async (db, actor) =>
            await fixture.CreatePaymentService(db, clock, actor)
                .RecordCashAsync(new RecordCashPaymentCommand(tabId, total, null, 0L, Guid.CreateVersion7())));

        var report = await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            Performance(db, clock, actor).GetBranchAsync(branch.BranchId, new StaffPerformanceQuery()));
        var revenue = await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            SqlServerFixture.CreateReportQuery(db, actor).GetRevenueAsync(new ReportRequest(branch.BranchId, Today, Today)));

        var manager = report.Staff.Single(r => r.StaffMemberId == branch.ManagerId);
        Assert.Equal((1, 1, 0), (manager.Voids, manager.Discounts, manager.Comps));
        Assert.Equal(total, report.Staff.Single(r => r.StaffMemberId == lilit).SalesAmd);
        Assert.Equal(revenue.HallAmd.Value, report.Totals.SalesAmd);
        Assert.Equal(revenue.AdjustmentsTotalAmd.Value, report.Totals.AdjustmentsAmd);
        Assert.Equal(revenue.AdjustmentsTotalAmd.Value, manager.AdjustmentsAmd);
        Assert.Equal(revenue.CashAmd.Value, report.Totals.CashAmd);

        // Both money entries kept three years, the void with what it was and why.
        await using var verify = fixture.CreateContext(clock);
        var voided = await verify.ActivityEntries.AsNoTracking()
            .SingleAsync(e => e.BranchId == branch.BranchId && e.Action == ActivityActions.OrderLineVoided);
        Assert.True(voided.IsMoney);
        Assert.Equal("Guest changed their mind", voided.Reason);
        Assert.Equal(TestMenu.WineAmd, voided.AmountAmd);
        Assert.True(await verify.ActivityEntries.AnyAsync(
            e => e.BranchId == branch.BranchId && e.Action == ActivityActions.PaymentDiscount && e.IsMoney));
    }

    /// <summary>
    /// Medians from the call to the claim and from the guest's text to the reply. Aram claims three calls (30, 50, 70
    /// seconds: 50) and Lilit one (90); the branch's is over all four - 60, the mean of the middle two - not a median of
    /// the two medians (70). Two replies of 60 and 121 seconds give 91: half a second rounds up. A second guest text
    /// before the reply still counts from the first.
    /// </summary>
    [SkippableFact]
    public async Task Call_and_reply_medians_take_the_middle_two_of_an_even_count()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(SeatedAt);
        var (branch, _, aram, _) = await ArrangeAsync(clock);
        var lilit = branch.WaiterId;
        var tableId = branch.FirstTableId;

        var tabId = await AsAsync(clock, TestActor.Waiter(lilit), async (db, actor) =>
        {
            await fixture.CreateService(db, clock, actor)
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));

            return (await fixture.CreateTabService(db, clock, actor)
                .OpenForStaffAsync(new OpenTabForStaffCommand(branch.BranchId, tableId, Guid.CreateVersion7()))).TabId;
        });

        foreach (var (claimer, seconds, minute) in new[] { (aram, 30, 1), (aram, 50, 5), (aram, 70, 10), (lilit, 90, 15) })
        {
            var calledAt = SeatedAt.AddMinutes(minute);
            Guid requestId;

            await using (var db = fixture.CreateContext(clock))
            {
                var request = new ServiceRequest(tabId, branch.BranchId, tableId, null, ServiceRequestPreset.Water, calledAt);
                db.ServiceRequests.Add(request);
                await db.SaveChangesAsync();
                requestId = request.Id;
            }

            clock.UtcNow = calledAt.AddSeconds(seconds);

            // Aram says "I'm on it"; Lilit acknowledges hers straight away, which claims it too.
            var claimed = await AsAsync(clock, TestActor.Waiter(claimer), (db, actor) =>
                claimer == aram
                    ? fixture.CreateServiceRequests(db, clock, actor).ClaimAsync(branch.BranchId, requestId)
                    : fixture.CreateServiceRequests(db, clock, actor).AcknowledgeAsync(requestId));

            Assert.Equal(claimer, claimed.ClaimedByStaffMemberId);
            Assert.Equal(clock.UtcNow, claimed.ClaimedAtUtc);
            Assert.NotNull(claimed.ClaimedByName);

            // A second claim, by anybody, changes nothing.
            clock.UtcNow = clock.UtcNow.AddSeconds(5);
            var again = await AsAsync(clock, TestActor.Waiter(lilit), (db, actor) =>
                fixture.CreateServiceRequests(db, clock, actor).ClaimAsync(branch.BranchId, requestId));
            Assert.Equal(claimer, again.ClaimedByStaffMemberId);
        }

        // Aram's three claimed calls are still on the list, showing he has them; Lilit's, acknowledged, is not.
        var open = await AsAsync(clock, TestActor.Waiter(lilit), (db, actor) =>
            fixture.CreateServiceRequests(db, clock, actor).GetOpenAsync(branch.BranchId));
        Assert.Equal(3, open.Count);
        Assert.All(open, call => Assert.Equal("Aram Petrosyan", call.ClaimedByName));

        // A claim at another branch's route is not found.
        await AsAsync(clock, TestActor.Waiter(aram), (db, actor) =>
            Assert.ThrowsAsync<KeyNotFoundException>(() =>
                fixture.CreateServiceRequests(db, clock, actor).ClaimAsync(Guid.CreateVersion7(), open[0].ServiceRequestId)));

        // Two threads: one answered in 60 seconds, one in 121 - the guest's second text does not restart the wait.
        await using (var db = fixture.CreateContext(clock))
        {
            var first = await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, branch.TableIds[1], SeatedAt.AddHours(2));
            var second = await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, branch.TableIds[2], SeatedAt.AddHours(3));

            var at = SeatedAt.AddMinutes(20);
            var one = Conversation.ForReservation(branch.BranchId, first.Id, null, at);
            one.WriteAsGuest("Is there parking?", null, at);
            one.WriteAsVenue("Yes, behind the building.", aram, "Aram", null, at.AddSeconds(60));

            var two = Conversation.ForReservation(branch.BranchId, second.Id, null, at);
            two.WriteAsGuest("Can we bring a cake?", null, at);
            two.WriteAsGuest("It is a birthday.", null, at.AddSeconds(30));
            two.WriteAsVenue("Of course.", aram, "Aram", null, at.AddSeconds(121));

            // A venue message nobody was waiting for answers nothing.
            two.WriteAsVenue("See you at eight.", aram, "Aram", null, at.AddSeconds(200));

            db.Conversations.AddRange(one, two);
            await db.SaveChangesAsync();
        }

        clock.UtcNow = SeatedAt.AddHours(1);

        var report = await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            Performance(db, clock, actor).GetBranchAsync(branch.BranchId, new StaffPerformanceQuery()));

        var aramRow = report.Staff.Single(r => r.StaffMemberId == aram);
        var lilitRow = report.Staff.Single(r => r.StaffMemberId == lilit);

        Assert.Equal((3, 50), (aramRow.CallsClaimed, aramRow.CallClaimMedianSeconds!.Value));
        Assert.Equal((1, 90), (lilitRow.CallsClaimed, lilitRow.CallClaimMedianSeconds!.Value));
        Assert.Equal((4, 60), (report.Totals.CallsClaimed, report.Totals.CallClaimMedianSeconds!.Value));

        Assert.Equal((2, 91), (aramRow.MessagesAnswered, aramRow.MessageReplyMedianSeconds!.Value));
        Assert.Null(lilitRow.MessageReplyMedianSeconds);
        Assert.Equal((2, 91), (report.Totals.MessagesAnswered, report.Totals.MessageReplyMedianSeconds!.Value));

        // The claims are on Aram's calls list, newest first, each with its seconds.
        var page = await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            Performance(db, clock, actor).GetPersonAsync(branch.BranchId, aram, new StaffPerformanceQuery(List: "calls", Limit: "2")));

        Assert.Equal([70, 50], page.Recent.Items.Select(i => i.Seconds!.Value).ToArray());
        Assert.NotNull(page.Recent.NextCursor);

        var rest = await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            Performance(db, clock, actor).GetPersonAsync(
                branch.BranchId, aram, new StaffPerformanceQuery(List: "calls", Limit: "2", Cursor: page.Recent.NextCursor)));

        Assert.Equal([30], rest.Recent.Items.Select(i => i.Seconds!.Value).ToArray());
        Assert.Null(rest.Recent.NextCursor);
    }

    /// <summary>
    /// Who may transfer, and to whom: the target must serve - not the kitchen - and work at this branch; a paid, closed
    /// bill has nobody to hand over; a manager may hand anybody's table.
    /// </summary>
    [SkippableFact]
    public async Task A_transfer_goes_only_to_somebody_who_serves_here_and_only_while_the_party_sits()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(SeatedAt);
        var (branch, menu, aram, cook) = await ArrangeAsync(clock);
        var lilit = branch.WaiterId;
        var tableId = branch.FirstTableId;
        Guid elsewhere;

        await using (var db = fixture.CreateContext(clock))
        {
            var other = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
            var stranger = new StaffMember(branch.VenueId, "Other Branch Waiter", $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}", StaffRole.Waiter, "hash", other);
            db.StaffMembers.Add(stranger);
            await db.SaveChangesAsync();
            elsewhere = stranger.Id;
        }

        var tabId = await AsAsync(clock, TestActor.Waiter(lilit), async (db, actor) =>
        {
            await fixture.CreateService(db, clock, actor)
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));

            return (await fixture.CreateTabService(db, clock, actor)
                .OpenForStaffAsync(new OpenTabForStaffCommand(branch.BranchId, tableId, Guid.CreateVersion7()))).TabId;
        });

        foreach (var (target, why) in new[]
                 {
                     (cook, TransferTargetIneligibleException.DoesNotServe),
                     (elsewhere, TransferTargetIneligibleException.OtherBranch),
                     (Guid.CreateVersion7(), TransferTargetIneligibleException.NotFound),
                 })
        {
            var refused = await AsAsync(clock, TestActor.Waiter(lilit), (db, actor) =>
                Assert.ThrowsAsync<TransferTargetIneligibleException>(() =>
                    Transfer(db, clock, actor).TransferAsync(new TransferTableCommand(tabId, target))));

            Assert.Equal(why, refused.Why);
        }

        // A manager hands Lilit's table to Aram.
        var moved = await AsAsync(clock, TestActor.Manager(branch.ManagerId), (db, actor) =>
            Transfer(db, clock, actor).TransferAsync(new TransferTableCommand(tabId, aram)));
        Assert.Equal(aram, moved.ServerStaffMemberId);

        // The floor shows him, and the branch stream says so.
        await using (var db = fixture.CreateContext(clock))
        {
            var floor = (await fixture.CreateFloorQuery(db, clock).GetFloorStateAsync(branch.BranchId, clock.UtcNow))!;
            var table = floor.Tables.Single(t => t.TableId == tableId);
            Assert.Equal(aram, table.ServerStaffMemberId);
            Assert.Equal("Aram Petrosyan", table.ServerName);
            Assert.True(await db.TableStateChanges.AnyAsync(c => c.TabId == tabId && c.Signal == TableChangeSignal.TableTransferred));
        }

        // Paid and closed: nothing to hand over.
        await OrderAsync(clock, aram, tabId, menu.Coffee, quantity: 1);
        long total;

        await using (var db = fixture.CreateContext(clock))
        {
            total = (await fixture.CreateLedger(db, clock, TestActor.Waiter(aram)).RecomputeTotalsAsync(tabId, default)).TotalAmd;
        }

        await AsAsync(clock, TestActor.Waiter(aram), async (db, actor) =>
            await fixture.CreatePaymentService(db, clock, actor)
                .RecordCashAsync(new RecordCashPaymentCommand(tabId, total, null, 0L, Guid.CreateVersion7())));

        await AsAsync(clock, TestActor.Waiter(aram), (db, actor) =>
            Assert.ThrowsAsync<TabNotTransferableException>(() =>
                Transfer(db, clock, actor).TransferAsync(new TransferTableCommand(tabId, lilit))));
    }

    /// <summary>
    /// The daily sweep: an ordinary entry goes at a year and a day, a money entry is kept until three years and a day.
    /// </summary>
    [SkippableFact]
    public async Task The_sweep_keeps_money_three_years_and_everything_else_one()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var now = new DateTime(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(now);
        var venueId = Guid.CreateVersion7();
        var branchId = Guid.CreateVersion7();

        ActivityEntry Entry(int daysAgo, bool money) => new(
            venueId,
            branchId,
            now.AddDays(-daysAgo),
            ActivityActorKind.System,
            null,
            null,
            null,
            null,
            money ? ActivityCategory.Payment : ActivityCategory.Table,
            money ? ActivityActions.PaymentCash : ActivityActions.TableSeated,
            ActivitySubjects.Tab,
            Guid.CreateVersion7(),
            "{}",
            isMoney: money);

        var kept = new[] { Entry(364, money: false), Entry(366, money: true), Entry(1_094, money: true) };
        var gone = new[] { Entry(366, money: false), Entry(1_096, money: true) };

        await using (var db = fixture.CreateContext(clock))
        {
            db.ActivityEntries.AddRange(kept);
            db.ActivityEntries.AddRange(gone);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext(clock))
        {
            Assert.True(await new ActivityRetention(db, clock).PurgeAsync() >= gone.Length);
        }

        await using var verify = fixture.CreateContext(clock);
        var left = await verify.ActivityEntries.AsNoTracking().Where(e => e.BranchId == branchId).Select(e => e.Id).ToListAsync();

        Assert.Equal(kept.Select(e => e.Id).Order(), left.Order());
    }

    /// <summary>
    /// The results, the History's page and a bill's timeline cost the same number of statements for one bill as for
    /// three with twice the lines: a fixed read per source, never one per bill, line or person.
    /// </summary>
    [SkippableFact]
    public async Task The_reads_cost_the_same_statements_however_many_bills_and_people()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(SeatedAt);
        var (small, smallMenu, smallAram, _) = await ArrangeAsync(clock);
        var (large, largeMenu, largeAram, _) = await ArrangeAsync(clock);

        var smallTab = await SettleAsync(clock, small, small.TableIds[0], small.WaiterId, smallAram, smallMenu, lines: 1);

        Guid largeTab = Guid.Empty;

        foreach (var tableId in large.TableIds)
        {
            largeTab = await SettleAsync(clock, large, tableId, large.WaiterId, largeAram, largeMenu, lines: 2);
        }

        async Task<int> CountAsync(TestBranch branch, Guid tabId)
        {
            var counter = new CommandCountingInterceptor();
            await using var db = fixture.CreateContext(clock, counter);
            var actor = TestActor.Manager(branch.ManagerId);

            await Performance(db, clock, actor).GetBranchAsync(branch.BranchId, new StaffPerformanceQuery());
            await Performance(db, clock, actor).GetPersonAsync(branch.BranchId, branch.WaiterId, new StaffPerformanceQuery());
            await History(db, clock, actor).ListAsync(branch.BranchId, new ActivityFeedQuery());
            await History(db, clock, actor).TimelineAsync(branch.BranchId, ActivitySubjects.Tab, tabId);

            return counter.Count;
        }

        Assert.Equal(await CountAsync(small, smallTab), await CountAsync(large, largeTab));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A table seated and opened by one waiter, handed to another, ordered on, and paid in cash.</summary>
    private async Task<Guid> SettleAsync(
        TestClock clock, TestBranch branch, Guid tableId, Guid first, Guid second, TestMenu menu, int lines)
    {
        var tabId = await AsAsync(clock, TestActor.Waiter(first), async (db, actor) =>
        {
            await fixture.CreateService(db, clock, actor)
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));

            return (await fixture.CreateTabService(db, clock, actor)
                .OpenForStaffAsync(new OpenTabForStaffCommand(branch.BranchId, tableId, Guid.CreateVersion7()))).TabId;
        });

        for (var i = 0; i < lines; i++)
        {
            await OrderAsync(clock, first, tabId, menu.Coffee, quantity: 1);
        }

        await AsAsync(clock, TestActor.Waiter(first), (db, actor) =>
            Transfer(db, clock, actor).TransferAsync(new TransferTableCommand(tabId, second)));

        for (var i = 0; i < lines; i++)
        {
            await OrderAsync(clock, second, tabId, menu.Khachapuri, quantity: 1);
        }

        long total;

        await using (var db = fixture.CreateContext(clock))
        {
            total = (await fixture.CreateLedger(db, clock, TestActor.Waiter(second)).RecomputeTotalsAsync(tabId, default)).TotalAmd;
        }

        await AsAsync(clock, TestActor.Waiter(second), async (db, actor) =>
            await fixture.CreatePaymentService(db, clock, actor)
                .RecordCashAsync(new RecordCashPaymentCommand(tabId, total, null, 500L, Guid.CreateVersion7())));

        return tabId;
    }

    /// <summary>A branch, its menu, a second waiter (Aram) and a cook.</summary>
    private async Task<(TestBranch Branch, TestMenu Menu, Guid Aram, Guid Cook)> ArrangeAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
        var unique = Random.Shared.Next(1_000_000, 9_999_999);

        var aram = new StaffMember(branch.VenueId, "Aram Petrosyan", $"+3747{unique}", StaffRole.Waiter, "hash", branch.BranchId);
        var cook = new StaffMember(branch.VenueId, "Gor Melkonyan", $"+3748{unique}", StaffRole.Kitchen, "hash", branch.BranchId);
        db.StaffMembers.AddRange(aram, cook);
        await db.SaveChangesAsync();

        return (branch, menu, aram.Id, cook.Id);
    }

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

    private TableTransferService Transfer(YallaDbContext db, TestClock clock, TestActor actor) =>
        new(db, clock, actor, fixture.CreateBranchGuard(db, actor), SqlServerFixture.CreateActivityLog(db, clock, actor),
            NullLogger<TableTransferService>.Instance);

    private StaffPerformanceReader Performance(YallaDbContext db, TestClock clock, TestActor actor) =>
        new(db, clock, actor, fixture.CreateBranchGuard(db, actor));

    private ActivityHistoryQuery History(YallaDbContext db, TestClock clock, TestActor actor) =>
        new(db, clock, fixture.CreateBranchGuard(db, actor));
}
