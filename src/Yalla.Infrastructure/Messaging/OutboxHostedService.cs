using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yalla.Application.Messaging;

namespace Yalla.Infrastructure.Messaging;

/// <summary>
/// The loop that runs the dispatcher. Everything it knows is when to call it.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="PeriodicTimer"/> built from <see cref="TimeProvider"/>, so a test can advance time
/// rather than wait for it. That is the whole reason this class is separate from the dispatcher: the
/// dispatcher is a method a test can call, and this is the thing that would otherwise make a test
/// suite sleep.
/// </para>
/// <para>
/// It runs a pass immediately on start, before the first tick. That is the machine-woke-up moment,
/// and the pass begins by discarding anything too overdue to send - so waking a laptop after a night
/// shut produces a log line and a dead-letter, not a burst of pushes about yesterday.
/// </para>
/// </remarks>
internal sealed class OutboxHostedService(
    IServiceScopeFactory scopes,
    OutboxOptions options,
    TimeProvider time,
    ILogger<OutboxHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation(
                "The outbox dispatcher is switched off ({Section}:Enabled). Messages will be written "
                + "and nothing will send them.",
                OutboxOptions.SectionName);

            return;
        }

        logger.LogInformation(
            "Outbox dispatcher starting: every {PollSeconds}s, {BatchSize} at a time, giving up after "
            + "{MaxAttempts} attempts, discarding anything more than {StaleAfterMinutes} minutes overdue.",
            options.PollSeconds, options.BatchSize, options.MaxAttempts, options.StaleAfterMinutes);

        // Before the first tick: this is the wake-up pass, and it is where a night's backlog gets
        // triaged rather than fired.
        await PumpAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds), time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PumpAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down. Not a failure.
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();

            var pass = await dispatcher.RunOnceAsync(cancellationToken);

            if (pass.Sent > 0 || pass.Failed > 0 || pass.Stale > 0)
            {
                logger.LogInformation(
                    "Outbox pass: {Sent} sent, {Failed} failed, {Stale} discarded as stale.",
                    pass.Sent, pass.Failed, pass.Stale);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The loop must survive anything one pass can throw. A dispatcher that dies on a bad
            // batch stops every notification in the system, and the symptom is silence.
            logger.LogError(ex, "An outbox pass failed. The dispatcher will try again on the next tick.");
        }

        await SweepFeedAsync(cancellationToken);
        await SweepConversationsAsync(cancellationToken);
    }

    /// <summary>How often the notifications feed is swept for entries past their retention.</summary>
    private static readonly TimeSpan FeedSweepInterval = TimeSpan.FromHours(1);

    private DateTimeOffset? _lastFeedSweep;

    /// <summary>How often guest-message threads are swept for those a year quiet.</summary>
    private static readonly TimeSpan ConversationSweepInterval = TimeSpan.FromDays(1);

    private DateTimeOffset? _lastConversationSweep;

    /// <summary>
    /// Deletes feed entries older than their 90 days (K12), on the wake-up pass and then about hourly.
    /// </summary>
    /// <remarks>
    /// Here rather than in a loop of its own: the feed is written beside the messages this loop
    /// dispatches, so its retention rides on the same timer. It is not the process's only background
    /// work - <see cref="Yalla.Infrastructure.Services.PhotoSweepHostedService"/> runs the orphan photo
    /// sweep on its own interval. Its own try, so a failed dispatch pass does not stop the sweep and a
    /// failed sweep does not stop the next pass.
    /// </remarks>
    private async Task SweepFeedAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        if (_lastFeedSweep is { } last && now - last < FeedSweepInterval)
        {
            return;
        }

        _lastFeedSweep = now;

        try
        {
            await using var scope = scopes.CreateAsyncScope();

            if (scope.ServiceProvider.GetService<Yalla.Infrastructure.Services.DinerNotificationRetention>() is not { } retention)
            {
                return;
            }

            var deleted = await retention.PurgeAsync(cancellationToken);

            // And the numbers on ended blocks of deleted accounts, on the same daily pass.
            await retention.ForgetEndedBlockNumbersAsync(cancellationToken);

            if (deleted > 0)
            {
                logger.LogInformation(
                    "Deleted {Count} notifications feed entries older than {Days} days.",
                    deleted, Yalla.Domain.Identity.DinerNotification.RetentionDays);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The notifications feed sweep failed. It will try again on a later tick.");
        }
    }

    /// <summary>
    /// Deletes guest-message threads whose last message is over a year old - unless the thread is still open -
    /// on the wake-up pass and then about daily (<see cref="Yalla.Infrastructure.Services.ConversationRetention"/>).
    /// </summary>
    /// <remarks>
    /// The wake-up pass included, so a server restarted more often than once a day still sweeps. Its own try, as the
    /// feed's: a failed sweep stops neither the dispatch nor the next attempt, about an hour later.
    /// </remarks>
    private async Task SweepConversationsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        if (_lastConversationSweep is { } last && now - last < ConversationSweepInterval)
        {
            return;
        }

        _lastConversationSweep = now;

        try
        {
            await using var scope = scopes.CreateAsyncScope();

            if (scope.ServiceProvider.GetService<Yalla.Infrastructure.Services.ConversationRetention>() is not { } retention)
            {
                return;
            }

            var deleted = await retention.PurgeAsync(cancellationToken);

            if (deleted > 0)
            {
                logger.LogInformation(
                    "Deleted {Count} guest-message threads whose last message is over {Days} days old.",
                    deleted, Yalla.Domain.Messages.ConversationRules.RetentionDays);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Again in about an hour rather than a day: nothing is lost by waiting, but a day is long to stay unswept.
            _lastConversationSweep = now - ConversationSweepInterval + FeedSweepInterval;
            logger.LogError(ex, "The guest-message retention sweep failed. It will try again in about an hour.");
        }
    }
}
