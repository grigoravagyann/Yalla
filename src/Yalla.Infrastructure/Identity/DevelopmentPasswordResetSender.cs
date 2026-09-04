using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Writes the password-reset link to the log instead of emailing it.
/// </summary>
/// <remarks>
/// Same pattern and same caveat as <see cref="DevelopmentVerificationCodeSender"/>: it puts a
/// working credential in the log, which is exactly what makes it useful locally and exactly what
/// makes it unacceptable in production.
/// </remarks>
internal sealed class DevelopmentPasswordResetSender(ILogger<DevelopmentPasswordResetSender> logger)
    : IPasswordResetSender
{
    public Task SendAsync(string email, string resetLink, string localeCode, CancellationToken ct)
    {
        var subject = AuthMessages.PasswordResetSubject(localeCode);
        var body = AuthMessages.PasswordResetBody(
            localeCode, resetLink, (int)Domain.Identity.PasswordResetToken.Lifetime.TotalMinutes);

        logger.LogWarning(
            "No email provider is configured. Reset mail to {Email} ({Locale}) would have read - {Subject}: {Body}",
            email, localeCode, subject, body);

        return Task.CompletedTask;
    }
}
