namespace Yalla.Domain.Common;

/// <summary>
/// Something that happened to an aggregate, raised by the aggregate (<see cref="Entity.Raise"/>) and handled by the
/// handlers registered for its type in the same save and the same transaction. See <c>docs/domain-events.md</c>.
/// </summary>
/// <remarks>
/// In-process only: never serialised, never queued. An event may therefore carry the aggregate it is about - a handler
/// reads the tracked entity as the save sees it.
/// </remarks>
public interface IDomainEvent
{
    /// <summary>When it happened: an instant from <c>IClock</c>, passed in by the caller as every domain instant is.</summary>
    DateTime OccurredAtUtc { get; }
}

/// <summary>An event as an entity holds it until dispatch: the event, and its place among every event raised.</summary>
/// <param name="Order">Monotonic across all entities in the process: dispatch sorts by it.</param>
/// <param name="Event">What happened.</param>
public readonly record struct RaisedDomainEvent(long Order, IDomainEvent Event);
