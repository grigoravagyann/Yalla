using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>Somebody scanned the table and is waiting for the host to let them on.</summary>
/// <remarks>
/// Sent to the host only while the joiner is still waiting. The host may have seen them on the
/// People screen first and let them on, or turned them away; "Anna wants to join" after Anna is
/// already on the tab is the push that teaches a host to ignore the next one.
/// </remarks>
internal sealed class JoinRequestedHandler(
    YallaDbContext db,
    INotificationChannel channel,
    ILogger<JoinRequestedHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.JoinRequested;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TabNotice>(payloadJson);

        // Still a question this person can answer: the joiner still waiting, the tab still taking
        // people - closing leaves a pending joiner pending, and nobody new is let on a tab being
        // settled - and the one it is addressed to still its host, which staff can change.
        var standing = await db.TabParticipants
            .AsNoTracking()
            .Where(p => p.Id == notice.ParticipantId && p.TabId == notice.TabId)
            .Select(p => new
            {
                p.Status,
                TabStatus = p.Tab.Status,
                HostUserId = p.Tab.Participants
                    .Where(h => h.Id == p.Tab.HostParticipantId)
                    .Select(h => h.UserId)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (standing is not { Status: ParticipantStatus.PendingApproval, TabStatus: TabStatus.Open }
            || standing.HostUserId != notice.DinerUserId)
        {
            logger.LogInformation(
                "Skipping the join request for participant {ParticipantId} on tab {TabId}: they are {Status}, "
                + "the tab is {TabStatus}, and the host it was written for is {Host} its host.",
                notice.ParticipantId,
                notice.TabId,
                standing?.Status,
                standing?.TabStatus,
                standing?.HostUserId == notice.DinerUserId ? "still" : "no longer");

            return;
        }

        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.JoinRequested(locale, notice.TableLabel, notice.GuestName);

        // The tab, so a tap opens the bill the request is waiting on; the participant too, so a
        // client can go straight to them. The kind is camelCase, unlike the older kebab-case ones,
        // because it is the exact string the diner app routes this tap on.
        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "tabJoinRequested",
                    ["tabId"] = notice.TabId.ToString(),
                    ["participantId"] = notice.ParticipantId.ToString(),
                }),
            cancellationToken);
    }
}
