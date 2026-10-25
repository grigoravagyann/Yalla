using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>"You can book again" - to a guest whose no-show block was lifted early.</summary>
internal sealed class DinerBookingsUnblockedHandler(YallaDbContext db, INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.DinerBookingsUnblocked;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<UnblockedNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);
        var (title, body) = NotificationText.BookingsUnblocked(locale);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "bookings-unblocked",
                    ["liftedBecause"] = ((int?)notice.LiftedBecause ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
            cancellationToken);
    }

    private sealed record UnblockedNotice(Guid DinerUserId, Domain.Occupancy.BookingBlockLiftCause? LiftedBecause = null);
}
