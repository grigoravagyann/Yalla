using System.Text.RegularExpressions;

namespace Yalla.Domain.Common;

/// <summary>
/// Normalises and checks phone numbers in E.164.
/// </summary>
/// <remarks>
/// <para>
/// One canonical form matters more than it sounds: <c>+374 11 22 33 44</c> and <c>+37411223344</c>
/// are one person, and if they reach the database as two rows then the per-number rate limit, the
/// unique index on <c>DinerUsers</c> and no-show tracking all quietly stop working. (A national
/// <c>037411223344</c> with no plus is refused outright, not read as that number.)
/// </para>
/// <para>
/// <b>Two levels.</b> <see cref="Normalise"/> strips what people type and insists on E.164 - the form an
/// account and a branch's contact number are stored in. <see cref="Canonical"/> goes one further with
/// Google's libphonenumber: <c>+374 091 23 45 67</c>, written with the trunk zero a national number carries,
/// is <c>+37491234567</c>. The one-active-booking-per-number rule and a number's no-show history key on
/// <see cref="Canonical"/>, so a zero is not a second number. Sign-in still looks accounts up by
/// <see cref="Normalise"/>'s form: moving stored accounts to the canonical form is its own migration.
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

    private static readonly PhoneNumbers.PhoneNumberUtil Library = PhoneNumbers.PhoneNumberUtil.GetInstance();

    /// <summary>
    /// The one form a number is compared in: E.164 as libphonenumber formats it, which drops a trunk zero
    /// written after the country code (<c>+374091234567</c> is <c>+37491234567</c>). A value the library
    /// cannot read comes back trimmed as it was, so nothing that passed <see cref="Normalise"/> is lost.
    /// </summary>
    /// <param name="e164">A number in E.164, as <see cref="Normalise"/> returns it.</param>
    public static string Canonical(string e164)
    {
        ArgumentNullException.ThrowIfNull(e164);

        var trimmed = e164.Trim();

        try
        {
            var parsed = Library.Parse(trimmed, null);

            return Library.Format(parsed, PhoneNumbers.PhoneNumberFormat.E164);
        }
        catch (PhoneNumbers.NumberParseException)
        {
            return trimmed;
        }
    }

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
