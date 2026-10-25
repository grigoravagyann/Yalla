using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// Puts what happens to a booking on its table's change stream: a same-status row naming the booking, signal
/// <see cref="TableChangeSignal.BookingChanged"/> (5) - on the table it left too, for a move - so a tablet that follows the
/// stream rather than reading the floor every few seconds refreshes that table's booking line; or, for a booking a no-show
/// block cancelled, <see cref="TableChangeSignal.BookingCancelledByBlock"/> (4) with the block's words.
/// </summary>
/// <remarks>
/// <para>
/// <b>Signal 5</b>, for a booking made, or one whose status, table, start, end or party changed - approved, declined
/// (or back at the slot it had), moved, promised to leave earlier, cancelled, marked a no-show, a no-show withdrawn - and
/// for a guest's answer to "are you coming?" (2026-10-21: the floor shows it beside the table's next booking, so the waiter
/// need not call). A change that changed none of those - a booking waiting for another reason, a hold extended - writes
/// none. It was detected from the change tracker in <c>YallaDbContext.SaveChanges</c> until 2026-10-25; it is told now,
/// and judged against where the booking stood before, as the tracker judged it against what it had loaded.
/// </para>
/// <para>
/// One row per table per booking per save: a table this save already put the booking on - a block's own row, the
/// table's own transition naming it (seated, freed), an earlier event of this save - gets no second one. The row carries
/// the table's status as saved, and nothing a client should fold. The pre-order rail's signal 6 is the pre-orders'.
/// </para>
/// <para>
/// A booking seated or completed raises nothing of its own: the table's <c>TableSeated</c> and <c>TableFreed</c> rows
/// name it.
/// </para>
/// </remarks>
internal sealed class BookingChangeStream(YallaDbContext db, IClock clock) :
    IDomainEventHandler<ReservationMade>,
    IDomainEventHandler<ReservationApproved>,
    IDomainEventHandler<ReservationDeclined>,
    IDomainEventHandler<ReservationWaitingInstead>,
    IDomainEventHandler<ReservationRescheduled>,
    IDomainEventHandler<ReservationPromisedToFreeBy>,
    IDomainEventHandler<ReservationCancelled>,
    IDomainEventHandler<ReservationMarkedNoShow>,
    IDomainEventHandler<ReservationNoShowWithdrawn>,
    IDomainEventHandler<GuestOnTheirWay>,
    IDomainEventHandler<GuestRunningLate>,
    IDomainEventHandler<OnMyWayAskRecorded>
{
    public Task HandleAsync(ReservationMade domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent, cancellationToken);

    public Task HandleAsync(ReservationApproved domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent, cancellationToken);

    public Task HandleAsync(ReservationDeclined domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent, cancellationToken);

    public Task HandleAsync(ReservationWaitingInstead domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent, cancellationToken);

    public Task HandleAsync(ReservationRescheduled domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent, cancellationToken);

    public Task HandleAsync(ReservationPromisedToFreeBy domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent, cancellationToken);

    public Task HandleAsync(ReservationCancelled domainEvent, CancellationToken cancellationToken) =>
        domainEvent.By == ReservationCanceller.NoShowBlock
            ? SignalBlockCancellationAsync(domainEvent.Reservation, domainEvent.OccurredAtUtc, cancellationToken)
            : SignalAsync(domainEvent, cancellationToken);

    public Task HandleAsync(ReservationMarkedNoShow domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent, cancellationToken);

    public Task HandleAsync(ReservationNoShowWithdrawn domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent, cancellationToken);

    // The guest's answer to "are you coming?" - and the venue asking it - are the pre-orders' events; the line beside the
    // table's next booking that shows it is the booking's.
    public Task HandleAsync(GuestOnTheirWay domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent.Reservation, before: null, answered: true, cancellationToken);

    public Task HandleAsync(GuestRunningLate domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent.Reservation, before: null, answered: true, cancellationToken);

    public Task HandleAsync(OnMyWayAskRecorded domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent.Reservation, before: null, answered: true, cancellationToken);

    private Task SignalAsync(IReservationEvent domainEvent, CancellationToken cancellationToken) =>
        SignalAsync(domainEvent.Reservation, domainEvent.Before, answered: false, cancellationToken);

    /// <summary>Signal 5 on each table the booking's change touched.</summary>
    private async Task SignalAsync(
        Reservation reservation,
        ReservationStanding? before,
        bool answered,
        CancellationToken cancellationToken)
    {
        var tables = new List<Guid>();

        if (before is null || answered || Moved(before, reservation))
        {
            tables.Add(reservation.DiningTableId);
        }

        if (before is { } left && left.DiningTableId != reservation.DiningTableId)
        {
            tables.Add(left.DiningTableId);
        }

        tables.RemoveAll(table => AlreadyOnTheStream(table, reservation.Id));

        if (tables.Count == 0)
        {
            return;
        }

        var statuses = await db.DiningTables
            .AsNoTracking()
            .Where(t => tables.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Status, cancellationToken);

        var nowUtc = clock.UtcNow;

        foreach (var tableId in tables)
        {
            if (!statuses.TryGetValue(tableId, out var status))
            {
                continue;
            }

            db.TableStateChanges.Add(
                new TableStateChange(
                    reservation.BranchId, tableId, status, status, "booking changed", ActorType.System, nowUtc,
                    Guid.CreateVersion7(), reservationId: reservation.Id)
                .WithSignal(TableChangeSignal.BookingChanged));
        }
    }

    /// <summary>
    /// Each venue is told on its change stream that a block cancelled the booking: a row on its table, naming it (review
    /// S8), with the block's words - so the floor and the console refetch and see why. A table held for it is freed
    /// afterwards, through the state machine (<c>NoShowBlocks.ReleaseHoldsOfAsync</c>).
    /// </summary>
    private async Task SignalBlockCancellationAsync(Reservation booking, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var status = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == booking.DiningTableId)
            .Select(t => (TableStatus?)t.Status)
            .FirstOrDefaultAsync(cancellationToken) ?? TableStatus.Free;

        db.TableStateChanges.Add(
            new TableStateChange(
                booking.BranchId,
                booking.DiningTableId,
                status,
                status,
                NoShowBlockRules.CancellationReason,
                ActorType.System,
                nowUtc,
                Guid.CreateVersion7(),
                reservationId: booking.Id)
            .WithSignal(TableChangeSignal.BookingCancelledByBlock));
    }

    /// <summary>Whether the booking's status, table, start, end or party changed - what the floor's line shows.</summary>
    private static bool Moved(ReservationStanding before, Reservation now) =>
        before.Status != now.Status
        || before.DiningTableId != now.DiningTableId
        || before.StartUtc != now.StartUtc
        || before.EndUtc != now.EndUtc
        || before.PartySize != now.PartySize;

    /// <summary>
    /// Whether this save already puts the booking on that table's stream - its block's row, the table's own transition,
    /// an earlier event's signal. The pre-order rail's signal 6 is about the pre-order, and does not count.
    /// </summary>
    private bool AlreadyOnTheStream(Guid tableId, Guid reservationId) =>
        db.ChangeTracker.Entries<TableStateChange>().Any(e =>
            e.State == EntityState.Added
            && e.Entity.ReservationId == reservationId
            && e.Entity.DiningTableId == tableId
            && e.Entity.Signal != TableChangeSignal.PreOrderChanged);
}
