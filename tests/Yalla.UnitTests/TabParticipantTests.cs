using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests;

public class TabParticipantTests
{
    private static readonly DateTime JoinedAt = new(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_participant_who_may_pay_but_not_see_the_total_cannot_be_constructed()
    {
        var construct = () => NewParticipant(canSeeTableTotal: false, canPay: true);

        Assert.Throws<ArgumentException>(construct);
    }

    [Fact]
    public void Revoking_sight_of_the_total_while_leaving_pay_rights_is_rejected()
    {
        var participant = NewParticipant(canSeeTableTotal: true, canPay: true);

        Assert.Throws<ArgumentException>(() => participant.SetPermissions(
            canOrder: true, canSeeTableTotal: false, canPay: true));

        // The failed call left the participant as it was.
        Assert.True(participant.CanSeeTableTotal);
        Assert.True(participant.CanPay);
    }

    [Fact]
    public void Dropping_both_pay_rights_and_sight_of_the_total_is_allowed()
    {
        var participant = NewParticipant(canSeeTableTotal: true, canPay: true);

        participant.SetPermissions(canOrder: true, canSeeTableTotal: false, canPay: false);

        Assert.False(participant.CanSeeTableTotal);
        Assert.False(participant.CanPay);
    }

    [Fact]
    public void A_guest_can_order_and_see_the_total_but_not_pay_by_default()
    {
        var participant = new TabParticipant(
            Guid.CreateVersion7(), "Ani", "device-1", ParticipantRole.Guest, ParticipantStatus.Approved, JoinedAt);

        Assert.True(participant.CanOrder);
        Assert.True(participant.CanSeeTableTotal);
        Assert.False(participant.CanPay);
    }

    private static TabParticipant NewParticipant(bool canSeeTableTotal, bool canPay) => new(
        Guid.CreateVersion7(),
        "Ani",
        "device-1",
        ParticipantRole.Host,
        ParticipantStatus.Approved,
        JoinedAt,
        canOrder: true,
        canSeeTableTotal: canSeeTableTotal,
        canPay: canPay);
}
