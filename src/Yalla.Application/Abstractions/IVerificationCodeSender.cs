namespace Yalla.Application.Abstractions;

/// <summary>
/// Delivers a one-time code to a phone number.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately abstract, and deliberately not an SMS interface. Whether production sends these
/// over SMS, over a Telegram bot, or both, is an open commercial question - SMS to Armenian
/// numbers is metered and a bot is free - and it is not a question that should be allowed to
/// block sign-in from being built. Nothing above this interface knows or cares.
/// </para>
/// <para>
/// The only implementation shipped today writes the code to the log. Adding a provider means
/// adding one class and one registration.
/// </para>
/// </remarks>
public interface IVerificationCodeSender
{
    /// <summary>Sends <paramref name="code"/> to <paramref name="phoneE164"/>.</summary>
    /// <param name="phoneE164">The destination number in E.164, e.g. <c>+37411223344</c>.</param>
    /// <param name="code">The plaintext code. Only ever held in memory; the database has a hash.</param>
    /// <param name="localeCode">
    /// Which language to write the message in: <c>hy</c>, <c>ru</c> or <c>en</c>. The text itself
    /// comes from resources, not from the caller, so a new channel cannot invent its own wording.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task SendAsync(string phoneE164, string code, string localeCode, CancellationToken ct);
}
