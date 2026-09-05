using Yalla.Domain.Enums;

namespace Yalla.Application.Auth;

/// <summary>
/// Why a token that is signed, unexpired and well-formed is nonetheless no longer good.
/// </summary>
/// <param name="Code">
/// A stable slug the clients render: <c>tab-closed</c>, <c>device-revoked</c>. Distinct codes,
/// because "this tab is closed" and "this tablet was revoked" send the holder to different places.
/// </param>
/// <param name="Message">A sentence written to be shown.</param>
public sealed record TokenRevoked(string Code, string Message)
{
    /// <summary>The tab this token is for has closed, and the receipt grace period has run out.</summary>
    public const string TabClosed = "tab-closed";

    /// <summary>The tablet this token came from was revoked.</summary>
    public const string DeviceRevoked = "device-revoked";
}

/// <summary>
/// What a tab participant's token may still do, decided from the tab as it stands now.
/// </summary>
/// <param name="TabStatus">1 Open, 2 Closing, 3 Closed, 4 Abandoned.</param>
/// <param name="ParticipantStatus">1 PendingApproval, 2 Approved, 3 Removed.</param>
/// <param name="MayRead">Whether the holder may still see the tab at all.</param>
/// <param name="MayMutate">
/// Whether they may still change it. False once staff mark the tab closing: someone who has paid
/// their share and left must not find a dessert added afterwards.
/// </param>
/// <param name="CanOrder">The stored flag: whether the host allows them to add items.</param>
/// <param name="Revoked">Set when the token should be refused outright, with the reason.</param>
public sealed record ParticipantAuthority(
    TabStatus TabStatus,
    ParticipantStatus ParticipantStatus,
    bool MayRead,
    bool MayMutate,
    bool CanOrder,
    TokenRevoked? Revoked);

/// <summary>
/// The lifecycle questions a stateless token cannot answer about itself.
/// </summary>
/// <remarks>
/// <para>
/// Three of the authentication rules are about <i>now</i> and a JWT is a statement about the past.
/// A participant token's lifetime is supposed to track its tab, but the close time is unknown when
/// the token is minted. A participant removed from a tab keeps their token, and the approval flow
/// exists precisely so the stranger from the next table cannot order on your bill. A tablet left in
/// a taxi has to stop working from the admin panel immediately, not when its year-long token
/// expires.
/// </para>
/// <para>
/// So the checks live here, consulted by the authorisation pipeline rather than by handlers - a
/// check inside a handler is one the next handler forgets, and the failure is silent. Answers are
/// cached for seconds, not minutes: long enough that the floor endpoint is not a database round
/// trip per call, short enough that revoking a tablet still takes effect while the manager is
/// looking at the screen.
/// </para>
/// </remarks>
public interface ITokenAuthorityCheck
{
    /// <summary>
    /// What this participant may still do. Null when the participant is not on that tab at all,
    /// which is the case a swapped or stolen token hits.
    /// </summary>
    Task<ParticipantAuthority?> GetParticipantAuthorityAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default);

    /// <summary>Whether an enrolled tablet is still live. Null-safe: an unknown device is not.</summary>
    Task<bool> IsDeviceActiveAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets what was cached about a tablet, so a revocation takes effect on the next request
    /// rather than when the cache happens to lapse.
    /// </summary>
    /// <remarks>
    /// The cache exists so the floor endpoint is not a database round trip per call. It must never
    /// buy that at the cost of the guarantee it sits in front of: a tablet left in a taxi stops
    /// working when the manager taps revoke, not a few seconds later. So every write that changes
    /// one of these answers says so, and the window only ever covers reads.
    /// </remarks>
    void InvalidateDevice(Guid deviceId);

    /// <inheritdoc cref="InvalidateDevice"/>
    void InvalidateParticipant(Guid tabId, Guid participantId);

    /// <summary>
    /// Forgets everything cached about one tab. Staff marking it closing changes what every
    /// participant on it may do, not just one of them.
    /// </summary>
    void InvalidateTab(Guid tabId);
}
