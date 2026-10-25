using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>"Your booking was changed" - see <see cref="ManageLinkActionHandler"/>.</summary>
internal sealed class ReservationChangedByLinkHandler(YallaDbContext db, INotificationChannel channel)
    : ManageLinkActionHandler(db, channel)
{
    public override string MessageType => OutboxMessageTypes.ReservationChangedByLink;

    protected override string Kind => "reservation-changed-by-link";

    protected override (string Title, string Body) Text(string locale, ReservationNotice notice) =>
        NotificationText.ReservationChangedByLink(
            locale,
            notice.VenueName,
            DateOf(notice),
            notice.LocalStartTime,
            notice.PartySize,
            waiting: notice.Status == ReservationStatus.PendingApproval);
}
