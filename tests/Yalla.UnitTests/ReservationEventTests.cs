using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// What a booking, a request for its table and a no-show block raise (<c>docs/domain-events.md</c>, bookings): one event
/// per thing that happened, carrying where the booking stood before and what a handler cannot read back - and nothing for
/// a booking built straight to the rows, a refusal, a seating or a second lift.
/// </summary>
public class ReservationEventTests
{
    private static readonly DateTime StartUtc = new(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = StartUtc.AddDays(-2);

    private static readonly ReservationPolicy Policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);

    [Fact]
    public void A_booking_built_tells_nobody_and_one_booked_is_made()
    {
        var booking = Booking();
        Assert.Empty(booking.DequeueEvents());

        booking.Book(Now);

        var made = Assert.IsType<ReservationMade>(Single(booking));
        Assert.Same(booking, made.Reservation);
        Assert.Null(made.Before);
        Assert.Equal(Now, made.OccurredAtUtc);
    }

    [Fact]
    public void A_yes_says_who_and_from_where_and_a_refused_one_raises_nothing()
    {
        var waiting = Booking(ReservationStatus.PendingApproval, ApprovalTrigger.BranchApprovesEveryBooking);
        waiting.Confirm(Now);

        var approved = Assert.IsType<ReservationApproved>(Single(waiting));
        Assert.Equal(ReservationApprover.Staff, approved.By);
        Assert.Equal(ReservationStatus.PendingApproval, approved.Before.Status);
        Assert.Equal(ReservationStatus.Confirmed, waiting.Status);

        Assert.Throws<DomainStateException>(() => waiting.Confirm(Now));
        Assert.Empty(waiting.DequeueEvents());

        var asked = Booking(ReservationStatus.PendingApproval, ApprovalTrigger.BranchApprovesEveryBooking);
        asked.Confirm(Now, ReservationApprover.NoLongerAsked);
        Assert.Equal(ReservationApprover.NoLongerAsked, Assert.IsType<ReservationApproved>(Single(asked)).By);
    }

    [Fact]
    public void A_no_says_whose_and_whether_it_was_a_change_and_what_it_asked_for()
    {
        var waiting = Booking(ReservationStatus.PendingApproval, ApprovalTrigger.BranchApprovesEveryBooking);
        waiting.Reject(Now, "  Full tonight  ");

        var declined = Assert.IsType<ReservationDeclined>(Single(waiting));
        Assert.Equal(DeclineReason.StaffDeclined, declined.Because);
        Assert.Equal("  Full tonight  ", declined.Reason);
        Assert.False(declined.WasChange);
        Assert.False(declined.SlotKept);

        // A confirmed booking moved into waiting, its change declined: back at the slot it had, the slot asked for named.
        var kept = Moved(Booking());
        kept.KeepConfirmedSlot(Now.AddMinutes(1), DeclineReason.StaffDeclined, "No");

        var back = Assert.IsType<ReservationDeclined>(Single(kept));
        Assert.True(back.WasChange);
        Assert.True(back.SlotKept);
        Assert.Equal(new TimeOnly(20, 0), back.RequestedTime);
        Assert.Equal(new TimeOnly(18, 0), kept.LocalStartTime);
        Assert.Equal(ReservationStatus.Confirmed, kept.Status);

        // And one whose slot is gone: the booking ends with the change.
        var lost = Moved(Booking());
        lost.DeclineChange(Now.AddMinutes(1), DeclineReason.StaffDeclined, null);

        var gone = Assert.IsType<ReservationDeclined>(Single(lost));
        Assert.True(gone.WasChange);
        Assert.False(gone.SlotKept);
        Assert.Equal(ReservationStatus.CancelledByVenue, lost.Status);

        Assert.Throws<DomainStateException>(() => Booking().DeclineChange(Now, DeclineReason.StaffDeclined, null));
    }

    [Fact]
    public void Moves_and_cancellations_say_where_from_and_by_whom()
    {
        var link = Moved(Booking(), viaManageLink: true);
        var moved = Assert.IsType<ReservationRescheduled>(link.DequeueEvents().Select(e => e.Event).OfType<ReservationRescheduled>().Single());
        Assert.True(moved.ViaManageLink);
        Assert.Equal(new TimeOnly(18, 0), moved.Before.LocalStartTime);
        Assert.Equal(ReservationStatus.Confirmed, moved.Before.Status);

        var guest = Booking();
        guest.CancelByDiner(Now, null, afterDeadline: false, viaManageLink: true);
        var byGuest = Assert.IsType<ReservationCancelled>(Single(guest));
        Assert.Equal(ReservationCanceller.Guest, byGuest.By);
        Assert.True(byGuest.ViaManageLink);

        var venue = Booking();
        venue.CancelByVenue(Now, null);
        var byVenue = Assert.IsType<ReservationCancelled>(Single(venue));
        Assert.Equal(ReservationCanceller.Venue, byVenue.By);
        Assert.Null(byVenue.Reason);
        Assert.Equal(Reservation.ReleasedByVenueReason, venue.CancellationReason);

        var blocked = Booking();
        blocked.CancelForNoShowBlock(Now);
        Assert.Equal(ReservationCanceller.NoShowBlock, Assert.IsType<ReservationCancelled>(Single(blocked)).By);
    }

    [Fact]
    public void The_evening_raises_for_no_shows_and_holds_and_nothing_for_a_seating()
    {
        var missed = Booking();
        missed.ExtendHold(StartUtc.AddMinutes(5), 10);
        Assert.Equal(10, Assert.IsType<ReservationHoldExtended>(Single(missed)).Minutes);

        missed.MarkNoShow(StartUtc.AddMinutes(30), Guid.CreateVersion7(), countsTowardBlock: true, "Nobody came");
        Assert.Equal("Nobody came", Assert.IsType<ReservationMarkedNoShow>(Single(missed)).Reason);

        missed.UndoNoShow(StartUtc.AddMinutes(40), Guid.CreateVersion7(), "At the bar");
        var withdrawn = Assert.IsType<ReservationNoShowWithdrawn>(Single(missed));
        Assert.Equal(ReservationStatus.NoShow, withdrawn.Before.Status);

        // Seated and completed are the table's to tell (TableSeated, TableFreed name the booking).
        var seated = Booking();
        seated.MarkSeated();
        seated.MarkCompleted();
        Assert.Empty(seated.DequeueEvents());
    }

    [Fact]
    public void A_request_is_asked_once_and_a_yes_holds_the_guest_before_to_it()
    {
        var earlier = Booking();
        var later = Booking();
        var request = new TableShareRequest(
            earlier.Id, later.Id, StartUtc.AddHours(2), StartUtc.AddMinutes(100), Now, Now.AddHours(2), asksEarlierGuest: true);

        Assert.Empty(request.DequeueEvents());

        request.Ask();
        Assert.Same(request, Assert.IsType<TableShareAsked>(Single(request)).Request);

        earlier.PromiseToFreeBy(request, Now.AddMinutes(1));
        var promised = Assert.IsType<ReservationPromisedToFreeBy>(Single(earlier));
        Assert.Same(request, promised.Request);
        Assert.Equal(StartUtc.AddMinutes(90), promised.Before.EndUtc);
    }

    [Fact]
    public void A_block_is_imposed_once_and_lifted_once()
    {
        var block = new BookingBlock(Guid.CreateVersion7(), "+37411223344", Now);
        Assert.Empty(block.DequeueEvents());

        block.Impose(2, new DateOnly(2026, 11, 1));
        var imposed = Assert.IsType<BookingBlockImposed>(Single(block));
        Assert.Equal(2, imposed.CancelledBookings);
        Assert.Equal(Now, imposed.OccurredAtUtc);

        Assert.True(block.Lift(Now.AddHours(1), BookingBlockLiftCause.PlatformAdmin, Guid.CreateVersion7(), "Came after all"));
        Assert.IsType<BookingBlockLifted>(Single(block));

        Assert.False(block.Lift(Now.AddHours(2), BookingBlockLiftCause.PlatformAdmin, Guid.CreateVersion7(), "Again"));
        Assert.Empty(block.DequeueEvents());
    }

    private static IDomainEvent Single(Entity entity) => Assert.Single(entity.DequeueEvents()).Event;

    /// <summary>Tonight's 20:00 instead: back to waiting, its confirmed 18:00 kept behind it.</summary>
    private static Reservation Moved(Reservation booking, bool viaManageLink = false)
    {
        booking.Reschedule(
            booking.DiningTableId,
            StartUtc.AddHours(2),
            booking.LocalDate,
            new TimeOnly(20, 0),
            booking.PartySize,
            Policy,
            ReservationStatus.PendingApproval,
            Now,
            ApprovalTrigger.ShortNotice,
            Now.AddMinutes(15),
            viaManageLink: viaManageLink);

        return viaManageLink ? booking : Drained(booking);
    }

    private static Reservation Drained(Reservation booking)
    {
        booking.DequeueEvents();

        return booking;
    }

    private static Reservation Booking(ReservationStatus status = ReservationStatus.Confirmed, ApprovalTrigger? trigger = null) =>
        Reservation.Create(
            branchId: Guid.CreateVersion7(),
            diningTableId: Guid.CreateVersion7(),
            startUtc: StartUtc,
            localDate: new DateOnly(2026, 10, 27),
            localStartTime: new TimeOnly(18, 0),
            partySize: 4,
            guestName: "Ani",
            guestPhone: "+37411223344",
            code: "AB7K2",
            policy: Policy,
            initialStatus: status,
            approvalTrigger: trigger);
}
