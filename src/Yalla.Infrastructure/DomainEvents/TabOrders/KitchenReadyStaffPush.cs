using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.TabOrders;

/// <summary>
/// The <c>kitchenReady</c> staff push: an order on a tab is up, so the floor's phones buzz (<c>docs/notifications.md</c>
/// "Staff push"). Only the kind and the order go in the outbox; who is told, and whether it is still ready, is decided
/// at send time by <c>StaffPushHandler</c>.
/// </summary>
/// <remarks>
/// It used to be detected by <see cref="StaffPushSignals"/> from the change tracker - an order modified into
/// <see cref="TabOrderStatus.Ready"/> - and is the first staff push kind to hang on a domain event instead. The key
/// carries a fresh id, as before: an order can be up again after a move back is ever allowed, and a collision would fail
/// the save.
/// </remarks>
internal sealed class KitchenReadyStaffPush(YallaDbContext db, IClock clock) : IDomainEventHandler<TabOrderMoved>
{
    public Task HandleAsync(TabOrderMoved domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.To == TabOrderStatus.Ready)
        {
            var orderId = domainEvent.Order.Id;

            StaffPushSignals.StageOne(
                db,
                StaffPushKinds.KitchenReady,
                orderId,
                $"{StaffPushSignals.KeyPrefix}ready:{orderId}:{StaffPushSignals.Again()}",
                clock.UtcNow);
        }

        return Task.CompletedTask;
    }
}
