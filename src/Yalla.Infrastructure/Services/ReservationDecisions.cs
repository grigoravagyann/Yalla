using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The floor says yes or no to a booking that is waiting for it.
/// </summary>
internal sealed class ReservationDecisions(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ReservationWriter reservations,
    IOutbox outbox,
    IActivityLog activity,
    ILogger<ReservationService> logger,
    ReservationLookups lookups,
    ReservationViews views)
{
    public Task<ReservationView> ApproveAsync(
        DecideReservationCommand command,
        CancellationToken cancellationToken = default) =>
        DecideAsync(command, approve: true, cancellationToken);

    public Task<ReservationView> RejectAsync(
        DecideReservationCommand command,
        CancellationToken cancellationToken = default) =>
        DecideAsync(command, approve: false, cancellationToken);

    private async Task<ReservationView> DecideAsync(
        DecideReservationCommand command,
        bool approve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reservation = await lookups.LoadReservationAsync(command.ReservationId, cancellationToken);
        var operation = approve ? "Approve a booking" : "Reject a booking";

        // A waiter or above at the booking's own branch, not only a manager: a short-notice booking is
        // a question about a table the person on the floor can see and the office cannot, and one that
        // waits for somebody senior to walk past is one the clock declines.
        var staffId = await lookups.RequireStaffForBranchAsync(reservation.BranchId, operation, cancellationToken);

        // The platform cancelled it - its guest was blocked for no-shows: said in so many words, not as an answer
        // already given.
        if (reservation.CancelledBecause == CancellationCause.NoShowBlock)
        {
            throw new ReservationCancelledByBlockException(reservation.Id, reservation.CancelledAtUtc);
        }

        // Waiting on the guest before it to answer in the app: theirs to answer, not the floor's. A yes
        // here would tell them they had agreed to a time they were never asked about. 409.
        if (await db.TableShareRequests
                .AsNoTracking()
                .Where(q => q.LaterReservationId == reservation.Id
                            && q.Status == TableShareRequestStatus.Pending
                            && q.AsksEarlierGuest)
                .Select(q => new { q.Id, q.ExpiresAtUtc })
                .FirstOrDefaultAsync(cancellationToken) is { } asked)
        {
            throw new ReservationAwaitingEarlierGuestException(reservation.Id, asked.Id, asked.ExpiresAtUtc);
        }

        var branch = await lookups.LoadBranchAsync(reservation.BranchId, cancellationToken);
        var nowUtc = clock.UtcNow;

        // Both throw ReservationApprovalExpiredException once the answer window has closed: past it
        // the decision is the sweep's, and the diner is told nobody looked rather than that somebody
        // said no a second too late.
        try
        {
            if (approve)
            {
                reservation.Confirm(nowUtc);

                await BookingActivity.RecordAsync(
                    activity, db, reservation, ActivityActions.BookingApproved, cancellationToken,
                    extra: new
                    {
                        trigger = (int?)reservation.ApprovalTrigger,
                        decisionSeconds = BookingActivity.DecisionSeconds(reservation, nowUtc),
                    });

                // The floor's yes for the guest before, when this booking was waiting on them: they are
                // held to the time asked - its end cut to it - and told.
                await TableShares.ApplyYesAsync(db, outbox, reservation.Id, branch, nowUtc, cancellationToken);

                // A booking the diner moved back into the queue is approved a second time, and the
                // first approval's message keeps its key once sent - see DecisionKeyAsync.
                var decidedKey = await ReservationNotices.DecisionKeyAsync(db, reservation.Id, "approved", nowUtc, cancellationToken);

                // The decision is worth telling the diner about: they are on a screen that says "waiting".
                ReservationNotices.TellDinerDecided(db, outbox, reservation, branch, approved: true, expired: false, decidedKey, nowUtc);

                await reservations.SaveAsync(cancellationToken);
            }
            else
            {
                // A no - to the booking, or, for a change of a confirmed one, to the change: the diner
                // keeps the slot they had when it is still free. Saved inside.
                await PendingDecline.DeclineAsync(
                    db,
                    outbox,
                    reservations,
                    reservation,
                    branch,
                    DeclineReason.StaffDeclined,
                    command.Reason,
                    quietIfAlreadyStarted: false,
                    nowUtc,
                    cancellationToken,
                    ct => BookingActivity.RecordAsync(
                        activity, db, reservation, ActivityActions.BookingDeclined, ct,
                        extra: new
                        {
                            trigger = (int?)reservation.ApprovalTrigger,
                            decisionSeconds = BookingActivity.DecisionSeconds(reservation, nowUtc),
                            keptEarlierSlot = reservation.Status == ReservationStatus.Confirmed,
                        },
                        reason: command.Reason));
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // Something else wrote the booking between the read above and this save. When that was the
            // sweep declining it for no answer - the waiter tapped on the last second, the sweep ran on
            // it - the tablet is told exactly that, not that the record merely changed.
            var current = await db.Reservations
                .AsNoTracking()
                .Where(r => r.Id == reservation.Id)
                .Select(r => new { r.Status, r.DeclineReason })
                .FirstOrDefaultAsync(cancellationToken);

            if (current is { DeclineReason: { } declined }
                && Reservation.IsForNoAnswer(declined)
                && current.Status != ReservationStatus.PendingApproval)
            {
                throw new ReservationApprovalExpiredException(
                    reservation.Id,
                    reservation.Code,
                    current.Status,
                    reservation.StartUtc,
                    approvalExpiresAtUtc: null,
                    $"Booking {reservation.Code} was declined because nobody answered in time, just before this "
                    + "decision arrived. The diner has been told.");
            }

            throw;
        }

        logger.LogInformation(
            "Booking {Code} was {Decision} by staff {StaffId}; it is now {Status}.",
            reservation.Code, approve ? "approved" : "rejected", staffId, reservation.Status);

        var view = await views.ToViewAsync(
            reservation, branch, table: null, wasReplay: false, trigger: null, cancellationToken);

        // The venue decided with the guest's record in front of it; the answer carries it too.
        view = view with
        {
            GuestRecord = await views.RecordForStaffAsync(reservation, view.PhoneUnverified, branch.TimeZoneId, cancellationToken),
        };

        // The guest's number is the venue's to call, not the platform's to read - as on the floor's list.
        return actor.Role == StaffRole.PlatformAdmin ? view with { GuestPhone = string.Empty } : view;
    }
}
