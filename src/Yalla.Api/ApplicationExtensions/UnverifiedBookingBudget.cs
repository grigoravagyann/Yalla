using System.Threading.RateLimiting;
using Yalla.Application.Abstractions;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// <see cref="IUnverifiedBookingBudget"/> over the request's network: one fixed window per address, an IPv6
/// address by its /64 (<see cref="RateLimitingExtensions.NetworkBucket"/>). A singleton, so every request of
/// the process spends from the same buckets; off wherever rate limiting is off.
/// </summary>
internal sealed class UnverifiedBookingBudget : IUnverifiedBookingBudget, IDisposable
{
    private readonly IHttpContextAccessor accessor;
    private readonly PartitionedRateLimiter<string>? limiter;

    public UnverifiedBookingBudget(IHttpContextAccessor accessor, IConfiguration configuration, IHostEnvironment environment)
    {
        this.accessor = accessor;

        if (!RateLimitingExtensions.IsEnabled(configuration, environment))
        {
            return;
        }

        var section = configuration.GetSection("RateLimiting");
        var permits = section.GetValue<int?>("PublicUnverifiedBookingPermitLimit") ?? 10;
        var windowSeconds = section.GetValue<int?>("PublicUnverifiedBookingWindowSeconds") ?? 3600;

        limiter = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(
                key,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromSeconds(windowSeconds),
                    QueueLimit = 0,
                }));
    }

    public bool TrySpend()
    {
        if (limiter is null || accessor.HttpContext is not { } context)
        {
            return true;
        }

        using var lease = limiter.AttemptAcquire(RateLimitingExtensions.NetworkBucket(context));

        return lease.IsAcquired;
    }

    public void Dispose() => limiter?.Dispose();
}
