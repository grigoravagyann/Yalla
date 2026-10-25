using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.DomainEvents.TabOrders;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Payments;

/// <summary>
/// The History's line for everything that happens to the money on a tab (<c>docs/activity.md</c>): <c>payment.cash</c>,
/// <c>payment.discount</c> / <c>payment.comp</c>, <c>payment.adjustment-reversed</c>, <c>payment.unpaid</c> - all money
/// entries, kept for <c>ActivityEntry.MoneyRetentionDays</c>. The one place each is written; who acted and on which
/// device is the request's, read by the activity log.
/// </summary>
/// <remarks>
/// The amounts are the bill's own arithmetic (<see cref="TabLedger"/>), read from the tracker as the save will write it:
/// what a discount took off is the subtotal with it standing against the subtotal without it, and the totals before and
/// after are the same two bills - so the History says what the diner's bill said.
/// </remarks>
internal sealed class PaymentActivity(YallaDbContext db, IActivityLog activity, TabLedger ledger) :
    IDomainEventHandler<TabPaymentRecorded>,
    IDomainEventHandler<TabAdjustmentAdded>,
    IDomainEventHandler<TabAdjustmentReversed>,
    IDomainEventHandler<TabWrittenOff>
{
    /// <summary>Cash taken at the table: the amount, the tip beside it, what is left, and whose table it was (the tip is theirs).</summary>
    public async Task HandleAsync(TabPaymentRecorded domainEvent, CancellationToken cancellationToken)
    {
        var (tab, payment) = (domainEvent.Tab, domainEvent.Payment);

        // Cash is the only rail that takes money today; a wallet's or a card's entry gets its own code when it comes.
        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Payment, ActivityActions.PaymentCash, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                TableSessionId = tab.TableSessionId,
                TableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken),
                AmountAmd = payment.AmountAmd,
                IsMoney = true,
                Summary = new
                {
                    paymentId = payment.Id,
                    amountAmd = payment.AmountAmd,
                    tipAmd = payment.TipAmd,
                    remainingAmd = tab.RemainingAmd,
                    totalAmd = tab.TotalAmd,
                    closed = domainEvent.SettledTheBill,
                    serverStaffMemberId = payment.ServerStaffMemberId,
                },
            },
            cancellationToken);
    }

    /// <summary>A discount or comp given: what it took off the subtotal, the line it is on, the total before and after.</summary>
    public async Task HandleAsync(TabAdjustmentAdded domainEvent, CancellationToken cancellationToken)
    {
        var adjustment = domainEvent.Adjustment;
        var tab = await TabAsync(adjustment.TabId, cancellationToken);

        var before = ledger.ComputeTogglingAdjustment(tab, adjustment.Id);
        var after = ledger.Compute(tab);
        var lineName = adjustment.TabOrderLineId is { } lineId ? await LineNameAsync(lineId, cancellationToken) : null;

        await activity.RecordAsync(
            new ActivityRecord(
                ActivityCategory.Payment,
                adjustment.Kind == AdjustmentKind.Comp ? ActivityActions.PaymentComp : ActivityActions.PaymentDiscount,
                ActivitySubjects.Tab,
                tab.Id)
            {
                BranchId = tab.BranchId,
                TableSessionId = tab.TableSessionId,
                TableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken),
                ItemNames = lineName is null ? null : [lineName],
                AmountAmd = before.SubtotalAmd - after.SubtotalAmd,
                IsMoney = true,
                Reason = adjustment.Reason,
                Before = new { totalAmd = before.TotalAmd },
                After = new { totalAmd = after.TotalAmd },
                Summary = new
                {
                    adjustmentId = adjustment.Id,
                    percent = adjustment.Percent,
                    amountAmd = adjustment.AmountAmd,
                    lineId = adjustment.TabOrderLineId,
                    lineName,
                    totalBeforeAmd = before.TotalAmd,
                    totalAfterAmd = after.TotalAmd,
                },
            },
            cancellationToken);
    }

    /// <summary>A discount or comp reversed: what the total went back up by, before and after.</summary>
    public async Task HandleAsync(TabAdjustmentReversed domainEvent, CancellationToken cancellationToken)
    {
        var adjustment = domainEvent.Adjustment;
        var tab = await TabAsync(adjustment.TabId, cancellationToken);

        var reversedFrom = ledger.ComputeTogglingAdjustment(tab, adjustment.Id).TotalAmd;
        var reversedTo = ledger.Compute(tab).TotalAmd;

        await activity.RecordAsync(
            new ActivityRecord(
                ActivityCategory.Payment, ActivityActions.PaymentAdjustmentReversed, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                TableSessionId = tab.TableSessionId,
                TableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken),
                AmountAmd = reversedTo - reversedFrom,
                IsMoney = true,
                Reason = adjustment.Reason,
                Before = new { totalAmd = reversedFrom },
                After = new { totalAmd = reversedTo },
                Summary = new
                {
                    adjustmentId = adjustment.Id,
                    kind = adjustment.Kind == AdjustmentKind.Comp ? "comp" : "discount",
                    totalBeforeAmd = reversedFrom,
                    totalAfterAmd = reversedTo,
                },
            },
            cancellationToken);
    }

    /// <summary>A balance written off: what was still owed, the bill and what had been paid against it, and why.</summary>
    public async Task HandleAsync(TabWrittenOff domainEvent, CancellationToken cancellationToken)
    {
        var tab = domainEvent.Tab;

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Payment, ActivityActions.PaymentUnpaid, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                TableSessionId = tab.TableSessionId,
                TableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken),
                AmountAmd = domainEvent.WrittenOffAmd,
                IsMoney = true,
                Reason = domainEvent.Reason,
                Summary = new { writtenOffAmd = domainEvent.WrittenOffAmd, totalAmd = tab.TotalAmd, paidAmd = tab.PaidAmd },
            },
            cancellationToken);
    }

    /// <summary>
    /// The adjustment's tab, from the tracker: the code that gave or reversed it loaded the tab for the bill's arithmetic.
    /// A path that loaded less gets the tab loaded the same way, so the bill read here is the whole bill.
    /// </summary>
    private async Task<Tab> TabAsync(Guid tabId, CancellationToken cancellationToken) =>
        db.Tabs.Local.FirstOrDefault(t => t.Id == tabId)
        ?? await ledger.LoadForWriteAsync(tabId, cancellationToken);

    /// <summary>The adjusted line's name as it was ordered, from the tracker when it is there.</summary>
    private async Task<string?> LineNameAsync(Guid lineId, CancellationToken cancellationToken) =>
        db.TabOrderLines.Local.FirstOrDefault(l => l.Id == lineId)?.NameSnapshot
        ?? await db.TabOrderLines
            .AsNoTracking()
            .Where(l => l.Id == lineId)
            .Select(l => l.NameSnapshot)
            .FirstOrDefaultAsync(cancellationToken);
}
