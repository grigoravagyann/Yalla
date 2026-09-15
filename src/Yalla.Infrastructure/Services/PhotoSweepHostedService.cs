using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yalla.Application.Media;
using Yalla.Infrastructure.Media;

namespace Yalla.Infrastructure.Services;

/// <summary>How often the orphan photo sweep runs: <c>PhotoStorage:SweepIntervalMinutes</c> (K10).</summary>
internal sealed class PhotoSweepOptions
{
    /// <summary>The same section as the storage root: both are about where photos live and for how long.</summary>
    public const string SectionName = PhotoStorageOptions.SectionName;

    /// <summary>The default: once an hour.</summary>
    public const int DefaultIntervalMinutes = 60;

    /// <summary>Minutes between passes. Zero switches the sweep off.</summary>
    public int SweepIntervalMinutes { get; set; } = DefaultIntervalMinutes;
}

/// <summary>
/// Runs <see cref="IPhotoService.SweepOrphansAsync"/> every <see cref="PhotoSweepOptions.SweepIntervalMinutes"/>.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed nothing called the sweep outside a test. A replaced profile picture, a cover
/// swapped for another, an upload a manager abandoned: every one stayed on disk and kept answering at
/// its old URL for good, while the route descriptions promised a day.
/// </para>
/// <para>
/// Each pass gets its own scope - the sweep holds a <c>DbContext</c> - and a failed pass is logged
/// and waited out, never allowed to end the loop. The first pass is one interval after start rather
/// than at start: what the sweep takes is at least a day old, so an hour either way changes nothing,
/// and a process restarted during a deploy does not delete anything in its first seconds.
/// </para>
/// <para>
/// The timer is built from <see cref="TimeProvider"/>, like the outbox loop's, so a test advances
/// time instead of waiting an hour.
/// </para>
/// </remarks>
internal sealed class PhotoSweepHostedService(
    IServiceScopeFactory scopes,
    PhotoSweepOptions options,
    PhotoStorageOptions storage,
    TimeProvider time,
    ILogger<PhotoSweepHostedService> logger) : BackgroundService
{
    private PeriodicTimer? _timer;

    /// <summary>
    /// Says where the photos are and builds the timer, before the loop starts.
    /// </summary>
    /// <remarks>
    /// Here rather than in <see cref="ExecuteAsync"/>, which the host may start on another thread: the
    /// timer then exists, and the interval is counted, from the moment the host reports started - and a
    /// test that advances time straight after start cannot advance it past a timer not yet built.
    /// </remarks>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.SweepIntervalMinutes <= 0)
        {
            logger.LogInformation(
                "Photo storage root is {Root}. The orphan photo sweep is switched off "
                + "({Section}:SweepIntervalMinutes is {Minutes}): photos nothing uses are kept on disk.",
                storage.RootPath, PhotoSweepOptions.SectionName, options.SweepIntervalMinutes);
        }
        else
        {
            logger.LogInformation(
                "Photo storage root is {Root}. Photos nothing uses are swept every {Minutes} minutes once "
                + "they are more than {Hours} hours old.",
                storage.RootPath, options.SweepIntervalMinutes, PhotoService.OrphanGrace.TotalHours);

            _timer = new PeriodicTimer(TimeSpan.FromMinutes(options.SweepIntervalMinutes), time);
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

    /// <summary>One pass, in its own scope. Returns how many photos went; a failure is logged and counts as none.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            return await scope.ServiceProvider.GetRequiredService<IPhotoService>().SweepOrphansAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The orphan photo sweep failed. It will try again in {Minutes} minutes.", options.SweepIntervalMinutes);

            return 0;
        }
    }
}
