namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Credential policy, from the <c>Auth</c> configuration section. Contains no secrets.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Consecutive wrong PINs before a staff member is locked out.</summary>
    public int PinMaxAttempts { get; set; } = 5;

    /// <summary>
    /// How long a PIN lockout lasts unattended. A manager clearing it is the path that actually
    /// gets used; this is only there so a lockout during a closed shift resolves itself.
    /// </summary>
    public int PinLockoutMinutes { get; set; } = 15;

    /// <summary>
    /// Minimum admin-panel password length. Length is the only rule: composition rules push
    /// people towards <c>Password1!</c> and towards writing it on the till.
    /// </summary>
    public int MinimumPasswordLength { get; set; } = 12;

    /// <summary>
    /// Verification codes one phone number may request per hour, whatever address they come from.
    /// The IP limiter alone does not stop someone with a phone farm from billing us for SMS.
    /// </summary>
    public int CodeRequestsPerPhonePerHour { get; set; } = 5;

    /// <summary>
    /// Returns the verification code in the response body so the diner flow can be exercised with
    /// no SMS provider wired up.
    /// </summary>
    /// <remarks>
    /// <b>Development only.</b> The registration refuses to honour this outside Development, so
    /// setting it in a production environment variable does nothing.
    /// </remarks>
    public bool ReturnVerificationCodeInResponse { get; set; }

    /// <summary>
    /// Where the password-reset link points. <c>{token}</c> is replaced with the single-use handle.
    /// </summary>
    public string PasswordResetUrlTemplate { get; set; } = "http://localhost:5173/reset-password?token={token}";

    /// <summary>Language used when a caller does not ask for one. Armenian, because most diners are.</summary>
    public string DefaultLocale { get; set; } = "hy";
}
