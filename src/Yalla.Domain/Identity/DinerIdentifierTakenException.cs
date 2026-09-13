namespace Yalla.Domain.Identity;

/// <summary>
/// A username, email address or phone number already belongs to another diner account.
/// </summary>
/// <remarks>
/// <para>
/// Three codes rather than one "taken", because the sign-up form does three different things
/// with them: a taken username gets a suggestion, a taken email gets "sign in instead", and a
/// phone number that already has an account gets <i>"log in with a code instead"</i> - that
/// account was created by the SMS flow and its owner can prove the number in thirty seconds.
/// </para>
/// <para>
/// <see cref="Field"/> is the wire name of the input to highlight, so the client does not have to
/// map a code back to a field by hand. A <c>DomainStateException</c>, and therefore a 409, because
/// the request is well-formed and the world already holds the value.
/// </para>
/// </remarks>
public sealed class DinerIdentifierTakenException : DomainStateException
{
    /// <summary>The username is somebody else's.</summary>
    public const string UsernameTakenCode = "username-taken";

    /// <summary>The email address is somebody else's.</summary>
    public const string EmailTakenCode = "email-taken";

    /// <summary>The phone number already has an account - offer the code flow.</summary>
    public const string PhoneInUseCode = "phone-in-use";

    private DinerIdentifierTakenException(string code, string field, string message)
        : base(message)
    {
        Code = code;
        Field = field;
    }

    /// <summary>The stable slug the client branches on. Reaches the wire as the error code.</summary>
    public string Code { get; }

    /// <summary>The request field to highlight: <c>username</c>, <c>email</c> or <c>phoneE164</c>.</summary>
    public string Field { get; }

    public static DinerIdentifierTakenException Username() =>
        new(UsernameTakenCode, "username", "That username is taken. Choose another.");

    public static DinerIdentifierTakenException Email() =>
        new(EmailTakenCode, "email", "An account already uses that email address.");

    public static DinerIdentifierTakenException Phone() =>
        new(PhoneInUseCode, "phoneE164", "That phone number already has an account. Sign in with a code instead.");
}
