using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// The text of the messages sent to diners and venue users, in Armenian, Russian and English.
/// </summary>
/// <remarks>
/// <para>
/// The wording lives in resources rather than in the senders, so the SMS provider, the Telegram
/// bot and the email sender that eventually exist all say the same thing, and so a translator can
/// change it without touching code.
/// </para>
/// <para>
/// The neutral fallback is English. An unknown locale falls back rather than failing: nobody
/// should be unable to sign in because a client sent <c>hy-AM</c> instead of <c>hy</c>.
/// </para>
/// </remarks>
internal static class AuthMessages
{
    /// <summary>The locales this system actually has translations for.</summary>
    public static readonly IReadOnlySet<string> SupportedLocales =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hy", "ru", "en" };

    private static readonly ResourceManager Resources = new(
        "Yalla.Infrastructure.Resources.AuthMessages",
        Assembly.GetExecutingAssembly());

    /// <summary>
    /// Normalises whatever a client sent into one of <see cref="SupportedLocales"/>.
    /// </summary>
    /// <param name="localeCode">A BCP 47 tag, or null.</param>
    /// <param name="fallback">The locale to use when nothing matches.</param>
    public static string Normalise(string? localeCode, string fallback)
    {
        if (string.IsNullOrWhiteSpace(localeCode))
        {
            return fallback;
        }

        // "hy-AM" and "HY" are both Armenian. Taking the primary subtag is enough here because
        // this system does not distinguish regional variants of any of its three languages.
        var primary = localeCode.Trim().Split('-')[0];

        return SupportedLocales.Contains(primary) ? primary.ToLowerInvariant() : fallback;
    }

    /// <summary>The verification code message.</summary>
    /// <param name="localeCode">One of <see cref="SupportedLocales"/>.</param>
    /// <param name="code">The six digits.</param>
    /// <param name="expiresInMinutes">How long the code lasts.</param>
    public static string VerificationCode(string localeCode, string code, int expiresInMinutes) =>
        Format(localeCode, "VerificationCode", code, expiresInMinutes);

    /// <summary>Subject line of the password-reset email.</summary>
    public static string PasswordResetSubject(string localeCode) =>
        Format(localeCode, "PasswordResetSubject");

    /// <summary>Body of the password-reset email.</summary>
    /// <param name="localeCode">One of <see cref="SupportedLocales"/>.</param>
    /// <param name="resetLink">The full link, token included.</param>
    /// <param name="expiresInMinutes">How long the link lasts.</param>
    public static string PasswordResetBody(string localeCode, string resetLink, int expiresInMinutes) =>
        Format(localeCode, "PasswordResetBody", resetLink, expiresInMinutes);

    private static string Format(string localeCode, string key, params object[] arguments)
    {
        var culture = CultureInfo.GetCultureInfo(localeCode);
        var template = Resources.GetString(key, culture)
                       ?? throw new InvalidOperationException($"Auth message resource '{key}' is missing.");

        return arguments.Length == 0
            ? template
            : string.Format(culture, template, arguments);
    }
}
