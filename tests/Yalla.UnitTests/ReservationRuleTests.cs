using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// Every booking rule, and the distinct reason each one gives.
/// </summary>
/// <remarks>
/// The point of these is not only that a bad request is refused - it is that each refusal is
/// <b>its own</b> reason. A diner told "that table seats four" picks another table; a diner told
/// "we are closed then" picks another time; a diner told "invalid booking" leaves.
/// </remarks>
public class ReservationRuleTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 9, 10);

    private static readonly Guid BranchId = Guid.CreateVersion7();

    /// <summary>
    /// A policy with every threshold named, so each test can move exactly one of them and the
    /// numbers a test depends on are visible in the test.
    /// </summary>
    private static ReservationPolicy Policy(
        int turnTimeMinutes = 90,
        int bufferMinutes = 15,
        int minLeadMinutes = 30,
        int bookingWindowDays = 14,
        int cancellationDeadlineMinutes = 120,
        bool autoConfirm = true,
        int? maxSeatOverhang = 2,
        int? approvalRequiredAbovePartySize = 8) => new(
        turnTimeMinutes: turnTimeMinutes,
        bufferMinutes: bufferMinutes,
        graceMinutes: 15,
        lateNudgeAfterMinutes: 10,
        graceExtensionMinutes: 10,
        minLeadMinutes: minLeadMinutes,
        bookingWindowDays: bookingWindowDays,
        cancellationDeadlineMinutes: cancellationDeadlineMinutes,
        autoConfirm: autoConfirm,
        serviceChargePercent: 10m,
        pricesIncludeVat: true,
        maxSeatOverhang: maxSeatOverhang,
        approvalRequiredAbovePartySize: approvalRequiredAbovePartySize);

    private static OpeningHours Open(DayOfWeek day, string opens, string closes, bool closesNextDay = false) =>
        new(BranchId, day, TimeOnly.Parse(opens), TimeOnly.Parse(closes), closesNextDay);

    // ---------------------------------------------------------------- lead time

    [Fact]
    public void A_slot_inside_the_minimum_lead_time_is_refused_with_its_own_reason()
    {
        // Nobody books a table they are standing next to.
        var reason = ReservationRules.CheckTiming(
            Now.AddMinutes(29), Now, Today, Today, Policy(minLeadMinutes: 30));

        Assert.Equal(ReservationRejectionReason.LeadTimeTooShort, reason);
    }

    [Fact]
    public void A_slot_exactly_on_the_lead_time_boundary_is_allowed()
    {
        // A client that computed the same boundary and offered the slot must not be contradicted
        // a second later.
        var reason = ReservationRules.CheckTiming(
            Now.AddMinutes(30), Now, Today, Today, Policy(minLeadMinutes: 30));

        Assert.Null(reason);
    }

    [Fact]
    public void The_lead_time_comes_from_the_branch_and_not_from_a_constant()
    {
        var slot = Now.AddMinutes(45);

        Assert.Null(ReservationRules.CheckTiming(slot, Now, Today, Today, Policy(minLeadMinutes: 30)));
        Assert.Equal(
            ReservationRejectionReason.LeadTimeTooShort,
            ReservationRules.CheckTiming(slot, Now, Today, Today, Policy(minLeadMinutes: 60)));
    }

    // ---------------------------------------------------------------- booking window

    [Fact]
    public void A_date_beyond_the_booking_window_is_refused_with_its_own_reason()
    {
        var reason = ReservationRules.CheckTiming(
            Now.AddDays(15), Now, Today.AddDays(15), Today, Policy(bookingWindowDays: 14));

        Assert.Equal(ReservationRejectionReason.OutsideBookingWindow, reason);
    }

    [Fact]
    public void The_last_day_of_the_booking_window_is_still_bookable()
    {
        var reason = ReservationRules.CheckTiming(
            Now.AddDays(14), Now, Today.AddDays(14), Today, Policy(bookingWindowDays: 14));

        Assert.Null(reason);
    }

    // ---------------------------------------------------------------- opening hours

    [Fact]
    public void A_sitting_wholly_inside_the_opening_block_is_allowed()
    {
        // Thursday 10 September 2026, 19:00-20:30, branch open 10:00-23:00.
        var hours = new[] { Open(DayOfWeek.Thursday, "10:00", "23:00") };

        var reason = ReservationRules.CheckOpeningHours(
            hours,
            new DateTime(2026, 9, 10, 19, 0, 0),
            new DateTime(2026, 9, 10, 20, 30, 0));

        Assert.Null(reason);
    }

    [Fact]
    public void A_2230_booking_at_a_venue_open_until_0100_succeeds()
    {
        // The ClosesNextDay case. The sitting runs 22:30-00:00 and belongs to THURSDAY's block,
        // even though half of it happens on Friday. A rule that looked only at the day the
        // interval starts on would still get this one right; a rule that looked only at the day
        // each end falls on would refuse it, because Friday's block does not open until 10:00.
        var hours = new[] { Open(DayOfWeek.Thursday, "10:00", "01:00", closesNextDay: true) };

        var reason = ReservationRules.CheckOpeningHours(
            hours,
            new DateTime(2026, 9, 10, 22, 30, 0),
            new DateTime(2026, 9, 11, 0, 0, 0));

        Assert.Null(reason);
    }

    [Fact]
    public void A_sitting_after_midnight_belongs_to_the_previous_days_block()
    {
        // 00:15 on Friday is inside Thursday's 10:00-01:00. This is the case that needs the
        // previous local day examined, and the one that breaks if it is not.
        var hours = new[] { Open(DayOfWeek.Thursday, "10:00", "01:00", closesNextDay: true) };

        var reason = ReservationRules.CheckOpeningHours(
            hours,
            new DateTime(2026, 9, 11, 0, 15, 0),
            new DateTime(2026, 9, 11, 0, 45, 0));

        Assert.Null(reason);
    }

    [Fact]
    public void A_booking_outside_the_opening_hours_is_refused_with_its_own_reason()
    {
        var hours = new[] { Open(DayOfWeek.Thursday, "10:00", "23:00") };

        var reason = ReservationRules.CheckOpeningHours(
            hours,
            new DateTime(2026, 9, 10, 7, 0, 0),
            new DateTime(2026, 9, 10, 8, 30, 0));

        Assert.Equal(ReservationRejectionReason.OutsideOpeningHours, reason);
    }

    [Fact]
    public void A_sitting_that_runs_past_closing_is_refused_because_the_whole_interval_must_fit()
    {
        // Starts inside the block, ends after it. A booking the venue cannot see out is not a
        // booking, which is why the rule checks the interval and not just its start.
        var hours = new[] { Open(DayOfWeek.Thursday, "10:00", "23:00") };

        var reason = ReservationRules.CheckOpeningHours(
            hours,
            new DateTime(2026, 9, 10, 22, 30, 0),
            new DateTime(2026, 9, 11, 0, 0, 0));

        Assert.Equal(ReservationRejectionReason.OutsideOpeningHours, reason);
    }

    [Fact]
    public void A_sitting_straddling_a_split_service_is_refused()
    {
        // Lunch 12:00-15:00, dinner 18:00-23:00. 14:30-16:00 is not "open for both halves".
        var hours = new[]
        {
            Open(DayOfWeek.Thursday, "12:00", "15:00"),
            Open(DayOfWeek.Thursday, "18:00", "23:00"),
        };

        Assert.Equal(
            ReservationRejectionReason.OutsideOpeningHours,
            ReservationRules.CheckOpeningHours(
                hours, new DateTime(2026, 9, 10, 14, 30, 0), new DateTime(2026, 9, 10, 16, 0, 0)));

        Assert.Null(
            ReservationRules.CheckOpeningHours(
                hours, new DateTime(2026, 9, 10, 13, 0, 0), new DateTime(2026, 9, 10, 14, 30, 0)));
    }

    [Fact]
    public void A_day_the_branch_never_opens_is_refused()
    {
        var hours = new[] { Open(DayOfWeek.Thursday, "10:00", "23:00") };

        // Friday 11 September.
        var reason = ReservationRules.CheckOpeningHours(
            hours,
            new DateTime(2026, 9, 11, 19, 0, 0),
            new DateTime(2026, 9, 11, 20, 30, 0));

        Assert.Equal(ReservationRejectionReason.OutsideOpeningHours, reason);
    }

    // ---------------------------------------------------------------- capacity

    [Fact]
    public void A_party_larger_than_the_table_is_refused_with_its_own_reason()
    {
        var reason = ReservationRules.CheckTable(
            TableStatus.Free, isBookable: true, seats: 4, partySize: 5, Policy());

        Assert.Equal(ReservationRejectionReason.PartyExceedsCapacity, reason);
    }

    [Fact]
    public void A_party_that_exactly_fills_the_table_is_allowed()
    {
        var reason = ReservationRules.CheckTable(
            TableStatus.Free, isBookable: true, seats: 4, partySize: 4, Policy());

        Assert.Null(reason);
    }

    // ---------------------------------------------------------------- seat overhang

    [Fact]
    public void Two_people_are_refused_the_eight_seater_when_the_branch_caps_the_overhang_at_two()
    {
        // Six seats would sit empty on a Friday. Its own reason, not "party exceeds capacity" -
        // the two need completely different messages.
        var reason = ReservationRules.CheckTable(
            TableStatus.Free, isBookable: true, seats: 8, partySize: 2, Policy(maxSeatOverhang: 2));

        Assert.Equal(ReservationRejectionReason.SeatOverhangExceeded, reason);
    }

    [Fact]
    public void Two_people_may_take_the_eight_seater_when_the_branch_sets_no_overhang_limit()
    {
        // Null means the owner does not care, which is the right answer for a quiet weekday cafe.
        var reason = ReservationRules.CheckTable(
            TableStatus.Free, isBookable: true, seats: 8, partySize: 2, Policy(maxSeatOverhang: null));

        Assert.Null(reason);
    }

    [Fact]
    public void An_overhang_exactly_on_the_limit_is_allowed()
    {
        // Two empty seats with a limit of two. Strictly greater is what is refused.
        var reason = ReservationRules.CheckTable(
            TableStatus.Free, isBookable: true, seats: 4, partySize: 2, Policy(maxSeatOverhang: 2));

        Assert.Null(reason);
    }

    // ---------------------------------------------------------------- bookability

    [Fact]
    public void A_table_that_never_takes_bookings_is_refused_with_its_own_reason()
    {
        var reason = ReservationRules.CheckTable(
            TableStatus.Free, isBookable: false, seats: 4, partySize: 2, Policy());

        Assert.Equal(ReservationRejectionReason.TableNotBookable, reason);
    }

    [Fact]
    public void An_out_of_service_table_is_refused_with_its_own_reason()
    {
        var reason = ReservationRules.CheckTable(
            TableStatus.OutOfService, isBookable: true, seats: 4, partySize: 2, Policy());

        Assert.Equal(ReservationRejectionReason.TableOutOfService, reason);
    }

    [Theory]
    [InlineData(TableStatus.Free)]
    [InlineData(TableStatus.Held)]
    [InlineData(TableStatus.Occupied)]
    public void A_table_in_use_now_may_still_be_booked_for_later(TableStatus status)
    {
        // Occupied is a fact about right now. Somebody sitting there at lunchtime says nothing
        // about whether the table can be promised for eight o'clock, and refusing on it would
        // make the busiest tables the hardest to book.
        var reason = ReservationRules.CheckTable(
            status, isBookable: true, seats: 4, partySize: 2, Policy());

        Assert.Null(reason);
    }

    // ---------------------------------------------------------------- approval, not rejection

    [Fact]
    public void A_party_above_the_approval_threshold_needs_approval_rather_than_being_refused()
    {
        var policy = Policy(approvalRequiredAbovePartySize: 8);

        Assert.True(ReservationRules.NeedsApproval(9, policy));

        // And it is not a rejection: the table rules still pass it.
        Assert.Null(ReservationRules.CheckTable(
            TableStatus.Free, isBookable: true, seats: 10, partySize: 9, Policy(maxSeatOverhang: null)));
    }

    [Fact]
    public void A_party_exactly_on_the_approval_threshold_is_confirmed_outright()
    {
        Assert.False(ReservationRules.NeedsApproval(8, Policy(approvalRequiredAbovePartySize: 8)));
    }

    [Fact]
    public void A_branch_with_no_approval_threshold_never_asks_for_one()
    {
        Assert.False(ReservationRules.NeedsApproval(30, Policy(approvalRequiredAbovePartySize: null)));
    }

    // ---------------------------------------------------------------- cancellation deadline

    [Fact]
    public void A_cancellation_before_the_deadline_is_not_late()
    {
        var start = Now.AddHours(5);

        Assert.False(ReservationRules.IsLateCancellation(
            start, Now, Policy(cancellationDeadlineMinutes: 120)));
    }

    [Fact]
    public void A_cancellation_inside_the_deadline_is_recorded_as_late()
    {
        var start = Now.AddMinutes(60);

        Assert.True(ReservationRules.IsLateCancellation(
            start, Now, Policy(cancellationDeadlineMinutes: 120)));
    }

    [Fact]
    public void Every_rule_gives_a_different_reason()
    {
        // The guarantee the client depends on: eight rules, eight answers. If two ever collapsed
        // into one, an app could only show a generic message for both.
        var reasons = new[]
        {
            ReservationRules.CheckTiming(
                Now.AddMinutes(1), Now, Today, Today, Policy()),
            ReservationRules.CheckTiming(
                Now.AddDays(30), Now, Today.AddDays(30), Today, Policy()),
            ReservationRules.CheckOpeningHours(
                [Open(DayOfWeek.Thursday, "10:00", "23:00")],
                new DateTime(2026, 9, 10, 3, 0, 0),
                new DateTime(2026, 9, 10, 4, 30, 0)),
            ReservationRules.CheckTable(TableStatus.Free, true, seats: 2, partySize: 6, Policy()),
            ReservationRules.CheckTable(TableStatus.Free, true, seats: 8, partySize: 2, Policy()),
            ReservationRules.CheckTable(TableStatus.Free, false, seats: 4, partySize: 2, Policy()),
            ReservationRules.CheckTable(TableStatus.OutOfService, true, seats: 4, partySize: 2, Policy()),
        };

        Assert.All(reasons, reason => Assert.True(reason.HasValue, "A rule accepted what it should refuse."));
        Assert.Equal(reasons.Length, reasons.Distinct().Count());
    }
}
