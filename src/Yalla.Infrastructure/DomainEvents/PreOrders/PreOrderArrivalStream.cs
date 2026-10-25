using System.Text.Json;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.PreOrders;

/// <summary>
/// What a phone reading the tab's stream learns when the pre-order arrives on it (<see cref="PreOrderArrivedOnTab"/>):
/// <c>OrderPlaced</c>, then <c>OrderStatusChanged</c> from New to wherever the kitchen had got it - told as the sending
/// waiter's, who placed it.
/// </summary>
/// <remarks>
/// <para>
/// <b>On a tab opened for it</b> - the party sat with the ticket held, or the send opened the sitting's tab - the stream
/// starts here: the two events are numbered 1 and 2, in the seating's own save, which does not go through the ledger.
/// </para>
/// <para>
/// <b>On a tab the sitting already had</b>, through the ledger, which numbers them after the tab's own; the send saves
/// through it.
/// </para>
/// </remarks>
internal sealed class PreOrderArrivalStream(YallaDbContext db, TabLedger ledger) : IDomainEventHandler<PreOrderArrivedOnTab>
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public Task HandleAsync(PreOrderArrivedOnTab domainEvent, CancellationToken cancellationToken)
    {
        var order = domainEvent.Order;

        if (domainEvent.OnNewTab)
        {
            var sequence = 1L;

            foreach (var (type, payload) in Events(order))
            {
                var record = new TabEvent(
                    order.TabId,
                    type,
                    JsonSerializer.Serialize(payload, PayloadJson),
                    ActorType.Staff,
                    domainEvent.PreOrder.SentByStaffId,
                    domainEvent.OccurredAtUtc);
                record.PlaceAt(sequence++);
                db.TabEvents.Add(record);
            }
        }
        else
        {
            foreach (var (type, payload) in Events(order))
            {
                ledger.Append(order.TabId, type, payload);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>The order placed, then where it is on the rail.</summary>
    private static IEnumerable<(TabEventType Type, object Payload)> Events(TabOrder order)
    {
        yield return (TabEventType.OrderPlaced, new
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
                onBehalfOfParticipantId = (Guid?)null,
            }),
        });

        if (order.Status != TabOrderStatus.New)
        {
            yield return (TabEventType.OrderStatusChanged, new
            {
                orderId = order.Id,
                fromStatus = (int)TabOrderStatus.New,
                toStatus = (int)order.Status,
                byStaffId = order.PlacedByStaffId,
            });
        }
    }
}
