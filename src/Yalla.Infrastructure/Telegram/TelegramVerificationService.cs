using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Telegram;

/// <summary>
/// Start and claim, from the app's side. The bot's side is <see cref="TelegramUpdateHandler"/>.
/// </summary>
/// <remarks>
/// The claim is the only place the number leaves the challenge row, and it leaves into exactly
/// the account path the one-time code uses - <see cref="DinerPhoneSignIn"/> - so a number proved
/// over Telegram buys precisely what a number proved by SMS buys, and nothing else.
/// </remarks>
internal sealed class TelegramVerificationService(
    YallaDbContext db,
    IClock clock,
    TelegramOptions telegram,
    IOptions<AuthOptions> authOptions,
    DinerPhoneSignIn signIn,
    ILogger<TelegramVerificationService> logger) : ITelegramVerificationService
{
    public async Task<TelegramVerificationStart> StartAsync(
        string localeCode,
        string? requestedFromAddress,
        Guid? callerDinerUserId,
        CancellationToken cancellationToken = default)
    {
        var locale = AuthMessages.Normalise(localeCode, authOptions.Value.DefaultLocale);
        var nowUtc = clock.UtcNow;

        // 32 random bytes, base64url: 43 characters of [A-Za-z0-9_-], inside Telegram's 64-character
        // start-parameter alphabet and limit. Stored hashed; the plaintext exists only in the link.
        var nonce = Secrets.NewOpaqueToken();

        var challenge = new TelegramVerification(
            Secrets.Hash(nonce),
            locale,
            nowUtc,
            telegram.ChallengeLifetime,
            requestedFromAddress,
            callerDinerUserId);

        db.TelegramVerifications.Add(challenge);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Telegram challenge {ChallengeId} started.", challenge.Id);

        return new TelegramVerificationStart(
            challenge.Id,
            $"https://t.me/{telegram.BotUsername}?start={nonce}",
            (int)telegram.ChallengeLifetime.TotalSeconds,
            telegram.PollAfterMs);
    }

    public async Task<TelegramVerificationClaim> ClaimAsync(
        Guid challengeId,
        string localeCode,
        Guid? callerDinerUserId,
        CancellationToken cancellationToken = default)
    {
        var locale = AuthMessages.Normalise(localeCode, authOptions.Value.DefaultLocale);
        var nowUtc = clock.UtcNow;

        var challenge = await db.TelegramVerifications
            .FirstOrDefaultAsync(v => v.Id == challengeId, cancellationToken);

        // Unknown, finished, past its deadline, or started under another account: one answer. See
        // the exception for why the three are not told apart.
        if (challenge is null)
        {
            throw new TelegramVerificationExpiredException();
        }

        if (nowUtc >= challenge.ExpiresAtUtc)
        {
            challenge.MarkExpired(nowUtc);
            await db.SaveChangesAsync(cancellationToken);

            throw new TelegramVerificationExpiredException();
        }

        if (challenge.CallerDinerUserId is { } startedUnder && callerDinerUserId is { } claimedUnder && startedUnder != claimedUnder)
        {
            logger.LogWarning(
                "Telegram challenge {ChallengeId} claimed under a different diner than it was started under.",
                challenge.Id);

            throw new TelegramVerificationExpiredException();
        }

        if (challenge.IsPendingAt(nowUtc))
        {
            return new TelegramVerificationClaim(TelegramVerificationClaim.Pending, null, null);
        }

        if (!challenge.IsClaimableAt(nowUtc) || challenge.PhoneE164 is null)
        {
            throw new TelegramVerificationExpiredException();
        }

        // Spent before the account path runs, on the same context it saves: a deactivated account
        // still consumes the challenge, exactly as it consumes a code.
        challenge.Consume(nowUtc);

        // The account the claim's own token names decides whether a registrant keeps their
        // password; falling back to the start's token covers an app that started signed in and
        // polls anonymously. Both name the same account or the claim was refused above.
        var result = await signIn.SignInProvedNumberAsync(
            challenge.PhoneE164,
            locale,
            callerDinerUserId ?? challenge.CallerDinerUserId,
            nowUtc,
            channel: "telegram",
            cancellationToken);

        return new TelegramVerificationClaim(TelegramVerificationClaim.Verified, result, challenge.PhoneE164);
    }
}
