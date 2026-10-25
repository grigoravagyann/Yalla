using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// Voids the held kitchen ticket of a booking that ended before its party sat - cancelled by the guest or the venue,
/// declined, expired, crowded out, cancelled by a no-show block - so the kitchen stops cooking for nobody.
/// </summary>
/// <remarks>
/// <para>
/// A booking ends in a handful of places, and a rule each of them had to remember is a rule one of them forgets; it was
/// a change-tracker hook in <c>YallaDbContext.SaveChanges</c> until 2026-10-25. The voided ticket is a changed pre-order,
/// so the pre-order rail's signal 6 puts it on the change stream in the same save, and the kitchen's list drops it as it
/// drops any voided ticket.
/// </para>
/// <para>
/// A no-show is not an end here: a sent pre-order followed by a no-show is an ordinary no-show, and its ticket is the
/// kitchen's to deal with. A declined change that went back to the slot it had has not ended either.
/// </para>
/// </remarks>
internal sealed class EndedBookingPreOrder(YallaDbContext db, IClock clock) :
    IDomainEventHandler<ReservationDeclined>,
    IDomainEventHandler<ReservationCancelled>
{
    public Task HandleAsync(ReservationDeclined domainEvent, CancellationToken cancellationToken) =>
        VoidHeldAsync(domainEvent, cancellationToken);

    public Task HandleAsync(ReservationCancelled domainEvent, CancellationToken cancellationToken) =>
        VoidHeldAsync(domainEvent, cancellationToken);

    private async Task VoidHeldAsync(IReservationEvent domainEvent, CancellationToken cancellationToken)
    {
        var reservation = domainEvent.Reservation;

        if (reservation.Status is not (ReservationStatus.CancelledByDiner or ReservationStatus.CancelledByVenue)
            || domainEvent.Before?.Status == reservation.Status)
        {
            return;
        }

        var held = await db.PreOrders
            .Where(p => p.ReservationId == reservation.Id && p.SentAtUtc != null && p.TabOrderId == null)
            .ToListAsync(cancellationToken);
        var nowUtc = clock.UtcNow;

        foreach (var preOrder in held)
        {
            preOrder.VoidHeldBecauseBookingEnded(nowUtc);
        }
    }
}
