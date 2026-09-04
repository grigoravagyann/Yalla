using Yalla.Domain.Occupancy;

namespace Yalla.Application.Reservations;

/// <summary>
/// Somebody else's booking already holds this table over the requested interval.
/// </summary>
/// <remarks>
/// <para>
/// A <b>409</b>, not a 422. Every rule in <see cref="ReservationRules"/> refuses a request that was
/// wrong when it was made; this refuses a request that was right when it was made and lost. The
/// client's reaction is different in each case - fix the request, versus re-read and choose again -
/// so the two must not share a status code.
/// </para>
/// <para>
/// It carries the conflicting window because "that table is taken" is not actionable and "that
/// table is taken 19:00-21:00" is, and it carries a fresh
/// <see cref="BranchAvailability"/> snapshot so the app can redraw the floor and show what changed
/// in the same round trip rather than firing a second request into the same contention.
/// </para>
/// <para>
/// This lives in the application layer rather than beside the other reservation exceptions in the
/// domain precisely because of that snapshot: the domain has no read models and should not grow
/// one to describe a failure.
/// </para>
/// </remarks>
public sealed class TableAlreadyBookedException(
    Guid branchId,
    Guid tableId,
    string tableLabel,
    BookedInterval requested,
    BookedInterval conflicting,
    Guid conflictingReservationId,
    BranchAvailability? availability = null)
    : Exception(
        $"Table {tableLabel} is already booked from {conflicting.StartUtc:HH:mm} to "
        + $"{conflicting.EndUtc:HH:mm} UTC, which clashes with a "
        + $"{requested.StartUtc:HH:mm}-{requested.EndUtc:HH:mm} sitting once clearing time is allowed for.")
{
    /// <summary>The stable slug the client branches on.</summary>
    public const string ErrorCode = "table-already-booked";

    public Guid BranchId { get; } = branchId;

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    /// <summary>The interval that was asked for.</summary>
    public BookedInterval Requested { get; } = requested;

    /// <summary>The interval already spoken for. The buffer is not included - this is what is stored.</summary>
    public BookedInterval Conflicting { get; } = conflicting;

    public Guid ConflictingReservationId { get; } = conflictingReservationId;

    /// <summary>
    /// The branch as it stands now, so the app can refresh the floor without a second request.
    /// Null only when the snapshot itself could not be read.
    /// </summary>
    public BranchAvailability? Availability { get; } = availability;

    public ReservationRejectionReason Reason => ReservationRejectionReason.TableAlreadyBooked;
}

/// <summary>
/// The booking could not get the lock on the table in time.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> a conflict. A conflict means the answer is no and will stay no; this
/// means the question was never asked, because another booker held the table's lock for longer
/// than the timeout allows. The client should retry - with the same
/// <c>clientCommandId</c>, so a retry that crosses with a late-committing original is recognised
/// as a replay rather than becoming a second booking.
/// </para>
/// <para>
/// The timeout exists so a transaction stuck behind something slow cannot hang every booking for
/// that table indefinitely. Without it, the queue simply grows until requests time out at the
/// HTTP layer instead, which loses the distinction this type exists to make.
/// </para>
/// </remarks>
public sealed class ReservationLockTimeoutException(Guid tableId, string tableLabel, int timeoutMilliseconds)
    : Exception(
        $"Table {tableLabel} was busy with another booking for longer than {timeoutMilliseconds}ms. "
        + "Retry with the same clientCommandId.")
{
    /// <summary>The stable slug the client branches on.</summary>
    public const string ErrorCode = "reservation-lock-timeout";

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    public int TimeoutMilliseconds { get; } = timeoutMilliseconds;

    /// <summary>Always true. Stated on the wire so no client has to know which codes are retryable.</summary>
    public bool Retryable => true;
}
