using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Messaging;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Messaging;

/// <summary>
/// Takes due messages, sends them, and decides what to do when sending fails.
/// </summary>
/// <remarks>
/// <para>
/// Knows nothing about reminders or order-ready pushes. It knows about leases, backoff and
/// dead-lettering; what a message <i>is</i> lives in its handler. Adding a message type is a handler
/// and a constant, and this class does not change.
/// </para>
/// <para>
/// Driven by <see cref="RunOnceAsync"/> rather than by its own timer, so a test can advance a fake
/// clock and pump it deliberately instead of waiting. The hosted service is a loop around this and
/// nothing else.
/// </para>
/// </remarks>
internal sealed class OutboxDispatcher(
    YallaDbContext db,
    IClock clock,
    OutboxOptions options,
    IEnumerable<IOutboxHandler> handlers,
    ILogger<OutboxDispatcher> logger)
{
    /// <summary>Names this dispatcher in the lease, so a stuck message says which log to read.</summary>
    private static readonly string Owner =
        $"{Environment.MachineName}/{Environment.ProcessId}";

    /// <summary>How many messages this pass sent, failed and discarded.</summary>
    /// <param name="Sent">Delivered.</param>
    /// <param name="Failed">Threw, and will be retried or dead-lettered.</param>
    /// <param name="Stale">Too overdue to be worth sending - see <see cref="RunOnceAsync"/>.</param>
    public readonly record struct DispatchPass(int Sent, int Failed, int Stale);

    /// <summary>
    /// One pass: claim what is due, send it, record what happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The stale rule runs first.</b> A laptop sleeps, so due messages pile up and would all fire
    /// at once on wake - including a reminder for a booking that happened yesterday. Anything overdue
    /// by more than <c>StaleAfterMinutes</c> is dead-lettered with a reason and logged, never sent:
    /// telling a diner to come to dinner they have already eaten is worse than telling them nothing.
    /// </para>
    /// <para>
    /// Each message is claimed in its own transaction with <c>UPDLOCK, READPAST</c>, which is what
    /// makes two dispatchers safe: the second skips a row the first is holding rather than blocking
    /// on it, so they share the batch instead of serialising behind each other.
    /// </para>
    /// </remarks>
    public async Task<DispatchPass> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var stale = await DiscardStaleAsync(cancellationToken);

        var sent = 0;
        var failed = 0;

        for (var i = 0; i < options.BatchSize; i++)
        {
            var message = await ClaimNextAsync(cancellationToken);

            if (message is null)
            {
                break;
            }

            if (await TrySendAsync(message, cancellationToken))
            {
                sent++;
            }
            else
            {
                failed++;
            }
        }

        return new DispatchPass(sent, failed, stale);
    }

    /// <summary>
    /// Gives up on messages that are too old to be worth sending.
    /// </summary>
    /// <remarks>
    /// Deliberately not silent. Each one is logged with how late it was, because a machine that slept
    /// through a service is worth knowing about and the discarded reminders are the evidence.
    /// </remarks>
    private async Task<int> DiscardStaleAsync(CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;
        var cutoff = nowUtc.AddMinutes(-options.StaleAfterMinutes);

        var stale = await db.OutboxMessages
            .Where(m => m.SentAtUtc == null && m.DeadLetteredAtUtc == null && m.ScheduledForUtc < cutoff)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        foreach (var message in stale)
        {
            var lateBy = nowUtc - message.ScheduledForUtc;

            message.DeadLetter(
                $"Overdue by {lateBy.TotalMinutes:F0} minutes, past the {options.StaleAfterMinutes}-minute "
                + "staleness threshold. Not sent: the moment it was for has passed.",
                nowUtc);

            logger.LogWarning(
                "Outbox {MessageId} ({Type}, {Key}) was due {LateMinutes:F0} minutes ago and was discarded "
                + "rather than sent. This is the machine-was-asleep case.",
                message.Id, message.Type, message.IdempotencyKey, lateBy.TotalMinutes);
        }

        await db.SaveChangesAsync(cancellationToken);

        return stale.Count;
    }

    /// <summary>
    /// Claims one due message, or returns null when there is nothing to do.
    /// </summary>
    /// <remarks>
    /// <c>UPDLOCK, READPAST</c> in its own transaction. <c>UPDLOCK</c> holds the row for the length of
    /// the claim; <c>READPAST</c> is what makes a second dispatcher skip it instead of waiting, so two
    /// of them divide the work rather than taking turns. The lease is then written and committed
    /// before anything is sent, so a process that dies mid-send leaves a row that unlocks itself when
    /// the lease expires.
    /// </remarks>
    private async Task<OutboxMessage?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        await using var transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var candidate = await db.OutboxMessages
            .FromSql(
                $@"SELECT TOP (1) * FROM [OutboxMessages] WITH (UPDLOCK, READPAST)
                   WHERE [SentAtUtc] IS NULL
                     AND [DeadLetteredAtUtc] IS NULL
                     AND [ScheduledForUtc] <= {nowUtc}
                     AND ([LockedUntilUtc] IS NULL OR [LockedUntilUtc] <= {nowUtc})
                   ORDER BY [ScheduledForUtc]")
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        candidate.Lease(Owner, nowUtc.AddSeconds(options.LeaseSeconds));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return candidate;
    }

    private async Task<bool> TrySendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var handler = handlers.FirstOrDefault(h =>
            string.Equals(h.MessageType, message.Type, StringComparison.Ordinal));

        if (handler is null)
        {
            // A type nobody handles will never succeed, so retrying it is pointless. Dead-letter it
            // now with a message that says exactly what is missing.
            message.DeadLetter($"No handler is registered for message type '{message.Type}'.", clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogError(
                "Outbox {MessageId} has type {Type}, which no handler claims. Dead-lettered.",
                message.Id, message.Type);

            return false;
        }

        try
        {
            await handler.HandleAsync(message.PayloadJson, cancellationToken);

            message.MarkSent(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Outbox {MessageId} ({Type}) sent on attempt {Attempt}.",
                message.Id, message.Type, message.AttemptCount + 1);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message.RecordFailure(
                ex.Message,
                clock.UtcNow,
                options.MaxAttempts,
                TimeSpan.FromSeconds(options.BaseBackoffSeconds),
                TimeSpan.FromSeconds(options.MaxBackoffSeconds));

            await db.SaveChangesAsync(cancellationToken);

            if (message.IsDeadLettered)
            {
                logger.LogError(
                    ex,
                    "Outbox {MessageId} ({Type}) failed {Attempts} times and was dead-lettered.",
                    message.Id, message.Type, message.AttemptCount);
            }
            else
            {
                logger.LogWarning(
                    ex,
                    "Outbox {MessageId} ({Type}) failed on attempt {Attempt}; next try at {NextUtc}.",
                    message.Id, message.Type, message.AttemptCount, message.ScheduledForUtc);
            }

            return false;
        }
    }
}
