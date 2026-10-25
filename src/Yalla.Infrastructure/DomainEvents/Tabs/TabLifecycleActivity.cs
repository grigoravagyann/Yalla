using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.DomainEvents.TabOrders;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Tabs;

/// <summary>
/// The History's line for what staff do to a tab (<c>docs/activity.md</c>): <c>table.tab-opened</c>,
/// <c>table.tab-admitted</c> (a waiter's tap - the booker arriving writes none), <c>table.tab-turned-away</c>,
/// <c>table.host-reassigned</c> (a waiter's move - a host leaving writes none) and <c>table.bill-presented</c>. The one
/// place each is written; who acted and on which device is the request's, read by the activity log.
/// </summary>
internal sealed class TabLifecycleActivity(YallaDbContext db, IActivityLog activity) :
    IDomainEventHandler<TabOpenedByStaff>,
    IDomainEventHandler<TabAdmitted>,
    IDomainEventHandler<TabTurnedAway>,
    IDomainEventHandler<TabHostMoved>,
    IDomainEventHandler<TabBillPresented>
{
    /// <summary>A waiter opened a tab on a seated table: which table, and how many are sitting there.</summary>
    public async Task HandleAsync(TabOpenedByStaff domainEvent, CancellationToken cancellationToken)
    {
        var tab = domainEvent.Tab;

        await RecordAsync(
            tab,
            ActivityActions.TableTabOpened,
            new { tableId = tab.DiningTableId, partySize = await PartySizeAsync(tab, cancellationToken) },
            reason: null,
            cancellationToken);
    }

    /// <summary>A waiter let the table order.</summary>
    public Task HandleAsync(TabAdmitted domainEvent, CancellationToken cancellationToken) =>
        domainEvent.Because == TabAdmissionReason.Waiter
            ? RecordAsync(domainEvent.Tab, ActivityActions.TableTabAdmitted, summary: null, reason: null, cancellationToken)
            : Task.CompletedTask;

    /// <summary>A waiter turned a scan away, with the reason they gave and whether the table went with it.</summary>
    public Task HandleAsync(TabTurnedAway domainEvent, CancellationToken cancellationToken) =>
        RecordAsync(
            domainEvent.Tab,
            ActivityActions.TableTabTurnedAway,
            new { tableFreed = domainEvent.TableFreed },
            domainEvent.Reason,
            cancellationToken);

    /// <summary>A waiter moved the host: from whom, to whom, by the names on the tab.</summary>
    public Task HandleAsync(TabHostMoved domainEvent, CancellationToken cancellationToken) =>
        domainEvent.HostLeft
            ? Task.CompletedTask
            : RecordAsync(
                domainEvent.Tab,
                ActivityActions.TableHostReassigned,
                new { fromName = domainEvent.From?.DisplayName, toName = domainEvent.To.DisplayName },
                reason: null,
                cancellationToken);

    /// <summary>A waiter presented the bill: what it came to, and what is still owed.</summary>
    public Task HandleAsync(TabBillPresented domainEvent, CancellationToken cancellationToken) =>
        RecordAsync(
            domainEvent.Tab,
            ActivityActions.TableBillPresented,
            new { totalAmd = domainEvent.Tab.TotalAmd, remainingAmd = domainEvent.Tab.RemainingAmd },
            reason: null,
            cancellationToken);

    private async Task RecordAsync(Tab tab, string action, object? summary, string? reason, CancellationToken cancellationToken) =>
        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Table, action, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                TableSessionId = tab.TableSessionId,
                TableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken),
                Reason = reason,
                Summary = summary,
            },
            cancellationToken);

    /// <summary>How many are sitting at the tab's table, from the tracker: the waiter's open loaded the sitting.</summary>
    private async Task<int> PartySizeAsync(Tab tab, CancellationToken cancellationToken) =>
        db.TableSessions.Local.FirstOrDefault(s => s.Id == tab.TableSessionId)?.PartySize
        ?? await db.TableSessions
            .AsNoTracking()
            .Where(s => s.Id == tab.TableSessionId)
            .Select(s => s.PartySize)
            .FirstAsync(cancellationToken);
}
