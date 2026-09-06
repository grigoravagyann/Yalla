using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Writes the verification code to the log instead of sending it anywhere.
/// </summary>
/// <remarks>
/// <para>
/// The only <see cref="IVerificationCodeSender"/> that ships today, deliberately. Whether
/// production sends these over SMS or over a Telegram bot is an open commercial question, and
/// nothing about sign-in should have waited for it to be settled.
/// </para>
/// <para>
/// <b>Registered in Development only.</b> It writes a live credential to the log, so
/// <c>DependencyInjection.AddAuthenticationServices</c> chooses it from the host environment
/// rather than from configuration - an environment variable must not be able to switch this on in
/// a deployed environment. Everywhere else <see cref="UnconfiguredVerificationCodeSender"/> takes
/// its place and logs the failure without the credential. <c>Program.cs</c> says at startup which
/// of the two is live.
/// </para>
/// </remarks>
internal sealed class DevelopmentVerificationCodeSender(ILogger<DevelopmentVerificationCodeSender> logger)
    : IVerificationCodeSender
{
    public Task SendAsync(string phoneE164, string code, string localeCode, CancellationToken ct)
    {
        var message = AuthMessages.VerificationCode(
            localeCode, code, (int)Domain.Identity.PhoneVerificationCode.Lifetime.TotalMinutes);

        logger.LogWarning(
            "No verification code provider is configured. Code for {Phone} ({Locale}) would have read: {Message}",
            phoneE164, localeCode, message);

        return Task.CompletedTask;
    }
}
