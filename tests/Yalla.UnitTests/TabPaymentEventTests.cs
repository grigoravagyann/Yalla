using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests;

/// <summary>
/// What the money on a tab raises (<c>docs/domain-events.md</c>): a payment taken, the bill settled with its sitting, a
/// write-off, a discount or comp given and reversed - and that a refusal raises nothing.
/// </summary>
public class TabPaymentEventTests
{
    private static readonly DateTime OpenedAt = new(2026, 9, 4, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PaidAt = OpenedAt.AddMinutes(50);
    private static readonly Guid Waiter = Guid.CreateVersion7();
    private static readonly Guid Manager = Guid.CreateVersion7();

    [Fact]
    public void A_part_payment_is_recorded_locks_the_split_and_leaves_the_tab_open()
    {
        var (tab, sitting) = NewTab();
        var payment = Paid(tab, 4_000L);
        tab.ApplyComputedTotals(subtotalAmd: 10_000L, serviceChargeAmd: 1_000L, paidAmd: 4_000L);

        var (closed, sittingClosed) = tab.TakePayment(payment, PaidAt, sitting);

        Assert.False(closed);
        Assert.False(sittingClosed);
        Assert.Equal(TabStatus.Open, tab.Status);
        Assert.Equal(PaidAt, tab.SettlementModeLockedAtUtc);
        Assert.True(sitting.IsOpen);

        var recorded = Assert.IsType<TabPaymentRecorded>(Assert.Single(tab.DequeueEvents()).Event);
        Assert.Same(tab, recorded.Tab);
        Assert.Same(payment, recorded.Payment);
        Assert.False(recorded.SettledTheBill);
        Assert.Equal(PaidAt, recorded.OccurredAtUtc);
    }

    [Fact]
    public void Paying_the_rest_records_the_payment_then_settles_and_closes_the_sitting_not_the_table()
    {
        var (tab, sitting) = NewTab();
        var payment = Paid(tab, 11_000L);
        tab.ApplyComputedTotals(subtotalAmd: 10_000L, serviceChargeAmd: 1_000L, paidAmd: 11_000L);

        var (closed, sittingClosed) = tab.TakePayment(payment, PaidAt, sitting);

        Assert.True(closed);
        Assert.True(sittingClosed);
        Assert.Equal(TabStatus.Closed, tab.Status);
        Assert.Equal(PaidAt, tab.ClosedAtUtc);
        Assert.Equal(PaidAt, sitting.ClosedAtUtc);

        var raised = tab.DequeueEvents();
        Assert.Collection(
            raised,
            e => Assert.True(Assert.IsType<TabPaymentRecorded>(e.Event).SettledTheBill),
            e => Assert.True(Assert.IsType<TabSettled>(e.Event).SittingClosed));
        Assert.True(raised[0].Order < raised[1].Order);
    }

    [Fact]
    public void Settling_after_the_table_was_freed_says_the_sitting_was_already_closed()
    {
        var (tab, sitting) = NewTab();
        sitting.Close(PaidAt.AddMinutes(-5));
        var payment = Paid(tab, 11_000L);
        tab.ApplyComputedTotals(subtotalAmd: 10_000L, serviceChargeAmd: 1_000L, paidAmd: 11_000L);

        var (closed, sittingClosed) = tab.TakePayment(payment, PaidAt, sitting);

        Assert.True(closed);
        Assert.False(sittingClosed);
        Assert.False(Assert.IsType<TabSettled>(tab.DequeueEvents()[^1].Event).SittingClosed);
    }

    [Fact]
    public void A_closed_tab_or_a_payment_that_did_not_go_through_is_refused_and_raises_nothing()
    {
        var (tab, sitting) = NewTab();
        var reserved = Payment.Reserve(tab.Id, 1_000L, PaymentMethod.Cash, PaidAt);

        Assert.Throws<ArgumentException>(() => tab.TakePayment(reserved, PaidAt, sitting));

        tab.Close(PaidAt);

        Assert.Throws<DomainStateException>(() => tab.TakePayment(Paid(tab, 1_000L), PaidAt, sitting));
        Assert.Empty(tab.DequeueEvents());
        Assert.Null(tab.SettlementModeLockedAtUtc);
    }

    [Fact]
    public void A_write_off_raises_what_was_still_owed_and_a_second_one_is_refused()
    {
        var (tab, _) = NewTab();
        tab.ApplyComputedTotals(subtotalAmd: 10_000L, serviceChargeAmd: 1_000L, paidAmd: 4_000L);

        tab.WriteOff(PaidAt, Manager, "walked out");

        Assert.Equal(TabStatus.Abandoned, tab.Status);
        var writtenOff = Assert.IsType<TabWrittenOff>(Assert.Single(tab.DequeueEvents()).Event);
        Assert.Equal(7_000L, writtenOff.WrittenOffAmd);
        Assert.Equal("walked out", writtenOff.Reason);
        Assert.Equal(Manager, writtenOff.ByStaffId);
        Assert.Equal(PaidAt, writtenOff.OccurredAtUtc);

        Assert.Throws<DomainStateException>(() => tab.WriteOff(PaidAt, Manager, "again"));
        Assert.Empty(tab.DequeueEvents());
    }

    [Fact]
    public void A_discount_given_and_reversed_raises_each_once()
    {
        var tabId = Guid.CreateVersion7();
        var discount = TabAdjustment.ByPercent(tabId, null, AdjustmentKind.Discount, 10m, "birthday", Manager, PaidAt);

        var added = Assert.IsType<TabAdjustmentAdded>(Assert.Single(discount.DequeueEvents()).Event);
        Assert.Same(discount, added.Adjustment);
        Assert.Equal(PaidAt, added.OccurredAtUtc);

        discount.Void(PaidAt.AddMinutes(1), Manager);

        var reversed = Assert.IsType<TabAdjustmentReversed>(Assert.Single(discount.DequeueEvents()).Event);
        Assert.Same(discount, reversed.Adjustment);
        Assert.Equal(PaidAt.AddMinutes(1), reversed.OccurredAtUtc);

        Assert.Throws<DomainStateException>(() => discount.Void(PaidAt.AddMinutes(2), Manager));
        Assert.Empty(discount.DequeueEvents());
    }

    [Fact]
    public void A_comp_on_a_line_raises_its_addition()
    {
        var comp = TabAdjustment.ByAmount(
            Guid.CreateVersion7(), Guid.CreateVersion7(), AdjustmentKind.Comp, 1_500L, "came cold", Manager, PaidAt);

        Assert.IsType<TabAdjustmentAdded>(Assert.Single(comp.DequeueEvents()).Event);
    }

    private static Payment Paid(Tab tab, long amountAmd)
    {
        var payment = Payment.Reserve(tab.Id, amountAmd, PaymentMethod.Cash, PaidAt);
        payment.MarkSucceeded(PaidAt);
        payment.TakenBy(Waiter, Waiter);

        return payment;
    }

    private static (Tab Tab, TableSession Sitting) NewTab()
    {
        var sitting = TableSession.SeatWalkIn(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, OpenedAt, Waiter);
        var tab = new Tab(
            branchId: sitting.BranchId,
            diningTableId: sitting.DiningTableId,
            tableSessionId: sitting.Id,
            openedAtUtc: OpenedAt,
            serviceChargePercentSnapshot: 10m,
            admittedBecause: TabAdmissionReason.OpenedByStaff);

        return (tab, sitting);
    }
}
