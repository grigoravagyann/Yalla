using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// The History's line for what happens to a booking (<c>docs/activity.md</c>): <c>booking.created</c>,
/// <c>booking.approved</c>, <c>booking.declined</c>, <c>booking.auto-declined</c>, <c>booking.guest-answered</c>,
/// <c>booking.moved</c>, <c>booking.cancelled</c>, <c>booking.released</c>, <c>booking.no-show</c>,
/// <c>booking.no-show-undone</c>, <c>booking.hold-extended</c> - one shape, <see cref="BookingActivity"/>. Who acted and on
/// which device is the request's, read by the log: the guest's (<c>ByGuest</c>) for what they did in the app or from the
/// link, the system's for the sweep and the no-show block.
/// </summary>
/// <remarks>
/// <para>
/// Nothing for a booking said yes to because the question it waited on went away, nor for one crowded out when the guest
/// before it moved: nobody did either, and the History never had a line for them. The earlier guest's answer is one line,
/// on the later booking: <c>booking.guest-answered</c>.
/// </para>
/// <para>
/// A booking seated is the table's line (<c>booking.seated</c>, the Tables vertical's <c>TableActivity</c>), and "on my
/// way", "running late" and "are you coming?" are the pre-orders' and the messages' to record.
/// </para>
/// </remarks>
internal sealed class ReservationActivity(YallaDbContext db, IActivityLog activity) :
    IDomainEventHandler<ReservationMade>,
    IDomainEventHandler<ReservationApproved>,
    IDomainEventHandler<ReservationDeclined>,
    IDomainEventHandler<ReservationRescheduled>,
    IDomainEventHandler<ReservationCancelled>,
    IDomainEventHandler<ReservationMarkedNoShow>,
    IDomainEventHandler<ReservationNoShowWithdrawn>,
    IDomainEventHandler<ReservationHoldExtended>
{
    public Task HandleAsync(ReservationMade domainEvent, CancellationToken cancellationToken)
    {
        var reservation = domainEvent.Reservation;

        // Taken by hand from the floor (2026-10-31): the staff member's line, on their device, as the log reads the
        // request - "ամրագրում ընդունեց". Otherwise the guest's, from the app or the page.
        object extra = reservation.TakenByStaffId is { } taker
            ? new { channel = (int)reservation.Channel, trigger = (int?)reservation.ApprovalTrigger, takenByStaffId = taker }
            : new { channel = (int)reservation.Channel, trigger = (int?)reservation.ApprovalTrigger };

        return BookingActivity.RecordAsync(
            activity,
            db,
            reservation,
            ActivityActions.BookingCreated,
            cancellationToken,
            extra: extra,
            byGuest: reservation.TakenByStaffId is null);
    }

    public Task HandleAsync(ReservationApproved domainEvent, CancellationToken cancellationToken)
    {
        var reservation = domainEvent.Reservation;

        return domainEvent.By switch
        {
            ReservationApprover.Staff => BookingActivity.RecordAsync(
                activity,
                db,
                reservation,
                ActivityActions.BookingApproved,
                cancellationToken,
                extra: new
                {
                    trigger = (int?)reservation.ApprovalTrigger,
                    decisionSeconds = BookingActivity.DecisionSeconds(reservation, domainEvent.OccurredAtUtc),
                }),

            ReservationApprover.EarlierGuest => BookingActivity.RecordAsync(
                activity,
                db,
                reservation,
                ActivityActions.BookingGuestAnswered,
                cancellationToken,
                extra: new
                {
                    answer = "table-request",
                    accepted = true,
                    earlierReservationId = domainEvent.AnsweredRequest!.EarlierReservationId,
                },
                byGuest: true),

            _ => Task.CompletedTask,
        };
    }

    public Task HandleAsync(ReservationDeclined domainEvent, CancellationToken cancellationToken)
    {
        var reservation = domainEvent.Reservation;
        var keptEarlierSlot = reservation.Status == ReservationStatus.Confirmed;

        if (Reservation.IsForNoAnswer(domainEvent.Because))
        {
            return BookingActivity.RecordAsync(
                activity,
                db,
                reservation,
                ActivityActions.BookingAutoDeclined,
                cancellationToken,
                extra: new
                {
                    declineReason = (int)domainEvent.Because,
                    trigger = (int?)reservation.ApprovalTrigger,
                    deadlineUtc = domainEvent.Before.ApprovalExpiresAtUtc,
                    keptEarlierSlot,
                },
                bySystem: true);
        }

        if (domainEvent.Because == DeclineReason.StaffDeclined)
        {
            return BookingActivity.RecordAsync(
                activity,
                db,
                reservation,
                ActivityActions.BookingDeclined,
                cancellationToken,
                extra: new
                {
                    trigger = (int?)reservation.ApprovalTrigger,
                    decisionSeconds = BookingActivity.DecisionSeconds(reservation, domainEvent.OccurredAtUtc),
                    keptEarlierSlot,
                },
                reason: domainEvent.Reason);
        }

        // The guest before it said no: their answer, on this booking. Crowded out by them moving, nobody answered.
        return domainEvent.AnsweredRequest is { } answered
            ? BookingActivity.RecordAsync(
                activity,
                db,
                reservation,
                ActivityActions.BookingGuestAnswered,
                cancellationToken,
                extra: new { answer = "table-request", accepted = false, earlierReservationId = answered.EarlierReservationId },
                byGuest: true)
            : Task.CompletedTask;
    }

    public Task HandleAsync(ReservationRescheduled domainEvent, CancellationToken cancellationToken) =>
        BookingActivity.RecordAsync(
            activity,
            db,
            domainEvent.Reservation,
            ActivityActions.BookingMoved,
            cancellationToken,
            extra: new { viaManageLink = domainEvent.ViaManageLink },
            before: BookingActivity.Slot(domainEvent.Before),
            byGuest: true);

    public Task HandleAsync(ReservationCancelled domainEvent, CancellationToken cancellationToken)
    {
        var reservation = domainEvent.Reservation;

        return domainEvent.By switch
        {
            ReservationCanceller.Guest => BookingActivity.RecordAsync(
                activity,
                db,
                reservation,
                ActivityActions.BookingCancelled,
                cancellationToken,
                extra: new { late = reservation.CancelledAfterDeadline, viaManageLink = domainEvent.ViaManageLink },
                reason: domainEvent.Reason,
                byGuest: true),

            ReservationCanceller.Venue => BookingActivity.RecordAsync(
                activity,
                db,
                reservation,
                ActivityActions.BookingReleased,
                cancellationToken,
                extra: new { countsTowardBlock = reservation.NoShowCountsTowardBlock },
                reason: domainEvent.Reason),

            // In each venue's History as the platform's doing, not the staff member's at the venue whose no-show made it.
            _ => BookingActivity.RecordAsync(
                activity,
                db,
                reservation,
                ActivityActions.BookingCancelled,
                cancellationToken,
                extra: new { because = "no-show-block" },
                bySystem: true),
        };
    }

    public Task HandleAsync(ReservationMarkedNoShow domainEvent, CancellationToken cancellationToken) =>
        BookingActivity.RecordAsync(
            activity,
            db,
            domainEvent.Reservation,
            ActivityActions.BookingNoShow,
            cancellationToken,
            extra: new { countsTowardBlock = domainEvent.Reservation.NoShowCountsTowardBlock },
            reason: domainEvent.Reason);

    public Task HandleAsync(ReservationNoShowWithdrawn domainEvent, CancellationToken cancellationToken) =>
        BookingActivity.RecordAsync(
            activity, db, domainEvent.Reservation, ActivityActions.BookingNoShowUndone, cancellationToken, reason: domainEvent.Reason);

    public Task HandleAsync(ReservationHoldExtended domainEvent, CancellationToken cancellationToken) =>
        BookingActivity.RecordAsync(
            activity,
            db,
            domainEvent.Reservation,
            ActivityActions.BookingHoldExtended,
            cancellationToken,
            extra: new { minutes = domainEvent.Minutes, holdExpiresAtUtc = domainEvent.Reservation.HoldExpiresAtUtc },
            byGuest: true);
}
