using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// What a guest is told about their no-show block (<see cref="NoShowBlockRules"/>): blocked - one push
/// (<c>diner.bookings-blocked</c>) and one feed entry for the whole block, the day they can book again and how many
/// bookings it cancelled; lifted early - "you can book again" (<c>diner.bookings-unblocked</c>) and its feed entry.
/// </summary>
/// <remarks>
/// Each cancelled booking's own side effects are its <see cref="ReservationCancelled"/>'s. The block is judged in the
/// services, after the no-show is saved (<c>NoShowBlocks</c>): see <c>docs/domain-events.md</c>, bookings.
/// </remarks>
internal sealed class BookingBlockNotice(YallaDbContext db, IOutbox outbox) :
    IDomainEventHandler<BookingBlockImposed>,
    IDomainEventHandler<BookingBlockLifted>
{
    public Task HandleAsync(BookingBlockImposed domainEvent, CancellationToken cancellationToken)
    {
        var (block, nowUtc) = (domainEvent.Block, domainEvent.OccurredAtUtc);

        outbox.Enqueue(
            OutboxMessageTypes.DinerBookingsBlocked,
            new
            {
                dinerUserId = block.DinerUserId,
                blockedUntilUtc = block.BlockedUntilUtc,
                blockedUntilLocalDate = domainEvent.BlockedUntilLocalDate,
                cancelledCount = domainEvent.CancelledBookings,
            },
            nowUtc,
            OutboxMessageTypes.KeyFor("booking-block", block.Id, "notice"));

        DinerNotices.BookingsBlockedForNoShows(
            db, block.DinerUserId, block.BlockedUntilUtc, domainEvent.BlockedUntilLocalDate, domainEvent.CancelledBookings, nowUtc);

        return Task.CompletedTask;
    }

    public Task HandleAsync(BookingBlockLifted domainEvent, CancellationToken cancellationToken)
    {
        var (block, nowUtc) = (domainEvent.Block, domainEvent.OccurredAtUtc);

        outbox.Enqueue(
            OutboxMessageTypes.DinerBookingsUnblocked,
            new { dinerUserId = block.DinerUserId, liftedBecause = block.LiftedBecause },
            nowUtc,
            OutboxMessageTypes.KeyFor("booking-block", block.Id, "lifted"));

        DinerNotices.BookingsUnblocked(db, block.DinerUserId, block.LiftedBecause!.Value, nowUtc);

        return Task.CompletedTask;
    }
}
