using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.PreOrders;

/// <summary>
/// The History's line for what the floor and the guest decide about a pre-order and its booking
/// (<c>docs/activity.md</c>): <c>order.pre-order-sent</c>, and <c>booking.guest-answered</c> for "on my way", "running
/// late" and the waiter's "not coming".
/// </summary>
/// <remarks>
/// Who acted is read by the log from the request, as from the services before: the waiter's tablet for the rail, the
/// guest - from the app or the booking's link - for their answers. A guest saving or clearing a pre-order, a held ticket
/// moved by the kitchen and its arrival on the tab have no line of their own, as before.
/// </remarks>
internal sealed class PreOrderActivity(YallaDbContext db, IActivityLog activity)
    : IDomainEventHandler<PreOrderSent>,
      IDomainEventHandler<PartyMarkedNotComing>,
      IDomainEventHandler<GuestOnTheirWay>,
      IDomainEventHandler<GuestRunningLate>
{
    public async Task HandleAsync(PreOrderSent domainEvent, CancellationToken cancellationToken)
    {
        var preOrder = domainEvent.PreOrder;
        var reservation = await PreOrderEventReads.ReservationAsync(db, preOrder.ReservationId, cancellationToken);
        var amount = preOrder.Lines.Sum(l => l.LineTotalAmd);

        // Seated with a tab, it is the tab's; otherwise the booking's. As the sitting stood when it was sent: a tab the
        // send opened for the sitting is not the subject.
        await activity.RecordAsync(
            new ActivityRecord(
                ActivityCategory.Order,
                ActivityActions.OrderPreOrderSent,
                domainEvent.TabId is not null ? ActivitySubjects.Tab : ActivitySubjects.Reservation,
                domainEvent.TabId ?? reservation.Id)
            {
                BranchId = reservation.BranchId,
                TableSessionId = domainEvent.SittingId,
                TableLabel = await PreOrderEventReads.TableLabelAsync(db, reservation.DiningTableId, cancellationToken),
                GuestName = reservation.GuestName,
                SubjectCode = reservation.Code,
                ItemNames = [.. preOrder.Lines.Select(l => l.NameSnapshot)],
                AmountAmd = amount,
                Summary = new
                {
                    reservationId = reservation.Id,
                    items = preOrder.Lines.Sum(l => l.Quantity),
                    amountAmd = amount,
                    seated = domainEvent.SittingId is not null,
                    lines = preOrder.Lines.OrderBy(l => l.Position).Select(l => new { name = l.NameSnapshot, quantity = l.Quantity }),
                },
            },
            cancellationToken);
    }

    public async Task HandleAsync(PartyMarkedNotComing domainEvent, CancellationToken cancellationToken) =>
        await BookingActivity.RecordAsync(
            activity,
            db,
            await PreOrderEventReads.ReservationAsync(db, domainEvent.PreOrder.ReservationId, cancellationToken),
            ActivityActions.BookingGuestAnswered,
            cancellationToken,
            extra: new { answer = "not-coming", recordedByStaff = true });

    public Task HandleAsync(GuestOnTheirWay domainEvent, CancellationToken cancellationToken) =>
        BookingActivity.RecordAsync(
            activity, db, domainEvent.Reservation, ActivityActions.BookingGuestAnswered, cancellationToken,
            extra: new { answer = "coming" }, byGuest: true);

    public Task HandleAsync(GuestRunningLate domainEvent, CancellationToken cancellationToken) =>
        BookingActivity.RecordAsync(
            activity, db, domainEvent.Reservation, ActivityActions.BookingGuestAnswered, cancellationToken,
            extra: new { answer = "late", minutes = domainEvent.Minutes }, byGuest: true);
}
