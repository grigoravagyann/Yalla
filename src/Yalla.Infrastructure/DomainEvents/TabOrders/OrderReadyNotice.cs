using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.TabOrders;

/// <summary>
/// Food is up: the <c>tab.order-ready</c> push and the diner feed's entry, for each guest with an account whose food is
/// on the order (<c>docs/notifications.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Per-branch and off by default (<c>Branch.NotifyOnOrderReady</c>): noisy in a cafe where a waiter carries the plate
/// ten feet, useful in a canteen where the diner collects it. Defaulting it on would train a city to switch our
/// notifications off, taking the reminder and the nudge with them.
/// </para>
/// <para>
/// The entry and its push are the person's, so they are written under the account's lock (<c>DinerAccountLock</c>),
/// held to the commit, and only for a live account: a deletion that comes after waits for this commit and removes the
/// entry. The lock needs an open transaction to hold it - <see cref="YallaDbContext.EnsureTransactionAsync"/> opens one
/// that the save commits, or joins the caller's.
/// </para>
/// </remarks>
internal sealed class OrderReadyNotice(YallaDbContext db, IClock clock, IOutbox outbox) : IDomainEventHandler<TabOrderMoved>
{
    public async Task HandleAsync(TabOrderMoved domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.To != TabOrderStatus.Ready)
        {
            return;
        }

        var order = domainEvent.Order;
        var tab = await TabOrderReads.TabAsync(db, order, cancellationToken);
        var branch = await TabOrderReads.BranchAsync(db, tab, cancellationToken);

        if (!branch.NotifyOnOrderReady)
        {
            return;
        }

        var recipients = ReadyRecipients(order, tab);

        if (recipients.Count == 0)
        {
            return;
        }

        var tableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken) ?? string.Empty;

        await db.EnsureTransactionAsync(cancellationToken);

        foreach (var (recipient, dinerUserId) in recipients)
        {
            if (!await DinerAccountLock.IsLiveAsync(db, dinerUserId, cancellationToken))
            {
                continue;
            }

            outbox.Enqueue(
                OutboxMessageTypes.OrderReady,
                new
                {
                    tabId = order.TabId,
                    participantId = recipient.Id,
                    dinerUserId,
                    tableLabel,
                },
                clock.UtcNow,

                // The order's own guest keeps the key a single-owner order always had; a guest with
                // a line of their own on it gets one of their own beside it.
                OutboxMessageTypes.KeyFor(
                    "order", order.Id, recipient.Id == order.OwningParticipantId ? "ready" : $"ready:{recipient.Id}"));

            // Into the diner's feed beside the push, saved with it (K12).
            DinerNotices.OrderReady(
                db,
                dinerUserId,
                tab.BranchId,
                branch.Venue?.Name ?? branch.Name,
                branch.Name,
                order.TabId,
                order.Id,
                tableLabel,
                clock.UtcNow);
        }
    }

    /// <summary>
    /// Who to tell that an order is ready: everybody with an account it is for.
    /// </summary>
    /// <remarks>
    /// The diner who ordered it from their phone, as it always was, and each guest who owns a line of
    /// it that is still on the bill - named on the line, or by the order for a line that names nobody
    /// (<see cref="TabOrder.OwnerOf"/>). One round for the table can be three people's dinners, and
    /// each of them is waiting for theirs. The order-level guest is <b>not</b> told as such: an order
    /// "for Ani" whose every line the waiter put on Bob, or whose lines for Ani were all voided, has
    /// nothing of hers left to be ready. One entry per account, in account order, which is the order
    /// the account locks are then taken in.
    /// </remarks>
    private static List<(TabParticipant Participant, Guid DinerUserId)> ReadyRecipients(TabOrder order, Tab tab)
    {
        var owners = order.Lines
            .Where(l => !l.IsVoided)
            .Select(order.OwnerOf)
            .Prepend(order.PlacedByParticipantId)
            .OfType<Guid>()
            .ToHashSet();

        return
        [
            .. tab.Participants
                .Where(p => owners.Contains(p.Id) && p.UserId is not null)
                .Select(p => (Participant: p, DinerUserId: p.UserId!.Value))
                .GroupBy(r => r.DinerUserId)
                .Select(g => g.OrderBy(r => r.Participant.Id != order.OwningParticipantId).First())
                .OrderBy(r => r.DinerUserId),
        ];
    }
}
