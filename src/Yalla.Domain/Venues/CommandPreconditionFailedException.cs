using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// A queued command arrived describing a world that has since moved on.
/// </summary>
/// <remarks>
/// <para>
/// Idempotency stops a queued command being applied <i>twice</i>. It says nothing about a queued
/// command being <b>stale</b>. A waiter taps "free table 7" at 20:05 with the wifi down; the tablet
/// syncs at 20:40, by which time the table has been seated again. Applying it then frees an
/// occupied table on the busiest screen in the venue, and every replay of the queue does it again.
/// </para>
/// <para>
/// So a queued command carries the status the waiter was looking at, and is refused if the table
/// has moved. Deliberately <b>not</b> a <see cref="TableStateConflictException"/>, even though both
/// mean "somebody got there first", because the client does different things with them: a live race
/// gets an immediate "that table just went" while the waiter is still holding the tablet, and a
/// stale replay goes into a conflict list to be resolved later, when the waiter has a moment and can
/// see what actually happened.
/// </para>
/// </remarks>
public sealed class CommandPreconditionFailedException(
    Guid tableId,
    string tableLabel,
    TableStatus expectedFromStatus,
    TableStatus currentStatus,
    Guid clientCommandId)
    : DomainStateException(
        $"This change was queued while table {tableLabel} was {expectedFromStatus}; it is {currentStatus} now, "
        + "so it was not applied. Look at the table and decide what should happen.")
{
    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    /// <summary>What the waiter was looking at when they tapped.</summary>
    public TableStatus ExpectedFromStatus { get; } = expectedFromStatus;

    /// <summary>What the table is now.</summary>
    public TableStatus CurrentStatus { get; } = currentStatus;

    /// <summary>The queued command, so the client can match it to the entry in its own queue.</summary>
    public Guid ClientCommandId { get; } = clientCommandId;
}
