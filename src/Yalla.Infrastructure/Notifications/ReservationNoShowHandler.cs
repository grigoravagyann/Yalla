using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>"Marked as a no-show at {venue}" - to the booking's account, every time (review B1d).</summary>
internal sealed class ReservationNoShowHandler(YallaDbContext db, INotificationChannel channel)
    : ManageLinkActionHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.ReservationNoShow;

    protected override string Kind => "booking-no-show";

    protected override (string Title, string Body) Text(string locale, ReservationNotice notice) =>
        NotificationText.NoShowMarked(locale, notice.VenueName, DateOf(notice), notice.LocalStartTime);
}
