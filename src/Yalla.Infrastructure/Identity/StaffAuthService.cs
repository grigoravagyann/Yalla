using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Auth;
using Yalla.Application.Reservations;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Device enrolment and PIN sign-in for the staff tablet.
/// </summary>
/// <remarks>
/// The shape of this flow is set by one constraint: a waiter must never type an email address
/// during a Friday rush. So the tablet holds a long-lived token that identifies the <i>device and
/// its branch</i>, and a person becomes present by tapping four digits on top of it.
/// </remarks>
internal sealed partial class StaffAuthService(
    YallaDbContext db,
    IClock clock,
    TokenIssuer tokens,
    SecretHasher hasher,
    ITokenAuthorityCheck authority,
    IOptions<AuthOptions> authOptions,
    ICurrentActor actor,
    IActivityLog activity,
    ILogger<StaffAuthService> logger) : IStaffAuthService
{
    /// <summary>The audit action for issuing somebody a phone pairing code.</summary>
    public const string PhonePairingIssuedAuditAction = "staff.phone-pairing-issued";

    /// <summary>The audit action for unpairing somebody's phone - from the console, or from the phone itself.</summary>
    public const string PhoneUnpairedAuditAction = "staff.phone-unpaired";

    private readonly AuthOptions _options = authOptions.Value;

    /// <summary>
    /// Generic answer for every way a PIN sign-in can fail that is not a lockout. Wrong PIN,
    /// unknown staff member, someone from another branch - all the same sentence, so a tablet
    /// cannot be used to enumerate who works where.
    /// </summary>
    private static AuthenticationFailedException PinRejected() =>
        new("pin-invalid", "That PIN was not recognised.");

    public async Task<DeviceEnrolmentCodeResult> CreateEnrolmentCodeAsync(
        Guid branchId,
        Guid createdByStaffMemberId,
        CancellationToken cancellationToken = default)
    {
        var branch = await db.Branches
            .Where(b => b.Id == branchId)
            .Select(b => new { b.Id, b.VenueId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} does not exist.");

        var code = Secrets.NewEnrolmentCode();

        var entity = new StaffEnrolmentCode(
            branch.VenueId, branch.Id, Secrets.Hash(code), createdByStaffMemberId, clock.UtcNow);

        db.StaffEnrolmentCodes.Add(entity);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Device, ActivityActions.DeviceEnrolmentCodeCreated, ActivitySubjects.Branch, branch.Id)
            {
                BranchId = branch.Id,
                Summary = new { expiresAtUtc = entity.ExpiresAtUtc },
            },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Enrolment code issued for branch {BranchId} by staff member {StaffMemberId}.",
            branchId, createdByStaffMemberId);

        // The only time the plaintext exists. Only its hash was stored, so a manager who loses it
        // issues another rather than looking it up.
        return new DeviceEnrolmentCodeResult(code, entity.ExpiresAtUtc, branch.Id);
    }

    public async Task<DeviceEnrolmentResult> RedeemEnrolmentCodeAsync(
        string code,
        string clientDeviceId,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;
        var hash = Secrets.Hash(Secrets.NormalizeEnrolmentCode(code));

        var enrolment = await db.StaffEnrolmentCodes
            .FirstOrDefaultAsync(c => c.CodeHash == hash, cancellationToken);

        // A phone pairing code names one person and pairs only their phone; enrolling a shared tablet with it
        // would turn a code shown to one waiter into a device anybody's PIN opens. Answered as unknown.
        if (enrolment is null || enrolment.IsPhonePairing)
        {
            throw new AuthenticationFailedException(
                "enrolment-code-invalid", "That enrolment code is not valid.");
        }

        // Already spent is reported as a conflict rather than a bad credential, because it is a
        // different thing and the manager needs to know which: a code that never worked was
        // mistyped, a code that worked once means somebody already enrolled a device with it.
        if (enrolment.RedeemedAtUtc is not null)
        {
            throw new DomainStateException(
                "That enrolment code has already been used. Generate a new one, and check the "
                + "branch's device list for a tablet you did not enrol.");
        }

        if (!enrolment.IsUsableAt(nowUtc))
        {
            throw new AuthenticationFailedException(
                "enrolment-code-expired", "That enrolment code has expired. Ask for a new one.");
        }

        // A browser that is already enrolled here should be offering its PIN screen, not enrolling
        // again. Told apart from a mistyped code deliberately: this one means the client has state
        // it is not using, which is a client bug worth surfacing rather than a credential problem.
        var alreadyHere = await db.StaffDevices.AnyAsync(
            d => d.BranchId == enrolment.BranchId
                 && d.ClientDeviceId == clientDeviceId
                 && d.RevokedAtUtc == null,
            cancellationToken);

        if (alreadyHere)
        {
            throw new DomainStateException(
                "This device is already enrolled at that branch. Use the PIN screen rather than "
                + "enrolling again, or ask a manager to revoke it first.");
        }

        var device = new StaffDevice(
            enrolment.VenueId, enrolment.BranchId, deviceName, clientDeviceId, nowUtc);

        // Concurrency token on RedeemedAtUtc: if another tablet redeemed this code between the
        // read above and here, this UPDATE matches no rows and the whole transaction - including
        // the device insert - is rolled back.
        enrolment.Redeem(device.Id, nowUtc);

        db.StaffDevices.Add(device);

        // Nobody is signed in to a tablet being enrolled: the system's entry, naming who made the code it used.
        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Device, ActivityActions.DeviceEnrolled, ActivitySubjects.Device, device.Id)
            {
                BranchId = device.BranchId,
                Summary = new { name = device.Name, codeCreatedByStaffMemberId = enrolment.CreatedByStaffMemberId },
            },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var (token, expiresAtUtc) = tokens.IssueDeviceToken(device.Id, device.BranchId, device.VenueId);

        logger.LogInformation(
            "Device {DeviceId} ({DeviceName}) enrolled to branch {BranchId}.",
            device.Id, device.Name, device.BranchId);

        return new DeviceEnrolmentResult(token, device.Id, device.BranchId, device.Name, expiresAtUtc);
    }

    public async Task<StaffSessionResult> SignInWithPinAsync(
        Guid deviceId,
        Guid staffMemberId,
        string pin,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;

        var device = await db.StaffDevices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

        if (device is null || device.IsRevoked)
        {
            throw new AuthenticationFailedException(
                "device-revoked", "This tablet is no longer enrolled. Ask a manager to enrol it again.");
        }

        // A phone is one person's. Anybody else's id is refused before their row is even read - word for word the
        // wrong PIN's answer, and without touching their attempt count or saying whether they are locked - so the
        // phone cannot be used to learn whose PIN is whose, nor to lock a colleague out.
        if (device.IsBoundToSomebodyOtherThan(staffMemberId))
        {
            throw PinRejected();
        }

        // The branch check is here rather than in a policy because it decides whether a
        // credential is even offered to be checked: this person does not work at this tablet's
        // branch, so as far as this tablet is concerned they do not exist.
        var staff = await db.StaffMembers
            .Where(s => s.Id == staffMemberId)
            .Where(SignsInOn(device.VenueId, device.BranchId))
            .FirstOrDefaultAsync(cancellationToken);

        if (staff is null)
        {
            throw PinRejected();
        }

        // Before the PIN is checked, so a refusal never counts toward a lockout (docs/branch-features.md).
        await RequireSurfaceAllowedAsync(device, staff.Role, cancellationToken);

        await VerifyPinAsync(staff, pin, device.Id, nowUtc, cancellationToken);

        var (session, renewalToken) = await OpenSessionAsync(staff, device, nowUtc, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await SignedInAsync(staff, device, session, renewalToken, cancellationToken);
    }

    /// <summary>
    /// Checks a PIN against the person's hash, counting a wrong one toward the lockout - the one rule a tablet's
    /// sign-in and a phone's pairing share. Saves only on a wrong PIN; a right one leaves the caller to save.
    /// </summary>
    /// <exception cref="AccountLockedException">Locked already, or this wrong PIN locked them.</exception>
    /// <exception cref="AuthenticationFailedException"><c>pin-invalid</c>.</exception>
    private async Task VerifyPinAsync(
        StaffMember staff,
        string pin,
        Guid? deviceId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (staff.IsPinLockedAt(nowUtc))
        {
            throw new AccountLockedException(
                staff.PinLockedUntilUtc,
                "Too many wrong PINs. Ask a manager to unlock this PIN.");
        }

        var (matches, needsRehash) = hasher.Verify(staff.PinHash, pin);

        if (!matches)
        {
            var lockedOut = staff.RecordFailedPin(
                nowUtc, _options.PinMaxAttempts, TimeSpan.FromMinutes(_options.PinLockoutMinutes));

            await db.SaveChangesAsync(cancellationToken);

            // The PIN itself is never in this log line, and never anywhere else either.
            logger.LogWarning(
                "Wrong PIN for staff member {StaffMemberId} on device {DeviceId}. Attempt {Attempt}. Locked: {LockedOut}.",
                staff.Id, deviceId, staff.PinFailedAttempts, lockedOut);

            throw lockedOut
                ? new AccountLockedException(
                    staff.PinLockedUntilUtc, "Too many wrong PINs. Ask a manager to unlock this PIN.")
                : PinRejected();
        }

        if (needsRehash)
        {
            staff.SetPinHash(hasher.Hash(pin));
        }

        staff.ClearPinLockout();
    }

    /// <summary>
    /// Stages a new session for <paramref name="staff"/> on <paramref name="device"/>, ending whoever was signed in
    /// there. The caller saves.
    /// </summary>
    private async Task<(StaffSession Session, string RenewalToken)> OpenSessionAsync(
        StaffMember staff,
        StaffDevice device,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // One person on a tablet at a time. Whoever was signed in here is signed out, which is
        // what a waiter handing the tablet to a colleague means to happen.
        var open = await db.StaffSessions
            .Where(s => s.StaffDeviceId == device.Id && s.EndedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var previous in open)
        {
            previous.End(nowUtc);
        }

        var renewalToken = Secrets.NewOpaqueToken();
        var session = new StaffSession(
            staff.Id, device.Id, device.BranchId, Secrets.Hash(renewalToken), nowUtc);

        db.StaffSessions.Add(session);
        device.Touch(nowUtc);

        return (session, renewalToken);
    }

    /// <summary>
    /// Refuses a session the platform has not allowed at the device's branch (<c>docs/branch-features.md</c>): any on a
    /// paired phone without <see cref="BranchFeature.StaffPhone"/>, and the kitchen's - the kitchen display - without
    /// <see cref="BranchFeature.KitchenDisplay"/>. Waiters, managers and the rest on a tablet are not gated here.
    /// </summary>
    /// <exception cref="FeatureNotEnabledException">409 <c>feature-not-enabled</c>.</exception>
    private async Task RequireSurfaceAllowedAsync(StaffDevice device, StaffRole role, CancellationToken cancellationToken)
    {
        if (device.Kind != StaffDeviceKind.Phone && role != StaffRole.Kitchen)
        {
            return;
        }

        var bits = await BranchFeatureGate.BitsAsync(db, device.BranchId, cancellationToken);

        if (device.Kind == StaffDeviceKind.Phone)
        {
            BranchFeatures.Require(bits, BranchFeature.StaffPhone, device.BranchId);
        }

        if (role == StaffRole.Kitchen)
        {
            BranchFeatures.Require(bits, BranchFeature.KitchenDisplay, device.BranchId);
        }
    }

    /// <summary>Mints the session token for a session that has been saved.</summary>
    private async Task<StaffSessionResult> SignedInAsync(
        StaffMember staff,
        StaffDevice device,
        StaffSession session,
        string renewalToken,
        CancellationToken cancellationToken)
    {
        var (accessToken, _) = tokens.IssueStaffSessionToken(
            staff.Id, session.Id, device.Id, device.BranchId, device.VenueId, staff.Role);

        logger.LogInformation(
            "Staff member {StaffMemberId} signed in on device {DeviceId} at branch {BranchId}.",
            staff.Id, device.Id, device.BranchId);

        return new StaffSessionResult(
            accessToken,
            renewalToken,
            tokens.StaffSessionSeconds,
            staff.Id,
            staff.FullName,
            staff.Role,
            device.BranchId,
            await AllowedFeaturesAsync(device.BranchId, cancellationToken));
    }

    /// <summary>
    /// What the platform allows at the branch, for the session the tablet or phone holds. Read on every sign-in and
    /// renewal, so a change reaches the floor within one renewal.
    /// </summary>
    private async Task<IReadOnlyList<BranchFeature>> AllowedFeaturesAsync(Guid branchId, CancellationToken cancellationToken) =>
        BranchFeatures.FromBits(await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => b.AllowedFeatureBits)
            .FirstAsync(cancellationToken));

    public async Task<StaffSessionResult> RenewSessionAsync(
        string renewalToken,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;
        var hash = Secrets.Hash(renewalToken ?? string.Empty);

        var session = await db.StaffSessions
            .Include(s => s.StaffMember)
            .Include(s => s.StaffDevice)
            .FirstOrDefaultAsync(s => s.RenewalTokenHash == hash, cancellationToken);

        if (session is null)
        {
            throw new AuthenticationFailedException(
                "session-invalid", "This session is no longer valid. Tap your PIN again.");
        }

        if (session.StaffDevice.IsRevoked)
        {
            session.End(nowUtc);
            await db.SaveChangesAsync(cancellationToken);

            throw new AuthenticationFailedException(
                "device-revoked", "This tablet is no longer enrolled. Ask a manager to enrol it again.");
        }

        if (!session.IsRenewableAt(nowUtc))
        {
            session.End(nowUtc);
            await db.SaveChangesAsync(cancellationToken);

            throw new AuthenticationFailedException(
                "session-expired", "This session timed out. Tap your PIN again.");
        }

        // Deactivating somebody is how a venue takes them off the floor, and without this it did
        // nothing to a tablet they were already signed in on: SignInWithPinAsync refused the PIN,
        // while the session behind it renewed every thirty minutes for as long as anyone kept
        // tapping. Somebody let go at the end of a shift kept full floor access indefinitely, and
        // the manager's only real lever was revoking the device - which signs out the whole team.
        // Both other refresh paths in TokenRefreshService have always checked this.
        if (!session.StaffMember.IsActive)
        {
            session.End(nowUtc);
            await db.SaveChangesAsync(cancellationToken);

            throw new AuthenticationFailedException(
                "account-inactive", "This account is no longer active.");
        }

        // A phone, or the kitchen screen, the platform has blocked since this session began: it is not renewed, so the
        // last access token is the session's last. The session is left as it is - allowed again, it renews.
        await RequireSurfaceAllowedAsync(session.StaffDevice, session.StaffMember.Role, cancellationToken);

        var successor = Secrets.NewOpaqueToken();
        session.Renew(Secrets.Hash(successor), nowUtc);
        session.StaffDevice.Touch(nowUtc);

        await db.SaveChangesAsync(cancellationToken);

        var (accessToken, _) = tokens.IssueStaffSessionToken(
            session.StaffMemberId,
            session.Id,
            session.StaffDeviceId,
            session.BranchId,
            session.StaffDevice.VenueId,
            session.StaffMember.Role);

        return new StaffSessionResult(
            accessToken,
            successor,
            tokens.StaffSessionSeconds,
            session.StaffMemberId,
            session.StaffMember.FullName,
            session.StaffMember.Role,
            session.BranchId,
            await AllowedFeaturesAsync(session.BranchId, cancellationToken));
    }

    public async Task SignOutSessionAsync(string renewalToken, CancellationToken cancellationToken = default)
    {
        var hash = Secrets.Hash(renewalToken ?? string.Empty);

        var session = await db.StaffSessions
            .FirstOrDefaultAsync(s => s.RenewalTokenHash == hash, cancellationToken);

        // Signing out must never fail. An unknown handle means the session is already gone, which
        // is the outcome the caller asked for.
        if (session is null || session.EndedAtUtc is not null)
        {
            return;
        }

        session.End(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ClearPinLockoutAsync(
        Guid branchId,
        Guid staffMemberId,
        CancellationToken cancellationToken = default)
    {
        var venueId = await db.Branches
            .Where(b => b.Id == branchId)
            .Select(b => (Guid?)b.VenueId)
            .FirstOrDefaultAsync(cancellationToken);

        if (venueId is null)
        {
            return false;
        }

        // Only somebody this branch's tablets would lock out: at this branch, or venue-wide. Anyone
        // else's lockout is a sibling branch's business, and its own manager clears it there.
        var staff = await db.StaffMembers.FirstOrDefaultAsync(
            s => s.Id == staffMemberId
                 && s.VenueId == venueId
                 && (s.BranchId == null || s.BranchId == branchId),
            cancellationToken);

        if (staff is null)
        {
            return false;
        }

        staff.ClearPinLockout();

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Staff, ActivityActions.StaffPinLockoutCleared, ActivitySubjects.StaffMember, staff.Id)
            {
                BranchId = branchId,
                Summary = new { name = staff.FullName },
            },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("PIN lockout cleared for staff member {StaffMemberId}.", staffMemberId);

        return true;
    }
}
