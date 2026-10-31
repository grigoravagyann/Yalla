using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// A booking taken by staff by hand (2026-10-31, <c>docs/reservations.md</c> "Staff-taken bookings"): who took it is on
/// the row, and "coming" recorded over the phone names the recorder on the event - so the History says the staff member,
/// not the guest.
/// </summary>
public class StaffBookingDomainTests
{
    private static readonly DateTime StartUtc = new(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Taker = Guid.CreateVersion7();

    private static readonly ReservationPolicy Policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);

    [Fact]
    public void A_booking_taken_by_hand_names_its_taker_and_a_diners_names_nobody()
    {
        var taken = Booking(takenBy: Taker);

        Assert.Equal(Taker, taken.TakenByStaffId);
        Assert.Equal(ReservationChannel.Staff, taken.Channel);
        Assert.Null(taken.DinerUserId);
        Assert.Null(taken.BookerPhoneE164);
        Assert.Equal(ReservationStatus.Confirmed, taken.Status);

        Assert.Null(Booking().TakenByStaffId);

        // Nobody is not a taker: the all-zeros id is refused, as every staff id is.
        Assert.Throws<ArgumentException>(() => Booking(takenBy: Guid.Empty));
    }

    [Fact]
    public void Coming_recorded_by_staff_names_the_recorder_on_the_row_and_the_event()
    {
        var booking = Booking(takenBy: Taker);

        Assert.True(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-40), recordedByStaffId: Taker));

        var coming = Assert.IsType<GuestOnTheirWay>(Single(booking));
        Assert.Same(booking, coming.Reservation);
        Assert.Equal(Taker, coming.RecordedByStaffId);
        Assert.Equal(Taker, booking.OnWayRecordedByStaffId);
        Assert.Equal(StartUtc.AddMinutes(-40), booking.GuestConfirmedOnWayAtUtc);

        // Said already: a second answer changes nothing and raises nothing.
        Assert.False(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-35), recordedByStaffId: Guid.CreateVersion7()));
        Assert.Equal(Taker, booking.OnWayRecordedByStaffId);
        Assert.Empty(booking.DequeueEvents());
    }

    [Fact]
    public void The_guests_own_answer_names_no_recorder()
    {
        var booking = Booking();

        Assert.True(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-30)));

        Assert.Null(Assert.IsType<GuestOnTheirWay>(Single(booking)).RecordedByStaffId);
        Assert.Null(booking.OnWayRecordedByStaffId);
    }

    [Fact]
    public void Running_late_and_then_coming_again_takes_the_latest_recorder()
    {
        var booking = Booking(takenBy: Taker);

        Assert.True(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-60), recordedByStaffId: Taker));
        booking.ReportRunningLate(15, StartUtc.AddMinutes(-50));
        booking.DequeueEvents();

        // "Running late" reopened the question; the guest's own "on my way" after it is theirs, not the taker's.
        Assert.True(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-45)));
        Assert.Null(booking.OnWayRecordedByStaffId);
        Assert.Null(Assert.IsType<GuestOnTheirWay>(Single(booking)).RecordedByStaffId);
    }

    [Fact]
    public void A_move_to_another_start_forgets_who_recorded_the_answer()
    {
        var booking = Booking(takenBy: Taker);
        Assert.True(booking.ConfirmOnMyWay(StartUtc.AddMinutes(-30), recordedByStaffId: Taker));
        booking.DequeueEvents();

        booking.Reschedule(
            booking.DiningTableId, StartUtc.AddDays(1), new DateOnly(2026, 11, 1), new TimeOnly(19, 0), 4, Policy,
            ReservationStatus.Confirmed, StartUtc.AddMinutes(-29));

        Assert.Null(booking.GuestConfirmedOnWayAtUtc);
        Assert.Null(booking.OnWayRecordedByStaffId);
        Assert.Contains(booking.DequeueEvents(), e => e.Event is OnMyWayAnswersForgotten);

        // The taker is not an answer about a start: a move keeps them.
        Assert.Equal(Taker, booking.TakenByStaffId);
    }

    [Fact]
    public void Coming_cannot_be_recorded_before_the_window_opens()
    {
        var booking = Booking(takenBy: Taker);

        Assert.Throws<OnMyWayNotOpenException>(() => booking.ConfirmOnMyWay(StartUtc.AddHours(-3), recordedByStaffId: Taker));
        Assert.Null(booking.OnWayRecordedByStaffId);
        Assert.Empty(booking.DequeueEvents());
    }

    private static IDomainEvent Single(Entity entity) => Assert.Single(entity.DequeueEvents()).Event;

    private static Reservation Booking(Guid? takenBy = null) => Reservation.Create(
        branchId: Guid.CreateVersion7(),
        diningTableId: Guid.CreateVersion7(),
        startUtc: StartUtc,
        localDate: new DateOnly(2026, 10, 31),
        localStartTime: new TimeOnly(19, 0),
        partySize: 4,
        guestName: "Ani",
        guestPhone: "+37411223344",
        code: "AB7K2",
        policy: Policy,
        channel: takenBy is null ? ReservationChannel.App : ReservationChannel.Staff,
        takenByStaffId: takenBy);
}
