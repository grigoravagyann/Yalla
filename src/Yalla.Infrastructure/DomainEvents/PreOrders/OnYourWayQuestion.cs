using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.PreOrders;

/// <summary>
/// "Are you on your way?" - the <c>reservation.on-your-way</c> push and the <c>booking-on-your-way</c> feed entry asked
/// before a booking - as a pre-order moves it (<see cref="PreOrderQuestions"/>): asked at 45 minutes once a pre-order
/// waits for the kitchen, back at 30 once it is cleared, and withdrawn - push and feed entry not yet out - once there is
/// nothing left to ask: the guest answered, the waiter sent the pre-order or marked the party not coming.
/// </summary>
/// <remarks>
/// <para>
/// A booking's own question - written when it is made, moved with it, cancelled with it - is the bookings'
/// (<c>ReservationReminders</c>); this is what pre-orders and the answers do to it.
/// </para>
/// <para>
/// The feed entry is the guest's, so it is written only for a live account, under its lock (<c>DinerAccountLock</c>),
/// held to the commit. A guest's save has taken that lock before anything else; taking it again in the same transaction
/// reads the same answer.
/// </para>
/// </remarks>
internal sealed class OnYourWayQuestion(YallaDbContext db, IOutbox outbox)
    : IDomainEventHandler<PreOrderSaved>,
      IDomainEventHandler<PreOrderCleared>,
      IDomainEventHandler<PreOrderSent>,
      IDomainEventHandler<PartyMarkedNotComing>,
      IDomainEventHandler<GuestOnTheirWay>,
      IDomainEventHandler<GuestRunningLate>
{
    public async Task HandleAsync(PreOrderSaved domainEvent, CancellationToken cancellationToken)
    {
        var reservation = await PreOrderEventReads.ReservationAsync(db, domainEvent.PreOrder.ReservationId, cancellationToken);

        // Every booking is asked at 30 minutes; a pre-order waiting for the kitchen moves its question to 45. Saved
        // again, it is already there and stays put.
        var key = PreOrderQuestions.KeyFor(reservation, preOrderWaiting: true);

        if (!await db.OutboxMessages.AnyAsync(m => m.IdempotencyKey == key, cancellationToken))
        {
            await AskAgainAsync(reservation, preOrderWaiting: true, domainEvent.OccurredAtUtc, cancellationToken);
        }
    }

    /// <summary>Without its pre-order the booking is still asked, at 30 minutes like every other.</summary>
    public async Task HandleAsync(PreOrderCleared domainEvent, CancellationToken cancellationToken) =>
        await AskAgainAsync(
            await PreOrderEventReads.ReservationAsync(db, domainEvent.PreOrder.ReservationId, cancellationToken),
            preOrderWaiting: false,
            domainEvent.OccurredAtUtc,
            cancellationToken);

    /// <summary>Sent: "are you on your way?" has nothing left to decide.</summary>
    public Task HandleAsync(PreOrderSent domainEvent, CancellationToken cancellationToken) =>
        PreOrderQuestions.WithdrawAsync(db, outbox, domainEvent.PreOrder.ReservationId, domainEvent.OccurredAtUtc, cancellationToken);

    /// <summary>The waiter has called and decided: the guest is not asked on top.</summary>
    public Task HandleAsync(PartyMarkedNotComing domainEvent, CancellationToken cancellationToken) =>
        PreOrderQuestions.WithdrawAsync(db, outbox, domainEvent.PreOrder.ReservationId, domainEvent.OccurredAtUtc, cancellationToken);

    /// <summary>The question is answered.</summary>
    public Task HandleAsync(GuestOnTheirWay domainEvent, CancellationToken cancellationToken) =>
        PreOrderQuestions.WithdrawAsync(db, outbox, domainEvent.Reservation.Id, domainEvent.OccurredAtUtc, cancellationToken);

    /// <summary>"Running late" answers it too.</summary>
    public Task HandleAsync(GuestRunningLate domainEvent, CancellationToken cancellationToken) =>
        PreOrderQuestions.WithdrawAsync(db, outbox, domainEvent.Reservation.Id, domainEvent.OccurredAtUtc, cancellationToken);

    private async Task AskAgainAsync(
        Reservation reservation, bool preOrderWaiting, DateTime nowUtc, CancellationToken cancellationToken)
    {
        // A pre-order is always a guest's with an account: the app's, or the link's on the account behind it.
        var account = reservation.DinerUserId
                      ?? throw new InvalidOperationException($"Booking {reservation.Id} has a pre-order but no account.");

        var place = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == reservation.DiningTableId)
            .Select(t => new { t.Label, BranchName = t.Branch.Name, VenueName = t.Branch.Venue.Name })
            .FirstAsync(cancellationToken);

        await db.EnsureTransactionAsync(cancellationToken);
        var live = await DinerAccountLock.IsLiveAsync(db, account, cancellationToken);

        // The question's payload: the reminder's notice, so its handler reads the same shape.
        var payload = new
        {
            reservationId = reservation.Id,
            dinerUserId = account,
            venueName = place.VenueName,
            branchName = place.BranchName,
            tableLabel = place.Label,
            localStartTime = reservation.LocalStartTime,
            reservationCode = reservation.Code,
        };

        await PreOrderQuestions.AskAgainAsync(
            db,
            outbox,
            reservation,
            payload,
            place.VenueName,
            place.BranchName,
            writeFeed: live,
            preOrderWaiting,
            nowUtc,
            cancellationToken);
    }
}
