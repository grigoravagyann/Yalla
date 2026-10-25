using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Calls;

/// <summary>
/// The History's <c>table.call-claimed</c> (<c>docs/activity.md</c>): a waiter has a call, by "I'm on it" or by answering
/// one nobody had claimed. Its seconds from the call are the response time Staff → Results reports. The one place it is
/// written; who acted and on which device is the request's, read by the activity log.
/// </summary>
internal sealed class CallClaimActivity(YallaDbContext db, IActivityLog activity) :
    IDomainEventHandler<ServiceRequestClaimed>
{
    public async Task HandleAsync(ServiceRequestClaimed domainEvent, CancellationToken cancellationToken)
    {
        var request = domainEvent.Request;

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Table, ActivityActions.TableCallClaimed, ActivitySubjects.ServiceRequest, request.Id)
            {
                BranchId = request.BranchId,
                TableSessionId = await SittingIdAsync(request, cancellationToken),
                TableLabel = await TableLabelAsync(request, cancellationToken),
                Summary = new
                {
                    tabId = request.TabId,
                    preset = (int)request.Preset,
                    calledAtUtc = request.CreatedAtUtc,
                    responseSeconds = (int)Math.Max(0d, (domainEvent.OccurredAtUtc - request.CreatedAtUtc).TotalSeconds),
                },
            },
            cancellationToken);
    }

    /// <summary>The sitting of the call's tab, from the tracker when the path loaded the tab.</summary>
    private async Task<Guid?> SittingIdAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        db.Tabs.Local.FirstOrDefault(t => t.Id == request.TabId)?.TableSessionId
        ?? await db.Tabs
            .Where(t => t.Id == request.TabId)
            .Select(t => (Guid?)t.TableSessionId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The call's table label, from the tracker when it is there (both staff paths load it with the call).</summary>
    private async Task<string?> TableLabelAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        db.DiningTables.Local.FirstOrDefault(t => t.Id == request.DiningTableId)?.Label
        ?? await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == request.DiningTableId)
            .Select(t => t.Label)
            .FirstOrDefaultAsync(cancellationToken);
}
