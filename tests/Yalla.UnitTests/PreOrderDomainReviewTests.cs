using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The pre-order rules that need no database: "on my way" is an answer about one start, and a pre-order's bounds are a
/// refusal, never an overflow.
/// </summary>
public class PreOrderDomainReviewTests
{
    private static readonly DateTime StartUtc = new(2026, 9, 4, 16, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// "On my way" said for 20:00 is forgotten when the booking moves to another start - confirmed or waiting - so the
    /// waiter's card never says "Guest confirmed" for a time nobody confirmed. A change that keeps the start keeps it.
    /// </summary>
    [Fact]
    public void On_my_way_is_forgotten_by_a_move_to_another_start_and_kept_by_one_that_keeps_it()
    {
        var policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);
        var answeredAt = StartUtc.AddMinutes(-30);

        var moved = Confirmed(policy);
        Assert.True(moved.ConfirmOnMyWay(answeredAt));
        moved.Reschedule(
            moved.DiningTableId, StartUtc.AddDays(1), new DateOnly(2026, 9, 5), new TimeOnly(20, 0), 4, policy,
            ReservationStatus.Confirmed, answeredAt.AddMinutes(1));
        Assert.Null(moved.GuestConfirmedOnWayAtUtc);

        var waiting = Confirmed(policy);
        Assert.True(waiting.ConfirmOnMyWay(answeredAt));
        waiting.Reschedule(
            waiting.DiningTableId, StartUtc.AddHours(1), new DateOnly(2026, 9, 4), new TimeOnly(21, 0), 4, policy,
            ReservationStatus.PendingApproval, answeredAt.AddMinutes(1), ApprovalTrigger.ShortNotice, answeredAt.AddMinutes(16));
        Assert.Null(waiting.GuestConfirmedOnWayAtUtc);

        // A bigger party at the same time, waiting for the venue's yes, then declined back: the same start throughout.
        var bigger = Confirmed(policy);
        Assert.True(bigger.ConfirmOnMyWay(answeredAt));
        bigger.Reschedule(
            bigger.DiningTableId, StartUtc, new DateOnly(2026, 9, 4), new TimeOnly(20, 0), 6, policy,
            ReservationStatus.PendingApproval, answeredAt.AddMinutes(1), ApprovalTrigger.ShortNotice, answeredAt.AddMinutes(16));
        Assert.Equal(answeredAt, bigger.GuestConfirmedOnWayAtUtc);
        bigger.KeepConfirmedSlot(answeredAt.AddMinutes(2), DeclineReason.StaffDeclined);
        Assert.Equal(answeredAt, bigger.GuestConfirmedOnWayAtUtc);
    }

    /// <summary>Two lines of int.MaxValue are a 422 naming each bound - not an overflow answered as a 500.</summary>
    [Fact]
    public void Quantities_too_big_to_add_up_are_named_not_overflowed()
    {
        var violations = PreOrderRules.Check([(int.MaxValue, null), (int.MaxValue, null)], null);

        Assert.Contains(violations, v => v.Field == "lines[0].quantity");
        Assert.Contains(violations, v => v.Field == "lines[1].quantity");
        Assert.Contains(violations, v => v.Field == "lines" && v.Bound == "max");
    }

    private static Reservation Confirmed(ReservationPolicy policy) => Reservation.Create(
        branchId: Guid.CreateVersion7(),
        diningTableId: Guid.CreateVersion7(),
        startUtc: StartUtc,
        localDate: new DateOnly(2026, 9, 4),
        localStartTime: new TimeOnly(20, 0),
        partySize: 4,
        guestName: "Ani",
        guestPhone: "+37411223344",
        code: "AB7K2",
        policy: policy);
}
