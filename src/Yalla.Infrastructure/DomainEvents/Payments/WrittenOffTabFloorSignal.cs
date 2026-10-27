using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Payments;

/// <summary>
/// What every tablet following the branch's change stream learns when a tab is written off: signal 9,
/// <see cref="TableChangeSignal.TabWrittenOff"/> - a same-status row on the tab's table naming the tab and the manager
/// (<see cref="TabAdmissions.Signal"/>) - so the floor re-reads its calls and the kitchen's queue at once instead of at
/// their next poll: the tab's calls were answered (<see cref="WrittenOffTabCalls"/>) and its orders left the queue.
/// </summary>
/// <remarks>
/// Writing a tab off moves no table - the party may still be at it on paper, or the table may have been freed with the
/// balance outstanding before the manager decided - so the row carries the table's status as it stands, both sides.
/// </remarks>
internal sealed class WrittenOffTabFloorSignal(YallaDbContext db) : IDomainEventHandler<TabWrittenOff>
{
    public async Task HandleAsync(TabWrittenOff domainEvent, CancellationToken cancellationToken)
    {
        var tab = domainEvent.Tab;

        var tableStatus = db.DiningTables.Local.FirstOrDefault(t => t.Id == tab.DiningTableId)?.Status
                          ?? await db.DiningTables
                              .AsNoTracking()
                              .Where(t => t.Id == tab.DiningTableId)
                              .Select(t => t.Status)
                              .FirstAsync(cancellationToken);

        TabAdmissions.Signal(
            db,
            tab,
            TableChangeSignal.TabWrittenOff,
            "tab written off",
            (ActorType.Staff, domainEvent.ByStaffId),
            domainEvent.OccurredAtUtc,
            tableStatus);
    }
}
