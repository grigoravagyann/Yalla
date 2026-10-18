using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// Which bookings wait for the floor, how long the floor has to answer, and what a booking does when
/// the answer comes too late - as the domain owns it, with no database.
/// </summary>
/// <remarks>
/// Every date here is Yerevan's (UTC+4, no daylight saving), and "today" is the branch's, computed
/// through the same <see cref="BranchZone"/> the booking service uses.
/// </remarks>
public class ShortNoticeRuleTests
{
    private static readonly BranchZone Yerevan = BranchZone.For("Asia/Yerevan");

    private static readonly ReservationPolicy Policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);

    /// <summary>Is a booking for this local date and time, asked for at this local instant, short notice?</summary>
    private static bool ShortNotice(DateTime localNow, DateOnly date, TimeOnly time)
    {
        var nowUtc = Yerevan.ToUtc(DateOnly.FromDateTime(localNow), TimeOnly.FromDateTime(localNow));
        var startUtc = Yerevan.ToUtc(date, time);

        return ShortNoticeRule.Applies(startUtc, date, nowUtc, Yerevan.LocalDateAt(nowUtc));
    }

    // ---------------------------------------------------------------- which bookings

    [Fact]
    public void Any_time_today_at_the_branch_is_short_notice_and_tomorrow_beyond_six_hours_is_not()
    {
        var noon = new DateTime(2026, 9, 10, 12, 0, 0);

        // Tonight, eleven hours away: still today, so still the floor's.
        Assert.True(ShortNotice(noon, new DateOnly(2026, 9, 10), new TimeOnly(23, 0)));

        // Tomorrow lunchtime, a day away: confirmed on the spot as before.
        Assert.False(ShortNotice(noon, new DateOnly(2026, 9, 11), new TimeOnly(12, 0)));
    }

    [Fact]
    public void Today_is_the_branchs_today_so_just_after_midnight_tonight_is_the_new_day()
    {
        // 00:30 in Yerevan is 20:30 the previous day in UTC. The booking for this evening is today's at
        // the branch, whatever UTC's date says.
        var justAfterMidnight = new DateTime(2026, 9, 11, 0, 30, 0);

        Assert.True(ShortNotice(justAfterMidnight, new DateOnly(2026, 9, 11), new TimeOnly(20, 0)));

        // And tomorrow evening at the branch - the 12th - is more than six hours away and another day.
        Assert.False(ShortNotice(justAfterMidnight, new DateOnly(2026, 9, 12), new TimeOnly(20, 0)));
    }

    [Fact]
    public void After_ten_at_night_tomorrows_small_hours_are_short_notice_by_the_six_hours()
    {
        var tenPm = new DateTime(2026, 9, 10, 22, 0, 0);

        // 01:00 tomorrow is another date and three hours away.
        Assert.True(ShortNotice(tenPm, new DateOnly(2026, 9, 11), new TimeOnly(1, 0)));
    }

    [Fact]
    public void Five_hours_fifty_nine_is_short_notice_and_six_hours_exactly_is_not()
    {
        // 20:00 on the 10th. Starts on the 11th, so only the hours decide.
        var eightPm = new DateTime(2026, 9, 10, 20, 0, 0);
        var tomorrow = new DateOnly(2026, 9, 11);

        Assert.True(ShortNotice(eightPm, tomorrow, new TimeOnly(1, 59)));
        Assert.False(ShortNotice(eightPm, tomorrow, new TimeOnly(2, 0)));
    }

    // ---------------------------------------------------------------- how long the floor has

    [Fact]
    public void The_answer_window_is_fifteen_minutes_or_the_start_if_that_is_sooner()
    {
        var nowUtc = new DateTime(2026, 9, 10, 14, 0, 0, DateTimeKind.Utc);

        Assert.Equal(nowUtc.AddMinutes(15), ShortNoticeRule.AnswerDeadline(nowUtc, nowUtc.AddHours(2)));
        Assert.Equal(nowUtc.AddMinutes(10), ShortNoticeRule.AnswerDeadline(nowUtc, nowUtc.AddMinutes(10)));
        Assert.Equal(nowUtc.AddMinutes(15), ShortNoticeRule.AnswerDeadline(nowUtc, nowUtc.AddMinutes(15)));
    }

    [Fact]
    public void Availability_says_a_slot_awaits_approval_when_it_is_short_notice_even_where_nothing_else_would()
    {
        var nowUtc = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);           // noon in Yerevan
        var today = Yerevan.LocalDateAt(nowUtc);

        var tonight = Yerevan.ToUtc(today, new TimeOnly(20, 0));
        var nextWeek = Yerevan.ToUtc(today.AddDays(7), new TimeOnly(20, 0));

        Assert.True(ReservationRules.AwaitsApproval(2, Policy, tonight, today, nowUtc, today));
        Assert.False(ReservationRules.AwaitsApproval(2, Policy, nextWeek, today.AddDays(7), nowUtc, today));

        // And the two reasons it always knew still apply on any day.
        Assert.True(ReservationRules.AwaitsApproval(9, Policy, nextWeek, today.AddDays(7), nowUtc, today));
    }

    // ---------------------------------------------------------------- the booking

    private static readonly DateTime StartUtc = new(2026, 9, 10, 16, 0, 0, DateTimeKind.Utc);    // 20:00

    private static readonly DateTime MadeAtUtc = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);  // 16:00

    private static Reservation ShortNoticeBooking() =>
        Reservation.Create(
            branchId: Guid.CreateVersion7(),
            diningTableId: Guid.CreateVersion7(),
            startUtc: StartUtc,
            localDate: new DateOnly(2026, 9, 10),
            localStartTime: new TimeOnly(20, 0),
            partySize: 2,
            guestName: "Ani",
            guestPhone: "+37411223344",
            code: "ABC234",
            policy: Policy,
            initialStatus: ReservationStatus.PendingApproval,
            dinerUserId: Guid.CreateVersion7(),
            approvalTrigger: ApprovalTrigger.ShortNotice,
            approvalExpiresAtUtc: ShortNoticeRule.AnswerDeadline(MadeAtUtc, StartUtc));

    [Fact]
    public void A_short_notice_booking_stores_its_reason_and_its_deadline()
    {
        var booking = ShortNoticeBooking();

        Assert.Equal(ReservationStatus.PendingApproval, booking.Status);
        Assert.Equal(ApprovalTrigger.ShortNotice, booking.ApprovalTrigger);
        Assert.Equal(MadeAtUtc.AddMinutes(15), booking.ApprovalExpiresAtUtc);
        Assert.Null(booking.DeclineReason);
    }

    [Fact]
    public void A_booking_cannot_be_given_a_deadline_it_is_not_waiting_under_or_one_past_its_start()
    {
        Assert.Throws<ArgumentException>(() => Reservation.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), StartUtc, new DateOnly(2026, 9, 10), new TimeOnly(20, 0),
            2, "Ani", "+37411223344", "ABC234", Policy,
            initialStatus: ReservationStatus.Confirmed,
            approvalExpiresAtUtc: MadeAtUtc.AddMinutes(15)));

        Assert.Throws<ArgumentException>(() => Reservation.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), StartUtc, new DateOnly(2026, 9, 10), new TimeOnly(20, 0),
            2, "Ani", "+37411223344", "ABC234", Policy,
            initialStatus: ReservationStatus.Confirmed,
            approvalTrigger: ApprovalTrigger.ShortNotice));

        Assert.Throws<ArgumentOutOfRangeException>(() => Reservation.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), StartUtc, new DateOnly(2026, 9, 10), new TimeOnly(20, 0),
            2, "Ani", "+37411223344", "ABC234", Policy,
            initialStatus: ReservationStatus.PendingApproval,
            approvalTrigger: ApprovalTrigger.ShortNotice,
            approvalExpiresAtUtc: StartUtc.AddMinutes(1)));
    }

    [Fact]
    public void Approving_inside_the_window_confirms_and_clears_the_deadline()
    {
        var booking = ShortNoticeBooking();

        booking.Confirm(MadeAtUtc.AddMinutes(14));

        Assert.Equal(ReservationStatus.Confirmed, booking.Status);
        Assert.Null(booking.ApprovalExpiresAtUtc);

        // The reason is kept as the record of why it waited.
        Assert.Equal(ApprovalTrigger.ShortNotice, booking.ApprovalTrigger);
    }

    [Fact]
    public void Approving_or_declining_on_the_deadline_or_after_it_is_too_late()
    {
        var booking = ShortNoticeBooking();
        var deadline = booking.ApprovalExpiresAtUtc!.Value;

        var approve = Assert.Throws<ReservationApprovalExpiredException>(() => booking.Confirm(deadline));
        Assert.Equal(booking.Id, approve.ReservationId);
        Assert.Equal(deadline, approve.ApprovalExpiresAtUtc);
        Assert.IsAssignableFrom<DomainStateException>(approve);

        Assert.Throws<ReservationApprovalExpiredException>(() => booking.Reject(deadline.AddSeconds(5), "full"));

        // Nothing changed: the decision is the sweep's now.
        Assert.Equal(ReservationStatus.PendingApproval, booking.Status);
    }

    [Fact]
    public void A_booking_without_a_deadline_cannot_be_approved_once_it_has_started_but_can_still_be_declined()
    {
        var booking = Reservation.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), StartUtc, new DateOnly(2026, 9, 10), new TimeOnly(20, 0),
            10, "Ani", "+37411223344", "ABC234", Policy,
            initialStatus: ReservationStatus.PendingApproval,
            approvalTrigger: ApprovalTrigger.LargeParty);

        Assert.Throws<ReservationApprovalExpiredException>(() => booking.Confirm(StartUtc));

        booking.Reject(StartUtc.AddMinutes(5), "they never called back");

        Assert.Equal(ReservationStatus.CancelledByVenue, booking.Status);
        Assert.Equal(DeclineReason.StaffDeclined, booking.DeclineReason);
    }

    [Fact]
    public void Nobody_answering_declines_it_for_no_answer_and_only_once_the_window_has_run_out()
    {
        var booking = ShortNoticeBooking();
        var deadline = booking.ApprovalExpiresAtUtc!.Value;

        Assert.False(booking.ApprovalHasLapsedAt(deadline.AddSeconds(-1)));
        Assert.Throws<DomainStateException>(() => booking.DeclineForNoAnswer(deadline.AddSeconds(-1)));

        Assert.True(booking.ApprovalHasLapsedAt(deadline));
        booking.DeclineForNoAnswer(deadline);

        Assert.Equal(ReservationStatus.CancelledByVenue, booking.Status);
        Assert.Equal(DeclineReason.NoAnswer, booking.DeclineReason);
        Assert.Equal(deadline, booking.CancelledAtUtc);
        Assert.Null(booking.ApprovalExpiresAtUtc);
        Assert.False(booking.ApprovalHasLapsedAt(deadline.AddMinutes(1)));

        // And a decision arriving now says it is too late, not that the state is wrong.
        Assert.Throws<ReservationApprovalExpiredException>(() => booking.Confirm(deadline.AddSeconds(1)));
        Assert.Throws<ReservationApprovalExpiredException>(() => booking.Reject(deadline.AddSeconds(1), null));
    }

    [Fact]
    public void A_staff_decline_says_so_and_a_venue_releasing_an_accepted_booking_is_not_a_decline()
    {
        var declined = ShortNoticeBooking();
        declined.Reject(MadeAtUtc.AddMinutes(3), "a private event");

        Assert.Equal(DeclineReason.StaffDeclined, declined.DeclineReason);
        Assert.Null(declined.ApprovalExpiresAtUtc);

        var released = ShortNoticeBooking();
        released.Confirm(MadeAtUtc.AddMinutes(1));
        released.CancelByVenue(StartUtc.AddMinutes(30), "released: nobody arrived");

        Assert.Null(released.DeclineReason);
    }

    [Fact]
    public void A_move_back_into_the_window_starts_a_fresh_deadline_and_a_move_out_of_it_clears_it()
    {
        var booking = ShortNoticeBooking();
        booking.Confirm(MadeAtUtc.AddMinutes(2));

        var movedAt = MadeAtUtc.AddMinutes(30);
        var newStart = StartUtc.AddHours(1);

        booking.Reschedule(
            booking.DiningTableId, newStart, new DateOnly(2026, 9, 10), new TimeOnly(21, 0), 2, Policy,
            ReservationStatus.PendingApproval, movedAt,
            ApprovalTrigger.ShortNotice, ShortNoticeRule.AnswerDeadline(movedAt, newStart));

        Assert.Equal(ReservationStatus.PendingApproval, booking.Status);
        Assert.Equal(movedAt.AddMinutes(15), booking.ApprovalExpiresAtUtc);
        Assert.Null(booking.ConfirmedAtUtc);

        booking.Reschedule(
            booking.DiningTableId, newStart.AddDays(3), new DateOnly(2026, 9, 13), new TimeOnly(21, 0), 2, Policy,
            ReservationStatus.Confirmed, movedAt.AddMinutes(1));

        Assert.Equal(ReservationStatus.Confirmed, booking.Status);
        Assert.Null(booking.ApprovalExpiresAtUtc);
    }
}
