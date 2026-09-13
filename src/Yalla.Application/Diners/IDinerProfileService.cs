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
    /// Sets a first password, or replaces the current one.
    /// </summary>
    /// <remarks>
    /// An account the code flow created has no password and sets one without proving anything
    /// further - the bearer token is the proof. An account that has one must send it.
    /// </remarks>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The current password was wrong, or was needed and not sent.</exception>
    Task SetPasswordAsync(string? currentPassword, string newPassword, CancellationToken cancellationToken = default);

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

    /// <summary>Clears the profile picture. Succeeds when there was none.</summary>
    Task RemovePhotoAsync(CancellationToken cancellationToken = default);
}
