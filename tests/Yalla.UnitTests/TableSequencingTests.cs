using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// How bookings follow one another on one table, to the minute on both sides of every edge - the
/// rule the writer applies under the lock and every availability read applies with it.
/// </summary>
/// <remarks>
/// The shipped restaurant policy throughout: a 90-minute turn and <b>5 minutes</b> of clearing time.
/// The earlier booking <c>P</c> starts at 18:00 (ends 19:30); where there is a next one, <c>N</c> starts
/// at 21:30.
/// </remarks>
public class TableSequencingTests
{
    private const int Turn = 90;

    private const int Buffer = ReservationPolicy.DefaultBufferMinutes;

    private static readonly Guid PreviousId = Guid.CreateVersion7();

    private static readonly Guid NextId = Guid.CreateVersion7();

    private static DateTime At(int hour, int minute) => new(2026, 10, 20, hour, minute, 0, DateTimeKind.Utc);

    private static SequencedBooking Previous(
        ReservationStatus status = ReservationStatus.Confirmed,
        DateTime? promisedFreeBy = null,
        bool beingAsked = false,
        int turn = Turn) =>
        new(PreviousId, At(18, 0), promisedFreeBy is { } p && p < At(18, 0).AddMinutes(turn) ? p : At(18, 0).AddMinutes(turn),
            status, promisedFreeBy, beingAsked);

    private static SequencedBooking NextAt(int hour, int minute) =>
        new(NextId, At(hour, minute), At(hour, minute).AddMinutes(Turn), ReservationStatus.Confirmed);

    private static TableSequencingVerdict Judge(DateTime start, params SequencedBooking[] bookings) =>
        TableSequencing.Judge(start, Turn, Buffer, bookings);

    [Fact]
    public void The_shipped_policy_clears_a_table_in_five_minutes_and_books_from_ten_minutes_ahead()
    {
        var policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);

        Assert.Equal(5, policy.BufferMinutes);
        Assert.Equal(10, policy.MinLeadMinutes);
    }

    [Fact]
    public void An_empty_table_is_free_for_a_full_sitting()
    {
        var verdict = Judge(At(19, 0));

        Assert.True(verdict.IsFree);
        Assert.Equal(At(20, 30), verdict.EndUtc);
        Assert.Null(verdict.MustFreeByUtc);
        Assert.Null(verdict.EarlierGuestFreeByUtc);
    }

    // ---------------------------------------------------------------- 1. ninety minutes after the previous start

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(45)]
    [InlineData(89)]
    public void Less_than_ninety_minutes_after_the_previous_start_the_table_is_refused(int minutesAfter)
    {
        var verdict = Judge(At(18, 0).AddMinutes(minutesAfter), Previous());

        Assert.Equal(ReservationRejectionReason.TableJustBooked, verdict.Refusal);
        Assert.Equal(PreviousId, verdict.RefusedBecauseOf?.ReservationId);
    }

    [Fact]
    public void Exactly_ninety_minutes_after_the_previous_start_the_table_can_be_asked_for()
    {
        var verdict = Judge(At(19, 30), Previous());

        Assert.True(verdict.NeedsEarlierGuest);
        Assert.Equal(PreviousId, verdict.Earlier?.ReservationId);

        // They are asked to have left by the start less the clearing time.
        Assert.Equal(At(19, 25), verdict.EarlierGuestFreeByUtc);
    }

    [Fact]
    public void The_ninety_minutes_apply_whatever_the_turn_time()
    {
        // A cafe's two-hour turn: still refused at 89 minutes, asked for at 90.
        var cafe = new SequencedBooking(PreviousId, At(18, 0), At(20, 0), ReservationStatus.Confirmed);

        Assert.Equal(
            ReservationRejectionReason.TableJustBooked,
            TableSequencing.Judge(At(19, 29), 120, Buffer, [cafe]).Refusal);
        Assert.True(TableSequencing.Judge(At(19, 30), 120, Buffer, [cafe]).NeedsEarlierGuest);
    }

    // ---------------------------------------------------------------- 3. within three hours: ask first

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(179)]
    public void Within_three_hours_of_the_previous_start_the_earlier_guest_is_asked(int minutesAfter)
    {
        var start = At(18, 0).AddMinutes(minutesAfter);
        var verdict = Judge(start, Previous());

        Assert.True(verdict.NeedsEarlierGuest);
        Assert.False(verdict.IsFree);
        Assert.Equal(start.AddMinutes(-Buffer), verdict.EarlierGuestFreeByUtc);
    }

    [Fact]
    public void From_three_hours_after_the_previous_start_the_table_is_free_as_usual()
    {
        var verdict = Judge(At(21, 0), Previous());

        Assert.True(verdict.IsFree);
        Assert.Null(verdict.Earlier);
    }

    [Fact]
    public void A_previous_sitting_that_runs_past_three_hours_is_asked_until_it_ends_plus_clearing()
    {
        // A four-hour sitting from 18:00 ends 22:00, so 21:00 and 22:04 still need its guest's yes and
        // 22:05 does not.
        var long_ = new SequencedBooking(PreviousId, At(18, 0), At(22, 0), ReservationStatus.Confirmed);

        Assert.True(TableSequencing.Judge(At(21, 0), 60, Buffer, [long_]).NeedsEarlierGuest);
        Assert.True(TableSequencing.Judge(At(22, 4), 60, Buffer, [long_]).NeedsEarlierGuest);
        Assert.True(TableSequencing.Judge(At(22, 5), 60, Buffer, [long_]).IsFree);
    }

    [Fact]
    public void An_earlier_guest_who_already_promised_to_be_gone_in_time_is_not_asked_again()
    {
        // They accepted "free by 19:40" for somebody who has since cancelled; 19:45 needs exactly that.
        var promised = Previous(promisedFreeBy: At(19, 40));

        Assert.True(Judge(At(19, 45), promised).IsFree);

        // A promise that is not soon enough does not count: 19:44 needs them gone by 19:39.
        Assert.True(Judge(At(19, 44), promised).NeedsEarlierGuest);
    }

    [Fact]
    public void A_booking_right_after_one_that_promised_to_leave_in_time_is_not_refused_as_just_booked()
    {
        // X at 20:45 promised to be gone by 21:25 for a 21:30 booking. Judging that 21:30 again - a
        // change of party size - must find the table free, not "just booked" forty-five minutes ago.
        var x = new SequencedBooking(PreviousId, At(20, 45), At(21, 25), ReservationStatus.Confirmed, At(21, 25));

        var verdict = Judge(At(21, 30), x);

        Assert.True(verdict.IsFree);
        Assert.Null(verdict.Refusal);

        // A promise that is not soon enough is still the ninety-minute rule's.
        var notSoonEnough = x with { EndUtc = At(21, 27), PromisedFreeByUtc = At(21, 27) };

        Assert.Equal(ReservationRejectionReason.TableJustBooked, Judge(At(21, 30), notSoonEnough).Refusal);
    }

    [Fact]
    public void While_the_earlier_guest_is_being_asked_nobody_else_can_ask()
    {
        var verdict = Judge(At(19, 45), Previous(beingAsked: true));

        Assert.Equal(ReservationRejectionReason.EarlierGuestBeingAsked, verdict.Refusal);
        Assert.Equal(PreviousId, verdict.RefusedBecauseOf?.ReservationId);

        // From three hours on nobody has to be asked, so the open question does not matter.
        Assert.True(Judge(At(21, 0), Previous(beingAsked: true)).IsFree);
    }

    [Theory]
    [InlineData(ReservationStatus.PendingApproval)]
    [InlineData(ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.Seated)]
    public void Every_live_booking_counts_as_the_previous_one(ReservationStatus status)
    {
        Assert.Equal(ReservationRejectionReason.TableJustBooked, Judge(At(19, 0), Previous(status)).Refusal);
        Assert.True(Judge(At(19, 45), Previous(status)).NeedsEarlierGuest);
    }

    [Theory]
    [InlineData(ReservationStatus.Completed)]
    [InlineData(ReservationStatus.CancelledByDiner)]
    [InlineData(ReservationStatus.CancelledByVenue)]
    [InlineData(ReservationStatus.NoShow)]
    public void A_booking_that_no_longer_holds_the_table_is_ignored(ReservationStatus status)
    {
        Assert.True(Judge(At(18, 30), Previous(status)).IsFree);
    }

    [Fact]
    public void A_party_seated_late_against_the_previous_booking_is_judged_until_its_projected_end()
    {
        // Booked for 18:00, sat down at 20:00: projected to 21:30, so past the three hours 21:34 still
        // waits for them and 21:35 does not.
        var seated = Previous(ReservationStatus.Seated);
        var sitting = new OpenSitting(At(20, 0), PreviousId);

        Assert.True(TableSequencing.Judge(At(21, 34), Turn, Buffer, [seated], sitting).NeedsEarlierGuest);
        Assert.True(TableSequencing.Judge(At(21, 35), Turn, Buffer, [seated], sitting).IsFree);

        // Their sitting is judged as the booking, not as a walk-in in the way: the table can be asked
        // for, not refused as occupied.
        var asked = TableSequencing.Judge(At(19, 45), Turn, Buffer, [seated], sitting);

        Assert.Null(asked.Refusal);
        Assert.True(asked.NeedsEarlierGuest);
    }

    // ---------------------------------------------------------------- 2. forty-five minutes before the next

    [Fact]
    public void Exactly_forty_five_minutes_before_the_next_booking_is_the_latest_start()
    {
        var verdict = Judge(At(20, 45), NextAt(21, 30));

        Assert.Null(verdict.Refusal);

        // And the sitting ends five minutes before the next one starts.
        Assert.Equal(At(21, 25), verdict.MustFreeByUtc);
        Assert.Equal(At(21, 25), verdict.EndUtc);
        Assert.Equal(NextId, verdict.Next?.ReservationId);
    }

    [Theory]
    [InlineData(44)]
    [InlineData(30)]
    [InlineData(1)]
    public void Less_than_forty_five_minutes_before_the_next_booking_the_table_is_refused(int minutesBefore)
    {
        var verdict = Judge(At(21, 30).AddMinutes(-minutesBefore), NextAt(21, 30));

        Assert.Equal(ReservationRejectionReason.NextBookingTooSoon, verdict.Refusal);
        Assert.Equal(NextId, verdict.RefusedBecauseOf?.ReservationId);
    }

    // ---------------------------------------------------------------- must free by

    [Fact]
    public void A_full_sitting_that_runs_into_the_next_booking_must_free_the_table_by_next_less_five()
    {
        // 20:00 + 90 = 21:30, plus 5 clearing runs into a 21:30 booking: free by 21:25.
        var verdict = Judge(At(20, 0), NextAt(21, 30));

        Assert.True(verdict.IsFree);
        Assert.Equal(At(21, 25), verdict.MustFreeByUtc);
        Assert.Equal(At(21, 25), verdict.EndUtc);
    }

    [Fact]
    public void A_full_sitting_plus_clearing_that_ends_exactly_at_the_next_start_needs_no_promise()
    {
        // 19:55 + 90 + 5 = 21:30: back to back with the clearing time respected.
        var exactly = Judge(At(19, 55), NextAt(21, 30));

        Assert.Null(exactly.MustFreeByUtc);
        Assert.Equal(At(21, 25), exactly.EndUtc);

        // A minute later and it does.
        Assert.Equal(At(21, 25), Judge(At(19, 56), NextAt(21, 30)).MustFreeByUtc);
    }

    [Fact]
    public void The_free_by_time_is_the_next_start_less_the_branchs_own_clearing_time()
    {
        // A branch that kept fifteen minutes of clearing: the party is asked to be gone by 21:15.
        var verdict = TableSequencing.Judge(At(20, 0), Turn, 15, [NextAt(21, 30)]);

        Assert.Equal(At(21, 15), verdict.MustFreeByUtc);
    }

    [Fact]
    public void Between_two_bookings_the_guest_before_is_asked_and_the_party_must_free_by_the_next()
    {
        // P 18:00, N 21:00, asking for 19:45: P is asked to leave by 19:40, and this party to leave by 20:55.
        var verdict = Judge(At(19, 45), Previous(), NextAt(21, 0));

        Assert.True(verdict.NeedsEarlierGuest);
        Assert.Equal(At(19, 40), verdict.EarlierGuestFreeByUtc);
        Assert.Equal(At(20, 55), verdict.MustFreeByUtc);
        Assert.Equal(At(20, 55), verdict.EndUtc);
    }

    // ---------------------------------------------------------------- the net underneath

    [Fact]
    public void The_overlap_rule_still_refuses_a_clash_with_any_other_booking()
    {
        // A long booking from before these rules, 15:00-21:02, underneath P's 18:00: nothing above
        // looks past P, and the original rule still catches it.
        var clash = new SequencedBooking(Guid.CreateVersion7(), At(15, 0), At(21, 2), ReservationStatus.Confirmed);
        var refused = Judge(At(21, 0), clash, Previous(promisedFreeBy: At(19, 40)));

        Assert.Equal(ReservationRejectionReason.TableAlreadyBooked, refused.Refusal);
        Assert.Equal(clash.ReservationId, refused.RefusedBecauseOf?.ReservationId);

        // Clear of it by the clearing time, the table is free.
        Assert.True(Judge(At(21, 7), clash, Previous(promisedFreeBy: At(19, 40))).IsFree);
    }

    [Fact]
    public void A_walk_in_whose_sitting_runs_into_the_slot_refuses_it()
    {
        // Seated at 18:00, projected to 19:30, clear by 19:35.
        var walkIn = new OpenSitting(At(18, 0), null);

        Assert.Equal(
            ReservationRejectionReason.TableCurrentlyOccupied,
            TableSequencing.Judge(At(19, 34), Turn, Buffer, [], walkIn).Refusal);
        Assert.True(TableSequencing.Judge(At(19, 35), Turn, Buffer, [], walkIn).IsFree);
    }

    // ---------------------------------------------------------------- the answer window

    [Fact]
    public void The_earlier_guest_has_two_hours_never_past_their_own_start_nor_the_later_one()
    {
        var now = At(9, 0);

        Assert.Equal(At(11, 0), TableSequencing.AnswerDeadline(now, 120, At(18, 0), At(19, 45)));
        Assert.Equal(At(10, 0), TableSequencing.AnswerDeadline(now, 120, At(10, 0), At(19, 45)));

        // Already under way: the earlier start no longer caps it, the later start does.
        Assert.Equal(At(10, 30), TableSequencing.AnswerDeadline(now, 120, At(8, 0), At(10, 30)));
    }

    [Fact]
    public void A_wait_handed_to_another_guest_before_it_keeps_its_deadline_but_never_past_that_guests_start()
    {
        var now = At(9, 0);

        // Kept: sooner than two hours from now.
        Assert.Equal(At(10, 15), TableSequencing.AnswerDeadlineNoLaterThan(At(10, 15), now, 120, At(18, 0), At(19, 45)));

        // The new guest before sits down at 10:00, before the deadline it had: their start caps it.
        Assert.Equal(At(10, 0), TableSequencing.AnswerDeadlineNoLaterThan(At(10, 45), now, 120, At(10, 0), At(19, 45)));

        // A wait from before every such wait had a clock gets the two hours a new one would.
        Assert.Equal(At(11, 0), TableSequencing.AnswerDeadlineNoLaterThan(null, now, 120, At(18, 0), At(19, 45)));
    }
}
