using System.Text.RegularExpressions;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Normalises and checks phone numbers in E.164.
/// </summary>
/// <remarks>
/// One canonical form matters more than it sounds: <c>+374 11 22 33 44</c>, <c>+37411223344</c>
/// and <c>037411223344</c> are one person, and if they reach the database as three rows then the
/// per-number rate limit, the unique index on <c>DinerUsers</c> and no-show tracking all quietly
/// stop working.
/// </remarks>
internal static partial class PhoneNumber
{
    /// <summary>
    /// E.164: a plus, a non-zero country code, then up to fourteen more digits.
    /// </summary>
    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex E164();

    /// <summary>
    /// Strips the spaces, dashes and brackets people type, then insists on E.164.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the result is not a valid E.164 number. Deliberately a 400 rather than a
    /// generic sign-in failure: this is a malformed request, and telling the caller so reveals
    /// nothing about who has an account.
    /// </exception>
    public static string Normalise(string? value)
    {
        var trimmed = new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray())
            .Replace("-", string.Empty)
            .Replace("(", string.Empty)
            .Replace(")", string.Empty);

        if (!E164().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "Phone number must be in E.164 format, for example +37411223344.", nameof(value));
        }

        return trimmed;
    }
}
