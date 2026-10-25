using System.Globalization;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Takeaway;

namespace Yalla.Infrastructure.DomainEvents.TakeawayOrders;

/// <summary>
/// The History's line for everything that happens to a takeaway order (<c>docs/activity.md</c>): <c>takeaway.placed</c>
/// (and <c>takeaway.accepted</c> beside it when the branch accepted it on placing), <c>takeaway.accepted</c>,
/// <c>takeaway.declined</c> / <c>takeaway.auto-declined</c>, <c>takeaway.cancelled</c>, <c>takeaway.ready</c>,
/// <c>takeaway.collected</c>, <c>takeaway.no-show</c>. One shape for the counter, the guest and the sweep; who acted and
/// on which device is the request's, read by the activity log - a sweep's entry says the system did it, a guest's says
/// the guest.
/// </summary>
internal sealed class TakeawayOrderActivity(IActivityLog activity) :
    IDomainEventHandler<TakeawayPlaced>,
    IDomainEventHandler<TakeawayAccepted>,
    IDomainEventHandler<TakeawayDeclined>,
    IDomainEventHandler<TakeawayCancelled>,
    IDomainEventHandler<TakeawayMarkedReady>,
    IDomainEventHandler<TakeawayCollected>,
    IDomainEventHandler<TakeawayMarkedNoShow>
{
    /// <summary>
    /// Placed by the guest - shown as it was sent, waiting and with no ready time, even when the branch accepted it in
    /// the same step; that acceptance is its own entry, the system's.
    /// </summary>
    public async Task HandleAsync(TakeawayPlaced domainEvent, CancellationToken cancellationToken)
    {
        var order = domainEvent.Order;

        await RecordAsync(order, ActivityActions.TakeawayPlaced, cancellationToken, byGuest: true, asSent: true);

        if (domainEvent.AcceptedOnPlacing)
        {
            await RecordAsync(order, ActivityActions.TakeawayAccepted, cancellationToken, bySystem: true);
        }
    }

    /// <summary>Accepted by somebody at the venue, or by the sweep for nobody (the system's entry).</summary>
    public Task HandleAsync(TakeawayAccepted domainEvent, CancellationToken cancellationToken) =>
        RecordAsync(domainEvent.Order, ActivityActions.TakeawayAccepted, cancellationToken, bySystem: domainEvent.ByStaffId is null);

    /// <summary>Somebody's no, with the reason the guest was shown; or nobody's answer, the system's entry.</summary>
    public Task HandleAsync(TakeawayDeclined domainEvent, CancellationToken cancellationToken) =>
        domainEvent.Cause == TakeawayDeclineCause.NoAnswer
            ? RecordAsync(domainEvent.Order, ActivityActions.TakeawayAutoDeclined, cancellationToken, bySystem: true)
            : RecordAsync(domainEvent.Order, ActivityActions.TakeawayDeclined, cancellationToken, reason: domainEvent.Order.DeclineReason);

    public Task HandleAsync(TakeawayCancelled domainEvent, CancellationToken cancellationToken) =>
        RecordAsync(domainEvent.Order, ActivityActions.TakeawayCancelled, cancellationToken, byGuest: true);

    public Task HandleAsync(TakeawayMarkedReady domainEvent, CancellationToken cancellationToken) =>
        RecordAsync(domainEvent.Order, ActivityActions.TakeawayReady, cancellationToken);

    /// <summary>Handed over: a money entry, for what was taken at the counter.</summary>
    public Task HandleAsync(TakeawayCollected domainEvent, CancellationToken cancellationToken) =>
        RecordAsync(domainEvent.Order, ActivityActions.TakeawayCollected, cancellationToken);

    public Task HandleAsync(TakeawayMarkedNoShow domainEvent, CancellationToken cancellationToken) =>
        RecordAsync(domainEvent.Order, ActivityActions.TakeawayNoShow, cancellationToken);

    /// <summary>
    /// One entry, in one shape: the order's code, guest, dishes and total (what was paid, for a collection). With
    /// <c>asSent</c> it describes the order as the guest sent it - waiting, no ready time - whatever it is now.
    /// </summary>
    private Task RecordAsync(
        TakeawayOrder order,
        string action,
        CancellationToken cancellationToken,
        bool bySystem = false,
        bool byGuest = false,
        string? reason = null,
        bool asSent = false)
    {
        var collected = action == ActivityActions.TakeawayCollected;

        return activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Takeaway, action, ActivitySubjects.Takeaway, order.Id)
            {
                BranchId = order.BranchId,
                SubjectCode = order.Code,
                GuestName = order.GuestName,
                ItemNames = [.. order.Lines.Select(l => l.NameSnapshot)],
                AmountAmd = collected ? order.PaidAmountAmd : order.TotalAmd,
                IsMoney = collected,
                Reason = reason,
                BySystem = bySystem,
                ByGuest = byGuest,
                Summary = new
                {
                    code = order.Code,
                    items = order.Lines.Sum(l => l.Quantity),
                    totalAmd = order.TotalAmd,
                    status = (int)(asSent ? TakeawayStatus.Sent : order.Status),
                    readyAtUtc = asSent ? null : order.ReadyAtUtc,
                    pickupLocal = order.RequestedPickupLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    paymentMethod = (int?)order.PaymentMethod,
                    paidAmd = order.PaidAmountAmd,
                },
            },
            cancellationToken);
    }
}
