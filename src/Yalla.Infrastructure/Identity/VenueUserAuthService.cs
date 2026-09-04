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

        if (staff?.PasswordHash is null || !staff.IsActive)
        {
            throw SignInRejected();
        }

        var (matches, needsRehash) = hasher.Verify(staff.PasswordHash, password);

        if (!matches)
        {
            logger.LogWarning("Failed admin sign-in for staff member {StaffMemberId}.", staff.Id);
            throw SignInRejected();
        }

        if (needsRehash)
        {
            staff.SetPasswordHash(hasher.Hash(password));
        }

        var (accessToken, _) = tokens.IssueVenueUserToken(
            staff.Id, staff.VenueId, staff.BranchId, staff.Role);

        var (_, refreshToken) = refreshTokens.Issue(RefreshTokenSubject.VenueUser, staff.Id);

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

        var token = await db.PasswordResetTokens
            .Include(t => t.StaffMember)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null || !token.IsUsableAt(nowUtc))
        {
            throw new AuthenticationFailedException(
                "reset-token-invalid", "That reset link is not valid or has expired. Ask for a new one.");
        }

        token.Consume(nowUtc);
        token.StaffMember.SetPasswordHash(hasher.Hash(newPassword!));

        // Everything the account was signed in on is now signed out. The usual reason to reset a
        // password is that somebody else might have had it.
        await refreshTokens.RevokeAllForSubjectAsync(
            RefreshTokenSubject.VenueUser, token.StaffMemberId, "password-changed", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Password reset completed for staff member {StaffMemberId}.", token.StaffMemberId);
    }
}
