using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// Thrown when another writer changed the table first: two people acted on the same table at the
/// same moment and this one lost.
/// </summary>
/// <remarks>
/// <para>
/// This is the domain-level translation of EF Core's <c>DbUpdateConcurrencyException</c>, raised
/// by the table's <c>RowVersion</c>. It carries the table's <b>current</b> state so the caller
/// can tell the user what actually happened - "table 7 was just seated by Aram" - rather than
/// showing a bare failure.
/// </para>
/// <para>
/// The operation is deliberately <b>not</b> retried. If a waiter's walk-in lost to a diner's
/// reservation, retrying would seat the walk-in at a table someone else just took. A human has
/// to see the new state and decide.
/// </para>
/// </remarks>
public sealed class TableStateConflictException : Exception
{
    public TableStateConflictException(
        Guid tableId,
        string tableLabel,
        TableStatus attemptedFromStatus,
        TableStatus currentStatus,
        Guid? currentSessionId)
        : base($"Table {tableLabel} was changed by someone else and is now {currentStatus}.")
    {
        TableId = tableId;
        TableLabel = tableLabel;
        AttemptedFromStatus = attemptedFromStatus;
        CurrentStatus = currentStatus;
        CurrentSessionId = currentSessionId;
    }

    public Guid TableId { get; }

    public string TableLabel { get; }

    /// <summary>The state this caller believed the table was in when it acted.</summary>
    public TableStatus AttemptedFromStatus { get; }

    /// <summary>The state the table is actually in now, re-read after the conflict.</summary>
    public TableStatus CurrentStatus { get; }

    /// <summary>The session now occupying the table, if the winner seated someone.</summary>
    public Guid? CurrentSessionId { get; }
}
