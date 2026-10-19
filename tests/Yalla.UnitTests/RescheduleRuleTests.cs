using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// Moving a booking, as the domain owns it: what stays the booking's, what is recomputed, and when it
/// can no longer move.
/// </summary>
public class RescheduleRuleTests
{
    /// <summary>20:00 on Friday 4 September 2026 in Yerevan.</summary>
    private static readonly DateTime StartUtc = new(2026, 9, 4, 16, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime NowUtc = StartUtc.AddDays(-1);

    private static readonly ReservationPolicy Policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);

    [Fact]
    public void A_move_keeps_the_code_the_link_and_the_note_and_recomputes_the_interval()
    {
        var booking = Booking(ReservationStatus.Confirmed);
        booking.AttachManageToken(new string('a', 64));

        var code = booking.Code;
        var commandId = booking.ClientCommandId;
        var tableId = Guid.CreateVersion7();
        var newStart = StartUtc.AddHours(1);

        booking.Reschedule(
            tableId, newStart, new DateOnly(2026, 9, 4), new TimeOnly(21, 0), 3, Policy, ReservationStatus.Confirmed, NowUtc);

        Assert.Equal(code, booking.Code);
        Assert.Equal(new string('a', 64), booking.ManageTokenHash);
        Assert.Equal(commandId, booking.ClientCommandId);
        Assert.Equal("window table", booking.Note);
        Assert.Equal(tableId, booking.DiningTableId);
        Assert.Equal(newStart, booking.StartUtc);
        Assert.Equal(newStart.AddMinutes(Policy.TurnTimeMinutes), booking.EndUtc);
        Assert.Equal(new TimeOnly(21, 0), booking.LocalStartTime);
        Assert.Equal(3, booking.PartySize);
        Assert.Equal(ReservationStatus.Confirmed, booking.Status);
    }

    [Fact]
    public void Sending_a_booking_back_for_approval_clears_the_approval_it_had()
    {
        var booking = Booking(ReservationStatus.PendingApproval);
        booking.Confirm(NowUtc.AddHours(-1));

        booking.Reschedule(
            booking.DiningTableId, StartUtc, booking.LocalDate, booking.LocalStartTime, 10, Policy,
            ReservationStatus.PendingApproval, NowUtc);

        Assert.Equal(ReservationStatus.PendingApproval, booking.Status);
        Assert.Null(booking.ConfirmedAtUtc);

        // And a pending one may come back confirmed: the party shrank under the threshold.
        booking.Reschedule(
            booking.DiningTableId, StartUtc, booking.LocalDate, booking.LocalStartTime, 4, Policy,
            ReservationStatus.Confirmed, NowUtc);

        Assert.Equal(ReservationStatus.Confirmed, booking.Status);
    }

    [Theory]
    [InlineData(ReservationStatus.Seated)]
    [InlineData(ReservationStatus.Completed)]
    [InlineData(ReservationStatus.CancelledByDiner)]
    [InlineData(ReservationStatus.CancelledByVenue)]
    [InlineData(ReservationStatus.NoShow)]
    public void A_booking_that_is_finished_with_cannot_be_moved(ReservationStatus status)
    {
        var booking = InStatus(status);

        Assert.False(booking.IsChangeableAt(NowUtc));

        var refused = Assert.Throws<ReservationNotChangeableException>(() => booking.Reschedule(
            booking.DiningTableId, StartUtc.AddDays(1), booking.LocalDate.AddDays(1), booking.LocalStartTime, 2,
            Policy, ReservationStatus.Confirmed, NowUtc));

        Assert.Equal(booking.Id, refused.ReservationId);
        Assert.Equal(status, refused.Status);

        // Still a refused state, so whatever handles one handles this.
        Assert.IsAssignableFrom<DomainStateException>(refused);
        Assert.Equal(StartUtc, booking.StartUtc);
    }

    [Fact]
    public void A_booking_cannot_be_moved_from_the_moment_it_starts()
    {
        var booking = Booking(ReservationStatus.Confirmed);

        Assert.True(booking.IsChangeableAt(StartUtc.AddTicks(-1)));
        Assert.False(booking.IsChangeableAt(StartUtc));

        var refused = Assert.Throws<ReservationNotChangeableException>(() => booking.RequireChangeableAt(StartUtc));

        Assert.Contains("already started", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_move_lands_confirmed_or_pending_and_nothing_else()
    {
        var booking = Booking(ReservationStatus.Confirmed);

        Assert.Throws<ArgumentOutOfRangeException>(() => booking.Reschedule(
            booking.DiningTableId, StartUtc, booking.LocalDate, booking.LocalStartTime, 2, Policy,
            ReservationStatus.Seated, NowUtc));

        Assert.Equal(ReservationStatus.Confirmed, booking.Status);
    }

    private static Reservation InStatus(ReservationStatus status)
    {
        var booking = Booking(ReservationStatus.Confirmed);

        switch (status)
        {
            case ReservationStatus.Seated:
                booking.MarkSeated();
                break;

            case ReservationStatus.Completed:
                booking.MarkSeated();
                booking.MarkCompleted();
                break;

            case ReservationStatus.CancelledByDiner:
                booking.CancelByDiner(NowUtc, null, afterDeadline: false);
                break;

            case ReservationStatus.CancelledByVenue:
                booking.CancelByVenue(NowUtc, null);
                break;

            case ReservationStatus.NoShow:
                // A no-show is recorded from the start, never the day before it.
                booking.MarkNoShow(StartUtc);
                break;
        }

        return booking;
    }

    private static Reservation Booking(ReservationStatus status) =>
        Reservation.Create(
            branchId: Guid.CreateVersion7(),
            diningTableId: Guid.CreateVersion7(),
            startUtc: StartUtc,
            localDate: new DateOnly(2026, 9, 4),
            localStartTime: new TimeOnly(20, 0),
            partySize: 2,
            guestName: "Ani",
            guestPhone: "+37411223344",
            code: ReservationCode.Generate(),
            policy: Policy,
            initialStatus: status,
            note: "window table");
}
