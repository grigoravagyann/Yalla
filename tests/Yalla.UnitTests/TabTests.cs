using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests;

public class TabTests
{
    private static readonly DateTime OpenedAt = new(2026, 9, 4, 17, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_new_tab_is_open_and_owes_nothing()
    {
        var tab = NewTab();

        Assert.Equal(TabStatus.Open, tab.Status);
        Assert.Equal(0L, tab.TotalAmd);
        Assert.Equal(0L, tab.RemainingAmd);
    }

    [Fact]
    public void The_total_and_the_remainder_are_derived_from_their_parts()
    {
        var tab = NewTab();

        tab.ApplyComputedTotals(subtotalAmd: 12_000L, serviceChargeAmd: 1_200L, paidAmd: 5_000L);

        Assert.Equal(13_200L, tab.TotalAmd);
        Assert.Equal(8_200L, tab.RemainingAmd);
    }

    [Fact]
    public void Overpayment_never_produces_a_negative_remainder()
    {
        var tab = NewTab();

        tab.ApplyComputedTotals(subtotalAmd: 1_000L, serviceChargeAmd: 100L, paidAmd: 2_000L);

        Assert.Equal(0L, tab.RemainingAmd);
    }

    [Fact]
    public void The_settlement_mode_cannot_be_renegotiated_once_locked()
    {
        var tab = NewTab();

        tab.SetSettlementMode(SettlementMode.EveryonePaysOwnItems);
        tab.LockSettlementMode(OpenedAt.AddMinutes(50));

        Assert.Throws<DomainStateException>(
            () => tab.SetSettlementMode(SettlementMode.HostPaysEverything));
        Assert.Equal(SettlementMode.EveryonePaysOwnItems, tab.SettlementMode);
    }

    [Fact]
    public void A_scan_with_no_proved_number_waits_and_a_waiter_admits_it_once()
    {
        var tab = AwaitingTab();
        var waiter = Guid.CreateVersion7();

        Assert.Equal(TabAdmission.AwaitingAdmission, tab.Admission);
        Assert.False(tab.IsAdmitted);

        Assert.True(tab.Admit(OpenedAt.AddMinutes(2), TabAdmissionReason.Waiter, waiter));
        Assert.False(tab.Admit(OpenedAt.AddMinutes(3), TabAdmissionReason.Booker, null));

        Assert.Equal(TabAdmission.Admitted, tab.Admission);
        Assert.Equal(TabAdmissionReason.Waiter, tab.AdmittedBecause);
        Assert.Equal(waiter, tab.AdmissionDecidedByStaffId);
        Assert.Equal(OpenedAt.AddMinutes(2), tab.AdmissionDecidedAtUtc);

        Assert.Throws<TabAdmissionDecidedException>(() => tab.RejectAdmission(OpenedAt.AddMinutes(4), waiter));
    }

    [Fact]
    public void Turning_a_waiting_tab_away_closes_it_and_it_cannot_be_admitted_after()
    {
        var tab = AwaitingTab();
        var waiter = Guid.CreateVersion7();

        Assert.True(tab.RejectAdmission(OpenedAt.AddMinutes(5), waiter));
        Assert.False(tab.RejectAdmission(OpenedAt.AddMinutes(6), waiter));

        Assert.Equal(TabAdmission.Rejected, tab.Admission);
        Assert.Equal(TabStatus.Closed, tab.Status);
        Assert.Equal(OpenedAt.AddMinutes(5), tab.ClosedAtUtc);
        Assert.Throws<TabAdmissionDecidedException>(() => tab.Admit(OpenedAt.AddMinutes(7), TabAdmissionReason.Waiter, waiter));
    }

    [Fact]
    public void A_tab_opened_with_a_reason_is_admitted_from_the_start()
    {
        var tab = NewTab();

        Assert.Equal(TabAdmission.Admitted, tab.Admission);
        Assert.Equal(TabAdmissionReason.OpenedByStaff, tab.AdmittedBecause);
        Assert.Equal(OpenedAt, tab.AdmissionDecidedAtUtc);
    }

    private static Tab AwaitingTab() => new(
        branchId: Guid.CreateVersion7(),
        diningTableId: Guid.CreateVersion7(),
        tableSessionId: Guid.CreateVersion7(),
        openedAtUtc: OpenedAt,
        serviceChargePercentSnapshot: 10m,
        admittedBecause: null);

    private static Tab NewTab() => new(
        branchId: Guid.CreateVersion7(),
        diningTableId: Guid.CreateVersion7(),
        tableSessionId: Guid.CreateVersion7(),
        openedAtUtc: OpenedAt,
        serviceChargePercentSnapshot: 10m,
        admittedBecause: TabAdmissionReason.OpenedByStaff);
}

public class TabOrderTests
{
    private static readonly DateTime PlacedAt = new(2026, 9, 4, 17, 20, 0, DateTimeKind.Utc);

    [Fact]
    public void An_order_from_a_phone_names_the_participant_and_no_staff_member()
    {
        var order = TabOrder.PlacedByDiner(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);

        Assert.NotNull(order.PlacedByParticipantId);
        Assert.Null(order.PlacedByStaffId);
    }

    [Fact]
    public void A_spoken_order_names_the_waiter_and_no_participant()
    {
        var order = TabOrder.PlacedByStaffMember(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);

        Assert.Null(order.PlacedByParticipantId);
        Assert.NotNull(order.PlacedByStaffId);
    }

    [Fact]
    public void A_line_snapshots_the_name_and_price_it_was_ordered_at()
    {
        var order = TabOrder.PlacedByDiner(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);

        var line = order.AddLine(Guid.CreateVersion7(), "Flat white", 1_400L, quantity: 2);

        Assert.Equal("Flat white", line.NameSnapshot);
        Assert.Equal(1_400L, line.UnitPriceAmdSnapshot);
        Assert.Equal(2_800L, line.LineTotalAmd);
        Assert.Single(order.Lines);
    }

    [Fact]
    public void A_voided_line_stops_counting_towards_the_bill()
    {
        var order = TabOrder.PlacedByDiner(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);
        var line = order.AddLine(Guid.CreateVersion7(), "Flat white", 1_400L, quantity: 2);

        line.Void(PlacedAt.AddMinutes(3), "sent back");

        Assert.True(line.IsVoided);
        Assert.Equal(0L, line.LineTotalAmd);
    }

    [Fact]
    public void A_shared_line_records_each_person_present_only_once()
    {
        var order = TabOrder.PlacedByDiner(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);
        var line = order.AddLine(Guid.CreateVersion7(), "Cheese plate", 6_000L, quantity: 1, isShared: true);
        var participantId = Guid.CreateVersion7();

        line.AddShare(participantId);

        Assert.Throws<DomainStateException>(() => line.AddShare(participantId));
        Assert.Single(line.Shares);
    }

    [Fact]
    public void A_line_owner_is_its_own_guest_then_the_orders_and_never_on_a_split_line()
    {
        var (narine, aram) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var order = TabOrder.PlacedByStaffMember(
            Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt, onBehalfOfParticipantId: aram);

        var steak = order.AddLine(Guid.CreateVersion7(), "Steak", 9_000L, 1, onBehalfOfParticipantId: narine);
        var coffee = order.AddLine(Guid.CreateVersion7(), "Coffee", 1_200L, 1);
        var bottle = order.AddLine(Guid.CreateVersion7(), "Wine", 9_500L, 1, isShared: true);

        Assert.Equal(narine, order.OwnerOf(steak));
        Assert.Equal(aram, order.OwnerOf(coffee));
        Assert.Null(order.OwnerOf(bottle));
    }

    [Fact]
    public void A_line_cannot_be_put_in_a_guests_name_on_a_phones_order_or_when_it_is_split()
    {
        var phone = TabOrder.PlacedByDiner(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);
        var spoken = TabOrder.PlacedByStaffMember(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);
        var guest = Guid.CreateVersion7();

        Assert.Throws<ArgumentException>(
            () => phone.AddLine(Guid.CreateVersion7(), "Coffee", 1_200L, 1, onBehalfOfParticipantId: guest));
        Assert.Throws<ArgumentException>(
            () => spoken.AddLine(Guid.CreateVersion7(), "Wine", 9_500L, 1, isShared: true, onBehalfOfParticipantId: guest));
        Assert.Throws<ArgumentException>(
            () => spoken.AddLine(Guid.CreateVersion7(), "Wine", 9_500L, 1, isTableAttributed: true, onBehalfOfParticipantId: guest));
        Assert.Empty(phone.Lines);
        Assert.Empty(spoken.Lines);
    }

    /// <summary>
    /// Voiding a whole order takes every line still on the bill off it, stamped with the void's time, who and why; a
    /// line voided earlier keeps its own stamp. The bill is the lines, so this is what stops a voided order charging.
    /// </summary>
    [Fact]
    public void Voiding_an_order_voids_its_live_lines_and_leaves_an_earlier_void_as_it_was()
    {
        var order = TabOrder.PlacedByStaffMember(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);
        var coffee = order.AddLine(Guid.CreateVersion7(), "Cappuccino", 1_400L, 2);
        var cake = order.AddLine(Guid.CreateVersion7(), "Gata", 1_800L, 1);
        cake.Void(PlacedAt.AddMinutes(1), "Sold out");
        order.MoveTo(TabOrderStatus.InKitchen);

        var waiter = Guid.CreateVersion7();
        var takenOff = order.Void(PlacedAt.AddMinutes(4), waiter);

        Assert.Equal(TabOrderStatus.Voided, order.Status);
        Assert.Equal(new[] { coffee }, takenOff);
        Assert.Equal((PlacedAt.AddMinutes(4), waiter, TabOrder.WholeOrderVoidReason), (coffee.VoidedAtUtc, coffee.VoidedByStaffId, coffee.VoidReason));
        Assert.Equal((PlacedAt.AddMinutes(1), "Sold out"), (cake.VoidedAtUtc, cake.VoidReason));
        Assert.All(order.Lines, l => Assert.Equal(0L, l.LineTotalAmd));
    }

    /// <summary>
    /// The rail's own rules hold for a void - nothing served or ready comes off, and nothing is voided twice - and a void
    /// cannot go round <see cref="TabOrder.Void"/> through <see cref="TabOrder.MoveTo"/>, which would leave the lines charged.
    /// </summary>
    [Fact]
    public void A_void_goes_only_through_Void_and_only_where_the_rail_allows_it()
    {
        var order = TabOrder.PlacedByDiner(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);
        var line = order.AddLine(Guid.CreateVersion7(), "Cappuccino", 1_400L, 1);

        Assert.Throws<DomainStateException>(() => order.MoveTo(TabOrderStatus.Voided));
        Assert.Equal(TabOrderStatus.New, order.Status);
        Assert.False(line.IsVoided);

        order.MoveTo(TabOrderStatus.InKitchen);
        order.MoveTo(TabOrderStatus.Ready);

        Assert.Throws<DomainStateException>(() => order.Void(PlacedAt.AddMinutes(5), null));
        Assert.Equal(TabOrderStatus.Ready, order.Status);
        Assert.False(line.IsVoided);

        var fresh = TabOrder.PlacedByDiner(Guid.CreateVersion7(), Guid.CreateVersion7(), PlacedAt);
        fresh.AddLine(Guid.CreateVersion7(), "Cappuccino", 1_400L, 1);
        fresh.Void(PlacedAt.AddMinutes(1), null);

        Assert.Throws<DomainStateException>(() => fresh.Void(PlacedAt.AddMinutes(2), null));
    }
}

public class TabJoinTokenTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 4, 17, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_token_lapses_thirty_minutes_after_it_was_issued()
    {
        var token = new TabJoinToken(Guid.CreateVersion7(), Guid.CreateVersion7(), CreatedAt);

        Assert.Equal(CreatedAt.AddMinutes(30), token.ExpiresAtUtc);
        Assert.True(token.IsUsableAt(CreatedAt.AddMinutes(29)));
        Assert.False(token.IsUsableAt(CreatedAt.AddMinutes(31)));
    }

    [Fact]
    public void A_revoked_token_is_unusable_even_before_it_expires()
    {
        var token = new TabJoinToken(Guid.CreateVersion7(), Guid.CreateVersion7(), CreatedAt);

        token.Revoke(CreatedAt.AddMinutes(5));

        Assert.False(token.IsUsableAt(CreatedAt.AddMinutes(6)));
    }
}

public class PaymentTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 4, 18, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void A_reserved_payment_holds_an_amount_before_any_provider_is_called()
    {
        var payment = Payment.Reserve(Guid.CreateVersion7(), 5_000L, PaymentMethod.Idram, CreatedAt);

        Assert.Equal(PaymentStatus.Reserved, payment.Status);
        Assert.Equal(5_000L, payment.AmountAmd);
        Assert.Null(payment.CompletedAtUtc);
    }

    [Fact]
    public void A_terminal_payment_must_say_when_it_completed()
    {
        var construct = () => new Payment(
            Guid.CreateVersion7(), 5_000L, PaymentMethod.Cash, PaymentStatus.Succeeded, CreatedAt);

        Assert.Throws<ArgumentException>(construct);
    }

    [Fact]
    public void A_payment_for_nothing_is_rejected()
    {
        var construct = () => Payment.Reserve(Guid.CreateVersion7(), 0L, PaymentMethod.Card, CreatedAt);

        Assert.Throws<ArgumentOutOfRangeException>(construct);
    }
}
