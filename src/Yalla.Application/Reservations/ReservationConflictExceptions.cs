using Yalla.Domain.Venues;
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
/// <summary>
/// Somebody is sitting at the table now, and their sitting runs into the slot that was asked for.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <see cref="TableAlreadyBookedException"/>. Nobody booked this table - a waiter
/// seated a walk-in at it - and the two need different sentences. "Already booked" tells the diner
/// to pick another time; "someone is sitting there" tells them the table may well be free before
/// the projected end, because the projection is the branch's turn time and not a promise anybody
/// made.
/// </para>
/// <para>
/// This is the case the product exists to prevent and the one that used to slip through: walk-ins
/// are most of a cafe's traffic, and a sitting was invisible to the conflict rule, so a diner could
/// book a table that already had people at it and arrive to find them still there.
/// </para>
/// </remarks>
public sealed class TableCurrentlyOccupiedException(
    Guid branchId,
    Guid tableId,
    string tableLabel,
    BookedInterval requested,
    DateTime seatedAtUtc,
    DateTime projectedFreeAtUtc,
    Guid tableSessionId,
    BranchAvailability? availability = null)
    : Exception(
        $"Table {tableLabel} has a party seated at it since {seatedAtUtc:HH:mm} UTC. They are "
        + $"expected to be finished around {projectedFreeAtUtc:HH:mm} UTC, which runs into the "
        + $"{requested.StartUtc:HH:mm}-{requested.EndUtc:HH:mm} sitting once clearing time is allowed "
        + "for. They may leave sooner - the finish time is an estimate from the branch's turn time.")
{
    public Guid BranchId { get; } = branchId;

    public Guid TableId { get; } = tableId;

    public string TableLabel { get; } = tableLabel;

    /// <summary>The interval that was asked for.</summary>
    public BookedInterval Requested { get; } = requested;

    /// <summary>When the party sat down. A fact.</summary>
    public DateTime SeatedAtUtc { get; } = seatedAtUtc;

    /// <summary>Seated plus the branch's turn time. An estimate, and the reason the message hedges.</summary>
    public DateTime ProjectedFreeAtUtc { get; } = projectedFreeAtUtc;

    public Guid TableSessionId { get; } = tableSessionId;

    /// <summary>Why, for a client that switches on the reason rather than the message.</summary>
    public ReservationRejectionReason Reason => ReservationRejectionReason.TableCurrentlyOccupied;

    /// <summary>The branch as it stands now, so the app can redraw without a second request.</summary>
    public BranchAvailability? Availability { get; } = availability;
}

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
/// The idempotency key on this booking request already belongs to somebody else's booking.
/// </summary>
/// <remarks>
/// <para>
/// A <c>ClientCommandId</c> is unique across the whole table, but a <b>replay</b> is the same diner
/// sending the same command again. Those are not the same statement, and conflating them is a leak:
/// answering a second diner with the booking the id already names would hand them somebody else's
/// door code, guest name and telephone number.
/// </para>
/// <para>
/// So the replay lookup is scoped to the caller, and this is what is left over - a genuine
/// collision between two callers, which is a client bug rather than a retry. <b>409</b>, saying
/// plainly that the id is taken, and revealing nothing about the booking that holds it.
/// </para>
/// </remarks>
public sealed class ClientCommandIdAlreadyUsedException : Exception
{
    public ClientCommandIdAlreadyUsedException(Guid clientCommandId)
        : this(
            clientCommandId,
            "That clientCommandId already belongs to another booking. Generate a fresh one per "
            + "booking, and reuse it only when retrying that same booking.")
    {
    }

    /// <summary>The same collision, in words that name what the id belongs to.</summary>
    /// <remarks>An order on a tab uses this: "another booking" would send a developer the wrong way.</remarks>
    public ClientCommandIdAlreadyUsedException(Guid clientCommandId, string message)
        : base(message)
    {
        ClientCommandId = clientCommandId;
    }

    public Guid ClientCommandId { get; }
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
    : TableLockTimeoutException(
        tableId,
        tableLabel,
        timeoutMilliseconds,
        $"Table {tableLabel} was busy with another booking for longer than {timeoutMilliseconds}ms. "
        + "Retry with the same clientCommandId.");
