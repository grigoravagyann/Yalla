using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Caps how often one diner account can be signed in to with a password.
/// </summary>
/// <remarks>
/// <para>
/// The middleware limiter on <c>login</c> partitions by remote address, which stops one machine
/// hammering the endpoint and does nothing about a thousand machines each trying ten passwords
/// against one username. This is the other axis: keyed on the identifier - username or email,
/// lowercased, so <c>Ani.K</c> and <c>ani.k</c> spend one budget - and counted whether the
/// attempt is right or wrong. It lives inside the service for the same practical reason
/// <see cref="PhoneCodeRateLimiter"/> does: the identifier is in the request body, and the
/// middleware runs long before anything has read it.
/// </para>
/// <para>
/// A fixed window rather than a lockout flag on the row, deliberately. The staff PIN locks the
/// <i>account</i> and needs a manager to clear it; a diner has no manager, and an attacker who
/// could lock any account by typing its username ten times would have a way to stop any diner
/// signing in. A window that simply passes is the right shape here - and it answers 429 rather
/// than 401, so the app says "wait" instead of "wrong password" to somebody who has been typing
/// the right one.
/// </para>
/// <para>
/// In-process, so a multi-instance deployment gets the limit per instance. Honest rather than
/// ideal, and a one-class change when there is more than one instance to worry about.
/// </para>
/// </remarks>
internal sealed class PasswordAttemptLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public PasswordAttemptLimiter(IOptions<AuthOptions> options)
    {
        var permitLimit = Math.Max(1, options.Value.PasswordAttemptsPerIdentifier);
        var window = TimeSpan.FromMinutes(Math.Max(1, options.Value.PasswordAttemptWindowMinutes));

        _limiter = PartitionedRateLimiter.Create<string, string>(identifier =>
            RateLimitPartition.GetFixedWindowLimiter(
                identifier,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0,
                }));
    }

    /// <summary>Takes a permit for this identifier, or reports that the window's allowance is spent.</summary>
    public async Task<bool> TryAcquireAsync(string identifier, CancellationToken cancellationToken)
    {
        // Disposing the lease does not hand the permit back - a fixed window releases on time,
        // not on release - so the using is only tidiness.
        using var lease = await _limiter.AcquireAsync(identifier, 1, cancellationToken);

        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
