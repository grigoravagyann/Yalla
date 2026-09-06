using System.Text.RegularExpressions;

namespace Yalla.Domain.Common;

/// <summary>
/// Normalises and checks phone numbers in E.164.
/// </summary>
/// <remarks>
/// <para>
/// One canonical form matters more than it sounds: <c>+374 11 22 33 44</c>, <c>+37411223344</c>
/// and <c>037411223344</c> are one person, and if they reach the database as three rows then the
/// per-number rate limit, the unique index on <c>DinerUsers</c> and no-show tracking all quietly
/// stop working.
/// </para>
/// <para>
/// <b>In the domain rather than in Identity, where it started.</b> A diner's number and a branch's
/// published contact number are the same kind of value and must be normalised the same way; a
/// second copy of this regex living beside <see cref="Venues.Branch"/> is how the two would come to
/// disagree about whether <c>+374 11 22 33 44</c> is valid.
/// </para>
/// </remarks>
public static partial class PhoneNumber
{
    /// <summary>
    /// E.164: a plus, a non-zero country code, then up to fourteen more digits.
    /// </summary>
    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex E164();

    /// <summary>
    /// Strips the spaces, dashes and brackets people type, then insists on E.164.
    /// </summary>
    /// <param name="value">What the caller sent.</param>
    /// <param name="paramName">
    /// The <b>wire</b> field name to blame, camelCased. Required rather than defaulted: the API
    /// mapper turns this into <c>context.field</c> and drops anything that is not camelCase, so a
    /// call site that let it default to the C# parameter name would silently produce a refusal a
    /// form cannot place against an input.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the result is not a valid E.164 number. Deliberately a 400 rather than a
    /// generic failure: this is a malformed request, and telling the caller so reveals nothing
    /// about who has an account.
    /// </exception>
    public static string Normalise(string? value, string paramName)
    {
        var trimmed = new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray())
            .Replace("-", string.Empty)
            .Replace("(", string.Empty)
            .Replace(")", string.Empty);

        if (!E164().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "Phone number must be in E.164 format, for example +37411223344.", paramName);
        }

        return trimmed;
    }
}
