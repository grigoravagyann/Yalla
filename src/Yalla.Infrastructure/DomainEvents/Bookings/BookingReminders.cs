using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Bookings;

/// <summary>
/// A booking's queued messages - its reminder, late nudge and "are you on your way?", and the feed entries waiting to
/// appear with them - follow the booking: replaced for the slot it now has when it moves (or a declined change sends it
/// back to the slot it had), withdrawn when it ends before its party sits (cancelled, declined, let go, a no-show).
/// </summary>
/// <remarks>
/// <para>
/// Withdrawn by key prefix, unsent only (<c>IOutbox.CancelAsync</c>, <c>reservation:{id}:</c>): a reminder arriving for
/// a booking somebody cancelled an hour ago is worse than none. That reads the saved outbox, so a message this save writes
/// for the booking - the decision, the link's notice - is not taken with it, whichever handler runs first.
/// </para>
/// <para>
/// A new booking's reminders are not written here: they are written just after its insert commits, in a transaction of
/// their own under the account's lock, with the manage link only the creation knows (<c>ReservationReminders.ScheduleRemindersAsync</c>).
/// </para>
/// </remarks>
internal sealed class BookingReminders(YallaDbContext db, IOutbox outbox) :
    IDomainEventHandler<ReservationDeclined>,
    IDomainEventHandler<ReservationRescheduled>,
    IDomainEventHandler<ReservationCancelled>,
    IDomainEventHandler<ReservationMarkedNoShow>
{
    public Task HandleAsync(ReservationDeclined domainEvent, CancellationToken cancellationToken) =>
        domainEvent.SlotKept
            ? ReplaceAsync(domainEvent.Reservation, domainEvent.OccurredAtUtc, cancellationToken)
            : WithdrawAsync(domainEvent.Reservation, domainEvent.OccurredAtUtc, cancellationToken);

    public Task HandleAsync(ReservationRescheduled domainEvent, CancellationToken cancellationToken) =>
        ReplaceAsync(domainEvent.Reservation, domainEvent.OccurredAtUtc, cancellationToken);

    public Task HandleAsync(ReservationCancelled domainEvent, CancellationToken cancellationToken) =>
        WithdrawAsync(domainEvent.Reservation, domainEvent.OccurredAtUtc, cancellationToken);

    public Task HandleAsync(ReservationMarkedNoShow domainEvent, CancellationToken cancellationToken) =>
        WithdrawAsync(domainEvent.Reservation, domainEvent.OccurredAtUtc, cancellationToken);

    /// <summary>
    /// The unsent ones go and new ones are written for the slot it has now - see
    /// <see cref="ReservationReminders.ReplaceRemindersAsync"/>. The account is live: a move holds its lock, and so does a
    /// declined change going back to its slot.
    /// </summary>
    private async Task ReplaceAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var branch = await BookingReads.BranchAsync(db, reservation.BranchId, cancellationToken);
        var tableLabel = await BookingReads.TableLabelAsync(db, reservation.DiningTableId, cancellationToken);

        await ReservationReminders.ReplaceRemindersAsync(db, outbox, reservation, branch, tableLabel, nowUtc, cancellationToken);
    }

    /// <summary>Whatever is left unsent for the booking goes with it, and its feed entries not yet shown.</summary>
    private async Task WithdrawAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await outbox.CancelAsync(OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);
        await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);
    }
}
