namespace Yalla.Application.Auth;

/// <summary>
/// Identity type 2: a diner, identified by a phone number and a one-time code - or, once they
/// have chosen one, by a username or email and a password.
/// </summary>
/// <remarks>
/// <para>
/// The code flow is still the front door and still needs no password: the number has to be real
/// - it is what the reminder, the "still coming?" nudge and no-show tracking all hang off - and a
/// code proves it in thirty seconds. The password flow exists for the people who want an account
/// they can recognise, and for the moment there is no signal for an SMS. Registering with a
/// password does <b>not</b> verify the number; only the code flow does that.
/// </para>
/// <para>
/// Both flows issue the same tokens and the same refresh chain, so nothing downstream knows or
/// cares which door a diner came in by.
/// </para>
/// </remarks>
public interface IDinerAuthService
{
    /// <summary>
    /// Creates an account from the sign-up form and signs it in.
    /// </summary>
    /// <remarks>
    /// The number is stored as typed and stays unverified until a code is passed. A number that
    /// already has an account is refused rather than merged - that account's owner can prove the
    /// number, and a stranger typing it cannot.
    /// </remarks>
    /// <exception cref="ArgumentException">A field is malformed. Names the field.</exception>
    /// <exception cref="Domain.Identity.DinerIdentifierTakenException">The username, email or number is somebody else's.</exception>
    Task<DinerSignInResult> RegisterAsync(
        RegisterDinerCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs in with a username or an email and a password.
    /// </summary>
    /// <remarks>
    /// Unknown identifier, wrong password, an account with no password and a deactivated account
    /// all answer identically, and all cost the same time - see the service.
    /// </remarks>
    /// <param name="identifier">A username or an email address, case-insensitively.</param>
    /// <param name="password">The password.</param>
    /// <param name="localeCode">Language to store against the account, when a recognised one is sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The credentials do not match a usable account.</exception>
    /// <exception cref="Domain.Identity.TooManyAttemptsException">The identifier's attempt budget is spent.</exception>
    Task<DinerSignInResult> LoginAsync(
        string identifier,
        string password,
        string? localeCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a code to a phone number and hands it to <see cref="Abstractions.IVerificationCodeSender"/>.
    /// </summary>
    /// <remarks>
    /// Answers identically for a registered and an unregistered number. Any previous unused code
    /// for the number is retired first, so only the newest one can be verified.
    /// </remarks>
    /// <param name="phoneE164">The number, in E.164.</param>
    /// <param name="localeCode">Language for the message: <c>hy</c>, <c>ru</c> or <c>en</c>.</param>
    /// <param name="requestedFromAddress">Caller address, recorded for abuse diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VerificationCodeRequestResult> RequestCodeAsync(
        string phoneE164,
        string localeCode,
        string? requestedFromAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a code and, on success, signs the diner in - creating the account if this is their
    /// first time, and marking the number verified on an account that registered with it typed.
    /// </summary>
    /// <remarks>
    /// On the first proof of a registered number, the registered password and every session are
    /// cleared unless <paramref name="callerDinerUserId"/> names that same account - see
    /// <c>DinerUser.ProveNumberByCode</c>.
    /// </remarks>
    /// <param name="phoneE164">The number, in E.164.</param>
    /// <param name="code">The six digits.</param>
    /// <param name="localeCode">Language to store against the account.</param>
    /// <param name="callerDinerUserId">
    /// The account a valid diner bearer token on the request names, or null for an anonymous
    /// caller. Read from the authenticated principal, never from the body.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The code was wrong or has expired.</exception>
    /// <exception cref="Domain.Identity.TooManyAttemptsException">The code's attempt limit is spent.</exception>
    Task<DinerSignInResult> VerifyCodeAsync(
        string phoneE164,
        string code,
        string localeCode,
        Guid? callerDinerUserId = null,
        CancellationToken cancellationToken = default);
}
