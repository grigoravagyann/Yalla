using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>The host let a pending joiner onto the tab.</summary>
internal sealed class ParticipantApprovedHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.ParticipantApproved;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TabNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.ParticipantApproved(locale, notice.TableLabel);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "participant-approved",
                    ["tabId"] = notice.TabId.ToString(),
                }),
            cancellationToken);
    }
}
