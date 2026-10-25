using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Audit;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Deletes activity entries past their keeping: a year for most, three years for those about money
/// (<see cref="ActivityEntry.IsMoney"/> - payments, discounts, comps, voids, write-offs, takeaway collected). See
/// <c>docs/activity.md</c> §retention.
/// </summary>
/// <remarks>
/// <para>
/// Run by the outbox's background loop (<c>OutboxHostedService</c>) on its wake-up pass and then about once a day, beside
/// the guest-message threads' retention (<see cref="ConversationRetention"/>) - the same ride, the same interval.
/// </para>
/// <para>
/// In batches of <see cref="BatchSize"/>, oldest first, each its own statement, so a first sweep over years of history
/// never holds a lock on the whole table while the tablets write. Each half is a range on
/// <c>IX_ActivityEntries_Retention</c> (<c>IsMoney</c>, <c>OccurredAtUtc</c>).
/// </para>
/// </remarks>
internal sealed class ActivityRetention(YallaDbContext db, IClock clock)
{
    internal const int BatchSize = 1000;

    /// <summary>Deletes what has aged out and says how many entries went.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;

        return await PurgeAsync(isMoney: false, nowUtc.AddDays(-ActivityEntry.RetentionDays), cancellationToken)
               + await PurgeAsync(isMoney: true, nowUtc.AddDays(-ActivityEntry.MoneyRetentionDays), cancellationToken);
    }

    private async Task<int> PurgeAsync(bool isMoney, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var deleted = 0;

        while (true)
        {
            var batch = await db.ActivityEntries
                .Where(e => e.IsMoney == isMoney && e.OccurredAtUtc < cutoffUtc)
                .OrderBy(e => e.OccurredAtUtc)
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
