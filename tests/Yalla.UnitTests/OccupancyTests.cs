using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

public class ReservationTests
{
    private static readonly DateTime StartUtc = new(2026, 9, 4, 16, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void The_booked_interval_ends_one_turn_time_after_it_starts()
    {
        var policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);

        var reservation = NewReservation(policy);

        Assert.Equal(StartUtc, reservation.StartUtc);
        Assert.Equal(StartUtc.AddMinutes(90), reservation.EndUtc);
    }

    [Fact]
    public void The_buffer_is_not_baked_into_the_interval()
    {
        var policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);

        var reservation = NewReservation(policy);

        // 90 minutes of turn time, not 90 + 15 of buffer: the interval is what the diner booked.
        Assert.Equal(TimeSpan.FromMinutes(policy.TurnTimeMinutes), reservation.EndUtc - reservation.StartUtc);
    }

    [Fact]
    public void A_booking_cannot_start_life_already_seated()
    {
        var policy = ReservationPolicy.DefaultFor(VenueType.Cafe);

        var construct = () => NewReservation(policy, ReservationStatus.Seated);

        Assert.Throws<ArgumentOutOfRangeException>(construct);
    }

    [Fact]
    public void The_code_is_stored_upper_case_so_the_door_can_match_it()
    {
        var reservation = NewReservation(ReservationPolicy.DefaultFor(VenueType.Cafe), code: "ab7k2");

        Assert.Equal("AB7K2", reservation.Code);
    }

    private static Reservation NewReservation(
        ReservationPolicy policy,
        ReservationStatus status = ReservationStatus.Confirmed,
        string code = "AB7K2") =>
        Reservation.Create(
            branchId: Guid.CreateVersion7(),
            diningTableId: Guid.CreateVersion7(),
            startUtc: StartUtc,
            localDate: new DateOnly(2026, 9, 4),
            localStartTime: new TimeOnly(20, 30),
            partySize: 4,
            guestName: "Ani",
            guestPhone: "+37411223344",
            code: code,
            policy: policy,
            initialStatus: status);
}

public class TableSessionTests
{
    private static readonly DateTime SeatedAt = new(2026, 9, 4, 16, 45, 0, DateTimeKind.Utc);

    [Fact]
    public void A_walk_in_needs_no_reservation()
    {
        var session = TableSession.SeatWalkIn(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, SeatedAt);

        Assert.Equal(TableSessionSource.WalkIn, session.Source);
        Assert.Null(session.ReservationId);
        Assert.True(session.IsOpen);
    }

    [Fact]
    public void A_session_seated_from_a_booking_references_it()
    {
        var reservationId = Guid.CreateVersion7();

        var session = TableSession.SeatReservation(
            Guid.CreateVersion7(), Guid.CreateVersion7(), reservationId, 4, SeatedAt);

        Assert.Equal(TableSessionSource.Reservation, session.Source);
        Assert.Equal(reservationId, session.ReservationId);
    }

    [Fact]
    public void Closing_records_how_long_the_party_stayed()
    {
        var session = TableSession.SeatWalkIn(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, SeatedAt);

        session.Close(SeatedAt.AddMinutes(75));

        Assert.False(session.IsOpen);
        Assert.Equal(TimeSpan.FromMinutes(75), session.Duration);
    }

    [Fact]
    public void A_session_cannot_close_before_it_was_seated()
    {
        var session = TableSession.SeatWalkIn(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, SeatedAt);

        Assert.Throws<ArgumentException>(() => session.Close(SeatedAt.AddMinutes(-1)));
    }
}

public class DiningTableTests
{
    [Fact]
    public void An_occupied_table_must_name_the_session_seated_at_it()
    {
        var table = NewTable();

        Assert.Throws<ArgumentException>(() => table.ApplyStatus(TableStatus.Occupied));
    }

    [Fact]
    public void A_free_table_must_not_reference_a_session()
    {
        var table = NewTable();

        Assert.Throws<ArgumentException>(() => table.ApplyStatus(TableStatus.Free, Guid.CreateVersion7()));
    }

    [Fact]
    public void Seating_a_session_caches_it_on_the_table()
    {
        var table = NewTable();
        var sessionId = Guid.CreateVersion7();

        table.ApplyStatus(TableStatus.Occupied, sessionId);

        Assert.Equal(TableStatus.Occupied, table.Status);
        Assert.Equal(sessionId, table.CurrentSessionId);
    }

    [Fact]
    public void A_new_table_starts_free_with_its_own_qr_token()
    {
        var table = NewTable();

        Assert.Equal(TableStatus.Free, table.Status);
        Assert.Null(table.CurrentSessionId);
        Assert.NotEmpty(table.QrToken);
        Assert.NotEqual(table.QrToken, NewTable().QrToken);
    }

    private static DiningTable NewTable() => new(
        branchId: Guid.CreateVersion7(),
        label: "7",
        seats: 4,
        x: 100,
        y: 200,
        width: 80,
        height: 80,
        shape: TableShape.Round);
}

public class OpeningHoursTests
{
    [Fact]
    public void A_normal_day_must_close_after_it_opens()
    {
        var construct = () => new OpeningHours(
            Guid.CreateVersion7(), DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(9, 0), closesNextDay: false);

        Assert.Throws<ArgumentException>(construct);
    }

    [Fact]
    public void A_late_night_spans_midnight()
    {
        var hours = new OpeningHours(
            Guid.CreateVersion7(), DayOfWeek.Friday, new TimeOnly(10, 0), new TimeOnly(1, 0), closesNextDay: true);

        Assert.Equal(TimeSpan.FromHours(15), hours.Duration);
    }
}
