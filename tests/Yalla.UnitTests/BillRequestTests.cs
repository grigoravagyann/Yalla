using Yalla.Domain;
using Yalla.Domain.Billing;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests;

/// <summary>
/// Pricing a request for the bill from the diner's pay sheet, and the shape such a request may take.
/// </summary>
/// <remarks>
/// Priced off the documented three-person example in <c>docs/billing.md</c> - subtotal 23,200, service
/// 2,320, total 25,520; shares 10,525 / 8,433 / 6,562 on personal parts of 9,568 / 7,666 / 5,966 - so
/// every number here can be checked against a page a person can read.
/// </remarks>
public sealed class BillRequestTests
{
    private static readonly Guid Host = new("00000000-0000-0000-0000-000000000001");

    private static readonly Guid GuestA = new("00000000-0000-0000-0000-000000000002");

    private static readonly Guid GuestB = new("00000000-0000-0000-0000-000000000003");

    private static readonly DateTime Now = new(2026, 10, 17, 19, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ the price

    [Fact]
    public void The_whole_bill_is_the_tabs_remaining_balance_with_the_tip_on_the_subtotal()
    {
        var quote = BillQuote.For(Bill(), GuestA, BillScope.WholeBill, tipPercent: 10);

        Assert.Equal(BillScope.WholeBill, quote.Scope);
        Assert.Equal(25_520L, quote.AmountAmd);
        Assert.Equal(10, quote.TipPercent);

        // On the items, before service: a tip on the service charge is a tip on a tip.
        Assert.Equal(2_320L, quote.TipAmd);
    }

    [Fact]
    public void My_share_is_the_askers_own_share_with_the_tip_on_their_part_of_the_subtotal()
    {
        var quote = BillQuote.For(Bill(), GuestA, BillScope.MyShare, tipPercent: 5);

        Assert.Equal(8_433L, quote.AmountAmd);

        // 5% of 7,666 is 383.3: whole dram, half-up, once.
        Assert.Equal(383L, quote.TipAmd);
    }

    [Fact]
    public void A_tip_rounds_half_up_like_every_other_amount()
    {
        // 15% of 5,966 is 894.9.
        var guestB = BillQuote.For(Bill(), GuestB, BillScope.MyShare, tipPercent: 15);
        Assert.Equal(6_562L, guestB.AmountAmd);
        Assert.Equal(895L, guestB.TipAmd);

        // 5% of 9,568 is 478.4.
        Assert.Equal(478L, BillQuote.For(Bill(), Host, BillScope.MyShare, tipPercent: 5).TipAmd);
    }

    [Fact]
    public void What_has_been_paid_comes_off_the_amount_and_not_off_the_tip()
    {
        var bill = Bill(paidByGuestA: 3_000L);

        var mine = BillQuote.For(bill, GuestA, BillScope.MyShare, tipPercent: 10);
        Assert.Equal(5_433L, mine.AmountAmd);
        Assert.Equal(767L, mine.TipAmd);

        var whole = BillQuote.For(bill, Host, BillScope.WholeBill, tipPercent: 10);
        Assert.Equal(22_520L, whole.AmountAmd);
        Assert.Equal(2_320L, whole.TipAmd);
    }

    [Fact]
    public void No_tip_asked_is_no_tip_line_and_a_zero_tip_is_a_zero_one()
    {
        var none = BillQuote.For(Bill(), GuestA, BillScope.MyShare, tipPercent: null);
        Assert.Null(none.TipPercent);
        Assert.Null(none.TipAmd);

        var zero = BillQuote.For(Bill(), GuestA, BillScope.MyShare, tipPercent: 0);
        Assert.Equal(0, zero.TipPercent);
        Assert.Equal(0L, zero.TipAmd);
    }

    /// <summary>
    /// Cash a waiter took against the table, naming nobody, lowers the balance without lowering any
    /// share. The waiter must not be sent for more than the tab owes.
    /// </summary>
    [Fact]
    public void My_share_is_never_more_than_the_tab_still_owes()
    {
        var bill = Bill(paidAgainstTheTable: 20_000L);

        Assert.Equal(5_520L, bill.RemainingAmd);
        Assert.Equal(5_520L, BillQuote.For(bill, Host, BillScope.MyShare, tipPercent: null).AmountAmd);
        Assert.Equal(5_520L, BillQuote.For(bill, Host, BillScope.WholeBill, tipPercent: null).AmountAmd);
    }

    [Fact]
    public void A_share_already_paid_leaves_nothing_to_ask_for_and_is_refused_on_bill_scope()
    {
        var bill = Bill(paidByGuestA: 8_433L);

        var refused = Assert.Throws<FieldValidationException>(
            () => BillQuote.For(bill, GuestA, BillScope.MyShare, tipPercent: 10));

        var violation = Assert.Single(refused.Violations);
        Assert.Equal("billScope", violation.Field);
        Assert.Equal(BillQuote.NothingLeftToPay, violation.Message);
        Assert.Equal((int)BillScope.MyShare, violation.Value);

        // The rest of the table still owes, so the whole bill is still a thing to bring.
        Assert.Equal(17_087L, BillQuote.For(bill, GuestA, BillScope.WholeBill, tipPercent: null).AmountAmd);
    }

    [Fact]
    public void A_settled_tab_has_no_bill_to_bring_in_either_scope()
    {
        var bill = Bill(paidAgainstTheTable: 25_520L);

        Assert.Equal("billScope", Assert.Throws<FieldValidationException>(
            () => BillQuote.For(bill, Host, BillScope.WholeBill, tipPercent: null)).Field);
        Assert.Equal("billScope", Assert.Throws<FieldValidationException>(
            () => BillQuote.For(bill, Host, BillScope.MyShare, tipPercent: null)).Field);
    }

    [Fact]
    public void Somebody_with_no_share_on_the_bill_has_nothing_of_their_own_to_pay()
    {
        Assert.Equal("billScope", Assert.Throws<FieldValidationException>(
            () => BillQuote.For(Bill(), Guid.CreateVersion7(), BillScope.MyShare, tipPercent: null)).Field);
    }

    // ------------------------------------------------------------ the shape

    [Fact]
    public void Bill_fields_on_a_request_that_is_not_for_the_bill_are_refused_together()
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => ServiceRequest.ValidateBillFields(ServiceRequestPreset.Water, BillScope.WholeBill, 10));

        Assert.Equal(["billScope", "tipPercent"], refused.Violations.Select(v => v.Field));
        Assert.All(refused.Violations, v => Assert.Equal(FieldBounds.Conflict, v.Bound));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(26)]
    [InlineData(100)]
    public void A_tip_outside_0_to_25_is_refused_with_its_bounds(int tipPercent)
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => ServiceRequest.ValidateBillFields(ServiceRequestPreset.TheBill, BillScope.MyShare, tipPercent));

        var violation = Assert.Single(refused.Violations);
        Assert.Equal("tipPercent", violation.Field);
        Assert.Equal(FieldBounds.Range, violation.Bound);
        Assert.Equal(0, violation.Min);
        Assert.Equal(25, violation.Max);
        Assert.Equal(tipPercent, violation.Value);
    }

    [Fact]
    public void A_tip_with_no_scope_to_take_it_from_is_refused_on_the_scope()
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => ServiceRequest.ValidateBillFields(ServiceRequestPreset.TheBill, null, 10));

        Assert.Equal("billScope", Assert.Single(refused.Violations).Field);
    }

    [Fact]
    public void A_scope_that_is_not_one_is_refused()
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => ServiceRequest.ValidateBillFields(ServiceRequestPreset.TheBill, (BillScope)9, null));

        Assert.Equal("billScope", Assert.Single(refused.Violations).Field);
    }

    [Theory]
    [InlineData(ServiceRequestPreset.Napkins, null, null)]
    [InlineData(ServiceRequestPreset.TheBill, null, null)]
    [InlineData(ServiceRequestPreset.TheBill, BillScope.WholeBill, null)]
    [InlineData(ServiceRequestPreset.TheBill, BillScope.MyShare, 0)]
    [InlineData(ServiceRequestPreset.TheBill, BillScope.MyShare, 25)]
    public void The_shapes_a_request_may_take_pass(ServiceRequestPreset preset, BillScope? scope, int? tipPercent)
    {
        ServiceRequest.ValidateBillFields(preset, scope, tipPercent);
    }

    // ------------------------------------------------------------ the request carries the price

    [Fact]
    public void A_request_for_the_bill_stores_the_quote_and_any_other_request_refuses_one()
    {
        var quote = new BillQuote(BillScope.MyShare, 8_433L, 5, 383L);

        var request = new ServiceRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), GuestA,
            ServiceRequestPreset.TheBill, Now, bill: quote);

        Assert.Equal(BillScope.MyShare, request.BillScope);
        Assert.Equal(8_433L, request.BillAmountAmd);
        Assert.Equal(5, request.TipPercent);
        Assert.Equal(383L, request.TipAmd);

        var plain = new ServiceRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), GuestA,
            ServiceRequestPreset.TheBill, Now);

        Assert.Null(plain.BillScope);
        Assert.Null(plain.BillAmountAmd);
        Assert.Null(plain.TipPercent);
        Assert.Null(plain.TipAmd);

        Assert.Throws<ArgumentException>(() => new ServiceRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), GuestA,
            ServiceRequestPreset.Water, Now, bill: quote));

        Assert.Throws<ArgumentException>(() => new ServiceRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), GuestA,
            ServiceRequestPreset.TheBill, Now, bill: quote with { TipAmd = null }));
    }

    // ------------------------------------------------------------ helpers

    /// <summary>The documented example, with whatever has been paid against it.</summary>
    private static TabBill Bill(long paidByGuestA = 0L, long paidAgainstTheTable = 0L)
    {
        var wine = Guid.CreateVersion7();

        List<BillingLine> lines =
        [
            new(Guid.CreateVersion7(), Host, 3_200, 2, false, false, []),
            new(Guid.CreateVersion7(), GuestA, 4_500, 1, false, false, []),
            new(Guid.CreateVersion7(), GuestB, 2_800, 1, false, false, []),
            new(wine, null, 9_500, 1, false, true, [Host, GuestA, GuestB]),
        ];

        List<BillingParticipant> participants =
        [
            new(Host, IsHost: true, ParticipantStatus.Approved),
            new(GuestA, IsHost: false, ParticipantStatus.Approved, PaidAmd: paidByGuestA),
            new(GuestB, IsHost: false, ParticipantStatus.Approved),
        ];

        return TabBilling.Compute(
            lines, [], participants, serviceChargePercent: 10m, paidAmd: paidByGuestA + paidAgainstTheTable);
    }
}
