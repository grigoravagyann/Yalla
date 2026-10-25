using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.DomainEvents.Tabs;

/// <summary>
/// Tells a guest the host let them on: <c>tab.participant-approved</c>, to the account their phone scanned with.
/// </summary>
/// <remarks>
/// The one message worth pushing on the roster's paths: the guest is sitting at the table with the phone in their
/// pocket, on a screen that says "waiting for the host". Only reaches somebody whose phone was carrying a diner token
/// when it scanned - see <see cref="TabParticipant.UserId"/>. A booker let on by their booking was waiting for nobody,
/// and is not told. Two approvals racing collide on the message's key; the service answers the loser with the winner.
/// </remarks>
internal sealed class ParticipantApprovedNotice(IOutbox outbox, IClock clock) : IDomainEventHandler<TabParticipantApproved>
{
    public Task HandleAsync(TabParticipantApproved domainEvent, CancellationToken cancellationToken)
    {
        var (tab, participant) = (domainEvent.Tab, domainEvent.Participant);

        if (domainEvent.AsBooker || participant.UserId is not { } dinerUserId)
        {
            return Task.CompletedTask;
        }

        outbox.Enqueue(
            OutboxMessageTypes.ParticipantApproved,
            new
            {
                tabId = tab.Id,
                participantId = participant.Id,
                dinerUserId,
                tableLabel = tab.DiningTable?.Label ?? string.Empty,
            },
            clock.UtcNow,
            OutboxMessageTypes.KeyFor("participant", participant.Id, "approved"));

        return Task.CompletedTask;
    }
}
