using Yalla.Application.Reservations;

namespace Yalla.Application.Abstractions;

/// <summary>
/// Creating, cancelling and deciding bookings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pessimistic locking, unlike the table state machine.</b> Two writers changing a table's
/// status update the <i>same</i> row, so its <c>RowVersion</c> catches the race for free. Two
/// people booking the same table insert <i>different</i> rows: no row version ever collides and
/// both writers succeed, which is a double-booked table. So creation takes an update lock on the
/// table row, re-checks for overlaps inside it, inserts and commits. See <c>docs/reservations.md</c>.
/// </para>
/// <para>
/// Creation is idempotent on <c>ClientCommandId</c>: a retry returns the original booking and
/// writes nothing.
/// </para>
/// </remarks>
public interface IReservationService
{
    /// <summary>
    /// Books a table, or returns the booking a previous attempt with the same
    /// <c>ClientCommandId</c> already made.
    /// </summary>
    /// <exception cref="Domain.Occupancy.ReservationRejectedException">
    /// A branch rule refused the request. Each rule has its own derived type and its own code.
    /// </exception>
    /// <exception cref="TableAlreadyBookedException">
    /// The table was taken between the diner seeing it free and committing. Carries the clashing
    /// window and a fresh availability snapshot.
    /// </exception>
    /// <exception cref="ReservationLockTimeoutException">
    /// The table's lock could not be had in time. Retryable, and distinct from a real conflict.
    /// </exception>
    Task<ReservationView> CreateAsync(CreateReservationCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a booking on behalf of the diner who made it.
    /// </summary>
    /// <remarks>
    /// Free until the branch's <c>CancellationDeadlineMinutes</c> before the start, and still
    /// allowed after it - a late cancellation is far better than a no-show - with the lateness
    /// recorded on the booking.
    /// </remarks>
    Task<ReservationView> CancelAsync(CancelReservationCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a booking with the manage token from its link, for a caller with no account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same rule and the same writing as <see cref="CancelAsync"/> - one cancellation path,
    /// two ways of proving you may use it. A diner from the public page has no app, so this link
    /// is the only way cancelling is ever easier for them than not turning up, which is the
    /// premise the whole reservation product rests on.
    /// </para>
    /// <para>
    /// A booking that is already cancelled or finished is returned as it stands rather than
    /// refused.
    /// </para>
    /// </remarks>
    /// <exception cref="KeyNotFoundException">
    /// The token is unknown, expired, or its booking is gone. All three answer identically - see
    /// <c>ManageBookingFailure</c>.
    /// </exception>
    Task<ReservationView> CancelByManageTokenAsync(
        string manageToken,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>Staff accept a booking that was waiting for approval. Manager or above.</summary>
    Task<ReservationView> ApproveAsync(DecideReservationCommand command, CancellationToken cancellationToken = default);

    /// <summary>Staff decline a booking that was waiting for approval. Manager or above.</summary>
    Task<ReservationView> RejectAsync(DecideReservationCommand command, CancellationToken cancellationToken = default);

    /// <summary>The calling diner's own bookings, upcoming and past.</summary>
    Task<MyReservations> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A waiter lets a late booking go, and the table with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The action that was missing: a waiter could hold a table for a booking and had no way to stop
    /// holding it. Both outcomes free the table when it is held <i>for this booking</i>, through the
    /// state machine, so the audit row is written and every tablet sees it on the change stream.
    /// </para>
    /// <para>
    /// A table somebody is <b>sitting at</b> is left alone. The booking is released either way -
    /// that is a fact about the booking - but freeing an occupied table would make the floor plan
    /// lie about where people are, which costs more than a stale hold.
    /// </para>
    /// </remarks>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not staff at this branch.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The booking is not in a state that can be released.</exception>
    Task<ReservationReleaseResult> ReleaseAsync(
        ReleaseReservationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The diner extends their own hold, once, from the late nudge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Once.</b> <c>GraceExtensionsUsed</c> enforces it and has been an orphan column since Prompt
    /// 1. Repeated "just five more minutes" is how a table stays held all evening for somebody who is
    /// not coming, and the venue loses the cover without anybody ever deciding to.
    /// </para>
    /// <para>
    /// Written through the table state machine, so the extension reaches every tablet on the branch
    /// change sequence rather than sitting in a column the floor screen never reads.
    /// </para>
    /// </remarks>
    /// <exception cref="Domain.Tabs.TabPermissionException">Not this diner's booking.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">
    /// The one extension is already used, or the booking cannot hold a table.
    /// </exception>
    Task<ExtendHoldResult> ExtendHoldAsync(
        ExtendHoldCommand command,
        CancellationToken cancellationToken = default);
}
