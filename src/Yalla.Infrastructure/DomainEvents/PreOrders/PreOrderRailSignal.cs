using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.PreOrders;

/// <summary>
/// Signal 6 (<see cref="TableChangeSignal.PreOrderChanged"/>) on the change stream - one same-status row per booking per
/// save, on its table, naming it - so every tablet re-reads its pre-order rail: a pre-order saved, cleared, moved with its
/// booking, sent, marked not coming, its held ticket moved by the kitchen or voided by the booking's end, arriving on the
/// party's tab; the guest answering "on my way" or "running late", the venue asking, a move forgetting those; and the
/// branch's settings, for every card that is or could be on a rail.
/// </summary>
/// <remarks>
/// <para>
/// It names the bookings (<see cref="YallaDbContext.SignalPreOrderRail"/>) and the save writes their rows after its
/// change-tracker hooks, as the hook that detected them did: after signal 5's rows for the same bookings, which signal
/// 5's "already signalled in this save" would otherwise take a signal 6 row for, and with the table's status as the save
/// leaves it.
/// </para>
/// <para>
/// A booking without a pre-order is signalled too when its guest answers or the venue asks, as before: the rail reads no
/// card for it, and the row costs a tablet one read.
/// </para>
/// </remarks>
internal sealed class PreOrderRailSignal(YallaDbContext db)
    : IDomainEventHandler<PreOrderSaved>,
      IDomainEventHandler<PreOrderCleared>,
      IDomainEventHandler<PreOrderSent>,
      IDomainEventHandler<PartyMarkedNotComing>,
      IDomainEventHandler<PreOrderFollowedMove>,
      IDomainEventHandler<HeldTicketMoved>,
      IDomainEventHandler<HeldTicketVoided>,
      IDomainEventHandler<PreOrderArrivedOnTab>,
      IDomainEventHandler<PreOrderSettingsSaved>,
      IDomainEventHandler<GuestOnTheirWay>,
      IDomainEventHandler<GuestRunningLate>,
      IDomainEventHandler<OnMyWayAskRecorded>,
      IDomainEventHandler<OnMyWayAnswersForgotten>
{
    public Task HandleAsync(PreOrderSaved domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.PreOrder);

    public Task HandleAsync(PreOrderCleared domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.PreOrder);

    public Task HandleAsync(PreOrderSent domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.PreOrder);

    public Task HandleAsync(PartyMarkedNotComing domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.PreOrder);

    public Task HandleAsync(PreOrderFollowedMove domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.PreOrder);

    public Task HandleAsync(HeldTicketMoved domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.PreOrder);

    public Task HandleAsync(HeldTicketVoided domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.PreOrder);

    public Task HandleAsync(PreOrderArrivedOnTab domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.PreOrder);

    public Task HandleAsync(GuestOnTheirWay domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.Reservation);

    public Task HandleAsync(GuestRunningLate domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.Reservation);

    public Task HandleAsync(OnMyWayAskRecorded domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.Reservation);

    public Task HandleAsync(OnMyWayAnswersForgotten domainEvent, CancellationToken cancellationToken) => Signal(domainEvent.Reservation);

    /// <summary>
    /// The tablets' rails change with the settings - when a card appears, its "sold out now" flags, the next card's time
    /// - and nothing about a pre-order row did: every card that is or could be on a rail now, and the next one after
    /// them, so every tablet re-reads the rail and its <c>nextDueAtUtc</c>.
    /// </summary>
    public async Task HandleAsync(PreOrderSettingsSaved domainEvent, CancellationToken cancellationToken) =>
        db.SignalPreOrderRail(await RailBookingsAsync(domainEvent.Branch.Id, domainEvent.OccurredAtUtc, cancellationToken));

    private Task Signal(PreOrder preOrder)
    {
        db.SignalPreOrderRail([preOrder.ReservationId]);

        return Task.CompletedTask;
    }

    private Task Signal(Reservation reservation)
    {
        db.SignalPreOrderRail([reservation.Id]);

        return Task.CompletedTask;
    }

    /// <summary>
    /// The bookings a settings change reaches on the rail: every one whose card could show under any setting now (a
    /// pre-order on a confirmed booking starting within the longest <c>minutesBefore</c> and not over, or a seated one
    /// not yet on its tab), and the first confirmed one after those - the rail's <c>nextDueAtUtc</c>.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> RailBookingsAsync(Guid branchId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var horizon = nowUtc.AddMinutes(PreOrderRules.MaxMinutesBefore);

        var withPreOrders = db.PreOrders.AsNoTracking().Where(p => p.BranchId == branchId && p.Lines.Any());

        var onRail = await withPreOrders
            .Where(p => (p.Reservation.Status == ReservationStatus.Confirmed
                         && p.Reservation.StartUtc <= horizon
                         && p.Reservation.EndUtc > nowUtc)
                        || (p.Reservation.Status == ReservationStatus.Seated && p.TabOrderId == null))
            .Select(p => p.ReservationId)
            .ToListAsync(cancellationToken);

        var next = await withPreOrders
            .Where(p => p.Reservation.Status == ReservationStatus.Confirmed && p.Reservation.StartUtc > horizon)
            .OrderBy(p => p.Reservation.StartUtc)
            .Select(p => (Guid?)p.ReservationId)
            .FirstOrDefaultAsync(cancellationToken);

        return next is { } first ? [.. onRail, first] : onRail;
    }
}
