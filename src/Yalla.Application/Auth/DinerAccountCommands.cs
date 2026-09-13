namespace Yalla.Application.Auth;

/// <summary>
/// The sign-up form: an account with a username, an email and a password, and a phone number that
/// is typed rather than proved.
/// </summary>
/// <remarks>
/// Every value arrives as typed. The service normalises the username, the email and the number
/// through the domain's rules and refuses with the field named, so the form can put the sentence
/// against the right input.
/// </remarks>
/// <param name="Username">3-30 characters: letters, digits, dots and underscores. Lowercased on the way in.</param>
/// <param name="Email">An address, trimmed and lowercased on the way in.</param>
/// <param name="Password">8-128 characters, and not the username or the email.</param>
/// <param name="PhoneE164">The number, in E.164. Not verified by registering - the code flow does that.</param>
/// <param name="DisplayName">What the venue calls this person at the door. Required.</param>
/// <param name="LocaleCode">Language for messages: <c>hy</c>, <c>ru</c> or <c>en</c>. Anything else falls back.</param>
public sealed record RegisterDinerCommand(
    string Username,
    string Email,
    string Password,
    string PhoneE164,
    string DisplayName,
    string? LocaleCode);
