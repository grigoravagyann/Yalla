using Yalla.Domain;
using Yalla.Domain.Billing;
using Yalla.Domain.Common;
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
        var quote = BillQuote.For(Bill(), GuestA, BillScope.WholeBill, tipPercent: 10, canSeeTableTotal: true);

        Assert.Equal(BillScope.WholeBill, quote.Scope);
        Assert.Equal(25_520L, quote.AmountAmd);
        Assert.Equal(10, quote.TipPercent);

        // On the items, before service: a tip on the service charge is a tip on a tip.
        Assert.Equal(2_320L, quote.TipAmd);
    }

    [Fact]
    public void My_share_is_the_askers_own_share_with_the_tip_on_their_part_of_the_subtotal()
    {
        var quote = BillQuote.For(Bill(), GuestA, BillScope.MyShare, tipPercent: 5, canSeeTableTotal: true);

        Assert.Equal(8_433L, quote.AmountAmd);

        // 5% of 7,666 is 383.3: whole dram, half-up, once.
        Assert.Equal(383L, quote.TipAmd);
    }

    [Fact]
    public void A_tip_rounds_half_up_like_every_other_amount()
    {
        // 15% of 5,966 is 894.9.
        var guestB = BillQuote.For(Bill(), GuestB, BillScope.MyShare, tipPercent: 15, canSeeTableTotal: true);
        Assert.Equal(6_562L, guestB.AmountAmd);
        Assert.Equal(895L, guestB.TipAmd);

        // 5% of 9,568 is 478.4.
        Assert.Equal(
            478L,
            BillQuote.For(Bill(), Host, BillScope.MyShare, tipPercent: 5, canSeeTableTotal: true).TipAmd);
    }

    /// <summary>
    /// With nothing paid the amount is the whole of what the items come to with service, so the
    /// pro-rata base is the items themselves - to the dram, for every person, scope and tip the
    /// sheet offers. The client works the same figure out from the same rule; a dram of drift here
    /// is a pay sheet that disagrees with the waiter.
    /// </summary>
    [Fact]
    public void With_nothing_paid_the_tip_is_on_the_items_exactly()
    {
        var bill = Bill();

        for (var percent = 0; percent <= BillQuote.MaxTipPercent; percent++)
        {
            Assert.Equal(
                Money.PercentOf(bill.SubtotalAmd, percent),
                BillQuote.For(bill, Host, BillScope.WholeBill, percent, canSeeTableTotal: true).TipAmd);

            foreach (var share in bill.Shares)
            {
                foreach (var sees in new[] { true, false })
                {
                    var quote = BillQuote.For(bill, share.ParticipantId, BillScope.MyShare, percent, sees);

                    Assert.Equal(share.ShareAmd, quote.AmountAmd);
                    Assert.Equal(Money.PercentOf(share.PersonalAmd, percent), quote.TipAmd);
                }
            }
        }
    }

    /// <summary>
    /// A tip is on what is still being paid for. Guest A settled 3,000 of their 8,433, so the 5,433
    /// left carries the items in it - 7,666 x 5,433 / 8,433 = 4,938.9 - and not the coffee that is
    /// already paid for. The whole bill likewise: 23,200 x 22,520 / 25,520 = 20,472.7.
    /// </summary>
    [Fact]
    public void What_has_been_paid_comes_off_the_amount_and_off_the_tips_base()
    {
        var bill = Bill(paidByGuestA: 3_000L);

        var mine = BillQuote.For(bill, GuestA, BillScope.MyShare, tipPercent: 10, canSeeTableTotal: true);
        Assert.Equal(5_433L, mine.AmountAmd);
        Assert.Equal(494L, mine.TipAmd);

        var whole = BillQuote.For(bill, Host, BillScope.WholeBill, tipPercent: 10, canSeeTableTotal: true);
        Assert.Equal(22_520L, whole.AmountAmd);
        Assert.Equal(2_047L, whole.TipAmd);
    }

    /// <summary>
    /// Several payments, some named and some against the table: guest B paid 2,000 of their own and
    /// the waiter took 5,000 naming nobody. Each scope's tip follows its own unpaid part.
    /// </summary>
    [Fact]
    public void Partial_payments_scale_each_scopes_tip_by_what_it_still_asks_for()
    {
        var bill = Bill(paidByGuestB: 2_000L, paidAgainstTheTable: 5_000L);

        // 25,520 less 7,000; 23,200 x 18,520 / 25,520 = 16,836.4; 15% is 2,525.4.
        var whole = BillQuote.For(bill, GuestB, BillScope.WholeBill, tipPercent: 15, canSeeTableTotal: true);
        Assert.Equal(18_520L, whole.AmountAmd);
        Assert.Equal(2_525L, whole.TipAmd);

        // 6,562 less 2,000; 5,966 x 4,562 / 6,562 = 4,147.6; 15% is 622.1.
        var mine = BillQuote.For(bill, GuestB, BillScope.MyShare, tipPercent: 15, canSeeTableTotal: true);
        Assert.Equal(4_562L, mine.AmountAmd);
        Assert.Equal(622L, mine.TipAmd);

        // The table's 5,000 lowered nobody's share: guest A still owes all of theirs.
        var guestA = BillQuote.For(bill, GuestA, BillScope.MyShare, tipPercent: 5, canSeeTableTotal: true);
        Assert.Equal(8_433L, guestA.AmountAmd);
        Assert.Equal(383L, guestA.TipAmd);
    }

    [Fact]
    public void No_tip_asked_is_no_tip_line_and_a_zero_tip_is_a_zero_one()
    {
        var none = BillQuote.For(Bill(), GuestA, BillScope.MyShare, tipPercent: null, canSeeTableTotal: true);
        Assert.Null(none.TipPercent);
        Assert.Null(none.TipAmd);

        var zero = BillQuote.For(Bill(), GuestA, BillScope.MyShare, tipPercent: 0, canSeeTableTotal: true);
        Assert.Equal(0, zero.TipPercent);
        Assert.Equal(0L, zero.TipAmd);
    }

    /// <summary>
    /// Cash a waiter took against the table, naming nobody, lowers the balance without lowering any
    /// share. Somebody who can see the total is never sent for more than the tab owes - and the tip
    /// shrinks with the amount: 9,568 x 5,520 / 10,525 = 5,018.1.
    /// </summary>
    [Fact]
    public void My_share_is_never_more_than_the_tab_still_owes_for_somebody_who_sees_the_total()
    {
        var bill = Bill(paidAgainstTheTable: 20_000L);

        Assert.Equal(5_520L, bill.RemainingAmd);

        var mine = BillQuote.For(bill, Host, BillScope.MyShare, tipPercent: 10, canSeeTableTotal: true);
        Assert.Equal(5_520L, mine.AmountAmd);
        Assert.Equal(502L, mine.TipAmd);

        Assert.Equal(
            5_520L,
            BillQuote.For(bill, Host, BillScope.WholeBill, tipPercent: null, canSeeTableTotal: true).AmountAmd);
    }

    /// <summary>
    /// The cap would quote a guest the table's balance whenever it was the smaller number - and the
    /// balance is what the host hid from them. So their quote is their own share, whatever the table
    /// owes; a waiter's keypad still refuses more than the tab owes when the cash is taken.
    /// </summary>
    [Fact]
    public void A_guest_who_cannot_see_the_total_is_quoted_their_own_share_uncapped()
    {
        var bill = Bill(paidAgainstTheTable: 20_000L);

        var mine = BillQuote.For(bill, GuestA, BillScope.MyShare, tipPercent: 10, canSeeTableTotal: false);

        // All of guest A's 8,433, though the table owes 5,520; the tip on all of their 7,666.
        Assert.Equal(8_433L, mine.AmountAmd);
        Assert.Equal(767L, mine.TipAmd);
        Assert.NotEqual(bill.RemainingAmd, mine.AmountAmd);
    }

    [Fact]
    public void The_whole_bill_is_not_priced_for_somebody_who_may_not_see_the_total()
    {
        Assert.Throws<ArgumentException>(
            () => BillQuote.For(Bill(), GuestA, BillScope.WholeBill, tipPercent: null, canSeeTableTotal: false));
    }

    [Fact]
    public void A_share_already_paid_leaves_nothing_to_ask_for_and_is_refused_on_bill_scope()
    {
        var bill = Bill(paidByGuestA: 8_433L);

        var refused = Assert.Throws<FieldValidationException>(
            () => BillQuote.For(bill, GuestA, BillScope.MyShare, tipPercent: 10, canSeeTableTotal: true));

        var violation = Assert.Single(refused.Violations);
        Assert.Equal("billScope", violation.Field);
        Assert.Equal(BillQuote.NothingLeftToPay, violation.Message);
        Assert.Equal((int)BillScope.MyShare, violation.Value);

        // Refused the same way whether or not the total is in sight: it is the guest's own share.
        Assert.Equal("billScope", Assert.Throws<FieldValidationException>(
            () => BillQuote.For(bill, GuestA, BillScope.MyShare, tipPercent: 10, canSeeTableTotal: false)).Field);

        // The rest of the table still owes, so the whole bill is still a thing to bring.
        Assert.Equal(
            17_087L,
            BillQuote.For(bill, GuestA, BillScope.WholeBill, tipPercent: null, canSeeTableTotal: true).AmountAmd);
    }

    [Fact]
    public void A_settled_tab_has_no_bill_to_bring_in_either_scope()
    {
        var bill = Bill(paidAgainstTheTable: 25_520L);

        Assert.Equal("billScope", Assert.Throws<FieldValidationException>(
            () => BillQuote.For(bill, Host, BillScope.WholeBill, tipPercent: null, canSeeTableTotal: true)).Field);
        Assert.Equal("billScope", Assert.Throws<FieldValidationException>(
            () => BillQuote.For(bill, Host, BillScope.MyShare, tipPercent: null, canSeeTableTotal: true)).Field);
    }

    [Fact]
    public void Somebody_with_no_share_on_the_bill_has_nothing_of_their_own_to_pay()
    {
        Assert.Equal("billScope", Assert.Throws<FieldValidationException>(
            () => BillQuote.For(
                Bill(), Guid.CreateVersion7(), BillScope.MyShare, tipPercent: null, canSeeTableTotal: true)).Field);
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

    // ------------------------------------------------------------ asking again

    [Fact]
    public void Asking_for_the_bill_again_supersedes_an_open_request_and_leaves_it_unacknowledged()
    {
        var request = new ServiceRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), GuestA,
            ServiceRequestPreset.TheBill, Now, bill: new BillQuote(BillScope.MyShare, 8_433L, 5, 383L));

        Assert.True(request.IsOpen);
        Assert.True(request.Supersede(Now.AddMinutes(1)));

        Assert.False(request.IsOpen);
        Assert.Equal(Now.AddMinutes(1), request.SupersededAtUtc);

        // Nobody went: it was replaced, not answered.
        Assert.Null(request.AcknowledgedAtUtc);
        Assert.Null(request.AcknowledgedByStaffId);

        // Twice is a no-op, and the first stamp stands.
        Assert.False(request.Supersede(Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(1), request.SupersededAtUtc);

        // A superseded request is no longer anybody's to acknowledge.
        Assert.False(request.Acknowledge(Now.AddMinutes(3), Guid.CreateVersion7()));
    }

    [Fact]
    public void A_request_a_waiter_already_picked_up_stays_picked_up()
    {
        var request = new ServiceRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), GuestA,
            ServiceRequestPreset.TheBill, Now);

        Assert.True(request.Acknowledge(Now.AddMinutes(1), Guid.CreateVersion7()));

        Assert.False(request.Supersede(Now.AddMinutes(2)));
        Assert.Null(request.SupersededAtUtc);
    }

    [Fact]
    public void Only_a_request_for_the_bill_can_be_superseded()
    {
        var water = new ServiceRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), GuestA,
            ServiceRequestPreset.Water, Now);

        Assert.Throws<DomainStateException>(() => water.Supersede(Now.AddMinutes(1)));
        Assert.True(water.IsOpen);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>The documented example, with whatever has been paid against it.</summary>
    private static TabBill Bill(long paidByGuestA = 0L, long paidByGuestB = 0L, long paidAgainstTheTable = 0L)
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
            new(GuestB, IsHost: false, ParticipantStatus.Approved, PaidAmd: paidByGuestB),
        ];

        return TabBilling.Compute(
            lines,
            [],
            participants,
            serviceChargePercent: 10m,
            paidAmd: paidByGuestA + paidByGuestB + paidAgainstTheTable);
    }
}
