namespace Yalla.Domain.Common;

/// <summary>
/// Base type for every persisted entity.
/// </summary>
/// <remarks>
/// Identifiers are UUIDv7 values generated in application code (<see cref="Guid.CreateVersion7()"/>)
/// so the clustered primary key stays append-ordered in SQL Server. A random Guid clustered key
/// fragments the index badly, which is why persistence configures every key
/// with <c>ValueGeneratedNever()</c> rather than letting the database supply it.
/// </remarks>
public abstract class Entity
{
    /// <summary>
    /// Stamps every raised event in raise order across all entities, so a save dispatches events in the order they
    /// happened even when they were raised on different aggregates.
    /// </summary>
    private static long s_raised;

    /// <summary>
    /// Events raised and not yet dispatched. A field, not a property, so EF Core never maps it; null until the first
    /// raise, so an entity that raises nothing costs nothing.
    /// </summary>
    private List<RaisedDomainEvent>? _domainEvents;

    /// <summary>Time-sortable UUIDv7 primary key, assigned at construction.</summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// UTC instant the row was first persisted. Stamped by the DbContext on insert
    /// unless a constructor already supplied a value.
    /// </summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Materialisation constructor for EF Core.</summary>
    protected Entity()
    {
    }

    protected Entity(Guid id) => Id = Guard.NotEmpty(id, nameof(id));

    /// <summary>
    /// Sets the creation stamp from a constructor when the entity needs it before it is saved
    /// (for example to derive an expiry from it).
    /// </summary>
    protected void StampCreatedAt(DateTime createdAtUtc) =>
        CreatedAtUtc = Guard.NotLocalTime(createdAtUtc, nameof(createdAtUtc));

    /// <summary>
    /// Records that something happened to this entity, for the handlers registered for it to act on in the same save
    /// - the activity entry, the outbox message, the tab's stream, the staff push. See <c>docs/domain-events.md</c>.
    /// </summary>
    /// <remarks>
    /// Raise from the domain method that makes the change, after the change, and only when it really happened: a
    /// refused or no-op call raises nothing. The context dispatches what its tracked entities raised before it saves.
    /// </remarks>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        (_domainEvents ??= []).Add(new RaisedDomainEvent(Interlocked.Increment(ref s_raised), domainEvent));
    }

    /// <summary>
    /// Hands over the events raised since the last call and forgets them, so each is dispatched once - a save that is
    /// retried does not handle them again.
    /// </summary>
    public IReadOnlyList<RaisedDomainEvent> DequeueEvents()
    {
        if (_domainEvents is not { Count: > 0 } raised)
        {
            return [];
        }

        _domainEvents = null;

        return raised;
    }

    /// <summary>
    /// Whether events are waiting to be dispatched, without taking them - for the save's check that nothing it writes
    /// still carries an event its dispatch never saw. A method, not a property, so EF Core never maps it.
    /// </summary>
    public bool HasPendingEvents() => _domainEvents is { Count: > 0 };

    public override bool Equals(object? obj) =>
        obj is Entity other
        && GetType() == other.GetType()
        && Id != Guid.Empty
        && Id == other.Id;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
