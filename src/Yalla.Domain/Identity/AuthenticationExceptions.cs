namespace Yalla.Domain.Identity;

/// <summary>
/// A credential was not accepted.
/// </summary>
/// <remarks>
/// <para>
/// One type for every way a sign-in can fail, because the caller must not be able to tell them
/// apart. "No account with that email" and "wrong password" are the same answer, or the sign-in
/// form becomes an account-enumeration oracle.
/// </para>
/// <para>
/// <see cref="ReasonCode"/> is the exception: it distinguishes only failures the caller is
/// already entitled to know about - their own code expired, their own code was mistyped - and
/// never which accounts exist.
/// </para>
/// </remarks>
public class AuthenticationFailedException(string reasonCode, string message) : Exception(message)
{
    /// <summary>Stable kebab-case slug the client branches on. Reaches the wire as the error code.</summary>
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>
/// Sign-in is refused because the account is locked out, not because the credential was wrong.
/// </summary>
/// <remarks>
/// Distinct from a wrong PIN on purpose: this one has to be visible, because the fix is a manager
/// clearing it rather than the waiter trying harder. Reporting it as a generic failure would have
/// someone standing at a tablet retyping a PIN that was correct all along.
/// </remarks>
public sealed class AccountLockedException(DateTime? lockedUntilUtc, string message)
    : AuthenticationFailedException("account-locked", message)
{
    /// <summary>When the lockout lapses on its own. Null when only a manager can clear it.</summary>
    public DateTime? LockedUntilUtc { get; } = lockedUntilUtc;
}

/// <summary>
/// The attempt limit on a one-time credential is spent, so nothing will be checked against it
/// again. Ask for a new code.
/// </summary>
public sealed class TooManyAttemptsException(string message)
    : AuthenticationFailedException("too-many-attempts", message);

/// <summary>
/// A one-time code was not accepted, and how many tries are left on the number's live code.
/// </summary>
/// <remarks>
/// <para>
/// The same slug as before, <c>verification-code-invalid</c>, so nothing that branched on it
/// breaks; the count rides along in the problem's <c>context</c>. The diner app used to read a count
/// the server never sent, default it to zero, and tell a diner their code was spent after one slip -
/// so they asked for another and spent the hourly per-number budget instead.
/// </para>
/// <para>
/// <b>Zero when nothing is live for the number</b>: none was asked for, it was used, or a newer one
/// replaced it. That is the client's cue to offer a new code rather than a retry. Whether a number
/// has a code outstanding was already visible to an anonymous caller - that case always carried its
/// own sentence, and a live code answers 429 after five tries - so the count publishes nothing new.
/// </para>
/// </remarks>
public sealed class VerificationCodeInvalidException(int attemptsRemaining, string message)
    : AuthenticationFailedException("verification-code-invalid", message)
{
    /// <summary>Tries left on the live code. Zero when there is none, or the last one was just spent.</summary>
    public int AttemptsRemaining { get; } = attemptsRemaining;
}
