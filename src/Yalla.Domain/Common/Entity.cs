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

    public override bool Equals(object? obj) =>
        obj is Entity other
        && GetType() == other.GetType()
        && Id != Guid.Empty
        && Id == other.Id;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
