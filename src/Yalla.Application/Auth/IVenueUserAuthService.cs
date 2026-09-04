namespace Yalla.Application.Auth;

/// <summary>
/// Identity type 4: a venue owner or manager, with an email address and a password.
/// </summary>
/// <remarks>
/// The conventional one, and conventional is right here: this is a desktop browser session that
/// holds prices, refunds and staff accounts. It is used from a chair, not from a tray, so the
/// cost of typing an email is nil and the value of a familiar recovery story is high.
/// </remarks>
public interface IVenueUserAuthService
{
    /// <summary>Signs in with email and password.</summary>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">
    /// Wrong password, unknown address, or a deactivated account - all reported identically, so
    /// this endpoint cannot be used to discover which addresses have accounts.
    /// </exception>
    Task<VenueUserSignInResult> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a password reset and hands the link to <see cref="Abstractions.IPasswordResetSender"/>.
    /// </summary>
    /// <remarks>
    /// Returns nothing and never fails on an unknown address, for the same reason
    /// <c>request-code</c> answers identically to everyone.
    /// </remarks>
    Task RequestPasswordResetAsync(
        string email,
        string localeCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a password reset and revokes every refresh token the account holds.
    /// </summary>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The link is unknown, spent or expired.</exception>
    /// <exception cref="ArgumentException">The new password is shorter than the configured minimum.</exception>
    Task ResetPasswordAsync(
        string resetToken,
        string newPassword,
        CancellationToken cancellationToken = default);
}
