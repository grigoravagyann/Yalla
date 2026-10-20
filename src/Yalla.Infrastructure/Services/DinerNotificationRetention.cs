using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Deletes feed entries older than <see cref="DinerNotification.RetentionDays"/> days (K12).
/// </summary>
/// <remarks>
/// Run by the outbox's background loop (<c>OutboxHostedService</c>), about once an hour, beside the
/// messages that loop dispatches. The orphan photo sweep is a separate hosted service with its own
/// interval. One statement over the <c>CreatedAtUtc</c> index. An entry that has not appeared yet is
/// never old enough.
/// </remarks>
internal sealed class DinerNotificationRetention(YallaDbContext db, IClock clock)
{
    /// <summary>Deletes what has aged out and says how many.</summary>
    public Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var cutoffUtc = clock.UtcNow.AddDays(-DinerNotification.RetentionDays);

        return db.DinerNotifications
            .Where(n => n.CreatedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Takes the number off ended no-show blocks of deleted accounts (review S7). A block in force when its account
    /// was deleted keeps the number so re-registering does not escape it; once it ends, the number is nobody's
    /// business any more.
    /// </summary>
    public Task<int> ForgetEndedBlockNumbersAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;

        return db.BookingBlocks
            .Where(b => b.PhoneE164 != null
                        && (b.LiftedAtUtc != null || b.BlockedUntilUtc <= nowUtc)
                        && db.DinerUsers.Any(d => d.Id == b.DinerUserId && d.DeletedAtUtc != null))
            .ExecuteUpdateAsync(set => set.SetProperty(b => b.PhoneE164, (string?)null), cancellationToken);
    }
}
