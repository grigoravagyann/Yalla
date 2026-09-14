using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Diners;
using Yalla.Application.Media;
using Yalla.Domain;
using Yalla.Domain.Identity;
using Yalla.Domain.Media;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A diner's own account: reading it, editing it, setting a password, the profile picture, and
/// deleting it.
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
    IPhotoService photos,
    SecretHasher hasher,
    RefreshTokenStore refreshTokens,
    PasswordAttemptLimiter attemptLimiter,
    ITokenAuthorityCheck authority,
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
        int tokenSessionGeneration,
        CancellationToken cancellationToken = default)
    {
        var diner = await RequireDinerAsync(cancellationToken);

        if (diner.HasPassword)
        {
            // Proving the current one is what stops a phone left unlocked on a table from
            // becoming a changed password.
            var (matches, _) = hasher.Verify(diner.PasswordHash!, currentPassword);

            if (!matches)
            {
                logger.LogWarning("Diner {DinerUserId} gave a wrong current password.", diner.Id);

                throw new AuthenticationFailedException(
                    "invalid-credentials", "The current password is not right.");
            }
        }
        else if (tokenSessionGeneration != diner.SessionGeneration)
        {
            // A first password needs no current one - the bearer token is the proof - so the token
            // has to be one whose session is still live, read from the row and not from the
            // five-second cache the pipeline used. This is the squatter's move the generation exists
            // to stop: their number was proved by its owner, their password cleared, and their
            // still-unexpired token was about to set a new one on the owner's account.
            logger.LogWarning(
                "Diner {DinerUserId}: a first password was refused from a token whose session has ended.", diner.Id);

            throw SessionRevoked();
        }

        var checkedPassword = DinerAccountRules.CheckPassword(newPassword, diner.Username, diner.Email, "newPassword");

        // Bumps the session generation: every access token ends, this one included. Refresh
        // tokens are left alone, so the app that made the change refreshes and carries on.
        diner.SetPassword(hasher.Hash(checkedPassword));
        await db.SaveChangesAsync(cancellationToken);

        authority.InvalidateDiner(diner.Id);

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

        if (diner.PhotoId is not { } photoId)
        {
            return;
        }

        // Unlinked and saved first, then deleted. Removing a picture of yourself means it stops
        // being served now, not after a day of the sweep's grace; and if the delete fails after the
        // unlink, the picture is simply an orphan the sweep still takes.
        diner.SetPhoto(null);
        await db.SaveChangesAsync(cancellationToken);

        await photos.DeleteAsync(photoId, cancellationToken);

        logger.LogInformation("Diner {DinerUserId} removed and deleted their profile photo.", diner.Id);
    }

    public async Task DeleteAccountAsync(
        string? password,
        string? code,
        CancellationToken cancellationToken = default)
    {
        var diner = await RequireDinerAsync(cancellationToken);

        // Which proof is needed is decided by the row, and asked for by name before any budget is
        // spent: a form that forgot the field is not a guess.
        if (diner.HasPassword && string.IsNullOrWhiteSpace(password))
        {
            throw new FieldValidationException(new FieldViolation(
                "password", "Enter the account's password to delete it.", FieldBounds.Required));
        }

        if (!diner.HasPassword && string.IsNullOrWhiteSpace(code))
        {
            throw new FieldValidationException(new FieldViolation(
                "code", "Enter the code sent to the account's number to delete it.", FieldBounds.Required));
        }

        // Per account, right or wrong, the same window as a password sign-in. Deletion cannot be
        // undone, so somebody holding an unlocked phone must not get to grind through passwords here
        // when the sign-in form would stop them.
        if (!await attemptLimiter.TryAcquireAsync($"diner-delete:{diner.Id:N}", cancellationToken))
        {
            throw new TooManyAttemptsException(
                "Too many attempts to delete this account. Wait a few minutes and try again.");
        }

        if (diner.HasPassword)
        {
            var (matches, _) = hasher.Verify(diner.PasswordHash!, password);

            if (!matches)
            {
                logger.LogWarning("Diner {DinerUserId} gave a wrong password to delete the account.", diner.Id);

                throw ProofRejected();
            }
        }
        else
        {
            await ConsumeCodeAsync(diner.PhoneE164!, code!, cancellationToken);
        }

        await DinerAccountDeletion.EraseAsync(db, photos, refreshTokens, diner, clock.UtcNow, cancellationToken);

        authority.InvalidateDiner(diner.Id);

        logger.LogWarning("Diner {DinerUserId} deleted their account.", diner.Id);
    }

    /// <summary>
    /// Checks the newest live one-time code for the number, under the code flow's own limits, and
    /// marks it used. The save that marks it is the deletion's.
    /// </summary>
    /// <remarks>
    /// A wrong code spends one of the code's five attempts, saved at once so the refusal cannot be
    /// retried for free. Every failure answers <c>invalid-credentials</c> - deleting is not the
    /// place to explain which way a code went wrong - except a spent code, which is
    /// <c>too-many-attempts</c> so the app offers a new one.
    /// </remarks>
    private async Task ConsumeCodeAsync(string phoneE164, string code, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        var entity = await db.PhoneVerificationCodes
            .Where(c => c.PhoneE164 == phoneE164 && c.ConsumedAtUtc == null)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ProofRejected();

        if (entity.IsAttemptExhausted)
        {
            throw new TooManyAttemptsException("Too many attempts on that code. Ask for a new one.");
        }

        if (nowUtc >= entity.ExpiresAtUtc)
        {
            throw ProofRejected();
        }

        var (matches, _) = hasher.Verify(entity.CodeHash, code);

        if (!matches)
        {
            entity.RecordFailedAttempt();
            await db.SaveChangesAsync(cancellationToken);

            throw ProofRejected();
        }

        entity.Consume(nowUtc);
    }

    /// <summary>
    /// The caller's row, or a refusal. The policy on the route already requires a diner token, and
    /// the token check already refused an ended session; this is the same question asked of the row
    /// itself, past the check's five-second cache.
    /// </summary>
    private async Task<DinerUser> RequireDinerAsync(CancellationToken cancellationToken)
    {
        var dinerUserId = actor.DinerUserId
                          ?? throw new UnauthorizedAccessException("This needs a signed-in diner.");

        var diner = await db.DinerUsers
            .Include(d => d.Photo)
            .FirstOrDefaultAsync(d => d.Id == dinerUserId, cancellationToken);

        if (diner is null || !diner.IsActive || diner.IsDeleted)
        {
            throw SessionRevoked();
        }

        return diner;
    }

    private static AuthenticationFailedException SessionRevoked() =>
        new(TokenRevoked.SessionRevoked, "This session has ended. Sign in again.");

    private static AuthenticationFailedException ProofRejected() =>
        new("invalid-credentials", "That password or code is not right.");

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

    /// <summary>
    /// The profile. <c>RequireDinerAsync</c> has refused a deleted account, so the number is there.
    /// </summary>
    private static DinerProfileView ToView(DinerUser diner) =>
        new(
            diner.Id,
            diner.Username,
            diner.Email,
            diner.PhoneE164!,
            diner.IsPhoneVerified,
            diner.DisplayName,
            diner.LocaleCode,
            diner.HasPassword,
            diner.Photo is { } photo ? PhotoView.From(photo) : null);
}
