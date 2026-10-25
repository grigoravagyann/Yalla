using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>"Booking paused for a week" - to a guest just blocked for three no-shows in three days.</summary>
internal sealed class DinerBookingsBlockedHandler(YallaDbContext db, INotificationChannel channel) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.DinerBookingsBlocked;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<BookingsBlockedNotice>(payloadJson);
        var locale = await Payload.LocaleForAsync(db, notice.DinerUserId, cancellationToken);

        // The day on the venue's wall clock, not UTC's (review N3); a payload from before it falls back to UTC.
        var (title, body) = NotificationText.BookingsBlocked(
            locale, notice.BlockedUntilLocalDate ?? DateOnly.FromDateTime(notice.BlockedUntilUtc), notice.CancelledCount);

        await channel.SendAsync(
            new NotificationMessage(
                notice.DinerUserId,
                title,
                body,
                new Dictionary<string, string>
                {
                    ["kind"] = "bookings-blocked-no-shows",
                    ["blockedUntilUtc"] = notice.BlockedUntilUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                }),
            cancellationToken);
    }

    /// <summary>The push's payload.</summary>
    private sealed record BookingsBlockedNotice(
        Guid DinerUserId, DateTime BlockedUntilUtc, int CancelledCount, DateOnly? BlockedUntilLocalDate = null);
}
