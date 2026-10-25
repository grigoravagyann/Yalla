using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;

namespace Yalla.UnitTests;

/// <summary>
/// What a tab raises about itself, and when it raises nothing (<c>docs/domain-events.md</c>): its handlers write the
/// History's line, the tab's stream, the branch's change stream and the pushes, so a raise that is missing is a side
/// effect that never happens, and one too many tells every phone something happened twice.
/// </summary>
public sealed class TabLifecycleEventTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 19, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Waiter = Guid.CreateVersion7();

    [Fact]
    public void Building_a_tab_raises_nothing_and_opening_it_by_scan_or_by_staff_raises_once()
    {
        var scanned = new Tab(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now, 10m, admittedBecause: null);
        var host = TabParticipant.Host(scanned.Id, "Hasmik", "phone-hasmik", Now);

        Assert.Empty(scanned.DequeueEvents());

        scanned.OpenWithHost(host);

        var opened = Assert.IsType<TabOpenedByScan>(Assert.Single(scanned.DequeueEvents()).Event);
        Assert.Same(host, opened.Host);
        Assert.Equal(host.Id, scanned.HostParticipantId);
        Assert.False(opened.Tab.IsAdmitted);

        var byStaff = Tab.OpenByStaff(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now, 10m, Guid.CreateVersion7(), Waiter);

        var staffOpened = Assert.IsType<TabOpenedByStaff>(Assert.Single(byStaff.DequeueEvents()).Event);
        Assert.Equal(Waiter, staffOpened.ByStaffId);
        Assert.Equal((TabAdmission.Admitted, TabAdmissionReason.OpenedByStaff, (Guid?)Waiter), (byStaff.Admission, byStaff.AdmittedBecause, byStaff.AdmissionDecidedByStaffId));

        // Seeding a host raises nothing: the first phone onto a staff tab takes the role unannounced.
        byStaff.SetHostParticipant(Guid.CreateVersion7());
        Assert.Empty(byStaff.DequeueEvents());
    }

    [Fact]
    public void The_roster_raises_one_event_per_change_and_a_refusal_raises_nothing()
    {
        var (tab, host) = OpenTab();
        var ani = Guest(tab, "Ani");
        var bob = Guest(tab, "Bob");

        tab.AskToJoin(ani, "Ani", Now);
        tab.ApproveParticipant(ani, Now.AddMinutes(1), asBooker: false);
        tab.RejectParticipant(bob, Now.AddMinutes(2));
        tab.SetParticipantPermissions(ani, canOrder: true, canSeeTableTotal: true, canPay: true, Now.AddMinutes(3));
        tab.RenameParticipant(ani, "Ani B.", Now.AddMinutes(4));

        var events = tab.DequeueEvents().Select(e => e.Event).ToList();

        Assert.Collection(
            events,
            e => Assert.Equal("Ani", Assert.IsType<TabJoinRequested>(e).GivenName),
            e => Assert.False(Assert.IsType<TabParticipantApproved>(e).AsBooker),
            e => Assert.Same(bob, Assert.IsType<TabParticipantRejected>(e).Participant),
            e => Assert.True(Assert.IsType<TabParticipantPermissionsChanged>(e).Participant.CanPay),
            e => Assert.Equal("Ani B.", Assert.IsType<TabParticipantRenamed>(e).Participant.DisplayName));

        // Rejecting somebody no longer waiting, removing the host, an approved participant asking to join: refused,
        // and nothing raised.
        Assert.Throws<DomainStateException>(() => tab.RejectParticipant(bob, Now.AddMinutes(5)));
        Assert.Throws<DomainStateException>(() => tab.RemoveParticipant(host, Now.AddMinutes(5)));
        Assert.Throws<ArgumentException>(() => tab.AskToJoin(ani, null, Now.AddMinutes(5)));
        Assert.Throws<ArgumentException>(() => tab.ApproveParticipant(Guest(OpenTab().Tab, "Stranger"), Now, asBooker: false));
        Assert.Empty(tab.DequeueEvents());
    }

    [Fact]
    public void A_host_leaving_hands_the_tab_on_and_then_comes_off_it_in_that_order()
    {
        var (tab, host) = OpenTab();
        var ani = Guest(tab, "Ani");
        tab.ApproveParticipant(ani, Now, asBooker: false);
        tab.DequeueEvents();

        tab.Leave(host, successor: ani, Now.AddMinutes(1));

        var events = tab.DequeueEvents().Select(e => e.Event).ToList();

        Assert.Collection(
            events,
            e =>
            {
                var moved = Assert.IsType<TabHostMoved>(e);
                Assert.Equal((host, ani, true), (moved.From, moved.To, moved.HostLeft));
            },
            e =>
            {
                var removed = Assert.IsType<TabParticipantRemoved>(e);
                Assert.Equal((host, true), (removed.Participant, removed.Left));
            });

        Assert.Equal(ani.Id, tab.HostParticipantId);
        Assert.Equal((ParticipantRole.Guest, ParticipantStatus.Removed), (host.Role, host.Status));
        Assert.True(ani.IsHost);
    }

    [Fact]
    public void A_waiter_moving_the_host_raises_once_and_a_no_op_or_a_closed_tab_raises_nothing()
    {
        var (tab, host) = OpenTab();
        var ani = Guest(tab, "Ani");
        var pending = Guest(tab, "Pending");
        tab.ApproveParticipant(ani, Now, asBooker: false);
        tab.DequeueEvents();

        Assert.Throws<DomainStateException>(() => tab.MoveHostTo(host, Now, hostLeft: false));
        Assert.Throws<DomainStateException>(() => tab.MoveHostTo(pending, Now, hostLeft: false));
        Assert.Empty(tab.DequeueEvents());

        // A refusal changes nothing: the tab still answers to its host, who is still host.
        Assert.Equal(host.Id, tab.HostParticipantId);
        Assert.Equal((ParticipantRole.Host, ParticipantRole.Guest), (host.Role, pending.Role));

        tab.MoveHostTo(ani, Now.AddMinutes(1), hostLeft: false);

        var moved = Assert.IsType<TabHostMoved>(Assert.Single(tab.DequeueEvents()).Event);
        Assert.Equal((host, ani, false), (moved.From, moved.To, moved.HostLeft));
        Assert.Equal(ParticipantRole.Guest, host.Role);

        tab.BeginClosing(Now.AddMinutes(2));

        var presented = Assert.IsType<TabBillPresented>(Assert.Single(tab.DequeueEvents()).Event);
        Assert.Equal(Now.AddMinutes(2), presented.OccurredAtUtc);

        Assert.Throws<DomainStateException>(() => tab.BeginClosing(Now.AddMinutes(3)));
        Assert.Empty(tab.DequeueEvents());
    }

    [Fact]
    public void Changing_the_split_raises_and_a_locked_split_raises_nothing()
    {
        var (tab, _) = OpenTab();

        tab.SetSettlementMode(SettlementMode.HostPaysEverything, Now);

        Assert.IsType<TabSettlementModeChanged>(Assert.Single(tab.DequeueEvents()).Event);

        tab.LockSettlementMode(Now.AddMinutes(1));

        Assert.Throws<DomainStateException>(() => tab.SetSettlementMode(SettlementMode.EveryonePaysOwnItems, Now.AddMinutes(2)));
        Assert.Empty(tab.DequeueEvents());
    }

    private static (Tab Tab, TabParticipant Host) OpenTab()
    {
        var tab = new Tab(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now, 10m, TabAdmissionReason.NotRequired);
        var host = TabParticipant.Host(tab.Id, "Hasmik", "phone-hasmik", Now);

        tab.OpenWithHost(host);
        tab.DequeueEvents();

        OnRoster(tab, host);

        return (tab, host);
    }

    private static TabParticipant Guest(Tab tab, string name)
    {
        var guest = TabParticipant.Guest(tab.Id, name, $"phone-{name}", Now, hideTotalFromGuests: false);
        OnRoster(tab, guest);

        return guest;
    }

    /// <summary>Puts a participant on the tab's roster, as loading the tab with its participants does.</summary>
    private static void OnRoster(Tab tab, TabParticipant participant) =>
        ((List<TabParticipant>)typeof(Tab)
            .GetField("_participants", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(tab)!)
        .Add(participant);
}
