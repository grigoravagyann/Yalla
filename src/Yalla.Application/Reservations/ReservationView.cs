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

    /// <summary>
    /// The instant past which cancelling is recorded as late: the start less the branch's
    /// cancellation deadline as it stands, which is what a cancellation made now is judged against.
    /// Cancelling after it is still allowed - recorded in <see cref="CancelledAfterDeadline"/>, never
    /// refused.
    /// </summary>
    /// <remarks>
    /// Already past for a booking made inside the window. The app promised free cancellation until
    /// the start, because nothing here said otherwise.
    /// </remarks>
    public required DateTime CancellationDeadlineUtc { get; init; }

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

    /// <summary>
    /// The plaintext manage token, <b>returned exactly once</b> - in the response to the request
    /// that created this booking - and null on every later read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the hash is stored, so the server cannot return this again even if a later endpoint
    /// wanted to. That is the point: it is a bearer capability, and a capability a read endpoint
    /// will re-issue is a capability anybody who can read the booking can steal.
    /// </para>
    /// <para>
    /// It is what the caller builds the manage link from, and it matters most for a booking made
    /// from the public page: that diner has no app, so this link is the only way they will ever
    /// cancel rather than simply not turn up.
    /// </para>
    /// </remarks>
    public string? ManageToken { get; init; }
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

/// <summary>The branch booking list the console's approval panel reads.</summary>
public static class BranchReservationList
{
    /// <summary>
    /// The most rows one call returns. There is no paging: the list is a queue a manager works
    /// through, and a branch with two hundred bookings waiting has a problem no page two solves.
    /// The earliest two hundred come back, so what is cut is what is furthest away.
    /// </summary>
    public const int MaxRows = 200;
}

/// <summary>A diner's own bookings, split the way the app shows them.</summary>
/// <param name="Upcoming">Bookings that have not finished yet, soonest first.</param>
/// <param name="Past">Everything else - completed, cancelled, missed - most recent first.</param>
public sealed record MyReservations(
    IReadOnlyList<ReservationView> Upcoming,
    IReadOnlyList<ReservationView> Past);
