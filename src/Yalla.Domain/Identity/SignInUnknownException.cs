namespace Yalla.Domain.Identity;

/// <summary>
/// The request's access token names no sign-in - it carries no refresh-chain claim - so an action that
/// keeps "this sign-in" and ends the others cannot tell which one to keep. Answers 409
/// <c>sign-in-unknown</c>; nothing has been changed.
/// </summary>
/// <remarks>
/// Every admin-panel token minted today names its chain. One that does not came from before the claim
/// existed, and signing in again gives a token that does. Ending every sign-in instead - the caller's
/// included - would answer a button that says "everywhere else" by signing them out here too.
/// </remarks>
public sealed class SignInUnknownException()
    : DomainStateException("This sign-in cannot be told apart from your others. Sign in again, then try once more.");
