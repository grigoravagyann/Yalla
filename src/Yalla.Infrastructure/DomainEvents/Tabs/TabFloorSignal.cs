using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Tabs;

/// <summary>
/// What every tablet following the branch's change stream learns about a table's tab: a scan waiting to be let in
/// (signal 1), the table let in to order (signal 2), a scan turned away while the party stays (signal 3) - each a
/// same-status row (<see cref="TabAdmissions.Signal"/>), as the hold-extended row is.
/// </summary>
/// <remarks>
/// A scan onto a table a waiter seated changes no table state, and a scan that seated the table committed its own row
/// before the tab existed - so the waiting tab is a row of its own. A scan turned away from a table it seated frees the
/// table, and that transition's own row carries the signal (the caller's, in <c>TabAdmissionCommands</c>); this writes
/// none for it. The row names the waiter, else the diner account on the request, else the system.
/// </remarks>
internal sealed class TabFloorSignal(YallaDbContext db, ICurrentActor actor) :
    IDomainEventHandler<TabOpenedByScan>,
    IDomainEventHandler<TabAdmitted>,
    IDomainEventHandler<TabTurnedAway>
{
    public Task HandleAsync(TabOpenedByScan domainEvent, CancellationToken cancellationToken)
    {
        if (!domainEvent.Tab.IsAdmitted)
        {
            Signal(domainEvent.Tab, TableChangeSignal.TabAwaitingAdmission, "a scan is waiting to be let in", staffId: null, domainEvent.OccurredAtUtc);
        }

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabAdmitted domainEvent, CancellationToken cancellationToken)
    {
        Signal(domainEvent.Tab, TableChangeSignal.TabAdmitted, "table let in to order", domainEvent.ByStaffId, domainEvent.OccurredAtUtc);

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabTurnedAway domainEvent, CancellationToken cancellationToken)
    {
        if (!domainEvent.TableFreed)
        {
            Signal(
                domainEvent.Tab,
                TableChangeSignal.TabAdmissionRejected,
                domainEvent.Reason ?? "scan turned away",
                domainEvent.ByStaffId,
                domainEvent.OccurredAtUtc);
        }

        return Task.CompletedTask;
    }

    private void Signal(Tab tab, TableChangeSignal signal, string reason, Guid? staffId, DateTime atUtc) =>
        TabAdmissions.Signal(db, tab, signal, reason, SignalActor(staffId), atUtc);

    /// <summary>Who a branch-stream row names: the waiter, else the diner account on the request, else the system.</summary>
    private (ActorType Type, Guid? Id) SignalActor(Guid? staffId) =>
        staffId is { } staff
            ? (ActorType.Staff, staff)
            : actor.Type == ActorType.Diner && actor.DinerUserId is { } diner
                ? (ActorType.Diner, diner)
                : (ActorType.System, null);
}
