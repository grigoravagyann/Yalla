using Yalla.Domain.Enums;

namespace Yalla.Application.Auth;

/// <summary>
/// What <c>request-code</c> answers, whether or not the number has an account.
/// </summary>
/// <remarks>
/// Identical for a known and an unknown phone number, by design. A response that differed would
/// turn this endpoint into a way to ask "does this person use Yalla", which is exactly the
/// question a stranger with a phone book should not be able to answer.
/// </remarks>
/// <param name="ExpiresInSeconds">How long the code stays usable.</param>
/// <param name="MaxAttempts">How many wrong guesses the code tolerates before it dies.</param>
/// <param name="DevelopmentCode">
/// The code itself, returned <b>only</b> in Development so the flow can be exercised with no SMS
/// provider wired up. Always null in any other environment.
/// </param>
public sealed record VerificationCodeRequestResult(
    int ExpiresInSeconds,
    int MaxAttempts,
    string? DevelopmentCode);

/// <summary>Tokens issued to a diner who verified their phone number.</summary>
/// <param name="AccessToken">Bearer token for the diner surface. Short-lived.</param>
/// <param name="RefreshToken">Rotating handle. Send it once; the response carries its successor.</param>
/// <param name="ExpiresInSeconds">Lifetime of <paramref name="AccessToken"/>.</param>
/// <param name="DinerUserId">The account, created on first successful verification.</param>
/// <param name="IsNewAccount">True when this verification created the account.</param>
public sealed record DinerSignInResult(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    Guid DinerUserId,
    bool IsNewAccount);

/// <summary>A refreshed access token and the refresh token that replaces the one just spent.</summary>
/// <param name="AccessToken">The new bearer token.</param>
/// <param name="RefreshToken">The successor handle. The one you sent is now dead.</param>
/// <param name="ExpiresInSeconds">Lifetime of <paramref name="AccessToken"/>.</param>
public sealed record RefreshResult(string AccessToken, string RefreshToken, int ExpiresInSeconds);

/// <summary>
/// The tab-scoped token handed to someone who scanned a QR code or redeemed a join token.
/// </summary>
/// <remarks>
/// There is no refresh token here and no account behind it. The token is valid for one tab and
/// dies with it.
/// </remarks>
/// <param name="AccessToken">Bearer token valid only for <paramref name="TabId"/>.</param>
/// <param name="ExpiresAtUtc">When it stops working - the tab's close, plus a receipt grace period.</param>
/// <param name="ParticipantId">This person's row on the tab.</param>
/// <param name="TabId">The one tab this token can touch.</param>
/// <param name="BranchId">The branch the tab is in.</param>
/// <param name="DisplayName">What the host sees. Changeable, and not an account.</param>
/// <param name="CanOrder">Whether this participant may add items.</param>
public sealed record TabParticipantTokenResult(
    string AccessToken,
    DateTime ExpiresAtUtc,
    Guid ParticipantId,
    Guid TabId,
    Guid BranchId,
    string DisplayName,
    bool CanOrder);

/// <summary>A one-time code a manager reads out to a tablet being enrolled.</summary>
/// <param name="Code">The code. Shown once, never retrievable again - only its hash is stored.</param>
/// <param name="ExpiresAtUtc">When it stops being redeemable.</param>
/// <param name="BranchId">The branch the resulting device will be bound to.</param>
public sealed record DeviceEnrolmentCodeResult(string Code, DateTime ExpiresAtUtc, Guid BranchId);

/// <summary>What a tablet gets back for a redeemed enrolment code.</summary>
/// <param name="DeviceToken">
/// Long-lived bearer token identifying the tablet. It can do exactly one thing: offer a PIN.
/// </param>
/// <param name="DeviceId">The enrolled device, revocable from the admin panel.</param>
/// <param name="BranchId">The one branch this tablet is bound to.</param>
/// <param name="DeviceName">The name the manager gave it.</param>
/// <param name="ExpiresAtUtc">When the device token itself expires and the tablet must re-enrol.</param>
public sealed record DeviceEnrolmentResult(
    string DeviceToken,
    Guid DeviceId,
    Guid BranchId,
    string DeviceName,
    DateTime ExpiresAtUtc);

/// <summary>A staff member's session on a tablet, opened by a PIN.</summary>
/// <param name="AccessToken">Bearer token carrying the staff member, their role and their branch.</param>
/// <param name="RenewalToken">
/// Handle the tablet exchanges to stay signed in. Rotates on every use, and stops working after
/// thirty minutes of inactivity - at which point the PIN is needed again.
/// </param>
/// <param name="ExpiresInSeconds">Lifetime of <paramref name="AccessToken"/>.</param>
/// <param name="StaffMemberId">Who is acting. This is the id that lands in the audit log.</param>
/// <param name="FullName">Shown on the tablet so the waiter can see whose session is open.</param>
/// <param name="Role">Coarse permission level: 1 Owner, 2 Manager, 3 Waiter, 4 Kitchen.</param>
/// <param name="BranchId">The branch this session may act on, and only this one.</param>
public sealed record StaffSessionResult(
    string AccessToken,
    string RenewalToken,
    int ExpiresInSeconds,
    Guid StaffMemberId,
    string FullName,
    StaffRole Role,
    Guid BranchId);

/// <summary>An owner or manager signed in to the admin panel.</summary>
/// <param name="AccessToken">Bearer token for the admin surface.</param>
/// <param name="RefreshToken">Rotating handle, thirty days.</param>
/// <param name="ExpiresInSeconds">Lifetime of <paramref name="AccessToken"/>.</param>
/// <param name="StaffMemberId">Who signed in.</param>
/// <param name="FullName">Display name for the panel.</param>
/// <param name="Role">Coarse permission level: 1 Owner, 2 Manager, 3 Waiter, 4 Kitchen.</param>
/// <param name="VenueId">The venue this account administers.</param>
public sealed record VenueUserSignInResult(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    Guid StaffMemberId,
    string FullName,
    StaffRole Role,
    Guid VenueId);

/// <summary>One enrolled tablet, as the admin panel lists it.</summary>
/// <param name="Id">The device.</param>
/// <param name="Name">What the manager called it.</param>
/// <param name="BranchId">The branch it is bound to.</param>
/// <param name="EnrolledAtUtc">When it redeemed its enrolment code.</param>
/// <param name="LastSeenAtUtc">Last time a token from it was used. Null if never.</param>
/// <param name="IsRevoked">True once a manager killed it. Revocation is permanent; re-enrol instead.</param>
public sealed record StaffDeviceSummary(
    Guid Id,
    string Name,
    Guid BranchId,
    DateTime EnrolledAtUtc,
    DateTime? LastSeenAtUtc,
    bool IsRevoked);
