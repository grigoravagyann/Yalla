using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>Food is up. Per-branch, and off by default - noisy in a cafe, useful in a canteen.</summary>
internal sealed class OrderReadyHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.OrderReady;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TabNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        var (title, body) = NotificationText.OrderReady(locale, notice.TableLabel);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "order-ready",
                    ["tabId"] = notice.TabId.ToString(),
                }),
            cancellationToken);
    }
}
