using System.ComponentModel.DataAnnotations;
using Yalla.Domain.Enums;

namespace Yalla.Api.Endpoints;

/// <summary>Body of <c>POST /api/auth/diner/request-code</c>.</summary>
/// <param name="PhoneE164">
/// The number to send the code to, in E.164 - <c>+37411223344</c>. Spaces, dashes and brackets
/// are stripped before validation, so what a phone keypad produces is accepted.
/// </param>
/// <param name="LocaleCode">
/// Language for the message: <c>hy</c>, <c>ru</c> or <c>en</c>. A regional tag such as
/// <c>hy-AM</c> is accepted; anything unrecognised falls back rather than failing.
/// </param>
public sealed record RequestDinerCodeRequest(
    [Required] string PhoneE164,
    string? LocaleCode = null);

/// <summary>Body of <c>POST /api/auth/diner/verify-code</c>.</summary>
/// <param name="PhoneE164">The number the code was sent to.</param>
/// <param name="Code">The six digits.</param>
/// <param name="LocaleCode">Language to store against the account for future messages.</param>
public sealed record VerifyDinerCodeRequest(
    [Required] string PhoneE164,
    [Required] string Code,
    string? LocaleCode = null);

/// <summary>Starts a Telegram phone verification.</summary>
/// <param name="LocaleCode">Language for the bot's messages and the account, if one is created. Falls back to <c>Accept-Language</c>, then Armenian.</param>
public sealed record StartTelegramVerificationRequest(string? LocaleCode = null);

/// <summary>Polls a Telegram phone verification.</summary>
/// <param name="LocaleCode">Language to store against the account. Falls back to <c>Accept-Language</c>, then Armenian.</param>
public sealed record ClaimTelegramVerificationRequest(string? LocaleCode = null);

/// <summary>Body of <c>POST /api/auth/diner/register</c>.</summary>
/// <remarks>
/// The annotations here reach the schema and nothing else: this route sits outside the validation
/// filter with the other sign-in flows, and the domain rules refuse each field by name with a 400.
/// </remarks>
/// <param name="Username">
/// 3-30 characters - letters, digits, dots and underscores, starting with a letter or a digit.
/// Stored lowercased, unique case-insensitively.
/// </param>
/// <param name="Email">An email address. Stored trimmed and lowercased, unique.</param>
/// <param name="Password">8-128 characters, and not the username or the email. No composition rules.</param>
/// <param name="PhoneE164">
/// The number, in E.164 - <c>+37411223344</c>. <b>Not verified by registering:</b> the account's
/// <c>phoneVerified</c> stays false until a one-time code to it is passed once.
/// </param>
/// <param name="DisplayName">What the venue calls this person at the door. 1-100 characters, required.</param>
/// <param name="LocaleCode">Language for messages: <c>hy</c>, <c>ru</c> or <c>en</c>. Anything else falls back.</param>
public sealed record RegisterDinerRequest(
    [Required][StringLength(30, MinimumLength = 3)] string Username,
    [Required][EmailAddress] string Email,
    [Required][StringLength(128, MinimumLength = 8)] string Password,
    [Required] string PhoneE164,
    [Required][StringLength(100, MinimumLength = 1)] string DisplayName,
    string? LocaleCode = null);

/// <summary>Body of <c>POST /api/auth/diner/login</c>.</summary>
/// <param name="Identifier">A username or an email address, case-insensitively.</param>
/// <param name="Password">The password.</param>
/// <param name="LocaleCode">
/// Language to store against the account: <c>hy</c>, <c>ru</c> or <c>en</c>. Left alone when
/// absent or unrecognised - the account already knows its language.
/// </param>
public sealed record LoginDinerRequest(
    [Required] string Identifier,
    [Required] string Password,
    string? LocaleCode = null);

/// <summary>Body of the two refresh endpoints and of sign-out.</summary>
/// <param name="RefreshToken">
/// The handle from the last sign-in or refresh. It is spent by this call: use the one in the
/// response next time. Sending a spent handle revokes the whole chain.
/// </param>
public sealed record RefreshTokenRequest([Required] string RefreshToken);

/// <summary>Body of <c>POST /api/auth/staff/enrol</c>.</summary>
/// <param name="Code">The one-time enrolment code a manager generated for this branch.</param>
/// <param name="DeviceId">
/// The identifier the tablet minted for itself, stable across reinstalls. Enrolling the same one
/// twice on a branch is refused as a client bug rather than as a credential problem - the tablet is
/// already enrolled and holding a token it is not using.
/// </param>
/// <param name="DeviceName">
/// What the tablet should be called in the admin panel - "Bar tablet", "Terrace". A manager
/// revoking a lost device picks it out of a list by this name.
/// </param>
public sealed record RedeemEnrolmentCodeRequest(
    [Required] string Code,
    [Required][StringLength(128, MinimumLength = 8)] string DeviceId,
    [Required][StringLength(100, MinimumLength = 1)] string DeviceName);

/// <summary>Body of <c>POST /api/auth/staff/pin</c>.</summary>
/// <param name="StaffMemberId">Whose PIN is being tapped. The tablet lists the branch's staff.</param>
/// <param name="Pin">The four digits. Never logged and never stored in the clear.</param>
public sealed record StaffPinRequest(
    [Required] Guid StaffMemberId,
    [Required][StringLength(12, MinimumLength = 4)] string Pin);

/// <summary>Body of <c>POST /api/auth/staff/renew</c> and <c>/api/auth/staff/sign-out</c>.</summary>
/// <param name="RenewalToken">
/// The handle from the last PIN sign-in or renewal. Rotates on every use, and stops working after
/// thirty minutes of inactivity.
/// </param>
public sealed record RenewStaffSessionRequest([Required] string RenewalToken);

/// <summary>Body of <c>POST /api/auth/venue/sign-in</c>.</summary>
/// <param name="Email">The address on the account.</param>
/// <param name="Password">The password.</param>
public sealed record VenueUserSignInRequest(
    [Required][EmailAddress] string Email,
    [Required] string Password);

/// <summary>Body of <c>POST /api/auth/venue/request-password-reset</c>.</summary>
/// <param name="Email">The address to send the link to.</param>
/// <param name="LocaleCode">Language for the email: <c>hy</c>, <c>ru</c> or <c>en</c>.</param>
public sealed record RequestPasswordResetRequest(
    [Required][EmailAddress] string Email,
    string? LocaleCode = null);

/// <summary>Body of <c>POST /api/auth/venue/reset-password</c>.</summary>
/// <param name="ResetToken">The single-use handle from the emailed link.</param>
/// <param name="NewPassword">
/// The new password. Only a minimum length is enforced - no composition rules, which push people
/// towards predictable substitutions and towards writing the result down.
/// </param>
public sealed record ResetPasswordRequest(
    [Required] string ResetToken,
    [Required] string NewPassword);

/// <summary>Body of <c>POST /api/branches/{branchId}/devices/enrolment-codes</c>.</summary>
/// <remarks>Empty: the branch is in the route and the manager is in the token.</remarks>
public sealed record CreateEnrolmentCodeRequest;

/// <summary>Body of <c>POST /api/auth/staff/phone-pair/preview</c>.</summary>
/// <param name="Code">The pairing code from the QR's <c>#code=</c> or typed off the console. Case and dashes do not matter.</param>
public sealed record PhonePairPreviewRequest([Required] string Code);

/// <summary>Body of <c>POST /api/auth/staff/phone-pair</c>.</summary>
/// <param name="Code">The pairing code the manager showed, from the QR or typed.</param>
/// <param name="Pin">The person's own PIN - the one they tap on the tablet. Never logged, never stored in the clear.</param>
/// <param name="ClientDeviceId">
/// The identifier this browser minted for itself and keeps, as a tablet's <c>deviceId</c>. Pairing again with the same
/// one reuses the phone rather than adding a row.
/// </param>
/// <param name="DeviceName">
/// The platform and browser in a phrase, such as "iPhone · Safari": what the console's Phones tab shows for a phone that
/// did not send <paramref name="Platform"/>, and the name its History entries keep.
/// </param>
/// <param name="Platform">
/// What the phone runs on, read from its own user agent: <c>1</c> other, <c>2</c> iPhone, <c>3</c> iPad, <c>4</c>
/// Android, <c>5</c> Windows, <c>6</c> Mac, <c>7</c> Linux. The console words the device from this and
/// <paramref name="Browser"/> in its reader's language. Optional for an app that predates it; a number this list does
/// not name is kept as nothing rather than refused (the sign-in routes are outside the validation filter).
/// </param>
/// <param name="Browser">
/// Its browser: <c>1</c> Safari, <c>2</c> Chrome, <c>3</c> Edge, <c>4</c> Firefox, <c>5</c> Opera, <c>6</c> Samsung
/// Internet. Absent when the phone does not recognise it.
/// </param>
public sealed record PhonePairRequest(
    [Required] string Code,
    [Required][StringLength(12, MinimumLength = 4)] string Pin,
    [Required][StringLength(128, MinimumLength = 8)] string ClientDeviceId,
    [Required][StringLength(100, MinimumLength = 1)] string DeviceName,
    [EnumDataType(typeof(StaffDevicePlatform))] StaffDevicePlatform? Platform = null,
    [EnumDataType(typeof(StaffDeviceBrowser))] StaffDeviceBrowser? Browser = null);
