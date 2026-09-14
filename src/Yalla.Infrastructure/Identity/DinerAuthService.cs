using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// The diner's two ways in: a phone number plus a one-time code, or a username or email plus a
/// password.
/// </summary>
/// <remarks>
/// See <c>docs/auth.md</c> for why this identity type exists at all, why the code flow needs no
/// password, and why it is not the same as the account-free tab participant. The password flow
/// is the newer half and reuses everything the code flow mints - the same token, the same refresh
/// chain - so nothing downstream can tell which door somebody came in by.
/// </remarks>
internal sealed class DinerAuthService(
    YallaDbContext db,
    IClock clock,
    TokenIssuer tokens,
    RefreshTokenStore refreshTokens,
    IVerificationCodeSender sender,
    PhoneCodeRateLimiter phoneLimiter,
    PasswordAttemptLimiter passwordLimiter,
    SecretHasher hasher,
    ITokenAuthorityCheck authority,
    IOptions<AuthOptions> authOptions,
    ILogger<DinerAuthService> logger) : IDinerAuthService
{
    private readonly AuthOptions _options = authOptions.Value;

    /// <summary>
    /// The single answer to every failed password sign-in. Unknown identifier, wrong password, an
    /// account with no password and a deactivated one are indistinguishable on purpose - otherwise
    /// the form tells a stranger which usernames and addresses have accounts.
    /// </summary>
    private static AuthenticationFailedException SignInRejected() =>
        new("invalid-credentials", "That username or email and password do not match an account.");

    public async Task<VerificationCodeRequestResult> RequestCodeAsync(
        string phoneE164,
        string localeCode,
        string? requestedFromAddress,
        CancellationToken cancellationToken = default)
    {
        var phone = PhoneNumber.Normalise(phoneE164, "phoneE164");
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
        Guid? callerDinerUserId = null,
        CancellationToken cancellationToken = default)
    {
        var phone = PhoneNumber.Normalise(phoneE164, "phoneE164");
        var locale = AuthMessages.Normalise(localeCode, _options.DefaultLocale);
        var nowUtc = clock.UtcNow;

        var entity = await db.PhoneVerificationCodes
            .Where(c => c.PhoneE164 == phone && c.ConsumedAtUtc == null)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            // Nothing live for this number, so nothing left to try: say so, and the client offers
            // a new code rather than a retry. See VerificationCodeInvalidException for why the count
            // publishes nothing an anonymous caller could not already learn.
            throw new VerificationCodeInvalidException(
                attemptsRemaining: 0, "That code is not valid. Ask for a new one.");
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
            throw new VerificationCodeInvalidException(
                Math.Max(0, PhoneVerificationCode.MaxAttempts - entity.AttemptCount),
                "That code is not valid.");
        }

        entity.Consume(nowUtc);

        var diner = await db.DinerUsers.FirstOrDefaultAsync(d => d.PhoneE164 == phone, cancellationToken);

        if (diner is { IsActive: false })
        {
            // Refused here with the password sign-in's answer for a deactivated account, rather than
            // issued a token the authority check turns away on first use and a refresh token refresh
            // would refuse. The code was right, so it is spent: saved before refusing, it cannot be
            // presented again. (A deleted account has no number and is never found by one.)
            await db.SaveChangesAsync(cancellationToken);

            logger.LogWarning("Code sign-in refused for inactive diner {DinerUserId}.", diner.Id);

            throw SignInRejected();
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

        // The one thing only this flow can say: a code sent to the number came back. A registered
        // account's number is unverified until this line runs for it once. The first time, on an
        // account that registered with a password, the password was the registrant's and not
        // necessarily the number's owner's: it is cleared, and every session the registrant holds
        // goes with it - unless the request came in holding this account's own token, which makes
        // the registrant and the phone's holder the same person. Revoked before this sign-in's token
        // is issued, so the new one survives.
        var verifierIsAccountHolder = callerDinerUserId is { } caller && caller == diner.Id;

        // Displacing also bumps the session generation, which is what ends the registrant's access
        // tokens - the refresh tokens below are only half of "every session".
        var displaced = diner.ProveNumberByCode(nowUtc, verifierIsAccountHolder);

        if (displaced)
        {
            await refreshTokens.RevokeAllForSubjectAsync(
                RefreshTokenSubject.Diner, diner.Id, "phone-proved-by-another", cancellationToken);

            logger.LogWarning(
                "Diner {DinerUserId}'s unverified number was proved by code; the registered password was cleared and its sessions revoked.",
                diner.Id);
        }

        diner.RecordSignIn(nowUtc);

        // Minted under the generation this save commits, so the owner's token is the one that works.
        var (accessToken, _) = tokens.IssueDinerToken(diner.Id, diner.SessionGeneration);
        var (_, refreshToken) = refreshTokens.Issue(RefreshTokenSubject.Diner, diner.Id);

        await db.SaveChangesAsync(cancellationToken);

        if (displaced)
        {
            // After the commit, not before: evicting first leaves a moment in which a request from
            // the registrant re-reads the old generation and caches it for five seconds. The token
            // above has not left this process yet, so nobody can present it before this line runs.
            authority.InvalidateDiner(diner.Id);
        }

        logger.LogInformation(
            "Diner {DinerUserId} signed in by phone verification. New account: {IsNewAccount}.",
            diner.Id, isNewAccount);

        return new DinerSignInResult(
            accessToken, refreshToken, tokens.AccessTokenSeconds, diner.Id, isNewAccount);
    }

    public async Task<DinerSignInResult> RegisterAsync(
        RegisterDinerCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Every field normalised and refused by name before anything is read from the database,
        // so a malformed form never costs a query and never learns anything from one.
        var phone = PhoneNumber.Normalise(command.PhoneE164, "phoneE164");
        var username = DinerAccountRules.NormaliseUsername(command.Username, "username");
        var email = DinerAccountRules.NormaliseEmail(command.Email, "email");
        var password = DinerAccountRules.CheckPassword(command.Password, username, email, "password");
        var locale = AuthMessages.Normalise(command.LocaleCode, _options.DefaultLocale);
        var nowUtc = clock.UtcNow;

        // One read for all three clashes, answered in the order the form shows the fields. The
        // unique indexes are the backstop for the race this read cannot close - see the save.
        var clashes = await db.DinerUsers
            .Where(d => d.Username == username || d.Email == email || d.PhoneE164 == phone)
            .Select(d => new { d.Username, d.Email, d.PhoneE164 })
            .ToListAsync(cancellationToken);

        if (clashes.Any(c => c.Username == username))
        {
            throw DinerIdentifierTakenException.Username();
        }

        if (clashes.Any(c => c.Email == email))
        {
            throw DinerIdentifierTakenException.Email();
        }

        if (clashes.Any(c => c.PhoneE164 == phone))
        {
            // Not merged into the existing account, even though the number is the same: a
            // stranger typing somebody's number must not be handed their booking history. The
            // account's owner proves the number with a code, which is what the app offers next.
            throw DinerIdentifierTakenException.Phone();
        }

        var diner = DinerUser.Register(phone, locale, command.DisplayName, username, email, hasher.Hash(password));

        diner.RecordSignIn(nowUtc);
        db.DinerUsers.Add(diner);

        var (accessToken, _) = tokens.IssueDinerToken(diner.Id, diner.SessionGeneration);
        var (_, refreshToken) = refreshTokens.Issue(RefreshTokenSubject.Diner, diner.Id);

        await SaveGuardingIdentifiersAsync(cancellationToken);

        // The id and nothing else. The username, the email and the number are personal data and
        // the password is a secret; none of them belongs in a log line.
        logger.LogInformation("Diner {DinerUserId} registered with a password.", diner.Id);

        return new DinerSignInResult(
            accessToken, refreshToken, tokens.AccessTokenSeconds, diner.Id, IsNewAccount: true);
    }

    public async Task<DinerSignInResult> LoginAsync(
        string identifier,
        string password,
        string? localeCode,
        CancellationToken cancellationToken = default)
    {
        var key = (identifier ?? string.Empty).Trim().ToLowerInvariant();

        if (key.Length == 0)
        {
            // Nothing to look up and nothing to guess at, so no budget is spent and no hash is
            // paid: the empty identifier is the one input that cannot be an oracle.
            throw SignInRejected();
        }

        // Per identifier, counted before the answer is known. Ten a quarter-hour is generous for a
        // person and hopeless for a guesser, and a spent budget is a 429 rather than a 401 so the
        // app says "wait" instead of "wrong password" to somebody typing the right one.
        if (!await passwordLimiter.TryAcquireAsync(key, cancellationToken))
        {
            throw new TooManyAttemptsException(
                "Too many sign-in attempts for that account. Wait a few minutes and try again.");
        }

        var diner = await db.DinerUsers
            .FirstOrDefaultAsync(d => d.Username == key || d.Email == key, cancellationToken);

        var usable = diner is { IsActive: true, PasswordHash: not null };

        // Always pay the hash, even when there is nothing to check it against. Returning early for
        // an unknown, password-less or deactivated account would turn the shared rejection into a
        // timing oracle - see SecretHasher.DecoyHash.
        var (matches, needsRehash) = hasher.Verify(
            usable ? diner!.PasswordHash! : hasher.DecoyHash, password);

        if (!usable)
        {
            throw SignInRejected();
        }

        if (!matches)
        {
            logger.LogWarning("Failed password sign-in for diner {DinerUserId}.", diner!.Id);
            throw SignInRejected();
        }

        if (needsRehash)
        {
            // The same password, stored more strongly. Not a change, so the account's other
            // sessions keep working.
            diner!.RehashPassword(hasher.Hash(password));
        }

        // Only a recognised locale moves the stored one. The code flow falls back to the default
        // because it may be creating the row; here the row exists and already knows its language,
        // and a phone that sent nothing should not reset it.
        if (!string.IsNullOrWhiteSpace(localeCode))
        {
            diner!.SetLocale(AuthMessages.Normalise(localeCode, diner.LocaleCode));
        }

        diner!.RecordSignIn(clock.UtcNow);

        var (accessToken, _) = tokens.IssueDinerToken(diner.Id, diner.SessionGeneration);
        var (_, refreshToken) = refreshTokens.Issue(RefreshTokenSubject.Diner, diner.Id);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Diner {DinerUserId} signed in with a password.", diner.Id);

        return new DinerSignInResult(
            accessToken, refreshToken, tokens.AccessTokenSeconds, diner.Id, IsNewAccount: false);
    }

    /// <summary>
    /// Saves a registration, turning a lost race for a username, an email or a number into the
    /// same 409 the read above gives - rather than the 500 a bare unique violation would be.
    /// </summary>
    /// <remarks>
    /// Two sign-ups for the same username can pass the read together and both reach the insert;
    /// the index decides, and the loser is told the same thing it would have been told a second
    /// later. Matching on the index name is unlovely and is the only thing SQL Server says.
    /// </remarks>
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
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.DinerUserPhone))
        {
            throw DinerIdentifierTakenException.Phone();
        }
    }
}
