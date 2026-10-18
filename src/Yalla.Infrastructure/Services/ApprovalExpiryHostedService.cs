using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yalla.Domain.Occupancy;

namespace Yalla.Infrastructure.Services;

/// <summary>How often the short-notice approval sweep runs: <c>ApprovalExpiry:SweepIntervalSeconds</c>.</summary>
internal sealed class ApprovalExpiryOptions
{
    public const string SectionName = "ApprovalExpiry";

    /// <summary>The default: every twenty seconds, so a request is declined well within a minute of its deadline.</summary>
    public const int DefaultIntervalSeconds = 20;

    /// <summary>
    /// Seconds between passes. Zero switches the sweep off - for a test host sharing a database, never
    /// for a venue: with it off, a short-notice request nobody answers waits for ever.
    /// </summary>
    public int SweepIntervalSeconds { get; set; } = DefaultIntervalSeconds;
}

/// <summary>
/// Runs <see cref="ApprovalExpirySweep.RunOnceAsync"/> every <see cref="ApprovalExpiryOptions.SweepIntervalSeconds"/>.
/// </summary>
/// <remarks>
/// <para>
/// The deadline is a promise to the diner - an answer within fifteen minutes - so this is a loop of
/// its own rather than a message in the outbox, which discards anything more than an hour overdue
/// (see <see cref="ApprovalExpirySweep"/>). A pass runs as soon as the host starts, so a backlog left
/// by a machine that slept is cleared first, then one every interval.
/// </para>
/// <para>
/// Each pass gets its own scope - the sweep holds a <c>DbContext</c> - and a failed pass is logged
/// and waited out, never allowed to end the loop. The timer is built from <see cref="TimeProvider"/>,
/// like the outbox loop's and the photo sweep's, so a test advances time instead of waiting.
/// </para>
/// </remarks>
internal sealed class ApprovalExpiryHostedService(
    IServiceScopeFactory scopes,
    ApprovalExpiryOptions options,
    TimeProvider time,
    ILogger<ApprovalExpiryHostedService> logger) : BackgroundService
{
    private PeriodicTimer? _timer;

    /// <summary>
    /// Says what the sweep will do and builds the timer, before the loop starts - so the interval is
    /// counted from the moment the host reports started, as the photo sweep's is.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.SweepIntervalSeconds <= 0)
        {
            logger.LogWarning(
                "The short-notice approval sweep is switched off ({Section}:SweepIntervalSeconds is {Seconds}): "
                + "a booking nobody at the venue answers will wait for an answer for ever.",
                ApprovalExpiryOptions.SectionName, options.SweepIntervalSeconds);
        }
        else
        {
            logger.LogInformation(
                "Short-notice bookings nobody answers within {Minutes} minutes are declined; checked every {Seconds} seconds.",
                ShortNoticeRule.AnswerWindowMinutes, options.SweepIntervalSeconds);

            _timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepIntervalSeconds), time);
        }

        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_timer is not { } timer)
        {
            return;
        }

        try
        {
            await SweepOnceAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down. Not a failure.
        }
    }

    /// <summary>One pass, in its own scope. Returns how many bookings were declined; a failure is logged and counts as none.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            return await scope.ServiceProvider.GetRequiredService<ApprovalExpirySweep>().RunOnceAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "The short-notice approval sweep failed. It will try again in {Seconds} seconds.",
                options.SweepIntervalSeconds);

            return 0;
        }
    }
}
