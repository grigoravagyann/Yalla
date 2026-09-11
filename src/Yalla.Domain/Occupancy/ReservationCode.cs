using System.Security.Cryptography;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// The short code the diner quotes at the door.
/// </summary>
/// <remarks>
/// <para>
/// Two people read this out loud: a diner on the phone and a waiter off a tablet. So the alphabet
/// drops every character that survives print but not speech or a second script - <c>0</c> against
/// <c>O</c>, and <c>1</c> against <c>I</c> and <c>L</c>. Uppercase only, because a code that is
/// case-sensitive is a code somebody will get wrong.
/// </para>
/// <para>
/// It is not a secret and must not be treated as one: it is short enough to guess, so nothing is
/// ever authorised by quoting it. It is an identifier a human can say. Uniqueness is enforced by
/// the unique index on <c>Reservations.Code</c>, not by hoping; the generator is random and the
/// caller retries on the rare collision.
/// </para>
/// <para>
/// 31 characters over 6 places is about 887 million codes, which is far past what a branch will
/// ever issue while staying short enough to say in one breath.
/// </para>
/// </remarks>
public static class ReservationCode
{
    /// <summary>
    /// Uppercase letters and digits with <c>0</c>, <c>O</c>, <c>1</c>, <c>I</c> and <c>L</c>
    /// removed.
    /// </summary>
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>Short enough to say in one breath, long enough not to collide.</summary>
    public const int DefaultLength = 6;

    /// <summary>A fresh code.</summary>
    /// <remarks>
    /// Cryptographically random rather than sequential. A guessable sequence would let anyone
    /// enumerate the evening's bookings, and a random one costs nothing here.
    /// </remarks>
    public static string Generate(int length = DefaultLength)
    {
        if (length is < 4 or > Common.FieldLengths.ReservationCode)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"A reservation code must be between 4 and {Common.FieldLengths.ReservationCode} characters.");
        }

        return new string(RandomNumberGenerator.GetItems<char>(Alphabet, length));
    }

    /// <summary>Whether a code uses only characters this generator can produce.</summary>
    public static bool IsWellFormed(string? code) =>
        !string.IsNullOrEmpty(code) && code.All(Alphabet.Contains);

    /// <summary>
    /// A code as somebody typed it, in the form it is stored: every space and every dash removed,
    /// upper-cased.
    /// </summary>
    /// <remarks>
    /// <para>
    /// People type a code the way they read it off a screen or hear it at the door - "dfj-fqy",
    /// "DFJ FQY", with the space a keyboard adds after the last word. None of those characters can be
    /// part of a code (see <see cref="Alphabet"/>), so dropping them never turns one code into
    /// another; and every code is stored upper-case, so upper-casing the input matches by the stored
    /// rule rather than by whatever the column's collation happens to be.
    /// </para>
    /// <para>
    /// Every Unicode dash, not only the hyphen-minus: a phone keyboard or a code copied out of a
    /// message hands over an en dash just as easily, and it means the same to the person typing it.
    /// </para>
    /// </remarks>
    public static string Normalise(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return string.Empty;
        }

        var kept = code.Where(c =>
            !char.IsWhiteSpace(c)
            && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.DashPunctuation);

        return new string(kept.ToArray()).ToUpperInvariant();
    }
}
