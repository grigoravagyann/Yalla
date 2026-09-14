using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Deletes feed entries older than <see cref="DinerNotification.RetentionDays"/> days (K12).
/// </summary>
/// <remarks>
/// Run by the outbox's background loop, about once an hour - the process's one existing sweep. One
/// statement over the <c>CreatedAtUtc</c> index. An entry that has not appeared yet is never old enough.
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
}
