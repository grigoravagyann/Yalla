using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// To the booking's account: somebody holding the booking's manage link changed it, or cancelled it.
/// </summary>
/// <remarks>
/// <para>
/// The link goes round group chats, so whoever acted may not be the diner - and a booking moved without
/// their knowing becomes their no-show. So the account holder is told, whatever the booking has done since:
/// a later change from the link replaces this message while it is unsent, and a cancel replaces it too.
/// </para>
/// <para>
/// One class for both types: the words differ, the delivery does not. The data carries <c>kind</c>
/// (<c>reservation-changed-by-link</c> or <c>reservation-cancelled-by-link</c>) and <c>reservationId</c>, which
/// is where a tap lands.
/// </para>
/// </remarks>
internal abstract class ManageLinkActionHandler(
    YallaDbContext db,
    INotificationChannel channel) : IOutboxHandler
{
    public abstract string MessageType { get; }

    protected abstract string Kind { get; }

    protected abstract (string Title, string Body) Text(string locale, ReservationNotice notice);

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<ReservationNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);
        var (title, body) = Text(locale, notice);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = Kind,
                    ["reservationId"] = notice.ReservationId.ToString(),
                }),
            cancellationToken);
    }

    /// <summary>The day the notice was written about; a payload always carries it.</summary>
    protected static DateOnly DateOf(ReservationNotice notice) =>
        notice.LocalDate ?? throw new InvalidOperationException("A manage-link notice carries the booking's date.");
}
