using System.ComponentModel.DataAnnotations;

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

/// <summary>Body of the two refresh endpoints and of sign-out.</summary>
/// <param name="RefreshToken">
/// The handle from the last sign-in or refresh. It is spent by this call: use the one in the
/// response next time. Sending a spent handle revokes the whole chain.
/// </param>
public sealed record RefreshTokenRequest([Required] string RefreshToken);

/// <summary>Body of <c>POST /api/auth/tab/scan</c>.</summary>
/// <param name="QrToken">The token printed in the QR code on the table.</param>
/// <param name="DeviceId">
/// A stable identifier the app generates once per install. Not an account and not a login - it is
/// what lets someone who re-scans after their phone locked land back on the same participant
/// instead of appearing twice on the bill.
/// </param>
/// <param name="DisplayName">Optional. What the host sees; defaults to a numbered guest.</param>
public sealed record ScanTableQrRequest(
    [Required] string QrToken,
    [Required] string DeviceId,
    string? DisplayName = null);

/// <summary>Body of <c>POST /api/auth/tab/join</c>.</summary>
/// <param name="JoinToken">The invitation passed round the table. Short-lived.</param>
/// <param name="DeviceId">See <see cref="ScanTableQrRequest.DeviceId"/>.</param>
/// <param name="DisplayName">Optional display name.</param>
public sealed record JoinTabRequest(
    [Required] string JoinToken,
    [Required] string DeviceId,
    string? DisplayName = null);

/// <summary>Body of <c>PUT /api/tabs/{tabId}/me/display-name</c>.</summary>
/// <param name="DisplayName">What the host should see instead of "Guest 3".</param>
public sealed record SetDisplayNameRequest(
    [Required][StringLength(100, MinimumLength = 1)] string DisplayName);

/// <summary>Body of <c>POST /api/auth/staff/enrol</c>.</summary>
/// <param name="Code">The one-time enrolment code a manager generated for this branch.</param>
/// <param name="DeviceName">
/// What the tablet should be called in the admin panel - "Bar tablet", "Terrace". A manager
/// revoking a lost device picks it out of a list by this name.
/// </param>
public sealed record RedeemEnrolmentCodeRequest(
    [Required] string Code,
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
