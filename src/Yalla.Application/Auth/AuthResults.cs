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

/// <summary>Which ways of proving a phone number this deployment offers.</summary>
/// <param name="Sms">
/// A one-time code by SMS can be sent: a provider is configured, or this is Development, where the
/// code goes to the log. False means <c>request-code</c> answers <c>sms-unavailable</c>.
/// </param>
/// <param name="Telegram">The Telegram bot is on, so <c>/api/auth/diner/telegram</c> is mapped.</param>
public sealed record PhoneProofChannels(bool Sms, bool Telegram);

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
/// <param name="Role">Coarse permission level: 1 Owner, 6 GeneralManager, 2 Manager (a branch manager), 3 Waiter, 4 Kitchen.</param>
/// <param name="BranchId">The branch this session may act on, and only this one.</param>
/// <param name="Features">
/// The features the platform allows at the branch, in numeric order - read again on every renewal, so a change reaches
/// the tablet within one. Hide what is not listed (table service 5, takeaway 4, guest messages 6, kitchen display 9,
/// staff phone 8, pre-orders 3); the venue's own switches still apply inside it.
/// </param>
public sealed record StaffSessionResult(
    string AccessToken,
    string RenewalToken,
    int ExpiresInSeconds,
    Guid StaffMemberId,
    string FullName,
    StaffRole Role,
    Guid BranchId,
    IReadOnlyList<BranchFeature> Features);

/// <summary>An owner or manager signed in to the admin panel.</summary>
/// <param name="AccessToken">Bearer token for the admin surface.</param>
/// <param name="RefreshToken">Rotating handle, thirty days.</param>
/// <param name="ExpiresInSeconds">Lifetime of <paramref name="AccessToken"/>.</param>
/// <param name="StaffMemberId">Who signed in.</param>
/// <param name="FullName">Display name for the panel.</param>
/// <param name="Role">Coarse permission level: 1 Owner, 6 GeneralManager, 2 Manager (a branch manager), 3 Waiter, 4 Kitchen.</param>
/// <param name="VenueId">The venue this account administers. Null for a platform admin, who administers all of them.</param>
public sealed record VenueUserSignInResult(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    Guid StaffMemberId,
    string FullName,
    StaffRole Role,
    Guid? VenueId);

/// <summary>
/// Where an enrolled device thinks it is, shown on the PIN screen before anybody taps.
/// </summary>
/// <param name="DeviceId">The device row.</param>
/// <param name="ClientDeviceId">The identifier the client minted for itself.</param>
/// <param name="DeviceName">What the manager called it.</param>
/// <param name="BranchId">The one branch it can act on.</param>
/// <param name="BranchName">That branch, in words, because an id on a counter screen means nothing.</param>
/// <param name="VenueName">The venue, for a chain where two branches share a name.</param>
/// <param name="EnrolledAtUtc">When it was set up.</param>
/// <param name="LastSeenAtUtc">When it was last used.</param>
/// <param name="Kind">1 Tablet (the branch's, anybody's PIN), 2 Phone (one person's, only their PIN).</param>
/// <param name="BoundStaffMember">On a phone, the one person whose PIN opens it; absent on a tablet.</param>
/// <param name="Features">
/// The features the platform allows at the branch, in numeric order, for the PIN screen and the app shell before
/// anybody signs in (a kitchen screen without kitchen display 9, a phone without staff phone 8). Absent only from a
/// response built before 2026-10-27.
/// </param>
public sealed record EnrolledDeviceView(
    Guid DeviceId,
    string ClientDeviceId,
    string DeviceName,
    Guid BranchId,
    string BranchName,
    string VenueName,
    DateTime EnrolledAtUtc,
    DateTime? LastSeenAtUtc,
    IReadOnlyList<BranchFeature> Features,
    StaffDeviceKind Kind = StaffDeviceKind.Tablet,
    BoundStaffMemberView? BoundStaffMember = null);

/// <summary>The person a phone belongs to.</summary>
/// <param name="Id">Their staff member id - what <c>POST /api/auth/staff/pin</c> takes on this phone.</param>
/// <param name="FullName">Their name.</param>
/// <param name="Role">Their role: 1 Owner, 6 GeneralManager, 2 Manager, 3 Waiter.</param>
public sealed record BoundStaffMemberView(Guid Id, string FullName, StaffRole Role);

/// <summary>A code that pairs one person's phone to them, as the console shows it in a QR.</summary>
/// <param name="Code">
/// Ten characters, shown as <c>XXXXX-XXXXX</c>. Returned once; only its hash is stored. Case and the dash do not
/// matter when typed.
/// </param>
/// <param name="ExpiresAtUtc">Ten minutes from now. A newer code for the same person replaces this one.</param>
/// <param name="PairingUrl">What the QR carries: the staff app's pairing page with the code in the fragment.</param>
/// <param name="StaffMemberId">Whose phone it pairs.</param>
/// <param name="BranchId">The branch the phone will be bound to.</param>
public sealed record PhonePairingCodeResult(
    string Code,
    DateTime ExpiresAtUtc,
    string PairingUrl,
    Guid StaffMemberId,
    Guid BranchId);

/// <summary>Who a pairing code is for, so the phone can ask "is this you?" before the PIN.</summary>
/// <param name="StaffMemberId">The person.</param>
/// <param name="FullName">Their name.</param>
/// <param name="Role">Their role.</param>
/// <param name="BranchId">The branch the phone will be bound to.</param>
/// <param name="BranchName">That branch.</param>
/// <param name="VenueName">Its venue.</param>
/// <param name="ExpiresAtUtc">When the code stops working.</param>
public sealed record PhonePairingPreview(
    Guid StaffMemberId,
    string FullName,
    StaffRole Role,
    Guid BranchId,
    string BranchName,
    string VenueName,
    DateTime ExpiresAtUtc);

/// <summary>A paired phone: its device token and a session for its person, as enrolment and a PIN would give.</summary>
/// <param name="Device">Exactly what <c>POST /api/auth/staff/enrol</c> answers - keep the device token.</param>
/// <param name="Session">Exactly what <c>POST /api/auth/staff/pin</c> answers - the person is signed in.</param>
public sealed record PhonePairingResult(DeviceEnrolmentResult Device, StaffSessionResult Session);

/// <summary>
/// One person who may tap a PIN on this tablet, as its PIN screen lists them - and whether they are on
/// shift, so the screen can put those people first.
/// </summary>
/// <remarks>
/// <para>
/// The list is readable by anybody standing at a counter holding the tablet, so no phone, email or PIN
/// state is on it - a name to tap, the id the PIN exchange needs, and the hours of the person's
/// current or next shift here, nothing more.
/// </para>
/// <para>
/// <b>The shift is the one at this tablet's branch:</b> the one running now - last night's still
/// running past midnight included - else the next to start within twelve hours, across midnight
/// too. None when every shift is over or further off, at another branch, or a day off. The fields
/// are <c>StaffShiftView</c>'s, so the console and the tablet read a shift the same way.
/// </para>
/// <para>
/// <b>Running is decided on the instants, and the instants reported are the ones decided on.</b>
/// Across a clock change the wall clock repeats or skips an hour; the instants do not. Both are
/// always present with a shift, so a screen left open can re-check <paramref name="OnShiftNow"/>
/// against its own clock and agree.
/// </para>
/// <para>
/// <b>It never narrows the list.</b> Everybody who may sign in here is on it, on shift or not.
/// </para>
/// </remarks>
/// <param name="StaffMemberId">What <c>POST /api/auth/staff/pin</c> takes.</param>
/// <param name="FullName">The name to tap.</param>
/// <param name="Role">Their role, so the screen can group or badge people.</param>
/// <param name="OnShiftNow">A shift at this branch runs now: <paramref name="StartsAtUtc"/> at or before now, <paramref name="EndsAtUtc"/> after it.</param>
/// <param name="StartsAt">The shift's start, branch wall clock; absent with no shift to report.</param>
/// <param name="EndsAt">Its end, branch wall clock, on the next date when <paramref name="EndsNextDay"/>; absent with no shift.</param>
/// <param name="EndsNextDay">The shift ends after midnight - including at exactly 00:00. False with no shift.</param>
/// <param name="StartsAtUtc">
/// The start as an instant, so a screen left open can tell when it begins; absent with no shift. A start in an hour
/// the clocks skip is read on the clock from before the change: 02:30 that night is 03:30 summer time.
/// </param>
/// <param name="EndsAtUtc">The end as an instant, so a screen left open can tell when it is over; absent with no shift.</param>
public sealed record StaffRosterEntry(
    Guid StaffMemberId,
    string FullName,
    Domain.Enums.StaffRole Role,
    bool OnShiftNow,
    TimeOnly? StartsAt,
    TimeOnly? EndsAt,
    bool EndsNextDay,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc);

/// <summary>One enrolled tablet, as the admin panel lists it.</summary>
/// <param name="Id">The device.</param>
/// <param name="Name">What the manager called it.</param>
/// <param name="BranchId">The branch it is bound to.</param>
/// <param name="EnrolledAtUtc">When it redeemed its enrolment code.</param>
/// <param name="LastSeenAtUtc">Last time a token from it was used. Null if never.</param>
/// <param name="IsRevoked">True once a manager killed it. Revocation is permanent; re-enrol instead.</param>
/// <param name="Kind">1 Tablet, 2 Phone.</param>
/// <param name="BoundStaffMember">A phone's person; absent on a tablet.</param>
/// <param name="RevokedAtUtc">When it was revoked or unpaired; absent while live.</param>
/// <param name="Platform">
/// A phone's platform as it said when it paired (<c>1</c> other ... <c>7</c> Linux); absent on a tablet and on a phone
/// paired before 2026-10-26.
/// </param>
/// <param name="Browser">A phone's browser as it said when it paired; absent on a tablet, or when it did not say.</param>
/// <remarks>
/// A phone's <paramref name="Name"/> is what the phone called itself when it paired - its platform and browser in
/// English, such as "iPhone · Safari". The console's Phones tab words the device column from
/// <paramref name="Platform"/> and <paramref name="Browser"/> in its reader's language, and reads the name only for a
/// phone that sent neither.
/// </remarks>
public sealed record StaffDeviceSummary(
    Guid Id,
    string Name,
    Guid BranchId,
    DateTime EnrolledAtUtc,
    DateTime? LastSeenAtUtc,
    bool IsRevoked,
    StaffDeviceKind Kind = StaffDeviceKind.Tablet,
    BoundStaffMemberView? BoundStaffMember = null,
    DateTime? RevokedAtUtc = null,
    StaffDevicePlatform? Platform = null,
    StaffDeviceBrowser? Browser = null);
