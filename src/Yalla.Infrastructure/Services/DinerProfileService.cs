using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Application.Media;
using Yalla.Domain.Identity;
using Yalla.Domain.Media;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A diner's own account: reading it, editing it, setting a password, and the profile picture.
/// </summary>
/// <remarks>
/// <para>
/// Everything here acts on <see cref="ICurrentActor.DinerUserId"/> and nothing else. The route
/// is <c>/api/diner/me</c> with no id in it, and this is the other half of that: there is no
/// parameter through which one diner could reach another's row.
/// </para>
/// <para>
/// The username and email rules, and the 409s for a taken one, are exactly registration's -
/// the same domain functions and the same exception - so the profile screen and the sign-up form
/// cannot disagree about what is allowed.
/// </para>
/// </remarks>
internal sealed class DinerProfileService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPhotoStorage storage,
    SecretHasher hasher,
    ILogger<DinerProfileService> logger) : IDinerProfileService
{
    public async Task<DinerProfileView> GetAsync(CancellationToken cancellationToken = default) =>
        ToView(await RequireDinerAsync(cancellationToken));

    public async Task<DinerProfileView> UpdateAsync(
        UpdateDinerProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var diner = await RequireDinerAsync(cancellationToken);

        // Normalised first and checked for a clash before anything is written, so a refusal
        // leaves the row exactly as it was - and so the two 409s below come from a query with a
        // readable answer rather than only from the index violation, which is kept as the backstop
        // for the race the query cannot close.
        var username = command.Username is null
            ? null
            : DinerAccountRules.NormaliseUsername(command.Username, "username");

        var email = command.Email is null
            ? null
            : DinerAccountRules.NormaliseEmail(command.Email, "email");

        if (username is not null && username != diner.Username
            && await db.DinerUsers.AnyAsync(d => d.Username == username && d.Id != diner.Id, cancellationToken))
        {
            throw DinerIdentifierTakenException.Username();
        }

        if (email is not null && email != diner.Email
            && await db.DinerUsers.AnyAsync(d => d.Email == email && d.Id != diner.Id, cancellationToken))
        {
            throw DinerIdentifierTakenException.Email();
        }

        if (command.DisplayName is not null)
        {
            diner.Rename(command.DisplayName);
        }

        if (username is not null)
        {
            diner.SetUsername(username);
        }

        if (email is not null)
        {
            diner.SetEmail(email);
        }

        await SaveGuardingIdentifiersAsync(cancellationToken);

        logger.LogInformation("Diner {DinerUserId} updated their profile.", diner.Id);

        return ToView(diner);
    }

    public async Task SetPasswordAsync(
        string? currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var diner = await RequireDinerAsync(cancellationToken);

        if (diner.HasPassword)
        {
            // Proving the current one is what stops a phone left unlocked on a table from
            // becoming a changed password. A first password needs no such proof: the bearer
            // token is the proof, and there is nothing to compare against anyway.
            var (matches, _) = hasher.Verify(diner.PasswordHash!, currentPassword);

            if (!matches)
            {
                logger.LogWarning("Diner {DinerUserId} gave a wrong current password.", diner.Id);

                throw new AuthenticationFailedException(
                    "invalid-credentials", "The current password is not right.");
            }
        }

        var checkedPassword = DinerAccountRules.CheckPassword(newPassword, diner.Username, diner.Email, "newPassword");

        diner.SetPassword(hasher.Hash(checkedPassword));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Diner {DinerUserId} set a password.", diner.Id);
    }

    public async Task<PhotoView> SetPhotoAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var diner = await RequireDinerAsync(cancellationToken);

        // The same pipeline as a branch photo, under the diner's own prefix. Validation, EXIF
        // stripping and the three variants all happen in here; nothing stores the bytes as they
        // arrived.
        var stored = await storage.SaveAsync(
            PhotoRules.OwnerKeyForDiner(diner.Id), content, contentType, cancellationToken);

        // Identical bytes for this person reuse the row, for the same reason a branch's do: the
        // files already sit under this hash, and two ids over one set of files means deleting
        // either breaks the other.
        var photo = await db.Photos.FirstOrDefaultAsync(
            p => p.DinerUserId == diner.Id && p.ContentHash == stored.ContentHash, cancellationToken);

        if (photo is null)
        {
            photo = Photo.ForDiner(
                diner.Id,
                stored.ContentHash,
                stored.ThumbnailPath,
                stored.CardPath,
                stored.FullPath,
                stored.Width,
                stored.Height,
                stored.BytesStored,
                clock.UtcNow);

            db.Photos.Add(photo);
        }

        // The previous picture is not deleted here. Nothing references it once this saves, so the
        // orphan sweep takes it after the grace period - the same path every abandoned branch
        // upload follows, and one that cannot leave a row pointing at missing files.
        diner.SetPhoto(photo.Id);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Diner {DinerUserId} set profile photo {PhotoId} ({Width}x{Height}, {Bytes} bytes).",
            diner.Id, photo.Id, photo.Width, photo.Height, photo.BytesStored);

        return PhotoView.From(photo);
    }

    public async Task RemovePhotoAsync(CancellationToken cancellationToken = default)
    {
        var diner = await RequireDinerAsync(cancellationToken);

        if (diner.PhotoId is null)
        {
            return;
        }

        diner.SetPhoto(null);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Diner {DinerUserId} removed their profile photo.", diner.Id);
    }

    /// <summary>
    /// The caller's row, or a refusal. The policy on the route already requires a diner token;
    /// this is the check that the account behind it still exists and is still active.
    /// </summary>
    private async Task<DinerUser> RequireDinerAsync(CancellationToken cancellationToken)
    {
        var dinerUserId = actor.DinerUserId
                          ?? throw new UnauthorizedAccessException("This needs a signed-in diner.");

        var diner = await db.DinerUsers
            .Include(d => d.Photo)
            .FirstOrDefaultAsync(d => d.Id == dinerUserId, cancellationToken);

        if (diner is null || !diner.IsActive)
        {
            throw new UnauthorizedAccessException("This account is not active.");
        }

        return diner;
    }

    /// <summary>
    /// Saves, turning a lost race for a username or an email into the same 409 the check above
    /// gives - rather than the 500 a bare unique violation would be.
    /// </summary>
    private async Task SaveGuardingIdentifiersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.DinerUserUsername))
        {
            throw DinerIdentifierTakenException.Username();
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.DinerUserEmail))
        {
            throw DinerIdentifierTakenException.Email();
        }
    }

    private static DinerProfileView ToView(DinerUser diner) =>
        new(
            diner.Id,
            diner.Username,
            diner.Email,
            diner.PhoneE164,
            diner.IsPhoneVerified,
            diner.DisplayName,
            diner.LocaleCode,
            diner.HasPassword,
            diner.Photo is { } photo ? PhotoView.From(photo) : null);
}
