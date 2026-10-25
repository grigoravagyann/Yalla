using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Calls;

/// <summary>
/// What every phone and tablet following the tab's stream learns about its calls (<c>docs/tabs.md</c>):
/// <c>ServiceRequested</c> when one is raised, <c>ServiceRequestAcknowledged</c> when a waiter answers it. A claim is the
/// floor's and is not told.
/// </summary>
/// <remarks>
/// Appended through the ledger, which raises the unit of work's events before it numbers the stream. The ledger reads
/// who acted from the request.
/// </remarks>
internal sealed class CallStream(TabLedger ledger) :
    IDomainEventHandler<ServiceRequestRaised>,
    IDomainEventHandler<ServiceRequestAcknowledged>
{
    public Task HandleAsync(ServiceRequestRaised domainEvent, CancellationToken cancellationToken)
    {
        var request = domainEvent.Request;

        // What was asked for rides on the event - the scope and the tip percentage - but not the priced amounts. Every
        // phone at the table reads this stream, and a guest the host hid the total from would otherwise read a
        // tablemate's share, or the whole balance, off somebody else's request. The figures are for the waiter: the
        // asker's response and the staff list carry them.
        ledger.Append(request.TabId, TabEventType.ServiceRequested, new
        {
            serviceRequestId = request.Id,
            preset = (int)request.Preset,
            note = request.Note,
            requestedByParticipantId = request.RequestedByParticipantId,
            billScope = (int?)request.BillScope,
            tipPercent = request.TipPercent,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(ServiceRequestAcknowledged domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Request.TabId, TabEventType.ServiceRequestAcknowledged, new
        {
            serviceRequestId = domainEvent.Request.Id,
            byStaffId = domainEvent.ByStaffId,
        });

        return Task.CompletedTask;
    }
}
