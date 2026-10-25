using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// A takeaway order's statuses - each move only from the state it belongs to, each stamped - and the booking's
/// "running late" and the venue's "are you coming?", which need no database either.
/// </summary>
public class TakeawayOrderTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 14, 48, 0, DateTimeKind.Utc);
    private static readonly Guid Staff = Guid.CreateVersion7();

    private static TakeawayOrder Sent(DateTime? at = null) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "T-482",
        new DateOnly(2026, 10, 22),
        "Ani M.",
        "+37491000008",
        pickupAsap: true,
        requestedPickupUtc: (at ?? Now).AddMinutes(20),
        requestedPickupLocalTime: new TimeOnly(19, 8),
        note: "No onion",
        lines:
        [
            new TakeawayLineInput(Guid.CreateVersion7(), "Pork khorovats", 4_500, 1),
            new TakeawayLineInput(Guid.CreateVersion7(), "Tan", 600, 2),
        ],
        clientCommandId: Guid.CreateVersion7(),
        nowUtc: at ?? Now);

    /// <summary>Sent → Accepted → Ready → Collected, each stamped, the payment the whole total.</summary>
    [Fact]
    public void The_happy_path_moves_one_way_and_records_every_step()
    {
        var order = Sent();

        Assert.Equal(TakeawayStatus.Sent, order.Status);
        Assert.Equal(Now.AddMinutes(5), order.AcceptDeadlineUtc);
        Assert.Equal(5_700, order.TotalAmd);

        Assert.True(order.Accept(Now.AddMinutes(25), Staff, Now.AddMinutes(1)));
        Assert.Equal(TakeawayStatus.Accepted, order.Status);
        Assert.Equal(Now.AddMinutes(25), order.ReadyAtUtc);
        Assert.Null(order.AcceptDeadlineUtc);
        Assert.False(order.Accept(Now.AddMinutes(30), Staff, Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(25), order.ReadyAtUtc);

        Assert.True(order.MarkReady(Staff, Now.AddMinutes(24)));
        Assert.Equal(TakeawayStatus.Ready, order.Status);
        Assert.False(order.MarkReady(Staff, Now.AddMinutes(26)));

        Assert.True(order.Collect(TakeawayPaymentMethod.Card, Staff, Now.AddMinutes(40)));
        Assert.Equal(TakeawayStatus.Collected, order.Status);
        Assert.Equal(TakeawayPaymentMethod.Card, order.PaymentMethod);
        Assert.Equal(5_700, order.PaidAmountAmd);
        Assert.Equal(Now.AddMinutes(40), order.CollectedAtUtc);
        Assert.False(order.Collect(TakeawayPaymentMethod.Cash, Staff, Now.AddMinutes(41)));
        Assert.Equal(TakeawayPaymentMethod.Card, order.PaymentMethod);
    }

    /// <summary>Every move out of its state is refused with the status it is in, and changes nothing.</summary>
    [Fact]
    public void Moves_out_of_their_state_are_refused_with_where_the_order_is()
    {
        var sent = Sent();
        Assert.Equal(TakeawayStatus.Sent, Assert.Throws<TakeawayStatusConflictException>(() => sent.MarkReady(Staff, Now)).Status);
        Assert.Throws<TakeawayStatusConflictException>(() => sent.Collect(TakeawayPaymentMethod.Cash, Staff, Now));
        Assert.Throws<TakeawayStatusConflictException>(() => sent.MarkNoShow(Staff, Now));

        var accepted = Sent();
        accepted.Accept(Now.AddMinutes(20), Staff, Now);
        Assert.Throws<TakeawayStatusConflictException>(() => accepted.Decline(null, Staff, Now));
        Assert.Throws<TakeawayStatusConflictException>(() => accepted.CancelByGuest(Now));
        Assert.Throws<TakeawayStatusConflictException>(() => accepted.Collect(TakeawayPaymentMethod.Cash, Staff, Now));
        Assert.Equal(TakeawayStatus.Accepted, accepted.Status);

        var declined = Sent();
        declined.Decline("Kitchen closed early", Staff, Now);
        Assert.Throws<TakeawayStatusConflictException>(() => declined.Accept(Now.AddMinutes(20), Staff, Now));
        Assert.Throws<TakeawayStatusConflictException>(() => declined.CancelByGuest(Now));
        Assert.Equal(TakeawayDeclineCause.StaffDeclined, declined.DeclinedBecause);
        Assert.Equal("Kitchen closed early", declined.DeclineReason);
        Assert.False(declined.Decline(null, Staff, Now));
    }

    /// <summary>The guest cancels only while the venue has not answered; a second cancel changes nothing.</summary>
    [Fact]
    public void The_guest_cancels_only_while_sent()
    {
        var order = Sent();

        Assert.True(order.CancelByGuest(Now.AddMinutes(1)));
        Assert.Equal(TakeawayStatus.Cancelled, order.Status);
        Assert.Equal(Now.AddMinutes(1), order.CancelledAtUtc);
        Assert.Null(order.AcceptDeadlineUtc);
        Assert.False(order.CancelByGuest(Now.AddMinutes(2)));
    }

    /// <summary>The sweep declines only a sent order whose five minutes have passed, and says nobody answered.</summary>
    [Fact]
    public void Nobody_answering_declines_only_after_the_five_minutes()
    {
        var order = Sent();

        Assert.False(order.DeclineForNoAnswer(Now.AddMinutes(4)));
        Assert.Equal(TakeawayStatus.Sent, order.Status);

        Assert.True(order.DeclineForNoAnswer(Now.AddMinutes(5)));
        Assert.Equal(TakeawayStatus.Declined, order.Status);
        Assert.Equal(TakeawayDeclineCause.NoAnswer, order.DeclinedBecause);
        Assert.Null(order.DeclinedByStaffId);

        var accepted = Sent();
        accepted.Accept(Now.AddMinutes(20), Staff, Now.AddMinutes(1));
        Assert.False(accepted.DeclineForNoAnswer(Now.AddMinutes(10)));
        Assert.Equal(TakeawayStatus.Accepted, accepted.Status);
    }

    /// <summary>A ready time in the past or more than twelve hours ahead is refused naming <c>readyAt</c>.</summary>
    [Fact]
    public void A_ready_time_must_be_from_now_to_twelve_hours_on()
    {
        var order = Sent();

        var past = Assert.Throws<FieldValidationException>(() => order.Accept(Now.AddMinutes(-2), Staff, Now));
        Assert.Equal("readyAt", Assert.Single(past.Violations).Field);
        Assert.Throws<FieldValidationException>(() => order.Accept(Now.AddHours(13), Staff, Now));
        Assert.Equal(TakeawayStatus.Sent, order.Status);

        Assert.True(order.Accept(Now.AddSeconds(-30), null, Now));
        Assert.Null(order.AcceptedByStaffId);
    }

    /// <summary>
    /// No-show only when ready, and not before thirty minutes past ready - counted from the kitchen's "ready" when that
    /// came after the promised time.
    /// </summary>
    [Fact]
    public void No_show_waits_thirty_minutes_after_ready()
    {
        var order = Sent();
        order.Accept(Now.AddMinutes(20), Staff, Now);

        // Marked ready ten minutes late: the guest was told then.
        order.MarkReady(Staff, Now.AddMinutes(30));
        Assert.Equal(Now.AddMinutes(60), order.NoShowFromUtc);

        var early = Assert.Throws<TakeawayNoShowTooEarlyException>(() => order.MarkNoShow(Staff, Now.AddMinutes(59)));
        Assert.Equal(Now.AddMinutes(60), early.NoShowFromUtc);
        Assert.Equal(TakeawayStatus.Ready, order.Status);

        Assert.True(order.MarkNoShow(Staff, Now.AddMinutes(60)));
        Assert.Equal(TakeawayStatus.NoShow, order.Status);
        Assert.False(order.MarkNoShow(Staff, Now.AddMinutes(61)));
        Assert.Throws<TakeawayStatusConflictException>(() => order.Collect(TakeawayPaymentMethod.Cash, Staff, Now.AddMinutes(62)));
    }

    /// <summary>A code that is not "T-" and three digits is refused at construction.</summary>
    [Fact]
    public void A_malformed_code_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new TakeawayOrder(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "482", new DateOnly(2026, 10, 22), "Ani", "+37491000008", true,
            Now, new TimeOnly(19, 0), null, [new TakeawayLineInput(Guid.CreateVersion7(), "Tan", 600, 1)],
            Guid.CreateVersion7(), Now));
    }

    // ---------------------------------------------------------------- domain events

    /// <summary>
    /// What an order tells its handlers (docs/domain-events.md): building raises nothing, placing raises TakeawayPlaced,
    /// each move its own event with who made it and when; a repeated or refused move raises nothing.
    /// </summary>
    [Fact]
    public void An_order_raises_one_event_for_each_thing_that_happens_to_it_and_none_for_a_repeat_or_a_refusal()
    {
        var order = Sent();
        Assert.Empty(order.DequeueEvents());

        order.Place(acceptAutomatically: false);
        var placed = Assert.IsType<TakeawayPlaced>(Assert.Single(order.DequeueEvents()).Event);
        Assert.Equal((order, false, Now), (placed.Order, placed.AcceptedOnPlacing, placed.OccurredAtUtc));
        Assert.Equal(TakeawayStatus.Sent, order.Status);
        Assert.Throws<DomainStateException>(() => order.Place(acceptAutomatically: false));

        Assert.Throws<TakeawayStatusConflictException>(() => order.MarkReady(Staff, Now.AddMinutes(1)));
        Assert.Empty(order.DequeueEvents());

        order.Accept(Now.AddMinutes(25), Staff, Now.AddMinutes(1));
        Assert.False(order.Accept(Now.AddMinutes(30), Staff, Now.AddMinutes(2)));
        var accepted = Assert.IsType<TakeawayAccepted>(Assert.Single(order.DequeueEvents()).Event);
        Assert.Equal(((Guid?)Staff, Now.AddMinutes(1)), (accepted.ByStaffId, accepted.OccurredAtUtc));

        order.MarkReady(Staff, Now.AddMinutes(24));
        Assert.Equal(Staff, Assert.IsType<TakeawayMarkedReady>(Assert.Single(order.DequeueEvents()).Event).ByStaffId);

        order.Collect(TakeawayPaymentMethod.Cash, Staff, Now.AddMinutes(40));
        Assert.False(order.Collect(TakeawayPaymentMethod.Cash, Staff, Now.AddMinutes(41)));
        Assert.Equal(Now.AddMinutes(40), Assert.IsType<TakeawayCollected>(Assert.Single(order.DequeueEvents()).Event).OccurredAtUtc);

        var missed = Sent();
        missed.Accept(Now.AddMinutes(25), Staff, Now.AddMinutes(1));
        missed.MarkReady(Staff, Now.AddMinutes(25));
        missed.DequeueEvents();
        Assert.Throws<TakeawayNoShowTooEarlyException>(() => missed.MarkNoShow(Staff, Now.AddMinutes(54)));
        Assert.Empty(missed.DequeueEvents());
        missed.MarkNoShow(Staff, Now.AddMinutes(55));
        Assert.IsType<TakeawayMarkedNoShow>(Assert.Single(missed.DequeueEvents()).Event);
    }

    /// <summary>
    /// At a branch that accepts automatically, placing is accepting - ready when the guest asked, by nobody - told as one
    /// event that says so, not a separate acceptance; the sweep accepting a late one is an acceptance by nobody.
    /// </summary>
    [Fact]
    public void Placing_at_an_automatic_branch_accepts_in_the_same_event_and_the_sweeps_acceptance_is_nobodys()
    {
        var order = Sent();
        order.Place(acceptAutomatically: true);

        var placed = Assert.IsType<TakeawayPlaced>(Assert.Single(order.DequeueEvents()).Event);
        Assert.True(placed.AcceptedOnPlacing);
        Assert.Equal(TakeawayStatus.Accepted, order.Status);
        Assert.Equal(order.RequestedPickupUtc, order.ReadyAtUtc);
        Assert.Equal(Now, order.AcceptedAtUtc);
        Assert.Null(order.AcceptedByStaffId);
        Assert.Null(order.AcceptDeadlineUtc);

        var late = Sent();
        late.Place(acceptAutomatically: false);
        late.DequeueEvents();
        late.Accept(late.RequestedPickupUtc, null, Now.AddMinutes(6));
        Assert.Null(Assert.IsType<TakeawayAccepted>(Assert.Single(late.DequeueEvents()).Event).ByStaffId);
    }

    /// <summary>Somebody's no and nobody's answer are one event, by cause; a cancel is the guest's.</summary>
    [Fact]
    public void A_decline_says_whose_and_a_cancel_is_told_once()
    {
        var declined = Sent();
        declined.Decline(" Out of lamb ", Staff, Now.AddMinutes(1));
        Assert.False(declined.Decline("again", Staff, Now.AddMinutes(2)));
        var no = Assert.IsType<TakeawayDeclined>(Assert.Single(declined.DequeueEvents()).Event);
        Assert.Equal((TakeawayDeclineCause.StaffDeclined, (Guid?)Staff), (no.Cause, no.ByStaffId));

        var lapsed = Sent();
        Assert.False(lapsed.DeclineForNoAnswer(Now.AddMinutes(4)));
        Assert.Empty(lapsed.DequeueEvents());
        lapsed.DeclineForNoAnswer(Now.AddMinutes(5));
        var nobody = Assert.IsType<TakeawayDeclined>(Assert.Single(lapsed.DequeueEvents()).Event);
        Assert.Equal((TakeawayDeclineCause.NoAnswer, (Guid?)null), (nobody.Cause, nobody.ByStaffId));

        var cancelled = Sent();
        cancelled.CancelByGuest(Now.AddMinutes(1));
        Assert.False(cancelled.CancelByGuest(Now.AddMinutes(2)));
        Assert.IsType<TakeawayCancelled>(Assert.Single(cancelled.DequeueEvents()).Event);
    }

    // ---------------------------------------------------------------- "I'm coming"

    private static readonly DateTime StartUtc = new(2026, 10, 22, 15, 30, 0, DateTimeKind.Utc);

    private static Reservation Booking() => Reservation.Create(
        branchId: Guid.CreateVersion7(),
        diningTableId: Guid.CreateVersion7(),
        startUtc: StartUtc,
        localDate: new DateOnly(2026, 10, 22),
        localStartTime: new TimeOnly(19, 30),
        partySize: 4,
        guestName: "Ani",
        guestPhone: "+37411223344",
        code: "AB7K2",
        policy: ReservationPolicy.DefaultFor(VenueType.Restaurant));

    /// <summary>
    /// Running late is 10, 15 or 30, from two hours before the start of a confirmed booking until it ends; a later answer
    /// overwrites it, and "on my way" after it clears it.
    /// </summary>
    [Fact]
    public void Running_late_takes_the_on_my_way_window_and_the_latest_answer_wins()
    {
        var booking = Booking();
        Assert.Equal(ReservationStatus.Confirmed, booking.Status);

        Assert.Throws<OnMyWayNotOpenException>(() => booking.ReportRunningLate(15, StartUtc.AddHours(-2).AddMinutes(-1)));
        Assert.Equal("minutes", Assert.Single(
            Assert.Throws<FieldValidationException>(() => booking.ReportRunningLate(20, StartUtc.AddMinutes(-30))).Violations).Field);

        booking.ReportRunningLate(10, StartUtc.AddMinutes(-40));
        Assert.Equal(10, booking.GuestRunningLateMinutes);
        Assert.True(booking.GuestAnsweredOnMyWay);

        booking.ReportRunningLate(30, StartUtc.AddMinutes(-20));
        Assert.Equal(30, booking.GuestRunningLateMinutes);
        Assert.Equal(StartUtc.AddMinutes(-20), booking.GuestRunningLateAtUtc);

        // "On my way" after "late": on time after all.
        Assert.True(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-10)));
        Assert.Null(booking.GuestRunningLateMinutes);
        Assert.Equal(StartUtc.AddMinutes(-10), booking.GuestConfirmedOnWayAtUtc);
        Assert.False(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-5)));

        // And late after "on my way": recorded beside it.
        booking.ReportRunningLate(15, StartUtc.AddMinutes(-5));
        Assert.Equal(15, booking.GuestRunningLateMinutes);
        Assert.Equal(StartUtc.AddMinutes(-10), booking.GuestConfirmedOnWayAtUtc);

        Assert.Throws<OnMyWayNotOpenException>(() => booking.ReportRunningLate(10, booking.EndUtc));
    }

    /// <summary>
    /// The venue asks from two hours before until the start, for a confirmed booking whose guest has not answered, once
    /// every fifteen minutes - each refusal its own, with the time it opens again.
    /// </summary>
    [Fact]
    public void The_venue_asks_inside_the_window_until_answered_once_every_fifteen_minutes()
    {
        var booking = Booking();

        var early = Assert.Throws<AskOnMyWayTooEarlyException>(() => booking.RecordStaffAskOnMyWay(StartUtc.AddHours(-3)));
        Assert.Equal(StartUtc.AddHours(-2), early.OpensAtUtc);

        booking.RecordStaffAskOnMyWay(StartUtc.AddMinutes(-60));
        Assert.Equal(StartUtc.AddMinutes(-60), booking.StaffAskedOnMyWayAtUtc);

        var recent = Assert.Throws<AskOnMyWayAskedRecentlyException>(() => booking.RecordStaffAskOnMyWay(StartUtc.AddMinutes(-46)));
        Assert.Equal(StartUtc.AddMinutes(-45), recent.NextAllowedAtUtc);

        booking.RecordStaffAskOnMyWay(StartUtc.AddMinutes(-45));
        Assert.Equal(StartUtc.AddMinutes(-45), booking.StaffAskedOnMyWayAtUtc);

        Assert.Throws<AskOnMyWayNotOpenException>(() => booking.RecordStaffAskOnMyWay(StartUtc));

        var answered = Booking();
        answered.ReportRunningLate(15, StartUtc.AddMinutes(-50));
        var already = Assert.Throws<GuestAlreadyAnsweredException>(() => answered.RecordStaffAskOnMyWay(StartUtc.AddMinutes(-40)));
        Assert.Equal(15, already.RunningLateMinutes);

        var cancelled = Booking();
        cancelled.CancelByVenue(StartUtc.AddHours(-3), null);
        Assert.Throws<AskOnMyWayNotOpenException>(() => cancelled.RecordStaffAskOnMyWay(StartUtc.AddMinutes(-30)));
    }

    /// <summary>A move to another start forgets the answers and the venue's ask: they were about the old time.</summary>
    [Fact]
    public void A_move_forgets_running_late_and_the_venues_ask()
    {
        var policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);
        var booking = Booking();

        booking.RecordStaffAskOnMyWay(StartUtc.AddMinutes(-60));
        booking.ReportRunningLate(10, StartUtc.AddMinutes(-50));

        booking.Reschedule(
            booking.DiningTableId, StartUtc.AddDays(1), new DateOnly(2026, 10, 23), new TimeOnly(19, 30), 4, policy,
            ReservationStatus.Confirmed, StartUtc.AddMinutes(-40));

        Assert.Null(booking.GuestRunningLateMinutes);
        Assert.Null(booking.GuestRunningLateAtUtc);
        Assert.Null(booking.StaffAskedOnMyWayAtUtc);
    }
}
