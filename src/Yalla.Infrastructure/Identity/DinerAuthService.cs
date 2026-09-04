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
/// Phone number plus a one-time code. No password anywhere in this flow.
/// </summary>
/// <remarks>
/// See <c>docs/auth.md</c> for why this identity type exists at all, and why it is not the same
/// as the account-free tab participant.
/// </remarks>
internal sealed class DinerAuthService(
    YallaDbContext db,
    IClock clock,
    TokenIssuer tokens,
    RefreshTokenStore refreshTokens,
    IVerificationCodeSender sender,
    PhoneCodeRateLimiter phoneLimiter,
    SecretHasher hasher,
    IOptions<AuthOptions> authOptions,
    ILogger<DinerAuthService> logger) : IDinerAuthService
{
    private readonly AuthOptions _options = authOptions.Value;

    public async Task<VerificationCodeRequestResult> RequestCodeAsync(
        string phoneE164,
        string localeCode,
        string? requestedFromAddress,
        CancellationToken cancellationToken = default)
    {
        var phone = PhoneNumber.Normalise(phoneE164);
        var locale = AuthMessages.Normalise(localeCode, _options.DefaultLocale);

        if (!await phoneLimiter.TryAcquireAsync(phone, cancellationToken))
        {
            throw new TooManyAttemptsException(
                "Too many codes have been requested for that number. Try again later.");
        }

        var nowUtc = clock.UtcNow;

        // Retire any code still outstanding for this number, so only the newest one verifies.
        // Without this, five requests would leave five live codes and quintuple the guess budget.
        var outstanding = await db.PhoneVerificationCodes
            .Where(c => c.PhoneE164 == phone && c.ConsumedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var code in outstanding)
        {
            code.Supersede(nowUtc);
        }

        var plainCode = Secrets.NewNumericCode();

        var entity = new PhoneVerificationCode(
            phone,
            hasher.Hash(plainCode),
            nowUtc,
            requestedFromAddress);

        db.PhoneVerificationCodes.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        await sender.SendAsync(phone, plainCode, locale, cancellationToken);

        // Note what this method never did: look at DinerUsers. The answer below is assembled from
        // constants, so a registered and an unregistered number are indistinguishable - not
        // merely similar. Anything that branched on whether the account existed, including a
        // timing difference from an extra query, would turn this into a way to ask a stranger's
        // phone book who uses Yalla.
        return new VerificationCodeRequestResult(
            (int)PhoneVerificationCode.Lifetime.TotalSeconds,
            PhoneVerificationCode.MaxAttempts,
            _options.ReturnVerificationCodeInResponse ? plainCode : null);
    }

    public async Task<DinerSignInResult> VerifyCodeAsync(
        string phoneE164,
        string code,
        string localeCode,
        CancellationToken cancellationToken = default)
    {
        var phone = PhoneNumber.Normalise(phoneE164);
        var locale = AuthMessages.Normalise(localeCode, _options.DefaultLocale);
        var nowUtc = clock.UtcNow;

        var entity = await db.PhoneVerificationCodes
            .Where(c => c.PhoneE164 == phone && c.ConsumedAtUtc == null)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            throw new AuthenticationFailedException(
                "verification-code-invalid", "That code is not valid. Ask for a new one.");
        }

        if (entity.IsAttemptExhausted)
        {
            throw new TooManyAttemptsException(
                "Too many attempts on that code. Ask for a new one.");
        }

        if (nowUtc >= entity.ExpiresAtUtc)
        {
            throw new AuthenticationFailedException(
                "verification-code-expired", "That code has expired. Ask for a new one.");
        }

        var (matches, _) = hasher.Verify(entity.CodeHash, code);

        if (!matches)
        {
            entity.RecordFailedAttempt();
            await db.SaveChangesAsync(cancellationToken);

            // The fifth wrong guess answers the same as the first four. The status describes what
            // happened to this request - a code was checked and was wrong - and burning the last
            // attempt does not change that. "Out of attempts" is the answer to the *next*
            // request, which is not checked against anything.
            throw new AuthenticationFailedException(
                "verification-code-invalid", "That code is not valid.");
        }

        entity.Consume(nowUtc);

        var diner = await db.DinerUsers.FirstOrDefaultAsync(d => d.PhoneE164 == phone, cancellationToken);
        var isNewAccount = diner is null;

        if (diner is null)
        {
            // First successful verification is the sign-up. There is no separate registration
            // step, because a separate registration step is a step people abandon.
            diner = new DinerUser(phone, locale);
            db.DinerUsers.Add(diner);
        }
        else
        {
            diner.SetLocale(locale);
        }

        diner.RecordSignIn(nowUtc);

        var (accessToken, _) = tokens.IssueDinerToken(diner.Id);
        var (_, refreshToken) = refreshTokens.Issue(RefreshTokenSubject.Diner, diner.Id);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Diner {DinerUserId} signed in by phone verification. New account: {IsNewAccount}.",
            diner.Id, isNewAccount);

        return new DinerSignInResult(
            accessToken, refreshToken, tokens.AccessTokenSeconds, diner.Id, isNewAccount);
    }
}
