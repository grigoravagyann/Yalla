using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// The <c>bookingToCall</c> staff push (2026-10-31): a booking a member of staff took by hand is made, so the taker is
/// told thirty minutes before its start to call the guest - staged now, in the booking's own save, for then (the
/// <c>StaffPushSignals.StageOne</c> overload with a due time; <c>docs/notifications.md</c> "Staff push"). Only the kind and the booking
/// go in the outbox; whether it is still worth saying - the booking confirmed, the guest not yet said to be coming - and
/// who is told is decided at send time by <c>StaffPushHandler</c>.
/// </summary>
/// <remarks>
/// A booking made inside its last half hour gets no push: the card is already on the floor. The guest's "coming",
/// a release, a seating or a no-show before the half hour leave the row in the outbox, and the handler drops it unsent.
/// Key <c>staff-push:to-call:{id}</c>, once per booking: a staff booking is never moved to another start.
/// </remarks>
internal sealed class BookingToCallStaffPush(YallaDbContext db, IClock clock) : IDomainEventHandler<ReservationMade>
{
    public Task HandleAsync(ReservationMade domainEvent, CancellationToken cancellationToken)
    {
        var reservation = domainEvent.Reservation;
        var nowUtc = clock.UtcNow;
        var callAtUtc = BookingToCallRules.CallAtUtc(reservation.StartUtc);

        if (reservation.TakenByStaffId is not null
            && reservation.Status == ReservationStatus.Confirmed
            && callAtUtc > nowUtc)
        {
            StaffPushSignals.StageOne(
                db,
                StaffPushKinds.BookingToCall,
                reservation.Id,
                $"{StaffPushSignals.KeyPrefix}to-call:{reservation.Id}",
                nowUtc,
                callAtUtc);
        }

        return Task.CompletedTask;
    }
}
