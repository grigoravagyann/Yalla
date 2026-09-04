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

        Assert.Throws<InvalidOperationException>(
            () => tab.SetSettlementMode(SettlementMode.HostPaysEverything));
        Assert.Equal(SettlementMode.EveryonePaysOwnItems, tab.SettlementMode);
    }

    private static Tab NewTab() => new(
        branchId: Guid.CreateVersion7(),
        diningTableId: Guid.CreateVersion7(),
        tableSessionId: Guid.CreateVersion7(),
        openedAtUtc: OpenedAt,
        serviceChargePercentSnapshot: 10m);
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

        Assert.Throws<InvalidOperationException>(() => line.AddShare(participantId));
        Assert.Single(line.Shares);
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
