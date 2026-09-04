using System.Security.Cryptography;
using System.Text;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Generating and hashing the opaque secrets this system hands out.
/// </summary>
/// <remarks>
/// <para>
/// Two different hashing jobs live in this codebase and they need different tools, which is worth
/// stating plainly because using the wrong one is invisible until it matters.
/// </para>
/// <list type="bullet">
/// <item>
/// <b>High-entropy secrets</b> - refresh handles, enrolment codes, reset links - are 128 bits or
/// more of randomness, so guessing is already impossible and the only requirement is that a
/// database dump is useless. <see cref="Hash"/> (SHA-256) is right, and being fast is a feature:
/// these are looked up <i>by</i> their hash, which a salted hash could not do.
/// </item>
/// <item>
/// <b>Low-entropy secrets chosen or typed by a person</b> - passwords, four-digit PINs, six-digit
/// codes - are guessable by construction, so they need a deliberately slow, salted hash.
/// Those go through <c>PasswordHasher&lt;T&gt;</c> and are always verified against a row found by
/// some other key.
/// </item>
/// </list>
/// </remarks>
internal static class Secrets
{
    /// <summary>
    /// Alphabet for codes a human reads aloud or copies off a screen. No <c>I</c>, <c>L</c>,
    /// <c>O</c>, <c>U</c> or digits that look like them - a manager reading an enrolment code
    /// across a bar should not have to say "letter O, not zero".
    /// </summary>
    private const string ReadableAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    /// <summary>A URL-safe 256-bit random token, for anything a machine holds.</summary>
    public static string NewOpaqueToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// A one-time enrolment code a person types into a tablet. Ten characters of the readable
    /// alphabet is about 49 bits, which is far more than a single-use, day-long code needs.
    /// </summary>
    public static string NewEnrolmentCode() =>
        RandomNumberGenerator.GetString(ReadableAlphabet, 10);

    /// <summary>
    /// A uniformly random decimal code of <paramref name="digits"/> digits, leading zeros kept.
    /// </summary>
    /// <remarks>
    /// <c>RandomNumberGenerator</c> and not <c>Random</c>: a predictable verification code is the
    /// same as no verification code, and the two APIs are one character apart.
    /// </remarks>
    public static string NewNumericCode(int digits = 6)
    {
        var builder = new StringBuilder(digits);

        for (var i = 0; i < digits; i++)
        {
            builder.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        }

        return builder.ToString();
    }

    /// <summary>Hex SHA-256, for looking a high-entropy secret up without storing it.</summary>
    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
