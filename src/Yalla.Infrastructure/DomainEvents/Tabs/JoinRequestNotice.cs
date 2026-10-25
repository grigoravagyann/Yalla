using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.DomainEvents.Tabs;

/// <summary>
/// Tells the host somebody is waiting to be let on: <c>tab.join-requested</c> to the host's account, when the host
/// scanned with one. The tab's stream says it to every phone at the table (<see cref="TabLifecycleStream"/>).
/// </summary>
/// <remarks>
/// <para>
/// Only for a join that lands pending (<see cref="TabJoinRequested"/>). The first phone onto a tab staff opened hosts
/// it, and the booker on their own booking's sitting is approved on the spot and announced by
/// <c>ParticipantApproved</c>: neither is waiting for anybody.
/// </para>
/// <para>
/// The push has one address, the host's account - see <see cref="TabParticipant.UserId"/>. Most hosts scanned without
/// one, and then the stream is all there is. The host's own account on a second phone lands pending like anybody else,
/// and is not told that it wants to join itself.
/// </para>
/// <para>
/// Somebody the host already turned away on this tab - the same phone, or the same account on another - is on the
/// stream again but not pushed again. The host sees them on the bill and can still let them on; they are not woken for
/// every retry of a question they answered.
/// </para>
/// </remarks>
internal sealed class JoinRequestNotice(IOutbox outbox, IClock clock, ILogger<JoinRequestNotice> logger) :
    IDomainEventHandler<TabJoinRequested>
{
    public Task HandleAsync(TabJoinRequested domainEvent, CancellationToken cancellationToken)
    {
        var (tab, joiner) = (domainEvent.Tab, domainEvent.Participant);

        var hostUserId = tab.Participants.FirstOrDefault(p => p.Id == tab.HostParticipantId)?.UserId;

        if (hostUserId is not { } dinerUserId || joiner.UserId == dinerUserId)
        {
            return Task.CompletedTask;
        }

        var askedBefore = tab.Participants.Any(p =>
            p.WasTurnedAway
            && (p.DeviceId == joiner.DeviceId || (joiner.UserId is { } account && p.UserId == account)));

        if (askedBefore)
        {
            logger.LogInformation(
                "Participant {ParticipantId} is back on tab {TabId} after the host turned them away; not pushing again.",
                joiner.Id, tab.Id);

            return Task.CompletedTask;
        }

        outbox.Enqueue(
            OutboxMessageTypes.JoinRequested,
            new
            {
                tabId = tab.Id,
                participantId = joiner.Id,
                dinerUserId,
                tableLabel = tab.DiningTable?.Label ?? string.Empty,
                // The name they gave, or none. "Guest 3" is the roster's placeholder in English; the push says "a
                // guest" in the host's own language instead.
                guestName = string.IsNullOrWhiteSpace(domainEvent.GivenName) ? null : joiner.DisplayName,
            },
            clock.UtcNow,
            OutboxMessageTypes.KeyFor("participant", joiner.Id, "join-requested"));

        return Task.CompletedTask;
    }
}
