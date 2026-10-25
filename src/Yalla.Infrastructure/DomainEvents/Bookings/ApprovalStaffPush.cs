using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// The <c>approvals</c> staff push: a booking landed in "waiting for approval", so the floor's phones and tablets buzz
/// (<c>docs/notifications.md</c> "Staff push") - made waiting, moved into waiting, a waiting one moved to another time, or
/// a waiting one now waiting for another reason (the guest before it stepped aside and the floor decides now). Only the
/// kind and the booking go in the outbox; who is told, and whether it still waits, is decided at send time by
/// <c>StaffPushHandler</c>.
/// </summary>
/// <remarks>
/// It used to be detected by <see cref="StaffPushSignals"/> from the change tracker: a booking added waiting, or one
/// waiting whose status, start or reason changed in the save. It is the same rule, judged against where the booking
/// stood before the change. The key is the one the tracker wrote, <c>staff-push:approval:{id}:{n}</c>: a booking can go
/// back to waiting again, so it carries a fresh id after it (<see cref="StaffPushSignals.Again"/>), and a save stages at
/// most one per booking.
/// </remarks>
internal sealed class ApprovalStaffPush(YallaDbContext db, IClock clock) :
    IDomainEventHandler<ReservationMade>,
    IDomainEventHandler<ReservationRescheduled>,
    IDomainEventHandler<ReservationWaitingInstead>
{
    public Task HandleAsync(ReservationMade domainEvent, CancellationToken cancellationToken) => StageAsync(domainEvent);

    public Task HandleAsync(ReservationRescheduled domainEvent, CancellationToken cancellationToken) => StageAsync(domainEvent);

    public Task HandleAsync(ReservationWaitingInstead domainEvent, CancellationToken cancellationToken) => StageAsync(domainEvent);

    private Task StageAsync(IReservationEvent domainEvent)
    {
        var reservation = domainEvent.Reservation;

        if (reservation.Status == ReservationStatus.PendingApproval
            && (domainEvent.Before is not { } before
                || before.Status != ReservationStatus.PendingApproval
                || before.StartUtc != reservation.StartUtc
                || before.ApprovalTrigger != reservation.ApprovalTrigger))
        {
            StaffPushSignals.StageOne(
                db,
                StaffPushKinds.Approvals,
                reservation.Id,
                $"{StaffPushSignals.KeyPrefix}approval:{reservation.Id}:{StaffPushSignals.Again()}",
                clock.UtcNow);
        }

        return Task.CompletedTask;
    }
}
