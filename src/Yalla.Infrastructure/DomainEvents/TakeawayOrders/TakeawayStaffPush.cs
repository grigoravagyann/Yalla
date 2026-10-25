using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.TakeawayOrders;

/// <summary>
/// The <c>takeaway</c> staff push: an order was placed and waits for somebody to accept it, so the floor's phones and
/// tablets buzz (<c>docs/notifications.md</c> "Staff push"). One the branch accepted on placing needs nobody, and is not
/// pushed. Only the kind and the order go in the outbox; who is told, and whether it still waits, is decided at send
/// time by <c>StaffPushHandler</c>.
/// </summary>
/// <remarks>
/// It used to be detected by <see cref="StaffPushSignals"/> from the change tracker - an order added as
/// <see cref="TakeawayStatus.Sent"/>. The key is the one the tracker wrote: an order is placed once.
/// </remarks>
internal sealed class TakeawayStaffPush(YallaDbContext db, IClock clock) : IDomainEventHandler<TakeawayPlaced>
{
    public Task HandleAsync(TakeawayPlaced domainEvent, CancellationToken cancellationToken)
    {
        if (!domainEvent.AcceptedOnPlacing)
        {
            var orderId = domainEvent.Order.Id;

            StaffPushSignals.StageOne(
                db,
                StaffPushKinds.Takeaway,
                orderId,
                $"{StaffPushSignals.KeyPrefix}takeaway:{orderId}",
                clock.UtcNow);
        }

        return Task.CompletedTask;
    }
}
