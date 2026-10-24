using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Deletes guest-message threads whose last message is over <see cref="ConversationRules.RetentionDays"/> days old, with
/// their messages. See <c>docs/messages.md</c> §history.
/// </summary>
/// <remarks>
/// <para>
/// Run by the outbox's background loop (<c>OutboxHostedService</c>) on its wake-up pass and then about once a day,
/// beside the notifications feed's retention - the same ride, a longer interval.
/// </para>
/// <para>
/// <b>Only what is a year quiet and done.</b> A thread still open is kept however old its last message: a booking
/// pending, confirmed or seated until two hours after it ends - one may be made a year ahead - and an order still sent,
/// accepted or ready. Deleting either would take the guest's words away while the venue can still be asked. A booking
/// left "confirmed" long after its evening, or an order left "ready" for a year, is nobody's live business, and its
/// thread goes with the rest: otherwise a status nobody closed would keep a person's messages for ever. The messages go
/// with the thread by the foreign key's cascade; nothing else points at a thread.
/// </para>
/// <para>
/// In batches of <see cref="BatchSize"/>, each its own statement, so a first sweep over a year of history never holds a
/// lock on the whole table while the tablets write.
/// </para>
/// </remarks>
internal sealed class ConversationRetention(YallaDbContext db, IClock clock)
{
    internal const int BatchSize = 500;

    /// <summary>Deletes what has aged out and says how many threads went.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;
        var cutoffUtc = nowUtc.AddDays(-ConversationRules.RetentionDays);
        var endedBefore = nowUtc.AddHours(-ConversationRules.OpenAfterEndHours);
        var deleted = 0;

        while (true)
        {
            // The status lists as constants, so they reach the SQL as literals, as the inbox writes them.
            var batch = await db.Conversations
                .Where(c => c.LastMessageUtc < cutoffUtc
                            && !db.Reservations.Any(r => r.Id == c.ReservationId
                                                         && (r.Status == ReservationStatus.PendingApproval
                                                             || r.Status == ReservationStatus.Confirmed
                                                             || r.Status == ReservationStatus.Seated)
                                                         && r.EndUtc > endedBefore)
                            && !db.TakeawayOrders.Any(o => o.Id == c.TakeawayOrderId
                                                           && (o.Status == TakeawayStatus.Sent
                                                               || o.Status == TakeawayStatus.Accepted
                                                               || o.Status == TakeawayStatus.Ready)
                                                           && o.CreatedAtUtc >= cutoffUtc))
                .OrderBy(c => c.LastMessageUtc)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);

            deleted += batch;

            if (batch < BatchSize)
            {
                return deleted;
            }
        }
    }
}
