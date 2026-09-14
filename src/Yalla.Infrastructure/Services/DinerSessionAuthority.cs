using Microsoft.Extensions.Caching.Memory;
using Yalla.Application.Auth;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The diner half of the token authority check: whether a diner access token belongs to a session
/// that is still good.
/// </summary>
/// <remarks>
/// <para>
/// Diner tokens used to skip this check altogether, on the reasoning that fifteen minutes is short
/// and revoking the refresh chain is enough. It was not enough for the one case that mattered: a
/// squatter who registered somebody else's number kept their access token after the owner proved the
/// number, and could use it to set a new password on the account the owner had just taken back.
/// </para>
/// <para>
/// So every diner request now compares the token's <c>sgen</c> claim with the row's
/// <c>SessionGeneration</c>, and refuses an inactive or deleted account. The read is cached exactly
/// like a device's - five seconds, evicted by the write that changes it - with two differences, both
/// forced by what a generation is:
/// </para>
/// <list type="bullet">
/// <item><b>The window is measured on <see cref="Application.Abstractions.IClock"/></b>, not only by
/// the cache's own timer, so a test that advances the clock sees the cache lapse exactly as a
/// deployment sees it lapse in real time.</item>
/// <item><b>A token newer than the cached generation bypasses the cache.</b> Generations only go up,
/// so that token was minted after a bump this process has not seen yet - another instance handled
/// the change, or the eviction raced a read. Refusing it would sign the rightful owner out for five
/// seconds in exactly the moment they signed in.</item>
/// </list>
/// </remarks>
internal sealed partial class TokenAuthorityCheck
{
    public async Task<TokenRevoked?> CheckDinerSessionAsync(
        Guid dinerUserId,
        int tokenSessionGeneration,
        CancellationToken cancellationToken = default)
    {
        var key = DinerKey(dinerUserId);
        var nowUtc = clock.UtcNow;

        var fresh = cache.TryGetValue(key, out CachedDinerSession? cached)
                    && cached is not null
                    && nowUtc - cached.ReadAtUtc < CacheWindow
                    && (cached.State is null || tokenSessionGeneration <= cached.State.SessionGeneration);

        if (!fresh)
        {
            var state = await queries.GetDinerSessionStateAsync(dinerUserId, cancellationToken);

            cached = new CachedDinerSession(state, nowUtc);
            cache.Set(key, cached, CacheWindow);
        }

        return EvaluateDinerSession(cached!.State, tokenSessionGeneration);
    }

    public void InvalidateDiner(Guid dinerUserId) => cache.Remove(DinerKey(dinerUserId));

    /// <summary>
    /// The rule, with nothing cached in it: a live session is an active, undeleted account on the
    /// generation the token carries.
    /// </summary>
    /// <param name="state">The account as stored, or null when there is none.</param>
    /// <param name="tokenSessionGeneration">The token's <c>sgen</c>.</param>
    /// <returns>Null for a good token; the refusal otherwise.</returns>
    internal static TokenRevoked? EvaluateDinerSession(DinerSessionState? state, int tokenSessionGeneration) =>
        state is { IsActive: true, IsDeleted: false } live && live.SessionGeneration == tokenSessionGeneration
            ? null
            : new TokenRevoked(
                TokenRevoked.SessionRevoked,
                "This session has ended. Sign in again.");

    private static string DinerKey(Guid dinerUserId) => $"authority:diner:{dinerUserId}";

    /// <summary>One read of an account, and the application-clock instant it was made.</summary>
    private sealed record CachedDinerSession(DinerSessionState? State, DateTime ReadAtUtc);
}
