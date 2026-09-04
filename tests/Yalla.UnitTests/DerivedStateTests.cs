using Yalla.Domain;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The two rules that replaced stored <c>Reserved</c> and stored <c>Late</c>. Pure functions of
/// the clock and the branch policy, which is the whole argument for deriving them.
/// </summary>
public class DerivedTableStateTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 16, 0, 0, DateTimeKind.Utc);

    private const int BufferMinutes = 15;

    [Fact]
    public void A_free_table_with_no_booking_reads_as_free()
    {
        var state = TableStateProjection.Derive(TableStatus.Free, null, Now, BufferMinutes);

        Assert.Equal(DerivedTableState.Free, state);
    }

    [Fact]
    public void A_free_table_reads_as_reserved_once_the_booking_is_inside_the_buffer()
    {
        // 14 minutes away, buffer is 15: the table has to be clear and reset by now.
        var state = TableStateProjection.Derive(
            TableStatus.Free, Now.AddMinutes(14), Now, BufferMinutes);

        Assert.Equal(DerivedTableState.ReservedSoon, state);
    }

    [Fact]
    public void A_free_table_with_a_distant_booking_is_still_sellable()
    {
        var state = TableStateProjection.Derive(
            TableStatus.Free, Now.AddMinutes(90), Now, BufferMinutes);

        Assert.Equal(DerivedTableState.Free, state);
    }

    [Theory]
    [InlineData(TableStatus.Occupied, DerivedTableState.Occupied)]
    [InlineData(TableStatus.Held, DerivedTableState.Held)]
    [InlineData(TableStatus.OutOfService, DerivedTableState.OutOfService)]
    public void A_physical_fact_is_never_overridden_by_a_booking(TableStatus physical, DerivedTableState expected)
    {
        // Somebody is sitting there, or the table is broken. An imminent booking does not change
        // that, and showing "reserved" over an occupied table would be a lie.
        var state = TableStateProjection.Derive(physical, Now.AddMinutes(1), Now, BufferMinutes);

        Assert.Equal(expected, state);
    }

    [Fact]
    public void The_free_until_window_is_the_booking_less_the_turnaround()
    {
        var bookingAt = Now.AddMinutes(60);

        var freeUntil = TableStateProjection.FreeUntil(bookingAt, BufferMinutes);

        Assert.Equal(bookingAt.AddMinutes(-BufferMinutes), freeUntil);
    }

    [Fact]
    public void There_is_no_free_until_window_when_nothing_is_booked()
    {
        Assert.Null(TableStateProjection.FreeUntil(null, BufferMinutes));
    }

    [Fact]
    public void Seating_collides_when_the_booking_starts_inside_the_turn_time()
    {
        const int turnTime = 90;

        Assert.True(TableStateProjection.SeatingCollidesWithReservation(
            Now.AddMinutes(45), Now, turnTime));

        Assert.False(TableStateProjection.SeatingCollidesWithReservation(
            Now.AddMinutes(120), Now, turnTime));
    }
}

/// <summary>Lateness, derived rather than stored.</summary>
public class ReservationLatenessTests
{
    private static readonly DateTime StartUtc = new(2026, 9, 4, 16, 0, 0, DateTimeKind.Utc);

    private const int GraceMinutes = 15;

    [Fact]
    public void A_booking_is_not_late_inside_its_grace_window()
    {
        var reservation = Confirmed();

        Assert.False(reservation.IsLateAt(StartUtc.AddMinutes(14), GraceMinutes));
        Assert.Null(reservation.LatenessAt(StartUtc.AddMinutes(14), GraceMinutes));
    }

    [Fact]
    public void A_booking_is_late_once_grace_has_run_out()
    {
        var reservation = Confirmed();

        Assert.True(reservation.IsLateAt(StartUtc.AddMinutes(16), GraceMinutes));
        Assert.Equal(TimeSpan.FromMinutes(16), reservation.LatenessAt(StartUtc.AddMinutes(16), GraceMinutes));
    }

    [Fact]
    public void A_seated_party_is_never_late()
    {
        var reservation = Confirmed();
        reservation.MarkSeated();

        // They arrived. There is no timer to switch off and no stale status to clean up, which is
        // exactly why ReservationStatus has no Late member.
        Assert.False(reservation.IsLateAt(StartUtc.AddHours(3), GraceMinutes));
    }

    [Fact]
    public void The_stored_statuses_are_all_things_somebody_did()
    {
        var stored = Enum.GetNames<ReservationStatus>();

        Assert.DoesNotContain("Late", stored);
        Assert.Equal(7, stored.Length);
    }

    [Fact]
    public void Only_a_confirmed_booking_can_be_seated()
    {
        var reservation = Confirmed();
        reservation.MarkSeated();

        Assert.Throws<DomainStateException>(reservation.MarkSeated);
    }

    [Fact]
    public void A_seated_booking_completes_when_its_session_closes()
    {
        var reservation = Confirmed();
        reservation.MarkSeated();

        reservation.MarkCompleted();

        Assert.Equal(ReservationStatus.Completed, reservation.Status);
    }

    private static Reservation Confirmed() => Reservation.Create(
        branchId: Guid.CreateVersion7(),
        diningTableId: Guid.CreateVersion7(),
        startUtc: StartUtc,
        localDate: new DateOnly(2026, 9, 4),
        localStartTime: new TimeOnly(20, 0),
        partySize: 4,
        guestName: "Ani",
        guestPhone: "+37411223344",
        code: "AB7K2",
        policy: ReservationPolicy.DefaultFor(VenueType.Restaurant));
}
