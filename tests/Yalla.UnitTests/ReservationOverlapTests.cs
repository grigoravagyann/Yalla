using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests;

/// <summary>
/// The overlap rule at its boundaries.
/// </summary>
/// <remarks>
/// <para>
/// This is the rule the whole product rests on: get it wrong one way and two parties are sent to
/// the same table, get it wrong the other way and the venue cannot sell back-to-back sittings -
/// which is where a restaurant's money is. So it is a pure function and it is tested to the
/// minute, on both sides of every edge.
/// </para>
/// <para>
/// Existing booking throughout: <b>18:00-19:30</b>, clearing time <b>15 minutes</b>. The table is
/// therefore unsellable from 17:45 to 19:45, and sellable at exactly those two instants.
/// </para>
/// </remarks>
public class ReservationOverlapTests
{
    private const int BufferMinutes = 15;

    private static readonly BookedInterval Existing = new(
        new DateTime(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 10, 19, 30, 0, DateTimeKind.Utc));

    /// <summary>A 90-minute sitting starting at the given wall-clock UTC time.</summary>
    private static BookedInterval Sitting(int hour, int minute) =>
        Sitting(hour, minute, minutes: 90);

    private static BookedInterval Sitting(int hour, int minute, int minutes)
    {
        var start = new DateTime(2026, 9, 10, hour, minute, 0, DateTimeKind.Utc);
        return new BookedInterval(start, start.AddMinutes(minutes));
    }

    // ---------------------------------------------------------------- after the existing booking

    [Fact]
    public void A_sitting_starting_exactly_at_the_end_plus_the_buffer_is_allowed()
    {
        // 19:30 + 15 = 19:45. Back-to-back sittings with the clearing time respected are legal,
        // and they are how a venue makes money. Refusing this loses a whole second cover.
        var proposed = Sitting(19, 45);

        Assert.False(ReservationOverlap.Conflicts(proposed, Existing, BufferMinutes));
    }

    [Fact]
    public void A_sitting_starting_one_minute_earlier_conflicts()
    {
        // 19:44 leaves 14 minutes to clear a table the branch says needs 15.
        var proposed = Sitting(19, 44);

        Assert.True(ReservationOverlap.Conflicts(proposed, Existing, BufferMinutes));
    }

    // ---------------------------------------------------------------- before the existing booking

    [Fact]
    public void A_sitting_ending_exactly_at_the_start_less_the_buffer_is_allowed()
    {
        // Ends 17:45, which is 18:00 - 15. The table is clear in time.
        var proposed = Sitting(16, 15);

        Assert.Equal(new DateTime(2026, 9, 10, 17, 45, 0, DateTimeKind.Utc), proposed.EndUtc);
        Assert.False(ReservationOverlap.Conflicts(proposed, Existing, BufferMinutes));
    }

    [Fact]
    public void A_sitting_ending_one_minute_later_conflicts()
    {
        var proposed = Sitting(16, 16);

        Assert.Equal(new DateTime(2026, 9, 10, 17, 46, 0, DateTimeKind.Utc), proposed.EndUtc);
        Assert.True(ReservationOverlap.Conflicts(proposed, Existing, BufferMinutes));
    }

    // ---------------------------------------------------------------- containment

    [Fact]
    public void A_sitting_that_wholly_contains_an_existing_one_conflicts()
    {
        // 17:00-21:00 swallows 18:00-19:30. Neither end is inside the other interval's ends, which
        // is exactly the case a naive "does either endpoint fall inside?" test misses.
        var proposed = Sitting(17, 0, minutes: 240);

        Assert.True(ReservationOverlap.Conflicts(proposed, Existing, BufferMinutes));
    }

    [Fact]
    public void A_sitting_wholly_inside_an_existing_one_conflicts()
    {
        var proposed = Sitting(18, 15, minutes: 30);

        Assert.True(ReservationOverlap.Conflicts(proposed, Existing, BufferMinutes));
    }

    [Fact]
    public void An_identical_sitting_conflicts()
    {
        Assert.True(ReservationOverlap.Conflicts(Existing, Existing, BufferMinutes));
    }

    // ---------------------------------------------------------------- the buffer is a setting

    [Fact]
    public void With_no_clearing_time_a_sitting_may_start_the_instant_the_last_one_ends()
    {
        // A cafe that turns a table by wiping it. Nothing here is a constant, so zero works.
        var proposed = Sitting(19, 30);

        Assert.False(ReservationOverlap.Conflicts(proposed, Existing, bufferMinutes: 0));
        Assert.True(ReservationOverlap.Conflicts(Sitting(19, 29), Existing, bufferMinutes: 0));
    }

    [Fact]
    public void A_longer_clearing_time_pushes_the_boundary_out()
    {
        // The same 19:45 sitting that a 15-minute branch allows, a 30-minute branch refuses.
        Assert.True(ReservationOverlap.Conflicts(Sitting(19, 45), Existing, bufferMinutes: 30));
        Assert.False(ReservationOverlap.Conflicts(Sitting(20, 0), Existing, bufferMinutes: 30));
    }

    [Fact]
    public void A_negative_clearing_time_is_refused_rather_than_silently_narrowing_the_rule()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReservationOverlap.Conflicts(Sitting(19, 45), Existing, bufferMinutes: -15));
    }

    // ---------------------------------------------------------------- which statuses block

    [Theory]
    [InlineData(ReservationStatus.CancelledByDiner)]
    [InlineData(ReservationStatus.CancelledByVenue)]
    [InlineData(ReservationStatus.NoShow)]
    [InlineData(ReservationStatus.Completed)]
    public void A_finished_booking_blocks_nothing(ReservationStatus status)
    {
        // Somebody changed their mind at lunchtime. Counting that as a conflict would leave the
        // table unbookable for the rest of the evening for no reason at all.
        Assert.False(ReservationOverlap.Blocks(status));

        // Same interval, exactly overlapping - and still not a conflict.
        Assert.False(ReservationOverlap.Conflicts(Existing, Existing, status, BufferMinutes));
    }

    [Theory]
    [InlineData(ReservationStatus.PendingApproval)]
    [InlineData(ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.Seated)]
    public void A_live_booking_blocks(ReservationStatus status)
    {
        // PendingApproval holds the slot too: offering it to somebody else while a manager decides
        // is how one of the two parties gets turned away at the door.
        Assert.True(ReservationOverlap.Blocks(status));
        Assert.True(ReservationOverlap.Conflicts(Existing, Existing, status, BufferMinutes));
    }

    // ---------------------------------------------------------------- the SQL search window

    [Fact]
    public void The_search_window_is_the_rule_rearranged_and_catches_every_conflict()
    {
        // The persistence layer narrows candidates with a range predicate so SQL Server can seek
        // the index, then confirms each one with the rule above. That is only safe if the window
        // is genuinely wider than the rule - so check every minute across the whole boundary.
        var proposed = Sitting(19, 0);
        var (searchFrom, searchTo) = ReservationOverlap.SearchWindow(proposed, BufferMinutes);

        for (var offset = -300; offset <= 300; offset++)
        {
            var candidate = new BookedInterval(
                Existing.StartUtc.AddMinutes(offset), Existing.EndUtc.AddMinutes(offset));

            var conflicts = ReservationOverlap.Conflicts(proposed, candidate, BufferMinutes);
            var insideWindow = candidate.EndUtc > searchFrom && candidate.StartUtc < searchTo;

            Assert.True(
                !conflicts || insideWindow,
                $"A conflicting booking at offset {offset} would be missed by the range predicate.");
        }
    }
}
