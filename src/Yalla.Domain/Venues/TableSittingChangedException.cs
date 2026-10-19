using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// A free addressed to one booking's sitting found a different one - or none - open at the table.
/// </summary>
/// <remarks>
/// <para>
/// The console's Overview lists bookings, and "finish, free the table" on a seated row frees the table it
/// shows. That row can be stale: the waiter may already have freed the table and seated a walk-in, and a
/// plain free closes whatever sitting is open - the walk-in's, and again on every retry. So the console
/// names the booking it means, and the free is refused unless the sitting open at the table is that
/// booking's and the booking is still seated.
/// </para>
/// <para>
/// Not a <see cref="TableStateConflictException"/>: the table's state may be exactly what the caller
/// expected (occupied), with somebody else in it. The answer is "that party has gone", not "somebody
/// changed the table first".
/// </para>
/// </remarks>
public sealed class TableSittingChangedException(
    Guid tableId,
    string tableLabel,
    Guid reservationId,
    TableStatus tableStatus,
    Guid? currentSessionId)
    : DomainStateException(
        currentSessionId is null
            ? $"Nobody is sitting at table {tableLabel} now, so that booking's sitting is already over. The table was left as it is."
            : $"Table {tableLabel} has another party at it now, not that booking's. Their sitting was left alone.")
{
    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    /// <summary>The booking the caller meant to finish.</summary>
    public Guid ReservationId { get; } = reservationId;

    /// <summary>The table's status now.</summary>
    public TableStatus TableStatus { get; } = tableStatus;

    /// <summary>The sitting open at the table now, when there is one - not the booking's.</summary>
    public Guid? CurrentSessionId { get; } = currentSessionId;
}
