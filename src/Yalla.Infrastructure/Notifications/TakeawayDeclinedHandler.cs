using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>"Order declined" - with the venue's reason, or the apology for nobody answering.</summary>
internal sealed class TakeawayDeclinedHandler(YallaDbContext db, INotificationChannel channel)
    : TakeawayHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.TakeawayDeclined;

    protected override string Kind => "takeaway-declined";

    protected override IReadOnlyList<Domain.Takeaway.TakeawayStatus> SendWhile => [Domain.Takeaway.TakeawayStatus.Declined];

    protected override (string Title, string Body) Text(string locale, TakeawayNotice notice) =>
        NotificationText.TakeawayDeclined(locale, notice.VenueName, notice.Code, notice.Reason, notice.NoAnswer);
}
