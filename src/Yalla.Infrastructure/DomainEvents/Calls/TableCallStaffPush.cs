using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Calls;

/// <summary>
/// The <c>tableCalls</c> staff push: a table called - a waiter, water, napkins, the bill - so the floor's phones buzz
/// (<c>docs/notifications.md</c> "Staff push"). Only the kind and the call go in the outbox; who is told, and whether the
/// call is still open and unclaimed, is decided at send time by <c>StaffPushHandler</c>.
/// </summary>
/// <remarks>
/// It used to be detected by <see cref="StaffPushSignals"/> from the change tracker - a service request added - and keeps
/// the key the tracker wrote: a call is raised once.
/// </remarks>
internal sealed class TableCallStaffPush(YallaDbContext db, IClock clock) : IDomainEventHandler<ServiceRequestRaised>
{
    public Task HandleAsync(ServiceRequestRaised domainEvent, CancellationToken cancellationToken)
    {
        var callId = domainEvent.Request.Id;

        StaffPushSignals.StageOne(
            db, StaffPushKinds.TableCalls, callId, $"{StaffPushSignals.KeyPrefix}call:{callId}", clock.UtcNow);

        return Task.CompletedTask;
    }
}
