namespace Yalla.Application.Auth;

/// <summary>
/// The app's side of proving a phone number through the Telegram bot: start a challenge, then
/// poll it until the bot has been handed the number.
/// </summary>
/// <remarks>
/// <para>
/// The second door into identity type 2, beside the one-time code. Same account, same tokens,
/// same displacement rule for a registered number - the only difference is who proves the
/// number: an SMS that came back, or Telegram's own record of which number the account was
/// created with. See <c>docs/auth.md</c>, "Telegram verification".
/// </para>
/// <para>
/// The app never learns the number from here. It starts with nothing, polls with a challenge id,
/// and receives either "pending" or the sign-in - exactly what <c>verify-code</c> returns.
/// </para>
/// </remarks>
public interface ITelegramVerificationService
{
    /// <summary>
    /// Issues a challenge and the single-use deep link that opens the bot with it.
    /// </summary>
    /// <param name="localeCode">Language for the bot's messages and the account, if one is created.</param>
    /// <param name="requestedFromAddress">Caller address, recorded for abuse diagnostics.</param>
    /// <param name="callerDinerUserId">
    /// The account a valid diner bearer token on the request names, or null. A registrant proving
    /// their own number starts under their token so the claim can keep their password.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TelegramVerificationStart> StartAsync(
        string localeCode,
        string? requestedFromAddress,
        Guid? callerDinerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports where a challenge is and, once the bot has verified it, signs the diner in - creating
    /// the account on a first number, and marking a registered number verified - and spends it.
    /// </summary>
    /// <param name="challengeId">The id <see cref="StartAsync"/> returned.</param>
    /// <param name="localeCode">Language to store against the account.</param>
    /// <param name="callerDinerUserId">
    /// The account a valid diner bearer token on this request names, or null. Must match the
    /// account the start was made under when there was one.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Domain.Identity.TelegramVerificationExpiredException">
    /// Unknown id, past its deadline, already claimed, or claimed under a different account than
    /// it was started under. One answer for all of them.
    /// </exception>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The number's account is deactivated.</exception>
    Task<TelegramVerificationClaim> ClaimAsync(
        Guid challengeId,
        string localeCode,
        Guid? callerDinerUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>What the app needs to open the bot and to poll.</summary>
/// <param name="ChallengeId">Poll with this. Opaque, and useless to anyone who did not also open the link.</param>
/// <param name="DeepLinkUrl"><c>https://t.me/{bot}?start={nonce}</c>. Open it; Telegram starts the bot with the nonce.</param>
/// <param name="ExpiresInSeconds">How long the link, and the challenge, stay usable.</param>
/// <param name="PollAfterMs">How long to wait between claims. Polling faster than this spends the claim rate limit for nothing.</param>
public sealed record TelegramVerificationStart(
    Guid ChallengeId,
    string DeepLinkUrl,
    int ExpiresInSeconds,
    int PollAfterMs);

/// <summary>The answer to one poll.</summary>
/// <param name="State">
/// <c>pending</c> while the bot has not been handed the number; <c>verified</c> exactly once,
/// with <paramref name="SignIn"/> set. After that the challenge is spent and answers 410.
/// </param>
/// <param name="SignIn">The tokens, when <paramref name="State"/> is <c>verified</c>. Null otherwise.</param>
/// <param name="PhoneE164">
/// The number Telegram handed over, in E.164, when <paramref name="State"/> is <c>verified</c> -
/// so the app can show it without a second round trip to the profile. Null otherwise.
/// </param>
public sealed record TelegramVerificationClaim(string State, DinerSignInResult? SignIn, string? PhoneE164)
{
    public const string Pending = "pending";

    public const string Verified = "verified";
}
