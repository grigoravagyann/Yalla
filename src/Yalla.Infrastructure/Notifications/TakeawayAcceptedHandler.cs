using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>"Order accepted - ready at 19:10."</summary>
internal sealed class TakeawayAcceptedHandler(YallaDbContext db, INotificationChannel channel)
    : TakeawayHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.TakeawayAccepted;

    protected override string Kind => "takeaway-accepted";

    protected override IReadOnlyList<Domain.Takeaway.TakeawayStatus> SendWhile => [Domain.Takeaway.TakeawayStatus.Accepted];

    protected override (string Title, string Body) Text(string locale, TakeawayNotice notice) =>
        NotificationText.TakeawayAccepted(locale, notice.VenueName, notice.Code, notice.ReadyAtLocalTime ?? default);
}
