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
    /// confirmed, or send it back for approval - except that a confirmed booking whose party does not
    /// grow is not sent back for its size alone. Its reminder and late nudge are replaced with ones
    /// for the new time.
    /// </para>
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No such booking, or it is somebody else's - one answer for both.</exception>
    /// <exception cref="ClientCommandIdAlreadyUsedException">
    /// The <c>ClientCommandId</c> belongs to another command, or to a different change of this booking.
    /// </exception>
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
    /// Moves a booking with the manage token from its link, for a caller with no account - the same
    /// move as <see cref="RescheduleAsync"/>, by the same code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>only</b> thing this does not share with <see cref="RescheduleAsync"/> is how the caller
    /// proved they may: a signed-in diner is checked against <c>DinerUserId</c>, a link holder by
    /// holding an unguessable token. The phone gate, the rules, the status decided again, the lock,
    /// the replaced reminders and the record of the command are the diner's move, once; the change is
    /// recorded as the booking's diner's, so a retry is a retry whichever way it arrives.
    /// </para>
    /// <para>
    /// One answer is narrower than the diner's: a clash with another of the diner's bookings is
    /// <c>ManageLinkOverlapsOwnException</c>, which names nothing about that other booking - no time,
    /// table or code. Its code still says one exists. A deactivated, deleted or unproved account is
    /// <c>ManageLinkAccountUnavailableException</c>, never the session's 401. A change that is not a
    /// retry tells the booking's account, in the same save.
    /// </para>
    /// </remarks>
    /// <exception cref="KeyNotFoundException">
    /// The token is unknown, expired, or its booking is gone - see <c>ManageBookingFailure</c>; or no
    /// such table.
    /// </exception>
    /// <exception cref="Domain.Occupancy.ManageLinkAccountUnavailableException">
    /// The booking's account is deactivated, deleted or without a proved number - at the gate, or found
    /// not live by the move's own lock.
    /// </exception>
    /// <exception cref="ClientCommandIdAlreadyUsedException">As <see cref="RescheduleAsync"/>.</exception>
    /// <exception cref="Domain.Occupancy.ReservationNotChangeableException">As <see cref="RescheduleAsync"/>.</exception>
    /// <exception cref="Domain.Occupancy.ReservationRejectedException">As <see cref="RescheduleAsync"/>.</exception>
    /// <exception cref="TableAlreadyBookedException">As <see cref="RescheduleAsync"/>.</exception>
    /// <exception cref="TableCurrentlyOccupiedException">As <see cref="RescheduleAsync"/>.</exception>
    /// <exception cref="ReservationLockTimeoutException">As <see cref="RescheduleAsync"/>.</exception>
    Task<ReservationView> RescheduleByManageTokenAsync(
        string manageToken,
        RescheduleByManageTokenCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The earlier guest's answer to a later guest's request for their table. Yes: the later booking is
    /// confirmed, and this booking promises to free the table by the time asked - its end is cut to it.
    /// No: the later booking is declined. Both diners are told.
    /// </summary>
    /// <returns>The earlier guest's own booking, as it now stands.</returns>
    /// <exception cref="KeyNotFoundException">No such booking of the caller's, or no such request on it.</exception>
    /// <exception cref="Domain.Occupancy.TableRequestNotPendingException">
    /// The request was answered another way already, ran out of time, or went away.
    /// </exception>
    /// <exception cref="ClientCommandIdAlreadyUsedException">The id belongs to another command or another answer.</exception>
    Task<ReservationView> AnswerTableRequestAsync(
        AnswerTableRequestCommand command,
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
    /// refused. One that this cancels is told to its account - a push and a feed entry, in the same
    /// save - because whoever holds the link may not be the diner.
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

    /// <summary>
    /// Staff accept a booking that was waiting for approval. A waiter or above at the booking's own
    /// branch - an owner or general manager at any branch of their venue.
    /// </summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not a waiter or above at this booking's branch.</exception>
    /// <exception cref="Domain.Occupancy.ReservationApprovalExpiredException">
    /// The answer window has closed, the booking was already declined for nobody answering, or it has
    /// started.
    /// </exception>
    Task<ReservationView> ApproveAsync(DecideReservationCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Staff decline a booking that was waiting for approval. Scoped as <see cref="ApproveAsync"/>.
    /// </summary>
    /// <remarks>
    /// A waiting <i>change</i> of a confirmed booking is declined as a change: the booking goes back to
    /// the confirmed slot it had, and is answered as such, when that slot is still free; otherwise it is
    /// declined like any other.
    /// </remarks>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not a waiter or above at this booking's branch.</exception>
    /// <exception cref="Domain.Occupancy.ReservationApprovalExpiredException">
    /// The answer window has closed, or the booking was already declined for nobody answering.
    /// </exception>
    Task<ReservationView> RejectAsync(DecideReservationCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// The branch's bookings still waiting for a yes or no that can still be given - soonest deadline
    /// first, then those with none by start - each with its table's evening around it: the booking
    /// before it and its guest's number, the one after, and whoever is sitting there now. At most
    /// <see cref="BranchReservationList.MaxRows"/>, in one query.
    /// </summary>
    /// <remarks>
    /// The floor's read: a waiter or above at the branch, scoped as <see cref="ApproveAsync"/>. A
    /// booking past its answer window or its start is left out - nobody can say yes to it any more.
    /// A platform admin reads the list without the guests' numbers.
    /// </remarks>
    /// <exception cref="Domain.Staff.StaffPermissionException">Not a waiter or above at this branch.</exception>
    Task<IReadOnlyList<AwaitingApproval>> ListAwaitingApprovalAsync(
        Guid branchId,
        CancellationToken cancellationToken = default);

    /// <summary>The calling diner's own bookings, upcoming and past.</summary>
    Task<MyReservations> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One branch's bookings in one status, by local date then start time, at most
    /// <see cref="BranchReservationList.MaxRows"/>. The console's queue: manager or above at the
    /// branch, so a manager sees precisely the bookings they can decide. The floor's own list is
    /// <see cref="ListAwaitingApprovalAsync"/>.
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
    /// Withdraws a no-show marked by mistake (review B1c): waiter or above at the booking's branch, until 05:00 the
    /// morning after its date, branch time. The booking is confirmed again; if the guest was blocked and fewer than
    /// three no-shows are left to hold the block, it is lifted - nothing it cancelled comes back - and the guest told.
    /// Withdrawing one already withdrawn answers with the booking as it stands.
    /// </summary>
    /// <exception cref="Domain.Occupancy.NoShowUndoWindowClosedException">Its service day is over.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">It is not a no-show.</exception>
    Task<ReservationView> UndoNoShowAsync(
        UndoNoShowCommand command,
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
