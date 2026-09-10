using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// What the host ended up with for credential delivery, so startup can say it out loud once.
/// </summary>
/// <remarks>
/// The sender implementations are <c>internal</c> to this assembly, which is right - the Api layer
/// has no business naming them. It does have business knowing whether the process it just built
/// will put a live credential in a log file, so that fact is published here as a plain singleton
/// and <c>Program.cs</c> reads it. Both flags false means a real provider was registered.
/// </remarks>
/// <param name="WritesCredentialsToLog">
/// Development: verification codes and password reset links go to the log in plaintext.
/// </param>
/// <param name="DeliversNothing">
/// No provider is configured: requests are accepted and nothing is ever delivered.
/// </param>
public sealed record CredentialDeliveryReport(bool WritesCredentialsToLog, bool DeliversNothing);

/// <summary>
/// The senders registered outside Development, for as long as no real provider is wired up.
/// </summary>
/// <remarks>
/// <para>
/// The Development senders write the code and the reset link to the log, which is what makes them
/// useful locally and unacceptable anywhere else: a log line carrying a live reset token turns log
/// read access into a venue admin account, and that is precisely the property
/// <c>PasswordResetToken</c> storing only a hash exists to protect.
/// </para>
/// <para>
/// These replace them everywhere else. They send nothing - there is nothing to send them with -
/// and they say so at <b>Error</b>, naming the recipient and the row but never the credential. An
/// error rather than a warning because a deployment that cannot deliver a sign-in code is broken,
/// not merely unconfigured, and the log level in <c>appsettings.json</c> is <c>Error</c>: a warning
/// here would be filtered and the failure would be silent.
/// </para>
/// <para>
/// They deliberately do <b>not</b> throw. <c>RequestPasswordResetAsync</c> answers the same way for
/// an unknown address as for a real one, on purpose, so that the endpoint is not an address
/// checker; an exception here would turn it into one, and a 500 on the diner's sign-in would be
/// worse than a code that never arrives.
/// </para>
/// </remarks>
internal sealed class UnconfiguredVerificationCodeSender(
    ILogger<UnconfiguredVerificationCodeSender> logger) : IVerificationCodeSender
{
    public Task SendAsync(string phoneE164, string code, string localeCode, CancellationToken ct)
    {
        // The code itself is never in this line. That is the whole point of this class.
        logger.LogError(
            "No verification code provider is configured, so the code for {Phone} ({Locale}) was "
            + "not delivered. Nobody can sign in by phone until one is wired up. The code is NOT "
            + "logged - configure a real IVerificationCodeSender.",
            phoneE164, localeCode);

        return Task.CompletedTask;
    }
}

/// <summary>The password-reset half of <see cref="UnconfiguredVerificationCodeSender"/>.</summary>
internal sealed class UnconfiguredPasswordResetSender(
    ILogger<UnconfiguredPasswordResetSender> logger) : IPasswordResetSender
{
    public Task SendAsync(string email, string resetLink, string localeCode, CancellationToken ct)
    {
        // The link carries the token in its fragment, so the link is a credential and is never
        // logged - only the address it was owed to.
        logger.LogError(
            "No email provider is configured, so the password reset for {Email} ({Locale}) was not "
            + "delivered. The reset link is NOT logged - configure a real IPasswordResetSender.",
            email, localeCode);

        return Task.CompletedTask;
    }
}
