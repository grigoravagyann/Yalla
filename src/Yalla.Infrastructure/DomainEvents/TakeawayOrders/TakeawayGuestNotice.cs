using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Domain.Identity;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.TakeawayOrders;

/// <summary>
/// Tells a takeaway order's guest what happened to it - accepted, ready, declined - by a push through the outbox and the
/// feed entry beside it, in the save that made the change (<c>docs/takeaway.md</c>, <c>docs/notifications.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// One message per step, keyed <c>takeaway:{id}:accepted</c>, <c>:ready</c>, <c>:declined</c>: a step happens once, so
/// the key cannot collide. An order accepted on placing writes its feed entry and no push - the answer to the guest's
/// own tap already says it is accepted. One the sweep accepts later is pushed: the guest is still waiting to hear.
/// </para>
/// <para>
/// The entry and its push are the person's, so they are written under the account's lock (<c>DinerAccountLock</c>),
/// held to the commit, and only for a live account: a deletion that comes after waits for this commit and removes the
/// entry; an account already gone is told nothing. The lock needs an open transaction -
/// <see cref="YallaDbContext.EnsureTransactionAsync"/> joins the caller's (placing, the sweep) or opens one the save
/// commits (the counter's answers).
/// </para>
/// </remarks>
internal sealed class TakeawayGuestNotice(YallaDbContext db, IOutbox outbox) :
    IDomainEventHandler<TakeawayPlaced>,
    IDomainEventHandler<TakeawayAccepted>,
    IDomainEventHandler<TakeawayMarkedReady>,
    IDomainEventHandler<TakeawayDeclined>
{
    /// <summary>The step the guest is told about.</summary>
    private enum Step
    {
        Accepted,
        Ready,
        Declined,
    }

    public Task HandleAsync(TakeawayPlaced domainEvent, CancellationToken cancellationToken) =>
        domainEvent.AcceptedOnPlacing
            ? WriteAsync(domainEvent.Order, Step.Accepted, domainEvent.OccurredAtUtc, push: false, cancellationToken)
            : Task.CompletedTask;

    public Task HandleAsync(TakeawayAccepted domainEvent, CancellationToken cancellationToken) =>
        WriteAsync(domainEvent.Order, Step.Accepted, domainEvent.OccurredAtUtc, push: true, cancellationToken);

    public Task HandleAsync(TakeawayMarkedReady domainEvent, CancellationToken cancellationToken) =>
        WriteAsync(domainEvent.Order, Step.Ready, domainEvent.OccurredAtUtc, push: true, cancellationToken);

    public Task HandleAsync(TakeawayDeclined domainEvent, CancellationToken cancellationToken) =>
        WriteAsync(domainEvent.Order, Step.Declined, domainEvent.OccurredAtUtc, push: true, cancellationToken);

    private async Task WriteAsync(TakeawayOrder order, Step step, DateTime nowUtc, bool push, CancellationToken cancellationToken)
    {
        if (order.DinerUserId is not { } dinerUserId)
        {
            return;
        }

        await db.EnsureTransactionAsync(cancellationToken);

        if (!await DinerAccountLock.IsLiveAsync(db, dinerUserId, cancellationToken))
        {
            return;
        }

        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == order.BranchId)
            .Select(b => new { b.Name, VenueName = b.Venue.Name, b.TimeZoneId })
            .FirstAsync(cancellationToken);

        var zone = BranchZone.For(branch.TimeZoneId);
        TimeOnly? readyLocal = order.ReadyAtUtc is { } readyAt ? zone.LocalTimeAt(readyAt) : null;
        var noAnswer = order.DeclinedBecause == TakeawayDeclineCause.NoAnswer;

        var (type, kind, what) = step switch
        {
            Step.Accepted => (OutboxMessageTypes.TakeawayAccepted, DinerNotificationKinds.TakeawayAccepted, "accepted"),
            Step.Ready => (OutboxMessageTypes.TakeawayReady, DinerNotificationKinds.TakeawayReady, "ready"),
            _ => (OutboxMessageTypes.TakeawayDeclined, DinerNotificationKinds.TakeawayDeclined, "declined"),
        };

        var extra = new Dictionary<string, string>();

        if (step != Step.Declined && order.ReadyAtUtc is { } promised)
        {
            extra["readyTime"] = readyLocal!.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
            extra["readyAtUtc"] = promised.ToString("O", CultureInfo.InvariantCulture);
        }

        if (step == Step.Declined)
        {
            extra["noAnswer"] = noAnswer ? "true" : "false";

            if (order.DeclineReason is { } reason)
            {
                extra["reason"] = reason;
            }
        }

        if (push)
        {
            outbox.Enqueue(
                type,
                new TakeawayNotice(
                    order.Id,
                    dinerUserId,
                    branch.VenueName,
                    branch.Name,
                    order.Code,
                    readyLocal,
                    order.DeclineReason,
                    noAnswer),
                nowUtc,
                OutboxMessageTypes.KeyFor("takeaway", order.Id, what));
        }

        DinerNotices.Takeaway(
            db, kind, dinerUserId, order.BranchId, order.Id, branch.VenueName, branch.Name, order.Code, nowUtc, extra);
    }
}
