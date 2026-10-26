using Yalla.Application.Abstractions;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.TakeawayOrders;

/// <summary>
/// Puts every move of a takeaway order on its branch's change stream: one
/// <see cref="TableChangeSignal.TakeawayChanged"/> (8) row per event, in the same save - placed (accepted on placing or
/// not), accepted (by staff or by the sweep), declined (by staff or by the sweep), cancelled by the guest, marked ready,
/// collected, marked not collected. The floor (tablet and staff phone) follows the stream and re-reads the counter on it,
/// instead of polling the counter every ten seconds (2026-10-26).
/// </summary>
/// <remarks>
/// <para>
/// The row is about the counter, not a table (<see cref="TableStateChange.ForTakeaway"/>): no table, both statuses Free,
/// the system's, and it names no order - the client re-reads the whole counter, which is one request, and an order id
/// would need a column on every row of the stream. Who did it is the History's (<see cref="TakeawayOrderActivity"/>).
/// </para>
/// <para>
/// Replays and refusals raise no event, so they write no row. History built straight to the rows by a test seeder
/// dequeues its events, and writes none either.
/// </para>
/// </remarks>
internal sealed class TakeawayChangeStream(YallaDbContext db, IClock clock) :
    IDomainEventHandler<TakeawayPlaced>,
    IDomainEventHandler<TakeawayAccepted>,
    IDomainEventHandler<TakeawayDeclined>,
    IDomainEventHandler<TakeawayCancelled>,
    IDomainEventHandler<TakeawayMarkedReady>,
    IDomainEventHandler<TakeawayCollected>,
    IDomainEventHandler<TakeawayMarkedNoShow>
{
    public Task HandleAsync(TakeawayPlaced domainEvent, CancellationToken cancellationToken) =>
        Signal(domainEvent.Order, "takeaway placed");

    public Task HandleAsync(TakeawayAccepted domainEvent, CancellationToken cancellationToken) =>
        Signal(domainEvent.Order, "takeaway accepted");

    public Task HandleAsync(TakeawayDeclined domainEvent, CancellationToken cancellationToken) =>
        Signal(domainEvent.Order, "takeaway declined");

    public Task HandleAsync(TakeawayCancelled domainEvent, CancellationToken cancellationToken) =>
        Signal(domainEvent.Order, "takeaway cancelled");

    public Task HandleAsync(TakeawayMarkedReady domainEvent, CancellationToken cancellationToken) =>
        Signal(domainEvent.Order, "takeaway ready");

    public Task HandleAsync(TakeawayCollected domainEvent, CancellationToken cancellationToken) =>
        Signal(domainEvent.Order, "takeaway collected");

    public Task HandleAsync(TakeawayMarkedNoShow domainEvent, CancellationToken cancellationToken) =>
        Signal(domainEvent.Order, "takeaway not collected");

    private Task Signal(TakeawayOrder order, string reason)
    {
        db.TableStateChanges.Add(TableStateChange.ForTakeaway(order.BranchId, reason, clock.UtcNow));

        return Task.CompletedTask;
    }
}
