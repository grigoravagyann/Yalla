using Yalla.Domain.Enums;

namespace Yalla.Application.Tables;

/// <summary>
/// A warning that accompanies a <i>successful</i> transition.
/// </summary>
/// <remarks>
/// Warnings never mean failure. The waiter knows things the system does not - that the 20:00
/// booking just phoned to cancel, that these four will be gone in twenty minutes - so the service
/// reports the conflict and lets them proceed. Refusing would teach the floor staff to work
/// around the system, which is how state goes stale.
/// </remarks>
/// <param name="Code">Stable slug clients branch on, e.g. <c>upcoming-reservation</c>.</param>
/// <param name="Message">Text for the waiter, e.g. <c>table 7 reserved 20:00</c>.</param>
public sealed record TableStateWarning(string Code, string Message)
{
    /// <summary>A booking starts soon enough to collide with the party just seated.</summary>
    public const string UpcomingReservation = "upcoming-reservation";

    /// <summary>
    /// A walk-in is being seated inside the branch's holdback window for a booking on that table.
    /// </summary>
    /// <remarks>
    /// Narrower and more urgent than <see cref="UpcomingReservation"/>, which fires whenever the
    /// booking falls inside a whole turn time. This one means the booked party is nearly here. It
    /// is still only a warning: the waiter can see that the walk-in is two people wanting a coffee,
    /// and the system cannot.
    /// </remarks>
    public const string WalkInHoldback = "walk-in-holdback";

    /// <summary>The table was freed with money still owed on its tab.</summary>
    public const string OutstandingBalance = "outstanding-balance";
}

/// <summary>
/// The outcome of one table state change.
/// </summary>
/// <remarks>
/// Shaped so a SignalR hub can broadcast it verbatim once one exists: it carries the branch, the
/// table, the new physical status <i>and</i> the derived state a client would render, so a
/// listener needs no follow-up query and no reshaping. That is also why the reservation overlay
/// fields are here rather than only on the floor read model.
/// </remarks>
public sealed record TableStateChangeResult
{
    public required Guid BranchId { get; init; }

    public required Guid TableId { get; init; }

    public required string TableLabel { get; init; }

    public required TableStatus FromStatus { get; init; }

    public required TableStatus ToStatus { get; init; }

    /// <summary>What a client should draw: <see cref="ToStatus"/> with the reservation overlay applied.</summary>
    public required DerivedTableState State { get; init; }

    /// <summary>The occupancy opened or closed by this change.</summary>
    public Guid? TableSessionId { get; init; }

    public Guid? ReservationId { get; init; }

    public Guid? TabId { get; init; }

    /// <summary>Start of the next relevant booking, when there is one.</summary>
    public DateTime? NextReservationStartUtc { get; init; }

    /// <summary>When the table has to be clear again: the next booking's start less the branch buffer.</summary>
    public DateTime? FreeUntilUtc { get; init; }

    public required DateTime AtUtc { get; init; }

    public required Guid ClientCommandId { get; init; }

    /// <summary>
    /// True when this command had already been applied and the result was replayed from the audit
    /// log rather than performed again. Callers can treat it exactly like a fresh success.
    /// </summary>
    public required bool WasReplay { get; init; }

    /// <summary>Money still owed, when the change left an unresolved tab behind.</summary>
    public long? OutstandingAmd { get; init; }

    public IReadOnlyList<TableStateWarning> Warnings { get; init; } = [];

    /// <summary>
    /// Bookings this table still has tonight, when it has just been marked out of service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty on every other transition. <b>Never cancelled automatically.</b> A table out of service
    /// for ten minutes while a chair is replaced is not the same as one out for a week, and only a
    /// person standing in the room knows which this is - so the list goes to the waiter and the
    /// decision stays theirs.
    /// </para>
    /// <para>
    /// Releasing one of these uses <c>CancelledByVenue</c>, never <c>NoShow</c>: a broken table is
    /// the venue's doing and must not count against a diner who was never given the chance to turn
    /// up.
    /// </para>
    /// </remarks>
    public IReadOnlyList<AffectedReservation> AffectedReservations { get; init; } = [];
}

/// <summary>A booking left stranded by a table going out of service.</summary>
/// <param name="ReservationId">The booking, so the waiter can release it from this screen.</param>
/// <param name="Code">The short code the diner quotes at the door.</param>
/// <param name="GuestName">Who to call.</param>
/// <param name="GuestPhone">What to call them on. This is the point of surfacing the list at all.</param>
/// <param name="PartySize">How many, so the waiter can look for another table that fits.</param>
/// <param name="StartUtc">When they are expected.</param>
/// <param name="LocalDate">The booked date, as the diner sees it.</param>
/// <param name="LocalStartTime">The booked time, as the diner sees it.</param>
/// <param name="Status">1 PendingApproval, 2 Confirmed.</param>
public sealed record AffectedReservation(
    Guid ReservationId,
    string Code,
    string GuestName,
    string GuestPhone,
    int PartySize,
    DateTime StartUtc,
    DateOnly LocalDate,
    TimeOnly LocalStartTime,
    ReservationStatus Status);

/// <summary>The outcome of writing off a tab. Manager-only.</summary>
public sealed record TabAbandonResult
{
    public required Guid TabId { get; init; }

    public required Guid BranchId { get; init; }

    /// <summary>The balance that was written off.</summary>
    public required long WrittenOffAmd { get; init; }

    public required DateTime AtUtc { get; init; }

    public required bool WasReplay { get; init; }
}
