using System.Text.RegularExpressions;
using Yalla.Domain.Common;

namespace Yalla.Domain.Identity;

/// <summary>
/// What a diner's username, email address and password have to look like.
/// </summary>
/// <remarks>
/// <para>
/// Pure functions over strings, in the domain rather than in the service, for the same reason
/// <see cref="PhoneNumber"/> is: registration, the profile edit and the password change all accept
/// the same three values, and three copies of these rules is how <c>Ani.K</c> comes to be a valid
/// username on one screen and a refused one on the next.
/// </para>
/// <para>
/// <b>Username and email are stored normalised</b> - trimmed and lowercased - so the unique
/// indexes on them do not depend on the database's collation being case-insensitive, and so a
/// sign-in typed as <c>Ani.K</c> finds the account registered as <c>ani.k</c>. The password is
/// never normalised: a trailing space somebody typed is part of the secret they chose.
/// </para>
/// <para>
/// <b>Length is the only rule on the password</b>, plus one exclusion: it may not be the username
/// or the email address, which are the two guesses anybody who knows the account would try first.
/// No composition rules - "one capital, one symbol" measurably pushes people towards
/// <c>Password1!</c>, and the admin panel's passwords say the same in <c>docs/auth.md</c>.
/// </para>
/// <para>
/// Every refusal is an <see cref="ArgumentException"/> naming the <b>wire</b> field, so the API
/// mapper answers 400 with <c>context.field</c> and the sign-up form can put the sentence against
/// the right input.
/// </para>
/// </remarks>
public static partial class DinerAccountRules
{
    /// <summary>Shortest username accepted.</summary>
    public const int UsernameMinLength = 3;

    /// <summary>Longest username accepted. The same number as <see cref="FieldLengths.Username"/>.</summary>
    public const int UsernameMaxLength = FieldLengths.Username;

    /// <summary>Shortest password accepted.</summary>
    public const int PasswordMinLength = 8;

    /// <summary>
    /// Longest password accepted. A ceiling because the hash is deliberately slow, and a megabyte
    /// of password is a denial-of-service request rather than a strong secret.
    /// </summary>
    public const int PasswordMaxLength = 128;

    /// <summary>
    /// Lowercase letters, digits, dots and underscores, starting with a letter or a digit. Length
    /// is checked separately so the message can say which rule broke.
    /// </summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9._]*$")]
    private static partial Regex UsernameShape();

    /// <summary>
    /// One <c>@</c>, something on both sides of it, and a dot in the domain. The same test a
    /// browser's <c>type=email</c> applies; anything stricter refuses real addresses, and whether
    /// the mailbox exists is not something a regular expression can know.
    /// </summary>
    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")]
    private static partial Regex EmailShape();

    /// <summary>
    /// Trims and lowercases a username, then insists on the shape above.
    /// </summary>
    /// <param name="value">What the caller sent.</param>
    /// <param name="paramName">The wire field name to blame, camelCased - see <see cref="PhoneNumber.Normalise"/>.</param>
    /// <exception cref="ArgumentException">Missing, too short, too long, or the wrong characters.</exception>
    public static string NormaliseUsername(string? value, string paramName)
    {
        var username = (value ?? string.Empty).Trim().ToLowerInvariant();

        if (username.Length == 0)
        {
            throw new ArgumentException("Choose a username.", paramName);
        }

        if (username.Length < UsernameMinLength || username.Length > UsernameMaxLength)
        {
            throw new ArgumentException(
                $"A username is between {UsernameMinLength} and {UsernameMaxLength} characters.", paramName);
        }

        if (!UsernameShape().IsMatch(username))
        {
            throw new ArgumentException(
                "A username is letters, digits, dots and underscores, and starts with a letter or a digit.",
                paramName);
        }

        return username;
    }

    /// <summary>
    /// Trims and lowercases an email address, then insists it is shaped like one.
    /// </summary>
    /// <param name="value">What the caller sent.</param>
    /// <param name="paramName">The wire field name to blame, camelCased.</param>
    /// <exception cref="ArgumentException">Missing, too long, or not an address.</exception>
    public static string NormaliseEmail(string? value, string paramName)
    {
        var email = (value ?? string.Empty).Trim().ToLowerInvariant();

        if (email.Length == 0)
        {
            throw new ArgumentException("Enter an email address.", paramName);
        }

        if (email.Length > FieldLengths.Email || !EmailShape().IsMatch(email))
        {
            throw new ArgumentException("That is not a valid email address.", paramName);
        }

        return email;
    }

    /// <summary>
    /// Checks a password against the length rule and the two exclusions. Returns it unchanged.
    /// </summary>
    /// <remarks>
    /// Takes the plain password and gives it straight back: this is the one place in the domain
    /// that sees one, and it stores nothing. The caller hashes what comes out.
    /// </remarks>
    /// <param name="password">The password as typed. Never trimmed.</param>
    /// <param name="username">The account's normalised username, or null when it has none.</param>
    /// <param name="email">The account's normalised email, or null when it has none.</param>
    /// <param name="paramName">The wire field name to blame, camelCased.</param>
    /// <exception cref="ArgumentException">Too short, too long, or equal to the username or the email.</exception>
    public static string CheckPassword(string? password, string? username, string? email, string paramName)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Choose a password.", paramName);
        }

        if (password.Length < PasswordMinLength || password.Length > PasswordMaxLength)
        {
            throw new ArgumentException(
                $"A password is between {PasswordMinLength} and {PasswordMaxLength} characters.", paramName);
        }

        // Case-insensitive, because the username and the email are stored lowercased and the
        // guess somebody makes is the account name as they know it, however it was typed.
        if (Matches(password, username) || Matches(password, email))
        {
            throw new ArgumentException("A password cannot be the username or the email address.", paramName);
        }

        return password;
    }

    private static bool Matches(string password, string? identifier) =>
        identifier is not null && string.Equals(password.Trim(), identifier, StringComparison.OrdinalIgnoreCase);
}
