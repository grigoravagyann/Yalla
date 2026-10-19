using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Email and password, for the owner or manager working in the admin panel.
/// </summary>
/// <remarks>
/// The conventional identity type, and conventional is the right answer here: this is a desktop
/// browser session holding prices, refunds and staff accounts. Nobody is holding a tray while
/// they use it.
/// </remarks>
internal sealed class VenueUserAuthService(
    YallaDbContext db,
    IClock clock,
    TokenIssuer tokens,
    RefreshTokenStore refreshTokens,
    SecretHasher hasher,
    IPasswordResetSender resetSender,
    IOptions<AuthOptions> authOptions,
    ITokenAuthorityCheck authority,
    ILogger<VenueUserAuthService> logger) : IVenueUserAuthService
{
    private readonly AuthOptions _options = authOptions.Value;

    /// <summary>
    /// The single answer to every failed sign-in. Unknown address, wrong password and deactivated
    /// account are indistinguishable on purpose - otherwise the form tells a stranger which
    /// addresses have accounts.
    /// </summary>
    private static AuthenticationFailedException SignInRejected() =>
        new("credentials-invalid", "That email address and password do not match an account.");

    public async Task<VenueUserSignInResult> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();

        var staff = await db.StaffMembers
            .FirstOrDefaultAsync(s => s.Email == normalised, cancellationToken);

        var usable = staff?.PasswordHash is not null && staff.IsActive;

        // Always pay the hash, even when there is nothing to check it against. Returning early for
        // an unknown, password-less or deactivated account is what turned the shared rejection
        // message into a timing oracle - see SecretHasher.DecoyHash.
        var (matches, needsRehash) = hasher.Verify(
            usable ? staff!.PasswordHash! : hasher.DecoyHash, password);

        if (!usable)
        {
            throw SignInRejected();
        }

        if (!matches)
        {
            logger.LogWarning("Failed admin sign-in for staff member {StaffMemberId}.", staff!.Id);
            throw SignInRejected();
        }

        if (needsRehash)
        {
            staff!.SetPasswordHash(hasher.Hash(password));
        }

        // The refresh token first, so the access token can name its chain - which is how a password
        // change made from this sign-in keeps it and signs every other one out.
        var (refreshEntity, refreshToken) = refreshTokens.Issue(RefreshTokenSubject.VenueUser, staff!.Id);

        var (accessToken, _) = tokens.IssueVenueUserToken(
            staff.Id, staff.VenueId, staff.BranchId, staff.Role, staff.SessionGeneration, refreshEntity.ChainId);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Staff member {StaffMemberId} signed in to the admin panel.", staff.Id);

        return new VenueUserSignInResult(
            accessToken,
            refreshToken,
            tokens.AccessTokenSeconds,
            staff.Id,
            staff.FullName,
            staff.Role,
            staff.VenueId);
    }

    public async Task RequestPasswordResetAsync(
        string email,
        string localeCode,
        CancellationToken cancellationToken = default)
    {
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
        var locale = AuthMessages.Normalise(localeCode, _options.DefaultLocale);

        var staff = await db.StaffMembers
            .FirstOrDefaultAsync(s => s.Email == normalised && s.IsActive, cancellationToken);

        if (staff is null)
        {
            // Same silence as a successful request. Telling the caller "no such account" would
            // make this endpoint an address checker.
            logger.LogInformation("Password reset requested for an address with no active account.");
            return;
        }

        var plain = Secrets.NewOpaqueToken();

        db.PasswordResetTokens.Add(new PasswordResetToken(staff.Id, Secrets.Hash(plain), clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);

        var link = _options.PasswordResetUrlTemplate.Replace("{token}", Uri.EscapeDataString(plain));

        await resetSender.SendAsync(staff.Email!, link, locale, cancellationToken);
    }

    public async Task ResetPasswordAsync(
        string resetToken,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if ((newPassword ?? string.Empty).Length < _options.MinimumPasswordLength)
        {
            // Length is the only rule. Composition rules ("one capital, one symbol") measurably
            // push people towards Password1! and towards a sticky note on the till.
            throw new ArgumentException(
                $"Password must be at least {_options.MinimumPasswordLength} characters.",
                nameof(newPassword));
        }

        var nowUtc = clock.UtcNow;
        var hash = Secrets.Hash(resetToken ?? string.Empty);

        // The slow part first, before any lock is held.
        var passwordHash = hasher.Hash(newPassword!);

        // One transaction, holding the account's lock to the commit: the link and the row are decided
        // from what they are once it is held, not from a read taken before. A password change that
        // spent this link while this waited, a second use of the same link, a generation moved by a
        // sign-out elsewhere - each committed under the same lock, so each is what the reads below see.
        // And a refresh of one of the account's tokens either finished first, and its successor is
        // revoked here, or waits and finds its token revoked.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var token = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken)
                    ?? throw ResetLinkInvalid();

        await refreshTokens.LockSubjectAsync(RefreshTokenSubject.VenueUser, token.StaffMemberId, cancellationToken);
        await db.Entry(token).ReloadAsync(cancellationToken);

        if (!token.IsUsableAt(nowUtc))
        {
            throw ResetLinkInvalid();
        }

        // Read under the lock, so the generation moved below is the one that stands.
        var staff = await db.StaffMembers.FirstAsync(s => s.Id == token.StaffMemberId, cancellationToken);
        await db.Entry(staff).ReloadAsync(cancellationToken);

        token.Consume(nowUtc);
        staff.SetPasswordHash(passwordHash);

        // Everything the account was signed in on is now signed out - its refresh tokens revoked and,
        // by the generation, every access token it holds ended now rather than in fifteen minutes. The
        // usual reason to reset a password is that somebody else might have had it.
        staff.EndSessions();
        await refreshTokens.RevokeAllForSubjectAsync(
            RefreshTokenSubject.VenueUser, token.StaffMemberId, "password-changed", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // After the commit, so no other request re-reads the old generation into the cache.
        authority.InvalidateVenueUser(token.StaffMemberId);

        logger.LogInformation("Password reset completed for staff member {StaffMemberId}.", token.StaffMemberId);
    }

    private static AuthenticationFailedException ResetLinkInvalid() =>
        new("reset-token-invalid", "That reset link is not valid or has expired. Ask for a new one.");
}
