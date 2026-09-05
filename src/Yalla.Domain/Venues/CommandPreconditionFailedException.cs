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
/// <summary>Which half of the precondition failed. The two mean different things to a waiter.</summary>
public enum PreconditionFailure
{
    /// <summary>
    /// The table is in a different state now. Visible on the floor screen, and usually obvious:
    /// somebody seated the party you were about to hold it for.
    /// </summary>
    StatusChanged = 1,

    /// <summary>
    /// The table looks the same and is not the same. It moved and came back while the command sat
    /// in the queue - seated, served, paid, freed - so a status check would have let this through
    /// and applied it to a sitting that has already ended. Nothing on the screen shows this; only
    /// the version does.
    /// </summary>
    TableChangedAndChangedBack = 2,
}

public sealed class CommandPreconditionFailedException(
    Guid tableId,
    string tableLabel,
    TableStatus expectedFromStatus,
    TableStatus currentStatus,
    Guid clientCommandId,
    PreconditionFailure failure = PreconditionFailure.StatusChanged)
    : DomainStateException(
        failure == PreconditionFailure.StatusChanged
            ? $"This change was queued while table {tableLabel} was {expectedFromStatus}; it is "
              + $"{currentStatus} now, so it was not applied. Look at the table and decide what "
              + "should happen."
            : $"Table {tableLabel} is {currentStatus} again, but it has been used since this change "
              + "was queued - somebody was seated and has left. It was not applied. Look at the "
              + "table and decide what should happen.")
{
    /// <summary>
    /// Which half failed. A status mismatch is something the waiter can see; a version mismatch on
    /// a matching status is the case they cannot, and the one worth wording differently.
    /// </summary>
    public PreconditionFailure Failure { get; } = failure;

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    /// <summary>What the waiter was looking at when they tapped.</summary>
    public TableStatus ExpectedFromStatus { get; } = expectedFromStatus;

    /// <summary>What the table is now.</summary>
    public TableStatus CurrentStatus { get; } = currentStatus;

    /// <summary>The queued command, so the client can match it to the entry in its own queue.</summary>
    public Guid ClientCommandId { get; } = clientCommandId;
}
