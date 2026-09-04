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
}
