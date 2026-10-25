using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.TabOrders;

/// <summary>
/// What every phone and tablet following the tab's stream learns about its orders (<c>docs/tabs.md</c>):
/// <c>OrderPlaced</c>, <c>OrderStatusChanged</c> for each move along the rail (a void included), <c>LineVoided</c>.
/// </summary>
/// <remarks>
/// Appended through the ledger, which raises the unit of work's events before it numbers the stream - so these take
/// their places after the tab's own, in the order the events were raised. The ledger reads who acted from the request.
/// </remarks>
internal sealed class TabOrderStream(TabLedger ledger) :
    IDomainEventHandler<TabOrderPlaced>,
    IDomainEventHandler<TabOrderMoved>,
    IDomainEventHandler<TabOrderVoided>,
    IDomainEventHandler<TabOrderLineVoided>
{
    public Task HandleAsync(TabOrderPlaced domainEvent, CancellationToken cancellationToken)
    {
        var order = domainEvent.Order;

        ledger.Append(order.TabId, TabEventType.OrderPlaced, new
        {
            orderId = order.Id,
            placedByParticipantId = order.PlacedByParticipantId,
            placedByStaffId = order.PlacedByStaffId,
            onBehalfOfParticipantId = order.OnBehalfOfParticipantId,
            estimatedReadyAtUtc = order.EstimatedReadyAtUtc,
            lines = order.Lines.Select(l => new
            {
                lineId = l.Id,
                name = l.NameSnapshot,
                unitPriceAmd = l.UnitPriceAmdSnapshot,
                quantity = l.Quantity,
                isShared = l.IsShared,
                isTableAttributed = l.IsTableAttributed,
                orderedAtUtc = order.PlacedAtUtc,

                // Each line's guest, as the tab reads it: its own when the waiter named one for it,
                // otherwise the order's, and nobody's on a shared line. A phone catching up
                // attributes the line from this.
                onBehalfOfParticipantId = order.OnBehalfOf(l),
            }),
        });

        // A waiter's order went straight to the kitchen: told as the rail's own event, right after the placing, so
        // every phone and tablet reading the stream sees the move it already knows.
        if (domainEvent.WentStraightToKitchen)
        {
            StatusChanged(order, TabOrderStatus.New, TabOrderStatus.InKitchen, order.PlacedByStaffId);
        }

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabOrderMoved domainEvent, CancellationToken cancellationToken)
    {
        StatusChanged(domainEvent.Order, domainEvent.From, domainEvent.To, domainEvent.ByStaffId);

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabOrderVoided domainEvent, CancellationToken cancellationToken)
    {
        StatusChanged(domainEvent.Order, domainEvent.From, TabOrderStatus.Voided, domainEvent.ByStaffId);

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabOrderLineVoided domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Order.TabId, TabEventType.LineVoided, new
        {
            lineId = domainEvent.Line.Id,
            orderId = domainEvent.Order.Id,
            name = domainEvent.Line.NameSnapshot,
            reason = domainEvent.Reason,
            voidedByStaffId = domainEvent.ByStaffId,
        });

        return Task.CompletedTask;
    }

    private void StatusChanged(TabOrder order, TabOrderStatus from, TabOrderStatus to, Guid? byStaffId) =>
        ledger.Append(order.TabId, TabEventType.OrderStatusChanged, new
        {
            orderId = order.Id,
            fromStatus = (int)from,
            toStatus = (int)to,
            byStaffId,
        });
}
