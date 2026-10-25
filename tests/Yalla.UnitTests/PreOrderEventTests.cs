using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// What a pre-order and a booking's "are you on your way?" raise (<c>docs/domain-events.md</c>, pre-orders): one event per
/// thing that happened, carrying what a handler cannot read back - and nothing for a refusal or a replay.
/// </summary>
public class PreOrderEventTests
{
    private static readonly DateTime StartUtc = new(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = StartUtc.AddDays(-2);

    private static readonly Guid Staff = Guid.CreateVersion7();

    [Fact]
    public void Saving_clearing_and_marking_not_coming_each_raise_one_event()
    {
        var preOrder = Saved(out var reservation);

        var saved = Assert.IsType<PreOrderSaved>(Single(preOrder));
        Assert.Same(preOrder, saved.PreOrder);
        Assert.Equal(Now, saved.OccurredAtUtc);

        preOrder.MarkNotComing(Staff, StartUtc.AddMinutes(-50));
        var notComing = Assert.IsType<PartyMarkedNotComing>(Single(preOrder));
        Assert.Equal(Staff, notComing.ByStaffId);

        preOrder.FollowMove(Now.AddMinutes(1));
        Assert.IsType<PreOrderFollowedMove>(Single(preOrder));

        preOrder.Clear(Now.AddMinutes(2));
        Assert.Equal(Now.AddMinutes(2), Assert.IsType<PreOrderCleared>(Single(preOrder)).OccurredAtUtc);
        Assert.Empty(reservation.DequeueEvents());
    }

    [Fact]
    public void A_send_carries_the_sitting_and_tab_as_they_stood_and_a_refused_one_raises_nothing()
    {
        var preOrder = Saved(out var reservation);
        preOrder.DequeueEvents();

        var sitting = TableSession.SeatReservation(reservation.BranchId, reservation.DiningTableId, reservation.Id, 2, StartUtc.AddMinutes(-10));
        var tabId = Guid.CreateVersion7();
        sitting.AttachTab(tabId);

        preOrder.MarkSent(Staff, sitting, StartUtc.AddMinutes(-5));
        var sent = Assert.IsType<PreOrderSent>(Single(preOrder));
        Assert.Equal((Staff, sitting.Id, tabId), (sent.ByStaffId, sent.SittingId!.Value, sent.TabId!.Value));

        Assert.Throws<PreOrderAlreadySentException>(() => preOrder.MarkSent(Staff, null, StartUtc));
        Assert.Throws<PreOrderAlreadySentException>(() => preOrder.MarkNotComing(Staff, StartUtc));
        Assert.Throws<PreOrderAlreadySentException>(() => preOrder.Clear(StartUtc));
        Assert.Throws<PreOrderAlreadySentException>(() => preOrder.FollowMove(StartUtc));
        Assert.Empty(preOrder.DequeueEvents());

        var held = Saved(out _);
        held.MarkSent(Staff, null, StartUtc.AddMinutes(-45));
        var heldSent = Assert.IsType<PreOrderSent>(Assert.Single(held.DequeueEvents(), e => e.Event is PreOrderSent).Event);
        Assert.Null(heldSent.SittingId);
        Assert.Null(heldSent.TabId);
    }

    [Fact]
    public void A_held_ticket_moving_being_voided_and_arriving_on_a_tab_each_raise_one_event()
    {
        var preOrder = Saved(out _);
        preOrder.MarkSent(Staff, null, StartUtc.AddMinutes(-45));
        preOrder.DequeueEvents();

        preOrder.MoveKitchenStatus(TabOrderStatus.Ready, StartUtc.AddMinutes(-40));
        var moved = Assert.IsType<HeldTicketMoved>(Single(preOrder));
        Assert.Equal((TabOrderStatus.InKitchen, TabOrderStatus.Ready), (moved.From, moved.To));

        Assert.Throws<DomainStateException>(() => preOrder.MoveKitchenStatus(TabOrderStatus.InKitchen, StartUtc));
        Assert.Empty(preOrder.DequeueEvents());

        var order = TabOrder.FromPreOrder(preOrder.Id, Guid.CreateVersion7(), Staff, preOrder.SentAtUtc!.Value);
        Assert.Throws<ArgumentException>(
            () => preOrder.MarkAttached(TabOrder.FromPreOrder(Guid.CreateVersion7(), order.TabId, Staff, StartUtc), true, StartUtc));
        preOrder.MarkAttached(order, onNewTab: true, StartUtc.AddMinutes(-5));
        var arrived = Assert.IsType<PreOrderArrivedOnTab>(Single(preOrder));
        Assert.Same(order, arrived.Order);
        Assert.True(arrived.OnNewTab);

        // On a tab: the booking ending leaves it, and says nothing.
        Assert.False(preOrder.VoidHeldBecauseBookingEnded(StartUtc));
        Assert.Empty(preOrder.DequeueEvents());

        var cancelled = Saved(out _);
        cancelled.MarkSent(Staff, null, StartUtc.AddMinutes(-45));
        cancelled.DequeueEvents();
        Assert.True(cancelled.VoidHeldBecauseBookingEnded(StartUtc.AddMinutes(-30)));
        Assert.IsType<HeldTicketVoided>(Single(cancelled));
        Assert.False(cancelled.VoidHeldBecauseBookingEnded(StartUtc.AddMinutes(-29)));
        Assert.Empty(cancelled.DequeueEvents());
    }

    [Fact]
    public void The_guests_answers_and_the_venues_ask_raise_on_the_booking_and_a_replay_raises_nothing()
    {
        var booking = Booking();

        booking.RecordStaffAskOnMyWay(StartUtc.AddMinutes(-60));
        Assert.IsType<OnMyWayAskRecorded>(Single(booking));

        booking.ReportRunningLate(15, StartUtc.AddMinutes(-55));
        Assert.Equal(15, Assert.IsType<GuestRunningLate>(Single(booking)).Minutes);

        Assert.True(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-50)));
        Assert.Same(booking, Assert.IsType<GuestOnTheirWay>(Single(booking)).Reservation);

        Assert.False(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-45)));
        Assert.Throws<GuestAlreadyAnsweredException>(() => booking.RecordStaffAskOnMyWay(StartUtc.AddMinutes(-40)));
        Assert.Empty(booking.DequeueEvents());
    }

    [Fact]
    public void A_move_to_another_start_forgets_the_answers_and_says_so_only_when_there_were_any()
    {
        var policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);
        var answered = Booking();
        Assert.True(answered.ConfirmOnMyWay(StartUtc.AddMinutes(-30)));
        answered.DequeueEvents();

        answered.Reschedule(
            answered.DiningTableId, StartUtc.AddDays(1), new DateOnly(2026, 10, 28), new TimeOnly(18, 0), 4, policy,
            ReservationStatus.Confirmed, StartUtc.AddMinutes(-29));
        Assert.Equal(StartUtc.AddMinutes(-29), Assert.IsType<OnMyWayAnswersForgotten>(Single(answered)).OccurredAtUtc);

        var quiet = Booking();
        quiet.Reschedule(
            quiet.DiningTableId, StartUtc.AddDays(1), new DateOnly(2026, 10, 28), new TimeOnly(18, 0), 4, policy,
            ReservationStatus.Confirmed, StartUtc.AddDays(-1));
        Assert.DoesNotContain(quiet.DequeueEvents(), e => e.Event is OnMyWayAnswersForgotten);
    }

    [Fact]
    public void Saving_the_settings_raises_on_the_branch()
    {
        var branch = new Branch(new Venue("Lava", VenueType.Restaurant, "lava"), "Kentron", "kentron", "Abovyan 12", 40.18, 44.51, "Asia/Yerevan");

        branch.SetPreOrders(true, 45, Now);

        var saved = Assert.IsType<PreOrderSettingsSaved>(Single(branch));
        Assert.Same(branch, saved.Branch);
        Assert.Throws<ArgumentOutOfRangeException>(() => branch.SetPreOrders(true, 121, Now));
        Assert.Empty(branch.DequeueEvents());
    }

    private static IDomainEvent Single(Entity entity) => Assert.Single(entity.DequeueEvents()).Event;

    private static Reservation Booking() => Reservation.Create(
        branchId: Guid.CreateVersion7(),
        diningTableId: Guid.CreateVersion7(),
        startUtc: StartUtc,
        localDate: new DateOnly(2026, 10, 27),
        localStartTime: new TimeOnly(18, 0),
        partySize: 4,
        guestName: "Ani",
        guestPhone: "+37411223344",
        code: "AB7K2",
        policy: ReservationPolicy.DefaultFor(VenueType.Restaurant));

    private static PreOrder Saved(out Reservation reservation)
    {
        reservation = Booking();
        var preOrder = new PreOrder(reservation.Id, reservation.BranchId, Now);
        preOrder.Replace([new PreOrderLineInput(Guid.CreateVersion7(), "Flat white", 1_200L, 2, null)], null, Now);

        return preOrder;
    }
}
