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
    }
}
