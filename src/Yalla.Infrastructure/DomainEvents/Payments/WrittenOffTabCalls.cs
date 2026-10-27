using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Payments;

/// <summary>
/// A tab written off takes its open calls off the floor's rail: each is answered by the manager who wrote it off, at
/// that moment (<see cref="ServiceRequest.AnswerForWrittenOffTab"/>) - the "bill, please" of a table that walked out
/// among them. Answered, not deleted, and not a claim: nobody walked over, so it is no waiter's response time.
/// </summary>
/// <remarks>
/// <para>
/// The same thing the turn-away does for a scan nobody admitted (<c>TabAdmissionCommands</c>): the tab ended without the
/// party, so its calls are answered by whoever ended it. Raising nothing of their own - the write-off is what is told
/// (<c>payment.unpaid</c>, <c>TabAbandoned</c>, signal 9 from <see cref="WrittenOffTabFloorSignal"/>).
/// </para>
/// <para>
/// Under the calls' row locks, held to the commit, as a claim and an answer take them (<c>ServiceRequestService</c>):
/// a waiter tapping "I'm on it" or "On my way" at the same moment either lands first - and the write-off leaves that call
/// answered as the waiter left it - or waits and finds it answered, a no-op. The locks need an open transaction:
/// <see cref="YallaDbContext.EnsureTransactionAsync"/> joins the caller's or opens one the save commits.
/// </para>
/// </remarks>
internal sealed class WrittenOffTabCalls(YallaDbContext db) : IDomainEventHandler<TabWrittenOff>
{
    public async Task HandleAsync(TabWrittenOff domainEvent, CancellationToken cancellationToken)
    {
        var tabId = domainEvent.Tab.Id;

        await db.EnsureTransactionAsync(cancellationToken);

        // Tracked, so the answers below are saved with the write-off. A call already tracked comes back as that instance.
        await db.ServiceRequests
            .FromSqlInterpolated(
                $"SELECT * FROM ServiceRequests WITH (UPDLOCK, HOLDLOCK) WHERE TabId = {tabId} AND AcknowledgedAtUtc IS NULL AND SupersededAtUtc IS NULL")
            .ToListAsync(cancellationToken);

        // From the tracker: what the query loaded, and any call this unit of work added that is not in the table yet.
        foreach (var call in db.ServiceRequests.Local.Where(r => r.TabId == tabId && r.IsOpen).ToList())
        {
            call.AnswerForWrittenOffTab(domainEvent.OccurredAtUtc, domainEvent.ByStaffId);
        }
    }
}
