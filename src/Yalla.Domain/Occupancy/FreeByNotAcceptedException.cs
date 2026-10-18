namespace Yalla.Domain.Occupancy;

/// <summary>
/// The table's next booking cuts a full sitting short, and the booking did not accept the time to free
/// the table by - it sent none, or a different one.
/// </summary>
/// <remarks>
/// <para>
/// A <b>422</b> naming the field <c>acceptsFreeByUtc</c>, with the time the diner has to accept in
/// <see cref="RequiredFreeByUtc"/>: the client asks "There is a booking at 21:30. Will you free the table
/// by 21:25?" and sends the booking again with that instant. A party that never agreed to leave by
/// 21:25 must not be booked as though it had - the next party would find them still there.
/// </para>
/// <para>
/// Judged under the table's lock, like the overlap check, because the next booking can arrive or go
/// between the availability read and the booking.
/// </para>
/// </remarks>
public sealed class FreeByNotAcceptedException(
    Guid tableId,
    string tableLabel,
    DateTime requiredFreeByUtc,
    TimeOnly requiredFreeByLocal,
    DateTime nextBookingStartUtc,
    DateTime? acceptedFreeByUtc)
    : Exception(
        acceptedFreeByUtc is null
            ? $"Table {tableLabel} has another booking soon after this one, so it has to be free again by "
              + $"{requiredFreeByLocal:HH\\:mm}. Send acceptsFreeByUtc with that time to book it."
            : $"Table {tableLabel} has to be free again by {requiredFreeByLocal:HH\\:mm}, not the time sent. "
              + "Send acceptsFreeByUtc with requiredFreeByUtc to book it.")
{
    /// <summary>The request field this is about.</summary>
    public const string Field = "acceptsFreeByUtc";

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    /// <summary>The instant the diner has to accept: the next booking's start less the clearing time.</summary>
    public DateTime RequiredFreeByUtc { get; } = requiredFreeByUtc;

    /// <summary>The same instant on the branch's wall clock.</summary>
    public TimeOnly RequiredFreeByLocal { get; } = requiredFreeByLocal;

    /// <summary>When the next booking starts.</summary>
    public DateTime NextBookingStartUtc { get; } = nextBookingStartUtc;

    /// <summary>What the request sent, or null when it sent nothing.</summary>
    public DateTime? AcceptedFreeByUtc { get; } = acceptedFreeByUtc;
}
