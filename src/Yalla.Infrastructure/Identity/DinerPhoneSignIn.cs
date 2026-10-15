using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// What happens once a phone number has been proved, whichever way it was proved: the account is
/// found or created, the number is marked verified, and tokens are minted.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the one-time code and the Telegram bot, so the two doors cannot drift: the
/// displacement rule for a registered number, the refusal of a deactivated account, the session
/// invalidation and the log lines are one piece of code. See <c>DinerUser.ProveNumber</c> for
/// the rule itself and <c>docs/auth.md</c> §2 for why it exists.
/// </para>
/// <para>
/// Saves the unit of work. The caller has already marked its credential spent on the same context,
/// so one commit records both the proof and what it bought - including on the refusal path, where
/// the credential is still spent.
/// </para>
/// </remarks>
internal sealed class DinerPhoneSignIn(
    YallaDbContext db,
    TokenIssuer tokens,
    RefreshTokenStore refreshTokens,
    ITokenAuthorityCheck authority,
    ILogger<DinerPhoneSignIn> logger)
{
    /// <summary>
    /// Signs in the holder of a number that was just proved.
    /// </summary>
    /// <param name="phone">The proved number, already normalised to E.164.</param>
    /// <param name="locale">Language to store against the account.</param>
    /// <param name="callerDinerUserId">The account a valid diner token on the request named, or null.</param>
    /// <param name="nowUtc">When the proof arrived.</param>
    /// <param name="channel">How it was proved - <c>code</c> or <c>telegram</c> - for the log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="AuthenticationFailedException">The number's account is deactivated.</exception>
    public async Task<DinerSignInResult> SignInProvedNumberAsync(
        string phone,
        string locale,
        Guid? callerDinerUserId,
        DateTime nowUtc,
        string channel,
        CancellationToken cancellationToken)
    {
        var diner = await db.DinerUsers.FirstOrDefaultAsync(d => d.PhoneE164 == phone, cancellationToken);

        if (diner is { IsActive: false })
        {
            // Refused here with the password sign-in's answer for a deactivated account, rather than
            // issued a token the authority check turns away on first use and a refresh token refresh
            // would refuse. The proof was genuine, so the credential behind it is spent: saved before
            // refusing, it cannot be presented again. (A deleted account has no number and is never
            // found by one.)
            await db.SaveChangesAsync(cancellationToken);

            logger.LogWarning("Phone sign-in by {Channel} refused for inactive diner {DinerUserId}.", channel, diner.Id);

            throw new AuthenticationFailedException(
                "invalid-credentials", "That username or email and password do not match an account.");
        }

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
            // An account that registered with this number typed - or one this flow created - is
            // signed in, not duplicated. The number is the account either way.
            diner.SetLocale(locale);
        }

        // The one thing only a proof can say: the number's holder is here. A registered account's
        // number is unverified until this line runs for it once. The first time, on an account that
        // registered with a password, the password was the registrant's and not necessarily the
        // number's owner's: it is cleared, and every session the registrant holds goes with it -
        // unless the request came in holding this account's own token, which makes the registrant
        // and the phone's holder the same person. Revoked before this sign-in's token is issued, so
        // the new one survives.
        var verifierIsAccountHolder = callerDinerUserId is { } caller && caller == diner.Id;

        // Displacing also bumps the session generation, which is what ends the registrant's access
        // tokens - the refresh tokens below are only half of "every session".
        var displaced = diner.ProveNumber(nowUtc, verifierIsAccountHolder);

        if (displaced)
        {
            await refreshTokens.RevokeAllForSubjectAsync(
                RefreshTokenSubject.Diner, diner.Id, "phone-proved-by-another", cancellationToken);

            logger.LogWarning(
                "Diner {DinerUserId}'s unverified number was proved by {Channel}; the registered password was cleared and its sessions revoked.",
                diner.Id, channel);
        }

        diner.RecordSignIn(nowUtc);

        // Minted under the generation this save commits, so the owner's token is the one that works.
        var (refreshEntity, refreshToken) = refreshTokens.Issue(RefreshTokenSubject.Diner, diner.Id);
        var (accessToken, _) = tokens.IssueDinerToken(diner.Id, diner.SessionGeneration, refreshEntity.ChainId);

        await db.SaveChangesAsync(cancellationToken);

        if (displaced)
        {
            // After the commit, not before: evicting first leaves a moment in which a request from
            // the registrant re-reads the old generation and caches it for five seconds. The token
            // above has not left this process yet, so nobody can present it before this line runs.
            authority.InvalidateDiner(diner.Id);
        }

        logger.LogInformation(
            "Diner {DinerUserId} signed in by phone verification over {Channel}. New account: {IsNewAccount}.",
            diner.Id, channel, isNewAccount);

        return new DinerSignInResult(
            accessToken, refreshToken, tokens.AccessTokenSeconds, diner.Id, isNewAccount);
    }
}
