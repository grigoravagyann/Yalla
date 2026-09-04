using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// Thrown when a transition is not one the state machine allows - freeing a table nobody is
/// sitting at, releasing a hold that was never placed.
/// </summary>
/// <remarks>
/// This is a bug in the caller or a stale client, not a race. A race between two legal writers
/// produces <see cref="TableStateConflictException"/> instead, and the two must stay
/// distinguishable: one means "you asked for something impossible", the other means "you asked
/// for something reasonable and lost".
/// </remarks>
public sealed class InvalidTableTransitionException : InvalidOperationException
{
    public InvalidTableTransitionException(Guid tableId, string tableLabel, TableStatus from, TableStatus to)
        : base($"Table {tableLabel} cannot go from {from} to {to}.")
    {
        TableId = tableId;
        TableLabel = tableLabel;
        FromStatus = from;
        ToStatus = to;
    }

    public Guid TableId { get; }

    public string TableLabel { get; }

    public TableStatus FromStatus { get; }

    public TableStatus ToStatus { get; }
}
