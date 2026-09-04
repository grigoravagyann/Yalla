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
/// It is safe to leave registered outside Development in the sense that it sends nothing to
/// anybody - but it does write a live credential to the log, so a real sender must replace it
/// before this reaches production. The startup warning below is there to make that impossible to
/// forget.
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
