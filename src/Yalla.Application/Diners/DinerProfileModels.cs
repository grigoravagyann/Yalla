using Yalla.Application.Media;

namespace Yalla.Application.Diners;

/// <summary>
/// A diner's own account, as their app shows it on the profile screen.
/// </summary>
/// <remarks>
/// Two of these fields exist so the app can say what is <i>missing</i>: <see cref="PhoneVerified"/>
/// false puts a "verify" link next to the number, and <see cref="HasPassword"/> false turns the
/// "change password" section into a "set a password" one. Both are facts about the row, never
/// inferred from which other fields happen to be present.
/// </remarks>
/// <param name="DinerUserId">The account.</param>
/// <param name="Username">Sign-in name, lowercased. Null for an account the code flow created and nobody has filled in.</param>
/// <param name="Email">Sign-in address, lowercased. Null until given.</param>
/// <param name="PhoneE164">The number on the account.</param>
/// <param name="PhoneVerified">Whether a one-time code to that number has ever been passed.</param>
/// <param name="DisplayName">What the venue calls this person.</param>
/// <param name="LocaleCode">Language for messages: <c>hy</c>, <c>ru</c> or <c>en</c>.</param>
/// <param name="HasPassword">Whether a password sign-in exists. False for a code-only account.</param>
/// <param name="Photo">The profile picture's three links, or null when there is none.</param>
public sealed record DinerProfileView(
    Guid DinerUserId,
    string? Username,
    string? Email,
    string PhoneE164,
    bool PhoneVerified,
    string? DisplayName,
    string LocaleCode,
    bool HasPassword,
    PhotoView? Photo);

/// <summary>
/// The profile edit. Only the fields sent change; a null leaves the current value alone.
/// </summary>
/// <remarks>
/// Absent and null are the same thing on the wire, so there is no way to <i>clear</i> a field here
/// - which is right for all three: the name is read out at the door, and the username and email
/// are how the person signs in.
/// </remarks>
/// <param name="DisplayName">A new name, 1-100 characters.</param>
/// <param name="Username">A new sign-in name, under the same rules as registration.</param>
/// <param name="Email">A new sign-in address, under the same rules as registration.</param>
public sealed record UpdateDinerProfileCommand(
    string? DisplayName,
    string? Username,
    string? Email);
