namespace Yalla.Application.Auth;

/// <summary>
/// Identity type 2: a diner with a booking, identified by a phone number and a one-time code.
/// </summary>
/// <remarks>
/// No password anywhere in this flow. The number has to be real - it is what the reminder, the
/// "still coming?" nudge and no-show tracking all hang off - and once it is verified a password
/// would add nothing except something to forget while standing outside a restaurant.
/// </remarks>
public interface IDinerAuthService
{
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
    /// first time.
    /// </summary>
    /// <exception cref="Domain.Identity.AuthenticationFailedException">The code was wrong or has expired.</exception>
    /// <exception cref="Domain.Identity.TooManyAttemptsException">The code's attempt limit is spent.</exception>
    Task<DinerSignInResult> VerifyCodeAsync(
        string phoneE164,
        string code,
        string localeCode,
        CancellationToken cancellationToken = default);
}
