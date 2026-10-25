using Yalla.Application.Abstractions;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Tables;

/// <summary>
/// The branch's change stream (<c>GET /api/branches/{id}/tables/changes</c>): one <see cref="TableStateChange"/> per table
/// event - the audit row of a transition, which is also the command's idempotency ledger and the place in the sequence
/// every tablet catching up reads - and the <see cref="TableChangeSignal.TableTransferred"/> signal of a hand-over.
/// </summary>
/// <remarks>
/// <para>
/// The row names who acted as the request does (<see cref="TableStateActors"/>): the waiter, the diner or their phone's
/// participant, or the system for a scan with no token. A reason the caller left out is said in the row's own words.
/// </para>
/// <para>
/// Added in the order the events were raised, before the save's own hooks add theirs (the booking and pre-order signals),
/// so the sequence reads as it did when the service wrote the row itself: a seating's row first, then whatever it
/// signalled.
/// </para>
/// </remarks>
internal sealed class TableChangeStream(YallaDbContext db, ICurrentActor actor) :
    IDomainEventHandler<TableSeated>,
    IDomainEventHandler<TableFreed>,
    IDomainEventHandler<TableHeld>,
    IDomainEventHandler<TableHoldReleased>,
    IDomainEventHandler<TableHoldExtended>,
    IDomainEventHandler<TableTakenOutOfService>,
    IDomainEventHandler<TableReturnedToService>,
    IDomainEventHandler<TableTransferred>
{
    public Task HandleAsync(TableSeated domainEvent, CancellationToken cancellationToken)
    {
        var booking = domainEvent.Booking;

        var said = domainEvent.How switch
        {
            TableSeating.WalkIn => "seated walk-in",
            TableSeating.QrScan => "opened by QR scan",
            TableSeating.Booking => $"seated reservation {booking?.Code}",
            TableSeating.BookingByDiner => $"seated by the diner from booking {booking?.Code}",
            _ => "seated held party",
        };

        Add(
            domainEvent.Table, domainEvent.From, TableStatus.Occupied, domainEvent.Reason ?? said, domainEvent.ClientCommandId, domainEvent.OccurredAtUtc,
            reservationId: booking?.Id, tableSessionId: domainEvent.Sitting.Id);

        return Task.CompletedTask;
    }

    public Task HandleAsync(TableFreed domainEvent, CancellationToken cancellationToken)
    {
        Add(
            domainEvent.Table, domainEvent.From, TableStatus.Free, domainEvent.Reason ?? "table freed", domainEvent.ClientCommandId, domainEvent.OccurredAtUtc,
            reservationId: domainEvent.ReservationId, tabId: domainEvent.TabId, tableSessionId: domainEvent.SittingId);

        return Task.CompletedTask;
    }

    public Task HandleAsync(TableHeld domainEvent, CancellationToken cancellationToken)
    {
        Add(
            domainEvent.Table, domainEvent.From, TableStatus.Held, domainEvent.Reason ?? "held for late party", domainEvent.ClientCommandId, domainEvent.OccurredAtUtc,
            reservationId: domainEvent.ForReservationId);

        return Task.CompletedTask;
    }

    public Task HandleAsync(TableHoldReleased domainEvent, CancellationToken cancellationToken)
    {
        Add(domainEvent.Table, domainEvent.From, TableStatus.Free, domainEvent.Reason ?? "hold released", domainEvent.ClientCommandId, domainEvent.OccurredAtUtc);

        return Task.CompletedTask;
    }

    /// <summary>From-status equals to-status on purpose: nothing about the table changed; something happened at it.</summary>
    public Task HandleAsync(TableHoldExtended domainEvent, CancellationToken cancellationToken)
    {
        var status = domainEvent.Table.Status;

        Add(
            domainEvent.Table, status, status,
            domainEvent.Reason ?? $"hold extended to {domainEvent.HoldExpiresAtUtc:HH:mm} UTC", domainEvent.ClientCommandId, domainEvent.OccurredAtUtc,
            reservationId: domainEvent.ReservationId);

        return Task.CompletedTask;
    }

    public Task HandleAsync(TableTakenOutOfService domainEvent, CancellationToken cancellationToken)
    {
        Add(domainEvent.Table, domainEvent.From, TableStatus.OutOfService, domainEvent.Reason ?? "out of service", domainEvent.ClientCommandId, domainEvent.OccurredAtUtc);

        return Task.CompletedTask;
    }

    public Task HandleAsync(TableReturnedToService domainEvent, CancellationToken cancellationToken)
    {
        Add(domainEvent.Table, domainEvent.From, TableStatus.Free, domainEvent.Reason ?? "returned to service", domainEvent.ClientCommandId, domainEvent.OccurredAtUtc);

        return Task.CompletedTask;
    }

    /// <summary>
    /// A same-status signal naming the tab, so every tablet following the stream re-reads the floor and sees the new
    /// waiter.
    /// </summary>
    public async Task HandleAsync(TableTransferred domainEvent, CancellationToken cancellationToken)
    {
        var tab = await TableReads.TabAsync(db, domainEvent.TabId, cancellationToken);
        var toName = await TableReads.StaffNameAsync(db, domainEvent.ToStaffId, cancellationToken);
        var (actorType, actorId) = TableStateActors.Resolve(actor);

        db.TableStateChanges.Add(new TableStateChange(
                tab.BranchId,
                tab.DiningTableId,
                TableStatus.Occupied,
                TableStatus.Occupied,
                $"handed to {toName}",
                actorId is null ? ActorType.System : actorType,
                domainEvent.OccurredAtUtc,
                Guid.CreateVersion7(),
                actorId,
                tabId: tab.Id,
                tableSessionId: tab.TableSessionId)
            .WithSignal(TableChangeSignal.TableTransferred));
    }

    private void Add(
        DiningTable table,
        TableStatus from,
        TableStatus to,
        string reason,
        Guid clientCommandId,
        DateTime atUtc,
        Guid? reservationId = null,
        Guid? tabId = null,
        Guid? tableSessionId = null)
    {
        var (actorType, actorId) = TableStateActors.Resolve(actor);

        db.TableStateChanges.Add(new TableStateChange(
            table.BranchId,
            table.Id,
            from,
            to,
            reason,
            actorType,
            atUtc,
            clientCommandId,
            actorId,
            reservationId,
            tabId,
            tableSessionId));
    }
}
