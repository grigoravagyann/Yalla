using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Messaging;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A diner cancels their booking - in the app, or with its manage link - by one path either way.
/// </summary>
internal sealed class ReservationCancellation(
    YallaDbContext db,
    IClock clock,
    ReservationWriter reservations,
    IOutbox outbox,
    IActivityLog activity,
    ILogger<ReservationService> logger,
    ReservationLookups lookups,
    ReservationViews views)
{
    public async Task<ReservationView> CancelAsync(
        CancelReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = lookups.RequireDiner("Cancel a booking");
        var reservation = await lookups.LoadReservationAsync(command.ReservationId, cancellationToken);

        if (reservation.DinerUserId != dinerUserId)
        {
            // Deliberately the same answer as a booking that does not exist would give a stranger
            // guessing ids, except that the caller is told plainly they do not own this one.
            throw new UnauthorizedAccessException("A diner may only cancel their own bookings.");
        }

        return await CancelCoreAsync(reservation, command.Reason, cancellationToken);
    }

    /// <summary>
    /// Cancels a booking with the manage token from its link, for a caller with no account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>only</b> thing this does not share with <see cref="CancelAsync"/> is how the caller
    /// proved they may: a signed-in diner is checked against <c>DinerUserId</c>, a link holder by
    /// holding an unguessable token. Everything after that - the deadline rule, the lateness
    /// record, cancelling the outbox messages, the save - is <see cref="CancelCoreAsync"/>, once.
    /// A second cancellation path is how a web cancel would eventually stop cancelling the reminder.
    /// </para>
    /// <para>
    /// A booking that is already cancelled or finished is returned untouched rather than refused;
    /// the caller renders its state. Only an unusable token fails, and every one of those fails
    /// identically - see <see cref="ManageBookingFailure"/>.
    /// </para>
    /// </remarks>
    public async Task<ReservationView> CancelByManageTokenAsync(
        string manageToken,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var reservation = await lookups.FindByManageTokenAsync(manageToken, cancellationToken);

        // Already finished with. Cancelling a cancelled booking must not move CancelledAtUtc, and
        // cancelling a completed one is meaningless - so the state is reported, not rewritten.
        if (!CanStillCancel(reservation.Status))
        {
            var settled = await lookups.LoadBranchAsync(reservation.BranchId, cancellationToken);

            return await views.ToViewAsync(
                reservation, settled, table: null, wasReplay: false, trigger: null, cancellationToken);
        }

        var cancelled = await CancelCoreAsync(reservation, reason, cancellationToken, viaManageLink: true);

        logger.LogInformation(
            "Booking {Code} was cancelled from its manage link, not the app; its account was told.", cancelled.Code);

        return cancelled;
    }

    /// <summary>Whether cancelling would still do anything.</summary>
    internal static bool CanStillCancel(ReservationStatus status) =>
        status is ReservationStatus.Confirmed or ReservationStatus.PendingApproval;

    /// <summary>
    /// The one cancellation path. Both entry points arrive here having already settled that the
    /// caller is entitled to cancel this booking.
    /// </summary>
    private async Task<ReservationView> CancelCoreAsync(
        Reservation reservation,
        string? reason,
        CancellationToken cancellationToken,
        bool viaManageLink = false)
    {
        var branch = await lookups.LoadBranchAsync(reservation.BranchId, cancellationToken);
        var nowUtc = clock.UtcNow;
        var late = ReservationRules.IsLateCancellation(reservation.StartUtc, nowUtc, branch.ReservationPolicy);

        // Late is recorded, never refused. A diner who cannot cancel simply does not turn up, and
        // a no-show costs the venue the same table plus the chance to resell it.
        // Against the guest's record only when it was confirmed and had been made before the deadline it missed
        // (review S2): a same-evening booking is inside its deadline from the moment it is made.
        var madeInTime = reservation.CreatedAtUtc
                         <= ReservationRules.CancellationDeadline(reservation.StartUtc, branch.ReservationPolicy);

        reservation.CancelByDiner(nowUtc, reason, late, countsAgainstGuest: madeInTime);

        await BookingActivity.RecordAsync(
            activity, db, reservation, ActivityActions.BookingCancelled, cancellationToken,
            extra: new { late, viaManageLink }, reason: reason, byGuest: true);

        // A question this booking was waiting on goes with it, and a guest who was asked to free their
        // table for it - or a booking waiting on this one's guest - is settled now the table is free.
        await TableShares.CloseForLaterAsync(db, reservation.Id, TableShareRequestStatus.Withdrawn, nowUtc, cancellationToken);
        await TableShares.EarlierChangedAsync(
            db, outbox, reservation, branch, nowUtc, cancellationToken);

        // Cancelling the cause cancels the message, in the same transaction. A reminder arriving for
        // a booking somebody cancelled an hour ago is worse than no reminder at all - it is the push
        // the diner remembers, and it teaches them the notifications are wrong.
        await outbox.CancelAsync(
            OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);

        // And its feed entry that has not appeared yet - the reminder - with it (K12).
        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);

        // Cancelled by whoever holds the link: the account holder is told, in this save. After the
        // cancelling above, which would otherwise take this message with the reminder.
        if (viaManageLink)
        {
            ReservationNotices.TellDinerDoneFromLink(db, outbox, reservation, branch, changed: false, nowUtc);
        }

        // "Not coming" in the booking's thread, with "booking cancelled" after it - when it answers anybody: the thread
        // exists, or "are you coming?" is being asked (two hours before the start on). One transaction, under the
        // booking's lock, so the thread is made once.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await GuestThreads.AnswerAsync(
                db, reservation, Domain.Messages.GuestAnswer.NotComing, null, nowUtc, cancellationToken, onlyWhenAsked: true);

            await reservations.SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (late)
        {
            logger.LogInformation(
                "Booking {Code} was cancelled {Minutes} minutes before it started, past the branch deadline.",
                reservation.Code, (int)(reservation.StartUtc - nowUtc).TotalMinutes);
        }

        return await views.ToViewAsync(
            reservation, branch, table: null, wasReplay: false, trigger: null, cancellationToken);
    }
}
