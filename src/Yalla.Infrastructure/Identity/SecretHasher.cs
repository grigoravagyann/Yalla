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
