using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Payments;

/// <summary>
/// What every phone and tablet following the tab's stream learns about its money (<c>docs/tabs.md</c>):
/// <c>PaymentRecorded</c>, <c>TabClosed</c> when the bill is settled, <c>AdjustmentAdded</c>, <c>AdjustmentVoided</c>,
/// <c>TabAbandoned</c>.
/// </summary>
/// <remarks>
/// Appended through the ledger, which raises the unit of work's events before it numbers the stream - so these take
/// their places in the order the events were raised: a payment that settles the bill reads <c>PaymentRecorded</c>, then
/// <c>TabClosed</c>. The ledger reads who acted from the request.
/// </remarks>
internal sealed class PaymentStream(TabLedger ledger) :
    IDomainEventHandler<TabPaymentRecorded>,
    IDomainEventHandler<TabSettled>,
    IDomainEventHandler<TabAdjustmentAdded>,
    IDomainEventHandler<TabAdjustmentReversed>,
    IDomainEventHandler<TabWrittenOff>
{
    public Task HandleAsync(TabPaymentRecorded domainEvent, CancellationToken cancellationToken)
    {
        var (tab, payment) = (domainEvent.Tab, domainEvent.Payment);

        ledger.Append(tab.Id, TabEventType.PaymentRecorded, new
        {
            paymentId = payment.Id,
            amountAmd = payment.AmountAmd,
            tipAmd = payment.TipAmd,
            method = (int)payment.Method,
            tabParticipantId = payment.TabParticipantId,
            byStaffId = payment.RecordedByStaffId,
            remainingAmd = tab.RemainingAmd,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabSettled domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Tab.Id, TabEventType.TabClosed, new
        {
            closedAtUtc = domainEvent.OccurredAtUtc,
            totalAmd = domainEvent.Tab.TotalAmd,
            tableSessionClosed = domainEvent.SittingClosed,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabAdjustmentAdded domainEvent, CancellationToken cancellationToken)
    {
        var adjustment = domainEvent.Adjustment;

        ledger.Append(adjustment.TabId, TabEventType.AdjustmentAdded, new
        {
            adjustmentId = adjustment.Id,
            tabOrderLineId = adjustment.TabOrderLineId,
            kind = (int)adjustment.Kind,
            percent = adjustment.Percent,
            amountAmd = adjustment.AmountAmd,
            reason = adjustment.Reason,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabAdjustmentReversed domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Adjustment.TabId, TabEventType.AdjustmentVoided, new
        {
            adjustmentId = domainEvent.Adjustment.Id,
            reason = domainEvent.Adjustment.Reason,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabWrittenOff domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Tab.Id, TabEventType.TabAbandoned, new
        {
            reason = domainEvent.Reason,
            writtenOffAmd = domainEvent.WrittenOffAmd,
            byStaffId = domainEvent.ByStaffId,
        });

        return Task.CompletedTask;
    }
}
