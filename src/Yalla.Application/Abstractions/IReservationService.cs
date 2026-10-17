using Yalla.Application.Reservations;
using Yalla.Domain.Enums;

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
    /// Moves the calling diner's own booking to another time, table or party size, keeping its code
    /// and manage link - or, for a retry with the same <c>ClientCommandId</c>, returns the booking as
    /// it stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rule creation applies, applied again: the branch open for business, the channel the
    /// booking came from still accepted, timing, opening hours, the table pinned on its room and
    /// fitting the party. The clash checks run under the target table's lock, as creation's do,
    /// <b>excluding this booking</b> - a diner moving dinner by half an hour must not be told their
    /// own booking is in the way.
    /// </para>
    /// <para>
    /// The status is decided again by creation's rules, so a move can confirm a booking, keep it
    /// confirmed, or send it back for approval. Its reminder and late nudge are replaced with ones for
    /// the new time.
    /// </para>
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No such booking, or it is somebody else's - one answer for both.</exception>
    /// <exception cref="Domain.Occupancy.ReservationNotChangeableException">
    /// The booking is not pending or confirmed, or it has started.
    /// </exception>
    /// <exception cref="Domain.Occupancy.ReservationRejectedException">A branch rule refused the new slot.</exception>
    /// <exception cref="TableAlreadyBookedException">Another booking holds the table then.</exception>
    /// <exception cref="TableCurrentlyOccupiedException">Somebody sitting there now runs into the slot.</exception>
    /// <exception cref="ReservationLockTimeoutException">The table's lock could not be had in time. Retryable.</exception>
    Task<ReservationView> RescheduleAsync(
        RescheduleReservationCommand command,
        CancellationToken cancellationToken = default);

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
    /// One branch's bookings in one status, by local date then start time, at most
    /// <see cref="BranchReservationList.MaxRows"/>. Manager or above, scoped exactly as approve and
    /// reject are, so a manager sees precisely the bookings they can decide.
    /// </summary>
    Task<IReadOnlyList<ReservationView>> ListForBranchAsync(
        Guid branchId,
        ReservationStatus status,
        CancellationToken cancellationToken = default);

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
