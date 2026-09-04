namespace Yalla.Application.Abstractions;

/// <summary>
/// Delivers a password-reset link to an owner or manager.
/// </summary>
/// <remarks>
/// Same pattern, and the same reasoning, as <see cref="IVerificationCodeSender"/>: which email
/// provider the venue ends up on is a procurement decision, not a design one, and the
/// development implementation logs the link so the flow is testable today.
/// </remarks>
public interface IPasswordResetSender
{
    /// <summary>Sends the reset link to <paramref name="email"/>.</summary>
    /// <param name="email">The address on the account.</param>
    /// <param name="resetLink">The full link, including the single-use token.</param>
    /// <param name="localeCode"><c>hy</c>, <c>ru</c> or <c>en</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendAsync(string email, string resetLink, string localeCode, CancellationToken ct);
}
