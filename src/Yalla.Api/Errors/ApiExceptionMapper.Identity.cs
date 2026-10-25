using Yalla.Application.Abstractions;
using Yalla.Domain.Identity;
using Yalla.Domain.Media;
using Yalla.Domain.Staff;

namespace Yalla.Api.Errors;

internal static partial class ApiExceptionMapper
{
    /// <summary>Sign-in and the one-time codes, and the staff role and branch boundary.</summary>
    private static MappedError? MapSignIn(Exception exception) => exception switch
    {
        // The account is locked, not the credential wrong. Must stay ABOVE the general
        // authentication arm it derives from: reporting a lockout as a plain 401 leaves someone
        // standing at a tablet retyping a PIN that was correct all along.
        AccountLockedException e => Refuse(
            ErrorCodes.AccountLocked,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["lockedUntilUtc"] = e.LockedUntilUtc,
            }),

        // No SMS provider: no code can be sent. 503 and not an error in the log - the deployment
        // chose Telegram-only, and every client that reads /api/auth/diner/phone-proof avoids it.
        PhoneCodesUnavailableException e => Refuse(ErrorCodes.SmsUnavailable, e.Message),

        // A one-time credential is out of attempts. 429 rather than 401, because the useful
        // signal to a client is "stop retrying and ask for a new code", not "try again".
        TooManyAttemptsException e => Refuse(
            ErrorCodes.TooManyAttempts,
            e.Message,
            context: e.RetryAfter is { } wait
                ? new Dictionary<string, object?> { ["retryAfterSeconds"] = RetryAfterSeconds(wait) }
                : null,
            retryAfterSeconds: e.RetryAfter is { } after ? RetryAfterSeconds(after) : null),

        // A one-time code not accepted, with what is left on it. Must stay ABOVE the general
        // authentication arm it derives from, which carries no context: the app read a count the
        // server never sent, defaulted it to zero, and told a diner their code was spent after the
        // first slip.
        VerificationCodeInvalidException e => RefuseFamily(
            e.ReasonCode,
            StatusCodes.Status401Unauthorized,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["attemptsRemaining"] = e.AttemptsRemaining,
            }),

        // A Telegram challenge the app can no longer claim. 410, not 401: nothing was presented
        // wrongly, the thing polled for is gone, and the app starts a new one. Must stay ABOVE the
        // general authentication arm it derives from.
        TelegramVerificationExpiredException e => Refuse(ErrorCodes.TelegramVerificationExpired, e.Message),

        // Every other sign-in failure. The slug comes from the exception, which is deliberately
        // never specific enough to say whether an account exists - see AuthenticationFailedException.
        AuthenticationFailedException e => RefuseFamily(e.ReasonCode, StatusCodes.Status401Unauthorized, e.Message),

        StaffPermissionException e => Refuse(
            ErrorCodes.Forbidden,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["operation"] = e.Operation,
                ["requiredRole"] = (int)e.RequiredRole,
            }),

        // The other half of the same boundary. 403 and not 409: the caller is not entitled to the
        // thing, which is what every endpoint on this path already declares. Raised only where the
        // route-level BranchScoped policy cannot reach - routes addressed by a bare entity id -
        // so the two layers now answer a cross-branch attempt with the same status.
        StaffBranchScopeException e => Refuse(
            ErrorCodes.Forbidden,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["subject"] = e.Subject,
            }),

        _ => null,
    };

    /// <summary>Accounts, PINs, images and phones: identifiers taken and sign-ins unknown.</summary>
    private static MappedError? MapAccounts(Exception exception) => exception switch
    {
        // A diner's username, email or number is somebody else's. Three codes, one shape: the
        // field to highlight rides in the context so the sign-up form does not map a code back to
        // an input by hand. Must stay ABOVE DomainStateException, which it derives from.
        DinerIdentifierTakenException e => RefuseFamily(
            e.Code,
            StatusCodes.Status409Conflict,
            e.Message,
            context: new Dictionary<string, object?> { ["field"] = e.Field }),

        // Not a photo, or too big a one. Its own code rather than the generic conflict, because
        // "that is not a picture" and "that picture is too large" are the two things an upload
        // screen has to say and neither is "try again". Must stay ABOVE DomainStateException.
        UnsupportedImageException e => Refuse(
            ErrorCodes.UnsupportedImage,
            e.Message,
            context: e.DetectedFormat is { } format
                ? new Dictionary<string, object?> { ["detectedFormat"] = format }
                : null),

        // Somebody on the same tablet already taps that PIN. Its own code so the staff form can put
        // the sentence under the PIN input. Must stay ABOVE DomainStateException, which it derives from.
        PinTakenException e => Refuse(
            ErrorCodes.PinTaken,
            e.Message,
            context: new Dictionary<string, object?> { ["field"] = e.Field }),

        // A token that names no sign-in, on a route that keeps this sign-in and ends the others. Its own
        // code so the console says "sign in again" rather than a generic conflict. Must stay ABOVE
        // DomainStateException, which it derives from.
        SignInUnknownException e => Refuse(ErrorCodes.SignInUnknown, e.Message),

        // A phone for somebody in the kitchen. Its own code so the console says why. Must stay ABOVE
        // DomainStateException, which it derives from.
        Yalla.Domain.Identity.KitchenPhoneNotAllowedException e => Refuse(
            ErrorCodes.KitchenPhoneNotAllowed,
            e.Message,
            context: new Dictionary<string, object?> { ["staffMemberId"] = e.StaffMemberId }),

        _ => null,
    };
}
