using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Messaging;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Messaging;

/// <summary>
/// Writes messages into the outbox in the caller's transaction, and reports on it.
/// </summary>
/// <remarks>
/// No <c>SaveChanges</c> anywhere in here. The message goes in with its cause or not at all, which is
/// the whole reason the table exists - and the moment this class saves on its own, that stops being
/// true and nothing would fail visibly.
/// </remarks>
internal sealed class Outbox(YallaDbContext db, IClock clock, ILogger<Outbox> logger) : IOutbox
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public void Enqueue(string type, object payload, DateTime scheduledForUtc, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(payload);

        db.OutboxMessages.Add(new OutboxMessage(
            type,
            JsonSerializer.Serialize(payload, PayloadJson),
            scheduledForUtc,
            idempotencyKey,
            clock.UtcNow));
    }

    public async Task CancelAsync(string idempotencyKeyPrefix, CancellationToken cancellationToken = default)
    {
        // Unsent only. A message that already went out cannot be recalled, and deleting its row
        // would lose the record that it did.
        var pending = await db.OutboxMessages
            .Where(m => m.IdempotencyKey.StartsWith(idempotencyKeyPrefix))
            .Where(m => m.SentAtUtc == null)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        db.OutboxMessages.RemoveRange(pending);

        logger.LogInformation(
            "Cancelled {Count} unsent outbox message(s) for {Prefix}.", pending.Count, idempotencyKeyPrefix);
    }

    public async Task<OutboxHealthView> GetHealthAsync(
        int deadLetterLimit = 50,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;
        var dayAgo = nowUtc.AddDays(-1);

        var counts = await db.OutboxMessages
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Pending = g.Count(m => m.SentAtUtc == null && m.DeadLetteredAtUtc == null),
                Due = g.Count(m => m.SentAtUtc == null && m.DeadLetteredAtUtc == null && m.ScheduledForUtc <= nowUtc),
                Failed = g.Count(m => m.SentAtUtc == null && m.DeadLetteredAtUtc == null && m.AttemptCount > 0),
                DeadLettered = g.Count(m => m.DeadLetteredAtUtc != null),
                SentLastDay = g.Count(m => m.SentAtUtc != null && m.SentAtUtc >= dayAgo),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var deadLetters = await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.DeadLetteredAtUtc != null)
            .OrderByDescending(m => m.DeadLetteredAtUtc)
            .Take(Math.Clamp(deadLetterLimit, 1, 200))
            .Select(m => new DeadLetterView(
                m.Id, m.Type, m.IdempotencyKey, m.ScheduledForUtc, m.AttemptCount, m.DeadLetteredAtUtc, m.LastError))
            .ToListAsync(cancellationToken);

        return new OutboxHealthView(
            counts?.Pending ?? 0,
            counts?.Due ?? 0,
            counts?.Failed ?? 0,
            counts?.DeadLettered ?? 0,
            counts?.SentLastDay ?? 0,
            deadLetters);
    }
}
