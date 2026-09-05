namespace Yalla.Domain.Venues;

/// <summary>
/// Somebody else held the table's booking lock for longer than the wait allows.
/// </summary>
/// <remarks>
/// <para>
/// Not a conflict, and the difference is the whole point of the type. A conflict means the slot is
/// gone and retrying will never succeed. A lock timeout means nothing has been decided yet - the
/// request simply never got its turn - and the same request sent again will very likely work. A
/// client that cannot tell them apart either abandons bookings that were fine or hammers a table
/// that is genuinely taken.
/// </para>
/// <para>
/// The timeout exists so one transaction stuck behind something slow cannot hang every writer for
/// that table indefinitely. Without it the queue just grows until requests expire at the HTTP
/// layer, which loses this distinction and reports a timeout no client can act on.
/// </para>
/// </remarks>
public class TableLockTimeoutException(
    Guid tableId,
    string tableLabel,
    int timeoutMilliseconds,
    string? message = null)
    : Exception(
        message
        ?? $"Table {tableLabel} was busy with another change for longer than {timeoutMilliseconds}ms. "
           + "Nothing was changed - send the same request again.")
{
    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    public int TimeoutMilliseconds { get; } = timeoutMilliseconds;

    /// <summary>Always true. Stated on the wire so no client has to know which codes are retryable.</summary>
    public bool Retryable => true;
}
