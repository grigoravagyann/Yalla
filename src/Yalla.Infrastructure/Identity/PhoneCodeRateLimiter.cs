using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Caps how many verification codes one phone number can be sent per hour.
/// </summary>
/// <remarks>
/// <para>
/// The middleware rate limiter on <c>request-code</c> partitions by remote address, which is the
/// right first line and the wrong only line: a caller on a phone network changes address for
/// free, and codes cost money to send once a real provider is wired in. This limiter closes the
/// other half, keyed on the number itself.
/// </para>
/// <para>
/// It is the same <c>System.Threading.RateLimiting</c> primitive the middleware uses, applied
/// inside the service rather than in the pipeline for one practical reason: the phone number is
/// in the request body, and the middleware runs long before anything has read it.
/// </para>
/// <para>
/// In-process, so a multi-instance deployment gets the limit per instance. That is honest rather
/// than ideal, and moving it to a shared store is a one-class change when there is more than one
/// instance to worry about.
/// </para>
/// </remarks>
internal sealed class PhoneCodeRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public PhoneCodeRateLimiter(IOptions<AuthOptions> options)
    {
        var permitLimit = Math.Max(1, options.Value.CodeRequestsPerPhonePerHour);

        _limiter = PartitionedRateLimiter.Create<string, string>(phoneE164 =>
            RateLimitPartition.GetFixedWindowLimiter(
                phoneE164,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                }));
    }

    /// <summary>Takes a permit for this number, or reports that the hour's allowance is spent.</summary>
    public async Task<bool> TryAcquireAsync(string phoneE164, CancellationToken cancellationToken)
    {
        // Disposing the lease does not hand the permit back - a fixed window releases on time,
        // not on release - so the using is only tidiness.
        using var lease = await _limiter.AcquireAsync(phoneE164, 1, cancellationToken);

        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
