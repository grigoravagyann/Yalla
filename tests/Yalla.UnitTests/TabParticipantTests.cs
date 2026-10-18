using Yalla.Domain;
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

    // ------------------------------------------------------------ the host always sees the total

    [Fact]
    public void The_host_cannot_be_hidden_the_total()
    {
        var host = NewParticipant(canSeeTableTotal: true, canPay: true, ParticipantRole.Host);

        var refused = Assert.Throws<FieldValidationException>(() => host.SetPermissions(
            canOrder: true, canSeeTableTotal: false, canPay: false));

        // A field error on the flag the request may not hold, which the API answers as a 422.
        Assert.Equal("canSeeTableTotal", refused.Field);

        // And nothing moved.
        Assert.True(host.CanSeeTableTotal);
        Assert.True(host.CanPay);
    }

    [Fact]
    public void A_host_without_the_total_cannot_be_constructed()
    {
        Assert.Throws<FieldValidationException>(
            () => NewParticipant(canSeeTableTotal: false, canPay: false, ParticipantRole.Host));
    }

    /// <summary>
    /// The rule is about the role, not the person: a host who hands the tab over is a guest like any
    /// other, and the new host may hide the total from them.
    /// </summary>
    [Fact]
    public void A_host_who_hands_over_may_have_the_total_hidden_like_any_guest()
    {
        var former = NewParticipant(canSeeTableTotal: true, canPay: true, ParticipantRole.Host);

        former.BecomeGuest();
        former.SetPermissions(canOrder: true, canSeeTableTotal: false, canPay: false);

        Assert.False(former.CanSeeTableTotal);
    }

    // The pay-and-see rules hold for anybody, so they are tested on a guest - the host now has a rule
    // of their own about the total, above.
    private static TabParticipant NewParticipant(
        bool canSeeTableTotal,
        bool canPay,
        ParticipantRole role = ParticipantRole.Guest) => new(
        Guid.CreateVersion7(),
        "Ani",
        "device-1",
        role,
        ParticipantStatus.Approved,
        JoinedAt,
        canOrder: true,
        canSeeTableTotal: canSeeTableTotal,
        canPay: canPay);
}
