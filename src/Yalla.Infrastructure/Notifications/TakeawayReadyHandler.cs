using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>"Your order is ready - come and pick it up."</summary>
internal sealed class TakeawayReadyHandler(YallaDbContext db, INotificationChannel channel)
    : TakeawayHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.TakeawayReady;

    protected override string Kind => "takeaway-ready";

    protected override IReadOnlyList<Domain.Takeaway.TakeawayStatus> SendWhile => [Domain.Takeaway.TakeawayStatus.Ready];

    protected override (string Title, string Body) Text(string locale, TakeawayNotice notice) =>
        NotificationText.TakeawayReady(locale, notice.VenueName, notice.Code);
}
