using Yalla.Domain;
using Yalla.Domain.Billing;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests;

/// <summary>
/// What a call raises, and when it raises nothing (<c>docs/domain-events.md</c>): its handlers write the tab's stream,
/// the floor's push and the History's claim, so a raise that is missing is a side effect that never happens.
/// </summary>
public sealed class ServiceRequestEventTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 19, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Aram = Guid.CreateVersion7();

    private static readonly Guid Lilit = Guid.CreateVersion7();

    [Fact]
    public void A_built_call_raises_nothing_until_it_is_sent_and_is_sent_once()
    {
        var call = NewCall(ServiceRequestPreset.Water);

        Assert.Empty(call.DequeueEvents());

        call.Send();

        var raised = Assert.IsType<ServiceRequestRaised>(Assert.Single(call.DequeueEvents()).Event);
        Assert.Same(call, raised.Request);
        Assert.Equal(Now, raised.OccurredAtUtc);

        Assert.Throws<DomainStateException>(call.Send);
        Assert.Empty(call.DequeueEvents());
    }

    [Fact]
    public void The_first_claim_raises_and_a_second_raises_nothing()
    {
        var call = NewCall(ServiceRequestPreset.Napkins);

        Assert.True(call.Claim(Now.AddMinutes(2), Aram));

        var claimed = Assert.IsType<ServiceRequestClaimed>(Assert.Single(call.DequeueEvents()).Event);
        Assert.Equal(Aram, claimed.ByStaffId);
        Assert.Equal(Now.AddMinutes(2), claimed.OccurredAtUtc);

        Assert.False(call.Claim(Now.AddMinutes(3), Lilit));
        Assert.Empty(call.DequeueEvents());
    }

    [Fact]
    public void Answering_a_call_nobody_claimed_claims_it_then_acknowledges_it()
    {
        var call = NewCall(ServiceRequestPreset.Water);

        Assert.True(call.Acknowledge(Now.AddMinutes(4), Lilit));

        var events = call.DequeueEvents().Select(e => e.Event).ToList();
        Assert.Equal(2, events.Count);

        var claimed = Assert.IsType<ServiceRequestClaimed>(events[0]);
        Assert.Equal(Lilit, claimed.ByStaffId);
        Assert.Equal(Now.AddMinutes(4), claimed.OccurredAtUtc);

        var acknowledged = Assert.IsType<ServiceRequestAcknowledged>(events[1]);
        Assert.Equal(Lilit, acknowledged.ByStaffId);
        Assert.Equal(Now.AddMinutes(4), acknowledged.OccurredAtUtc);

        // Twice is a no-op, and so is a claim after it.
        Assert.False(call.Acknowledge(Now.AddMinutes(5), Aram));
        Assert.False(call.Claim(Now.AddMinutes(5), Aram));
        Assert.Empty(call.DequeueEvents());
    }

    [Fact]
    public void Answering_a_claimed_call_only_acknowledges_it()
    {
        var call = NewCall(ServiceRequestPreset.Other);
        call.Claim(Now.AddMinutes(1), Aram);
        call.DequeueEvents();

        Assert.True(call.Acknowledge(Now.AddMinutes(3), Lilit));

        var acknowledged = Assert.IsType<ServiceRequestAcknowledged>(Assert.Single(call.DequeueEvents()).Event);
        Assert.Equal(Lilit, acknowledged.ByStaffId);
        Assert.Equal(Aram, call.ClaimedByStaffId);
    }

    [Fact]
    public void A_replaced_request_for_the_bill_raises_nothing_and_cannot_be_claimed_or_answered()
    {
        var call = NewCall(ServiceRequestPreset.TheBill, new BillQuote(BillScope.MyShare, 5_000L, null, null));

        Assert.True(call.Supersede(Now.AddMinutes(1)));
        Assert.False(call.Claim(Now.AddMinutes(2), Aram));
        Assert.False(call.Acknowledge(Now.AddMinutes(2), Aram));
        Assert.Empty(call.DequeueEvents());
    }

    [Fact]
    public void A_call_answered_by_turning_its_tab_away_is_answered_and_claimed_but_raises_nothing()
    {
        var call = NewCall(ServiceRequestPreset.Water);

        Assert.True(call.AnswerForTurnedAwayTab(Now.AddMinutes(2), Aram));

        Assert.Equal(Now.AddMinutes(2), call.AcknowledgedAtUtc);
        Assert.Equal(Aram, call.AcknowledgedByStaffId);
        Assert.Equal(Now.AddMinutes(2), call.ClaimedAtUtc);
        Assert.Equal(Aram, call.ClaimedByStaffId);
        Assert.False(call.IsOpen);
        Assert.Empty(call.DequeueEvents());

        Assert.False(call.AnswerForTurnedAwayTab(Now.AddMinutes(3), Lilit));
        Assert.Equal(Aram, call.AcknowledgedByStaffId);
    }

    private static ServiceRequest NewCall(ServiceRequestPreset preset, BillQuote? bill = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), preset, Now, bill: bill);
}
