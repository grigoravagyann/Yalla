using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The lifecycle checks a stateless token cannot make about itself, with a short cache in front.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is cached is the database read, not the decision.</b> The facts - which tab, whose row,
/// what status - change only when somebody writes, and every such write says so through the
/// invalidate methods. Anything that depends on the clock is recomputed per call.
/// </para>
/// <para>
/// The window is seconds. These reads sit on the hot path - the floor endpoint and every tab call -
/// so a round trip each time would be felt; but a manager who revokes a tablet expects it dead now,
/// which is why revocation evicts rather than waiting the window out. The window only ever delays
/// noticing a change nobody announced.
/// </para>
/// </remarks>
internal sealed class TokenAuthorityCheck(
    IAuthorizationQueries queries,
    IMemoryCache cache,
    IClock clock,
    IOptions<JwtOptions> jwtOptions) : ITokenAuthorityCheck
{
    /// <summary>
    /// How long an answer is reused. Short enough that revoking a tablet takes effect while the
    /// manager is still looking at the screen.
    /// </summary>
    public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(5);

    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<ParticipantAuthority?> GetParticipantAuthorityAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        var key = ParticipantKey(tabId, participantId);

        if (!cache.TryGetValue(key, out TabParticipantAccess? access))
        {
            access = await queries.GetTabParticipantAccessAsync(tabId, participantId, cancellationToken);

            using var entry = cache.CreateEntry(key);
            entry.Value = access;
            entry.AbsoluteExpirationRelativeToNow = CacheWindow;

            // Everything about this tab drops together when the tab itself changes - staff marking
            // it closing changes what every participant on it may do, not just one.
            entry.AddExpirationToken(new CancellationChangeToken(TabEviction(tabId).Token));
        }

        // Evaluated fresh every call, because it reads the clock. Caching the decision would freeze
        // one that is meant to move on its own - the receipt grace period running out, say - and the
        // freeze would last exactly as long as nobody noticed.
        return access is null ? null : Evaluate(access);
    }

    public async Task<bool> IsDeviceActiveAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var key = DeviceKey(deviceId);

        if (cache.TryGetValue(key, out bool cached))
        {
            return cached;
        }

        var active = await queries.IsStaffDeviceActiveAsync(deviceId, cancellationToken);
        cache.Set(key, active, CacheWindow);

        return active;
    }

    public void InvalidateDevice(Guid deviceId) => cache.Remove(DeviceKey(deviceId));

    public void InvalidateParticipant(Guid tabId, Guid participantId) =>
        cache.Remove(ParticipantKey(tabId, participantId));

    public void InvalidateTab(Guid tabId)
    {
        // Cancelling the shared token drops every cached participant on this tab without needing
        // to know who they are.
        var source = TabEviction(tabId);
        cache.Remove(TabTokenKey(tabId));
        source.Cancel();
        source.Dispose();
    }

    /// <summary>
    /// The eviction handle shared by every cached participant on one tab.
    /// </summary>
    /// <remarks>
    /// Held in the cache itself because this class is scoped and the cache is not, so the handle
    /// outlives the entries that depend on it.
    /// </remarks>
    private CancellationTokenSource TabEviction(Guid tabId) =>
        cache.GetOrCreate(TabTokenKey(tabId), entry =>
        {
            entry.Priority = CacheItemPriority.NeverRemove;
            return new CancellationTokenSource();
        })!;

    private static string TabTokenKey(Guid tabId) => $"authority:tab-eviction:{tabId}";

    private static string DeviceKey(Guid deviceId) => $"authority:device:{deviceId}";

    private static string ParticipantKey(Guid tabId, Guid participantId) =>
        $"authority:participant:{tabId}:{participantId}";

    /// <summary>
    /// The rules, in one place: who may still read, who may still change, and whose token should
    /// stop working outright.
    /// </summary>
    private ParticipantAuthority Evaluate(TabParticipantAccess access)
    {
        // A removed participant is refused everything. This is what the approval flow is for: the
        // stranger who joined the wrong table and was taken off must not carry on ordering, and
        // their token alone cannot tell them apart from anyone else on the tab.
        var mayRead = TabPermissions.MayReadTab(access.ParticipantStatus);

        // Once staff mark the tab closing, nobody changes it. Somebody who paid their share and
        // left must not find a dessert added after they had gone.
        var mayMutate = mayRead && access.TabStatus == TabStatus.Open;

        TokenRevoked? revoked = null;

        if (access.TabClosedAtUtc is { } closedAtUtc)
        {
            // The receipt grace period, from Prompt 5: a diner may look at what they paid for a
            // while after the bill closed. Past that the token is simply over, and saying so beats
            // a generic auth failure the app would render as "something went wrong".
            var readableUntil = closedAtUtc.AddMinutes(_jwt.ParticipantReceiptGraceMinutes);

            if (clock.UtcNow > readableUntil)
            {
                revoked = new TokenRevoked(
                    TokenRevoked.TabClosed,
                    "This tab is closed. Scan the table's QR code to start a new one.");
            }
        }

        return new ParticipantAuthority(
            access.TabStatus,
            access.ParticipantStatus,
            mayRead && revoked is null,
            mayMutate && revoked is null,
            access.CanOrder,
            revoked);
    }
}
