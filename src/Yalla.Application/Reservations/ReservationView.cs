using Yalla.Domain.Enums;

namespace Yalla.Application.Reservations;

/// <summary>
/// One booking as every client sees it.
/// </summary>
/// <remarks>
/// Carries both the UTC instants and the wall-clock values the diner was shown. The instants are
/// what the staff app sorts and compares; the wall-clock values are what the confirmation says,
/// and they are read from the row rather than recomputed so a change to the branch's zone setting
/// can never move an existing booking on somebody's screen.
/// </remarks>
public sealed record ReservationView
{
    public required Guid Id { get; init; }

    /// <summary>The short code the diner quotes at the door.</summary>
    public required string Code { get; init; }

    public required Guid BranchId { get; init; }

    public required string BranchName { get; init; }

    public required string TimeZoneId { get; init; }

    public required Guid TableId { get; init; }

    public required string TableLabel { get; init; }

    public required int PartySize { get; init; }

    public required DateTime StartUtc { get; init; }

    /// <summary>Start plus the branch's turn time, fixed when the booking was made.</summary>
    public required DateTime EndUtc { get; init; }

    public required DateOnly LocalDate { get; init; }

    public required TimeOnly LocalStartTime { get; init; }

    /// <summary>Wall-clock end, derived from the stored interval for display.</summary>
    public required TimeOnly LocalEndTime { get; init; }

    public required ReservationStatus Status { get; init; }

    public required string GuestName { get; init; }

    public required string GuestPhone { get; init; }

    public DateTime? ConfirmedAtUtc { get; init; }

    public DateTime? CancelledAtUtc { get; init; }

    public string? CancellationReason { get; init; }

    /// <summary>
    /// Whether the cancellation came in past the branch's free-cancellation deadline. Recorded,
    /// never punished here.
    /// </summary>
    public bool CancelledAfterDeadline { get; init; }

    public required Guid ClientCommandId { get; init; }

    /// <summary>
    /// True when this booking already existed and was returned rather than created.
    /// </summary>
    /// <remarks>
    /// A retry over a dropped connection. Callers can treat it exactly like a fresh success - that
    /// is the point - but the flag is on the wire so a client can tell the difference when
    /// diagnosing one.
    /// </remarks>
    public bool WasReplay { get; init; }

    /// <summary>
    /// Why the booking is waiting for a human, when it is. Null for a confirmed booking.
    /// </summary>
    /// <remarks>
    /// Three different situations land in <c>PendingApproval</c> and a diner deserves to know
    /// which: the branch approves everything by hand, the party is over the branch's threshold, or
    /// the diner has run up no-shows. Only the last is about them.
    /// </remarks>
    public ApprovalTrigger? AwaitingApprovalBecause { get; init; }
}

/// <summary>Why a booking needs a human before it is promised.</summary>
public enum ApprovalTrigger
{
    /// <summary>The branch does not auto-confirm anything.</summary>
    BranchApprovesEveryBooking = 1,

    /// <summary>The party is above the branch's <c>ApprovalRequiredAbovePartySize</c>.</summary>
    LargeParty = 2,

    /// <summary>
    /// The diner is over the rolling no-show threshold. See <see cref="NoShowPolicy"/> - this
    /// costs instant confirmation and nothing else.
    /// </summary>
    NoShowHistory = 3,
}

/// <summary>A diner's own bookings, split the way the app shows them.</summary>
/// <param name="Upcoming">Bookings that have not finished yet, soonest first.</param>
/// <param name="Past">Everything else - completed, cancelled, missed - most recent first.</param>
public sealed record MyReservations(
    IReadOnlyList<ReservationView> Upcoming,
    IReadOnlyList<ReservationView> Past);
