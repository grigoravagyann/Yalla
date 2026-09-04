namespace Yalla.Application.Auth;

/// <summary>
/// Exchanges a refresh token for a new access token, rotating the refresh token as it goes.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the sign-in services because rotation and theft detection are one mechanism
/// shared by the two identity types that have refresh tokens, and duplicating it would mean two
/// places to get chain revocation subtly wrong.
/// </para>
/// <para>
/// Staff sessions deliberately do not come through here. They renew on inactivity rather than
/// rotating for thirty days - see <see cref="IStaffAuthService.RenewSessionAsync"/>.
/// </para>
/// </remarks>
public interface ITokenRefreshService
{
    /// <summary>Refreshes a diner session.</summary>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">
    /// The token is unknown, expired, revoked, or was already spent. A spent token means two
    /// parties hold it, so the whole chain is revoked and both must sign in again.
    /// </exception>
    Task<RefreshResult> RefreshDinerAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Refreshes an admin-panel session.</summary>
    /// <inheritdoc cref="RefreshDinerAsync"/>
    Task<RefreshResult> RefreshVenueUserAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a refresh token and every other token in its chain - the sign-out button.
    /// </summary>
    /// <remarks>Succeeds silently on an unknown token: signing out must never fail.</remarks>
    Task SignOutAsync(string refreshToken, CancellationToken cancellationToken = default);
}
