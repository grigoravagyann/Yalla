using Yalla.Application.BranchSettings;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests;

/// <summary>Booking by time only: the room's peak over a sitting, and the policy that asks for it.</summary>
public sealed class GuestsAtOnceTests
{
    private static readonly DateTime Seven = new(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc);

    private static BookedInterval Sitting(int fromMinutes, int toMinutes) =>
        new(Seven.AddMinutes(fromMinutes), Seven.AddMinutes(toMinutes));

    [Fact]
    public void Two_parties_that_never_overlap_do_not_add_up_but_both_inside_the_window_count_at_their_peak()
    {
        // 19:00-20:30 four, 20:30-22:00 three: a sitting from 19:30 to 21:00 meets both, never at once.
        var bookings = new[] { (Sitting(0, 90), 4), (Sitting(90, 180), 3) };

        Assert.Equal(4, GuestsAtOnce.PeakWithin(bookings, Sitting(30, 120)));
        Assert.Equal(2, GuestsAtOnce.LeftWithin(bookings, Sitting(30, 120), capacity: 6));
    }

    [Fact]
    public void Overlapping_parties_add_up_and_a_sitting_outside_them_has_the_whole_room()
    {
        var bookings = new[] { (Sitting(0, 90), 4), (Sitting(30, 120), 2) };

        Assert.Equal(6, GuestsAtOnce.PeakWithin(bookings, Sitting(30, 120)));
        Assert.Equal(0, GuestsAtOnce.LeftWithin(bookings, Sitting(30, 120), capacity: 6));
        Assert.Equal(6, GuestsAtOnce.LeftWithin(bookings, Sitting(120, 210), capacity: 6));
    }

    [Fact]
    public void Booking_by_time_only_needs_how_many_guests_at_once_and_an_older_client_keeps_the_mode()
    {
        var refused = Assert.Throws<FieldValidationException>(() => Command(BookingMode.TimeOnly, null).ToPolicy());
        Assert.Contains(refused.Violations, v => v.Field == "guestsAtOnce");

        var timeOnly = Command(BookingMode.TimeOnly, 40).ToPolicy();
        Assert.Equal(BookingMode.TimeOnly, timeOnly.BookingMode);
        Assert.Equal(40, timeOnly.GuestsAtOnce);

        // A client from before the mode sends neither: the branch keeps booking by time only, with its capacity.
        var kept = Command(null, null).ToPolicy(timeOnly);
        Assert.Equal(BookingMode.TimeOnly, kept.BookingMode);
        Assert.Equal(40, kept.GuestsAtOnce);

        // Back to tables: the capacity goes with the mode.
        Assert.Null(Command(BookingMode.ChooseTable, 40).ToPolicy(timeOnly).GuestsAtOnce);
    }

    private static ReservationPolicyCommand Command(BookingMode? mode, int? guestsAtOnce) =>
        new(90, 5, 15, 10, 10, 10, 14, 120, true, 10m, true, 2, 8, BookingMode: mode, GuestsAtOnce: guestsAtOnce);
}
