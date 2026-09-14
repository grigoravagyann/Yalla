using Yalla.Application.Media;

namespace Yalla.Application.Diners;

/// <summary>
/// A signed-in diner reading and changing their own account.
/// </summary>
/// <remarks>
/// Every method acts on the caller - the diner the bearer token names - and on nobody else. There is
/// no id parameter to get wrong, which is the point: a profile endpoint that took one would be a
/// profile endpoint that could be pointed at somebody else's row.
/// </remarks>
public interface IDinerProfileService
{
    /// <summary>The caller's account.</summary>
    Task<DinerProfileView> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the fields that were sent, under the same rules and with the same refusals as
    /// registration.
    /// </summary>
    /// <exception cref="Domain.Identity.DinerIdentifierTakenException">The new username or email is somebody else's.</exception>
    Task<DinerProfileView> UpdateAsync(UpdateDinerProfileCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a first password, or replaces the current one, ends every access token the account holds -
    /// the caller's included - and revokes the refresh tokens of every sign-in but the caller's.
    /// </summary>
    /// <remarks>
    /// An account the code flow created has no password and sets one without proving anything
    /// further - the bearer token is the proof, so that token's session generation must still be
    /// the account's, read from the row rather than trusted from a cache. An account that has one
    /// must send it.
    /// </remarks>
    /// <param name="currentPassword">Required when the account has a password.</param>
    /// <param name="newPassword">The new one, under registration's rule.</param>
    /// <param name="tokenSessionGeneration">The <c>sgen</c> claim of the token the request carried.</param>
    /// <param name="tokenRefreshChainId">
    /// The <c>rch</c> claim of the token the request carried: the one sign-in whose refresh tokens are
    /// kept. Null (a token minted before the claim) keeps none, so that device signs in again too.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">
    /// <c>invalid-credentials</c>: the current password was wrong, or was needed and not sent.
    /// <c>session-revoked</c>: a first password from a token whose session has ended.
    /// </exception>
    Task SetPasswordAsync(
        string? currentPassword,
        string newPassword,
        int tokenSessionGeneration,
        Guid? tokenRefreshChainId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the caller's account, after proving it is really them: the password when the account
    /// has one, otherwise a one-time code sent to its number.
    /// </summary>
    /// <remarks>
    /// One transaction: the account becomes a tombstone with nothing identifying left on it, every
    /// session ends, reviews, devices and pictures are deleted, and bookings and tab places are kept
    /// for the venue with the link to the person removed. See <c>docs/auth.md</c>.
    /// </remarks>
    /// <exception cref="Domain.FieldValidationException">The proof the account needs was not sent.</exception>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">
    /// <c>invalid-credentials</c> for a wrong password or code; <c>too-many-attempts</c> once the
    /// attempts on the account, or on the code, are spent.
    /// </exception>
    Task DeleteAccountAsync(string? password, string? code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an image as the caller's profile picture, replacing whatever was there.
    /// </summary>
    /// <remarks>
    /// The same pipeline as a branch photo - sniffed, stripped of metadata, three WebP variants -
    /// and the same rule on identical bytes: the same picture uploaded twice by the same person is
    /// one row. The picture it replaces is left for the orphan sweep.
    /// </remarks>
    /// <exception cref="Domain.Media.UnsupportedImageException">Not a JPEG, PNG or WebP, or too large.</exception>
    Task<PhotoView> SetPhotoAsync(Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the profile picture and deletes it - row and files - at once, so its links stop
    /// answering. Succeeds when there was none.
    /// </summary>
    Task RemovePhotoAsync(CancellationToken cancellationToken = default);
}
