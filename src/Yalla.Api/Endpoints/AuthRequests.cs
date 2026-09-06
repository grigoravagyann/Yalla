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
