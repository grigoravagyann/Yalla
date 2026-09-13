using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Salted, slow hashing for the secrets a person chooses or types: passwords, PINs and one-time
/// codes.
/// </summary>
/// <remarks>
/// <para>
/// A thin wrapper over <see cref="PasswordHasher{TUser}"/>, which is the only piece of ASP.NET
/// Core Identity this system uses. Wrapping it is not ceremony: the framework signature takes a
/// user object it then ignores, and every call site would otherwise have to pass a
/// <c>null!</c> and hope that stays true. It also puts the choice of algorithm in one place, and
/// gives the "needs rehashing" answer somewhere to live.
/// </para>
/// <para>
/// This is the slow path on purpose, and it is the correct one only for low-entropy secrets. High
/// entropy secrets - refresh handles, enrolment codes - go through <see cref="Secrets.Hash"/>
/// instead; see the note there.
/// </para>
/// </remarks>
internal sealed class SecretHasher
{
    /// <summary>
    /// The user argument <see cref="PasswordHasher{TUser}"/> takes and never reads. One instance,
    /// so nothing allocates per hash.
    /// </summary>
    private static readonly object Unused = new();

    private readonly PasswordHasher<object> _hasher = new();

    private readonly Lazy<string> _decoy;

    public SecretHasher() =>
        _decoy = new Lazy<string>(() => Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));

    /// <summary>
    /// A hash nothing will ever match, to verify against when there is no usable account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sign-in form that answers every failure with one message is not yet an answer that costs
    /// the same every time. Only a real account with a password reaches the slow hash, so an unknown
    /// address answers in the time of one indexed read and a real one in the tens of milliseconds
    /// PBKDF2 costs - a difference comfortably measurable over the internet, which turns the form
    /// into an oracle for "does this address have an account here", the exact question the shared
    /// message exists to refuse. Verifying against this when there is nothing else makes every
    /// rejection cost the same. Both password sign-ins - the admin panel's and the diner's - use it.
    /// </para>
    /// <para>
    /// Random per process rather than a constant, so the hash is never a recognisable value, and
    /// lazy so the PBKDF2 cost of building it is paid on first sign-in rather than at startup.
    /// </para>
    /// </remarks>
    public string DecoyHash => _decoy.Value;

    /// <summary>Hashes a password, PIN or code.</summary>
    public string Hash(string secret) => _hasher.HashPassword(Unused, secret);

    /// <summary>Checks a secret against a stored hash.</summary>
    /// <returns>
    /// True when it matches. <c>needsRehash</c> is true when the stored hash used an older
    /// iteration count and the caller should quietly re-store it.
    /// </returns>
    public (bool Matches, bool NeedsRehash) Verify(string hash, string? secret)
    {
        var result = _hasher.VerifyHashedPassword(Unused, hash, secret ?? string.Empty);

        return result switch
        {
            PasswordVerificationResult.Success => (true, false),
            PasswordVerificationResult.SuccessRehashNeeded => (true, true),
            _ => (false, false),
        };
    }
}
