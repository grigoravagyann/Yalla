using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.TabOrders;

/// <summary>
/// The History's line for everything that happens to an order on a tab (<c>docs/activity.md</c>): <c>order.sent</c> /
/// <c>order.placed-by-guest</c>, <c>order.in-kitchen</c>, <c>order.ready</c>, <c>order.served</c>, <c>order.voided</c>,
/// <c>order.line-voided</c>. The one place each is written; who acted and on which device is the request's, read by the
/// activity log.
/// </summary>
internal sealed class TabOrderActivity(YallaDbContext db, IActivityLog activity, TabLedger ledger) :
    IDomainEventHandler<TabOrderPlaced>,
    IDomainEventHandler<TabOrderMoved>,
    IDomainEventHandler<TabOrderVoided>,
    IDomainEventHandler<TabOrderLineVoided>
{
    /// <summary>An order placed: <c>order.sent</c> by a waiter (it went straight to the kitchen), <c>order.placed-by-guest</c> from a phone.</summary>
    public async Task HandleAsync(TabOrderPlaced domainEvent, CancellationToken cancellationToken)
    {
        var order = domainEvent.Order;
        var tab = await TabOrderReads.TabAsync(db, order, cancellationToken);

        var guest = order.PlacedByParticipantId is { } participantId
            ? tab.Participants.FirstOrDefault(p => p.Id == participantId)?.DisplayName
            : null;

        var amount = order.Lines.Sum(l => l.UnitPriceAmdSnapshot * l.Quantity);

        await activity.RecordAsync(
            new ActivityRecord(
                ActivityCategory.Order,
                order.PlacedByStaffId is null ? ActivityActions.OrderPlacedByGuest : ActivityActions.OrderSent,
                ActivitySubjects.Tab,
                tab.Id)
            {
                BranchId = tab.BranchId,
                TableSessionId = tab.TableSessionId,
                TableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken),
                GuestName = guest,
                ItemNames = [.. order.Lines.Select(l => l.NameSnapshot)],
                AmountAmd = amount,
                Summary = new
                {
                    orderId = order.Id,
                    items = order.Lines.Sum(l => l.Quantity),
                    amountAmd = amount,
                    lines = order.Lines.Select(l => new { name = l.NameSnapshot, quantity = l.Quantity }),
                    serverStaffMemberId = order.ServerStaffMemberId,
                },
            },
            cancellationToken);
    }

    public Task HandleAsync(TabOrderMoved domainEvent, CancellationToken cancellationToken) =>
        RecordRailMoveAsync(
            domainEvent.Order,
            domainEvent.From,
            domainEvent.To,
            domainEvent.To switch
            {
                TabOrderStatus.InKitchen => ActivityActions.OrderInKitchen,
                TabOrderStatus.Ready => ActivityActions.OrderReady,
                _ => ActivityActions.OrderServed,
            },
            amountAmd: null,
            cancellationToken);

    /// <summary>A whole order voided: what it still came to on the bill is the entry's amount (a money entry).</summary>
    public Task HandleAsync(TabOrderVoided domainEvent, CancellationToken cancellationToken) =>
        RecordRailMoveAsync(
            domainEvent.Order,
            domainEvent.From,
            TabOrderStatus.Voided,
            ActivityActions.OrderVoided,
            domainEvent.AmountTakenOffAmd,
            cancellationToken);

    /// <summary>One line off the bill: its amount, the reason, and the tab's total before and after.</summary>
    public async Task HandleAsync(TabOrderLineVoided domainEvent, CancellationToken cancellationToken)
    {
        var (order, line) = (domainEvent.Order, domainEvent.Line);
        var tab = await TabOrderReads.TabAsync(db, order, cancellationToken);
        var amount = line.UnitPriceAmdSnapshot * line.Quantity;

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Order, ActivityActions.OrderLineVoided, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                TableSessionId = tab.TableSessionId,
                TableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken),
                ItemNames = [line.NameSnapshot],
                AmountAmd = amount,
                IsMoney = true,
                Reason = domainEvent.Reason,
                Before = new { name = line.NameSnapshot, quantity = line.Quantity, amountAmd = amount },
                After = new { voided = true },
                Summary = new
                {
                    lineId = line.Id,
                    orderId = order.Id,
                    name = line.NameSnapshot,
                    quantity = line.Quantity,
                    totalBeforeAmd = ledger.Compute(tab, countingAsLive: line.Id).TotalAmd,
                    totalAfterAmd = ledger.Compute(tab).TotalAmd,
                },
            },
            cancellationToken);
    }

    /// <summary>A move along the rail, a void included: where from and to, and every line on the order as it was sent.</summary>
    private async Task RecordRailMoveAsync(
        TabOrder order,
        TabOrderStatus from,
        TabOrderStatus to,
        string action,
        long? amountAmd,
        CancellationToken cancellationToken)
    {
        var tab = await TabOrderReads.TabAsync(db, order, cancellationToken);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Order, action, ActivitySubjects.Tab, order.TabId)
            {
                BranchId = tab.BranchId,
                TableSessionId = tab.TableSessionId,
                TableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken),
                ItemNames = [.. order.Lines.Select(l => l.NameSnapshot)],
                AmountAmd = amountAmd,
                Summary = new
                {
                    orderId = order.Id,
                    from = (int)from,
                    to = (int)to,
                    items = order.Lines.Sum(l => l.Quantity),
                    lines = order.Lines.Select(l => new { name = l.NameSnapshot, quantity = l.Quantity }),
                },
            },
            cancellationToken);
    }
}
