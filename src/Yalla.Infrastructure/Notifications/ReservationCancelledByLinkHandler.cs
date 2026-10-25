using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>"Your booking was cancelled" - see <see cref="ManageLinkActionHandler"/>.</summary>
internal sealed class ReservationCancelledByLinkHandler(YallaDbContext db, INotificationChannel channel)
    : ManageLinkActionHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.ReservationCancelledByLink;

    protected override string Kind => "reservation-cancelled-by-link";

    protected override (string Title, string Body) Text(string locale, ReservationNotice notice) =>
        NotificationText.ReservationCancelledByLink(locale, notice.VenueName, DateOf(notice), notice.LocalStartTime);
}
