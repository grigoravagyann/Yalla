using System.Globalization;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Messages;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Messages;

/// <summary>
/// Tells the thread's guest that the venue wrote, or asked "are you coming?": push <c>message.to-guest</c> through the
/// outbox (<see cref="VenueMessageNotice"/>, keyed <c>conversation:{id}:message:{messageId}</c>, sent by
/// <see cref="VenueMessageHandler"/>) and the feed entry <c>venue-message</c> beside it, in the save that wrote the message
/// (<c>docs/messages.md</c>, <c>docs/notifications.md</c>).
/// </summary>
/// <remarks>
/// The entry and its push are the person's, so they are written under the account's lock, held to the commit, and only
/// for a live account (<see cref="ConversationReads.GuestIsLiveAsync"/>): a deletion that comes after waits for this
/// commit and removes the entry; a thread with no account - a booking taken by phone - or one already gone is told
/// nothing. The venue's write and the ask already hold a transaction, which the lock joins.
/// </remarks>
internal sealed class VenueMessageGuestNotice(YallaDbContext db, IOutbox outbox, ConversationReads reads) :
    IDomainEventHandler<VenueMessageWritten>,
    IDomainEventHandler<VenueAskedComing>
{
    public Task HandleAsync(VenueMessageWritten domainEvent, CancellationToken cancellationToken) =>
        TellAsync(domainEvent.Thread, domainEvent.Message, cancellationToken);

    public Task HandleAsync(VenueAskedComing domainEvent, CancellationToken cancellationToken) =>
        TellAsync(domainEvent.Thread, domainEvent.Ask, cancellationToken);

    private async Task TellAsync(Conversation thread, ConversationMessage message, CancellationToken cancellationToken)
    {
        if (!await reads.GuestIsLiveAsync(thread, message, cancellationToken))
        {
            return;
        }

        var dinerUserId = thread.DinerUserId!.Value;
        var facts = await reads.SubjectAsync(thread, message, cancellationToken);
        var ask = message.Kind == MessageKind.AskComing;
        var preview = message.Body is { } body ? ConversationRules.Preview(body) : null;
        var nowUtc = message.CreatedAtUtc;

        outbox.Enqueue(
            OutboxMessageTypes.MessageToGuest,
            new VenueMessageNotice(
                thread.Id,
                message.Id,
                dinerUserId,
                facts.VenueName,
                thread.ReservationId,
                thread.TakeawayOrderId,
                ask,
                preview,
                facts.LocalStartTime),
            nowUtc,
            OutboxMessageTypes.KeyFor("conversation", thread.Id, $"message:{message.Id}"));

        var values = new Dictionary<string, string>
        {
            ["conversationId"] = thread.Id.ToString(),
            ["subjectKind"] = GuestThreads.SubjectKindName(facts.Kind),
            ["ask"] = ask ? "true" : "false",
        };

        if (preview is not null)
        {
            values["text"] = preview;
        }

        if (facts.LocalDate is { } date)
        {
            values["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (facts.LocalStartTime is { } time)
        {
            values["time"] = GuestThreads.Hhmm(time);
        }

        if (facts.TakeawayCode is { } code)
        {
            values["code"] = code;
        }

        DinerNotices.VenueMessage(
            db, dinerUserId, thread.BranchId, thread.ReservationId, thread.TakeawayOrderId, facts.VenueName, facts.BranchName, values, nowUtc);
    }
}
