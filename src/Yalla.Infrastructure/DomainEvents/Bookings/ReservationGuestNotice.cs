using System.Globalization;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// What a booking's guest is told about it by somebody else's doing - the push through the outbox and the feed entry
/// beside it, in the save that made the change (<c>docs/notifications.md</c>): a waiting booking decided
/// (<c>reservation.decided</c>), the booking moved or cancelled from its link (<c>reservation.changed-by-link</c>,
/// <c>reservation.cancelled-by-link</c>), let go by the venue (a feed entry; it never had a push), marked a no-show
/// (<c>reservation.no-show</c>).
/// </summary>
/// <remarks>
/// <para>
/// A decline nobody gave - the sweep's - is not apologised for once the booking would already have started: a machine that
/// slept is no reason to tell somebody about an evening that has gone. Everybody else's answer is told.
/// </para>
/// <para>
/// The guest's own change and cancel in the app tell them nothing new, and write nothing. A booking with no account has
/// nobody to tell. Its reminders are <see cref="BookingReminders"/>'s; the guest before it on the table is told by
/// <see cref="EarlierGuestNotice"/>.
/// </para>
/// </remarks>
internal sealed class ReservationGuestNotice(YallaDbContext db, IOutbox outbox) :
    IDomainEventHandler<ReservationApproved>,
    IDomainEventHandler<ReservationDeclined>,
    IDomainEventHandler<ReservationRescheduled>,
    IDomainEventHandler<ReservationCancelled>,
    IDomainEventHandler<ReservationMarkedNoShow>
{
    public async Task HandleAsync(ReservationApproved domainEvent, CancellationToken cancellationToken)
    {
        var (reservation, nowUtc) = (domainEvent.Reservation, domainEvent.OccurredAtUtc);

        // A booking moved back into the queue is approved a second time, and the first approval's message keeps its key
        // once sent - see DecisionKeyAsync.
        var key = await ReservationNotices.DecisionKeyAsync(db, reservation.Id, "approved", nowUtc, cancellationToken);
        var branch = await BookingReads.BranchAsync(db, reservation.BranchId, cancellationToken);

        ReservationNotices.TellDinerDecided(db, outbox, reservation, branch, approved: true, expired: false, key, nowUtc);
    }

    public async Task HandleAsync(ReservationDeclined domainEvent, CancellationToken cancellationToken)
    {
        var (reservation, nowUtc) = (domainEvent.Reservation, domainEvent.OccurredAtUtc);
        var expired = Reservation.IsForNoAnswer(domainEvent.Because);

        // The clock's decline of a booking that would already have started - kept at its old slot, or the slot asked
        // for - is not apologised for.
        if (expired && reservation.StartUtc <= nowUtc)
        {
            return;
        }

        var key = await ReservationNotices.DecisionKeyAsync(
            db, reservation.Id, expired ? "expired" : "rejected", nowUtc, cancellationToken);
        var branch = await BookingReads.BranchAsync(db, reservation.BranchId, cancellationToken);

        ReservationNotices.TellDinerDecided(
            db,
            outbox,
            reservation,
            branch,
            approved: false,
            expired,
            key,
            nowUtc,
            domainEvent.WasChange
                ? new DeclinedChange(domainEvent.SlotKept, domainEvent.RequestedDate, domainEvent.RequestedTime)
                : null,
            domainEvent.Because);
    }

    public async Task HandleAsync(ReservationRescheduled domainEvent, CancellationToken cancellationToken)
    {
        if (!domainEvent.ViaManageLink)
        {
            return;
        }

        var branch = await BookingReads.BranchAsync(db, domainEvent.Reservation.BranchId, cancellationToken);

        ReservationNotices.TellDinerDoneFromLink(
            db, outbox, domainEvent.Reservation, branch, changed: true, domainEvent.OccurredAtUtc);
    }

    public async Task HandleAsync(ReservationCancelled domainEvent, CancellationToken cancellationToken)
    {
        var reservation = domainEvent.Reservation;

        switch (domainEvent.By)
        {
            // Cancelled by whoever holds the link: the account holder is told.
            case ReservationCanceller.Guest when domainEvent.ViaManageLink:
                ReservationNotices.TellDinerDoneFromLink(
                    db,
                    outbox,
                    reservation,
                    await BookingReads.BranchAsync(db, reservation.BranchId, cancellationToken),
                    changed: false,
                    domainEvent.OccurredAtUtc);
                break;

            // The venue let an accepted booking go, and the diner's feed hears about it (K12).
            case ReservationCanceller.Venue when reservation.DinerUserId is not null:
                var branch = await BookingReads.BranchAsync(db, reservation.BranchId, cancellationToken);

                DinerNotices.BookingCancelledByVenue(
                    db, reservation, branch.Venue?.Name ?? branch.Name, branch.Name, domainEvent.OccurredAtUtc);
                break;
        }
    }

    /// <summary>
    /// "Marked as a no-show at {venue}" - every time, so a mistake is put right with the venue before it counts (review
    /// B1d). Keyed under the booking with the instant, so a no-show withdrawn and marked again is told again.
    /// </summary>
    public async Task HandleAsync(ReservationMarkedNoShow domainEvent, CancellationToken cancellationToken)
    {
        var (reservation, nowUtc) = (domainEvent.Reservation, domainEvent.OccurredAtUtc);

        if (reservation.DinerUserId is not { } dinerUserId)
        {
            return;
        }

        var branch = await BookingReads.BranchAsync(db, reservation.BranchId, cancellationToken);
        var venueName = branch.Venue?.Name ?? branch.Name;

        outbox.Enqueue(
            OutboxMessageTypes.ReservationNoShow,
            new
            {
                reservationId = reservation.Id,
                dinerUserId,
                venueName,
                branchName = branch.Name,
                tableLabel = string.Empty,
                localStartTime = reservation.LocalStartTime,
                reservationCode = reservation.Code,
                localDate = reservation.LocalDate,
                partySize = reservation.PartySize,
                status = reservation.Status,
            },
            nowUtc,
            OutboxMessageTypes.KeyFor(
                "reservation",
                reservation.Id,
                "no-show:" + nowUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)));

        DinerNotices.BookingNoShow(db, reservation, venueName, branch.Name, nowUtc);
    }
}
