using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Notifications;

/// <summary>
/// A takeaway order moved on: accepted, ready or declined. Sent only while the order is still where the message says it
/// is - an "accepted" push for an order the guest has since collected says nothing true.
/// </summary>
internal abstract class TakeawayHandler(YallaDbContext db, INotificationChannel channel) : IOutboxHandler
{
    public abstract string MessageType { get; }

    /// <summary>The <c>kind</c> in the data - the feed entry's kind too.</summary>
    protected abstract string Kind { get; }

    /// <summary>The statuses in which the message is still true.</summary>
    protected abstract IReadOnlyList<Domain.Takeaway.TakeawayStatus> SendWhile { get; }

    protected abstract (string Title, string Body) Text(string locale, TakeawayNotice notice);

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var notice = Payload.Read<TakeawayNotice>(payloadJson);

        var status = await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.Id == notice.OrderId)
            .Select(o => (Domain.Takeaway.TakeawayStatus?)o.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is not { } current || !SendWhile.Contains(current))
        {
            return;
        }

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
                    ["orderId"] = notice.OrderId.ToString(),
                    ["code"] = notice.Code,
                }),
            cancellationToken);
    }
}
