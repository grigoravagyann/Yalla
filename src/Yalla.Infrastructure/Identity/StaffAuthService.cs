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
internal sealed class StaffAuthService(
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

    public async Task<EnrolledDeviceView> GetEnrolledDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var device = await db.StaffDevices
            .AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => new EnrolledDeviceView(
                d.Id,
                d.ClientDeviceId,
                d.Name,
                d.BranchId,
                d.Branch.Name,
                d.Branch.Venue.Name,
                d.CreatedAtUtc,
                d.LastSeenAtUtc,
                BranchFeatures.FromBits(d.Branch.AllowedFeatureBits),
                d.Kind,
                d.BoundStaffMember == null
                    ? null
                    : new BoundStaffMemberView(d.BoundStaffMember.Id, d.BoundStaffMember.FullName, d.BoundStaffMember.Role)))
            .FirstOrDefaultAsync(cancellationToken);

        // A revoked device is refused here as well as on every action. The PIN screen must not
        // render at all for a tablet that has been killed - showing a venue name and a keypad to
        // somebody holding a device the manager just revoked is the wrong answer twice.
        if (device is null || await IsRevokedAsync(deviceId, cancellationToken))
        {
            throw DeviceNoLongerEnrolled();
        }

        return device;
    }

    public async Task<IReadOnlyList<StaffRosterEntry>> GetRosterAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var device = await db.StaffDevices
            .AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => new { d.VenueId, d.BranchId, d.Branch.TimeZoneId, Revoked = d.RevokedAtUtc != null, d.BoundStaffMemberId })
            .FirstOrDefaultAsync(cancellationToken);

        // Refused exactly as GetEnrolledDeviceAsync refuses: the PIN screen this feeds must not
        // render for a killed tablet, let alone list the names of who works there.
        if (device is null || device.Revoked)
        {
            throw DeviceNoLongerEnrolled();
        }

        var roster = db.StaffMembers
            .AsNoTracking()
            .Where(SignsInOn(device.VenueId, device.BranchId));

        // A phone's PIN screen is its person's alone: the PIN exchange refuses everybody else on it, and the names
        // of who works here are no business of whoever holds somebody's phone.
        if (device.BoundStaffMemberId is { } bound)
        {
            roster = roster.Where(s => s.Id == bound);
        }

        var people = await roster
            .OrderBy(s => s.FullName)
            .Select(s => new { s.Id, s.FullName, s.Role })
            .ToListAsync(cancellationToken);

        var shifts = await ShiftsToShowAsync(device.BranchId, device.TimeZoneId, cancellationToken);

        // The shifts only annotate the list. Who is on it is SignsInOn's alone, as for the PIN.
        return
        [
            .. people.Select(p => shifts.TryGetValue(p.Id, out var shift)
                ? new StaffRosterEntry(
                    p.Id,
                    p.FullName,
                    p.Role,
                    shift.OnNow,
                    shift.View.StartsAt,
                    shift.View.EndsAt,
                    shift.View.EndsNextDay,
                    shift.View.StartsAtUtc,
                    shift.View.EndsAtUtc)
                : new StaffRosterEntry(p.Id, p.FullName, p.Role, false, null, null, false, null, null)),
        ];
    }

    /// <summary>How far ahead the PIN screen looks for somebody's next shift.</summary>
    private const int ShiftLookaheadHours = 12;

    /// <summary>
    /// Each person's shift at this branch for the PIN screen: the one running now, else the next to
    /// start within <see cref="ShiftLookaheadHours"/> hours. One read for the whole branch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Decided on instants, not the wall clock.</b> In the hour an autumn clock change repeats,
    /// 02:15 comes twice; a shift that ended at the first 02:30 is not running again at the second
    /// 02:15, and one that started at the first 02:30 has not stopped. Each shift's start and end are
    /// read in the branch's zone - the earlier instant of a repeated hour, and a time the clocks skip
    /// on the clock from before the change (<see cref="BranchZone.ToUtcLenient"/>) - and the very
    /// instants decided on are the ones reported, so a screen re-checking them later agrees.
    /// </para>
    /// <para>
    /// The branch's dates yesterday to tomorrow are read: last night's 18:00-02:00 is still running at
    /// 01:00, and at 23:50 tomorrow's 00:10 is the next. Nothing dated earlier can still run - a shift
    /// is at most sixteen hours on the clock, seventeen across a clock change - and nothing later is
    /// within the look-ahead. A person's shifts never overlap, so of the ones not yet over the
    /// earliest is the running one when there is one, and the next otherwise.
    /// </para>
    /// <para>
    /// Every row at this branch is read, whoever it belongs to; the caller keeps only the people on
    /// its roster. They all work here - this branch's own, or the whole venue's - which is the
    /// rule the console's shift list counts shifts by too.
    /// </para>
    /// <para>
    /// A branch whose zone this host cannot resolve gets no shifts rather than a 500: the roster is
    /// how a tablet signs anybody in, and it is whole without them.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, (StaffShiftView View, bool OnNow)>> ShiftsToShowAsync(
        Guid branchId,
        string timeZoneId,
        CancellationToken cancellationToken)
    {
        BranchZone zone;

        try
        {
            zone = BranchZone.For(timeZoneId);
        }
        catch (BranchTimeZoneMisconfiguredException ex)
        {
            logger.LogWarning(ex, "Branch {BranchId} has a time zone this host cannot read; its roster shows no shifts.", branchId);
            return new Dictionary<Guid, (StaffShiftView, bool)>();
        }

        var nowUtc = clock.UtcNow;
        var horizonUtc = nowUtc.AddHours(ShiftLookaheadHours);
        var today = zone.LocalDateAt(nowUtc);
        var (yesterday, tomorrow) = (today.AddDays(-1), today.AddDays(1));

        var shifts = await db.StaffShifts
            .AsNoTracking()
            .Where(s => s.BranchId == branchId && s.Date >= yesterday && s.Date <= tomorrow && s.StartsAt != null)
            .ToListAsync(cancellationToken);

        return shifts
            .Select(s => (Shift: s, Start: Instant(s.LocalStart!.Value), End: Instant(s.LocalEnd!.Value)))
            .Where(x => x.End > nowUtc && x.Start <= horizonUtc)
            .GroupBy(x => x.Shift.StaffMemberId)
            .Select(g => g.MinBy(x => x.Start))
            .ToDictionary(
                x => x.Shift.StaffMemberId,
                x => (StaffShiftService.ToView(x.Shift, zone) with { StartsAtUtc = x.Start, EndsAtUtc = x.End }, x.Start <= nowUtc));

        DateTime Instant(DateTime local) => zone.ToUtcLenient(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
    }

    /// <summary>
    /// Who may tap a PIN on a device of this venue and branch: active, of the same venue, and at
    /// that branch or at no particular one.
    /// </summary>
    /// <remarks>
    /// One rule for two readers - the PIN exchange and the roster the PIN screen shows. If they
    /// drifted, a tablet would list somebody whose PIN it then refuses, or accept a PIN from
    /// somebody it never listed. A platform admin has no venue, so the venue test alone keeps them
    /// off every tablet.
    /// </remarks>
    private static System.Linq.Expressions.Expression<Func<Yalla.Domain.Staff.StaffMember, bool>> SignsInOn(
        Guid venueId,
        Guid branchId) =>
        s => s.IsActive
             && s.VenueId == venueId
             && (s.BranchId == null || s.BranchId == branchId);

    private static AuthenticationFailedException DeviceNoLongerEnrolled() =>
        new("device-revoked", "This device is no longer enrolled. Ask a manager to set it up again.");

    private Task<bool> IsRevokedAsync(Guid deviceId, CancellationToken cancellationToken) =>
        db.StaffDevices.AnyAsync(d => d.Id == deviceId && d.RevokedAtUtc != null, cancellationToken);

    public async Task<IReadOnlyList<StaffDeviceSummary>> ListDevicesAsync(
        Guid branchId,
        StaffDeviceKind? kind = null,
        CancellationToken cancellationToken = default) =>
        await db.StaffDevices
            .AsNoTracking()
            .Where(d => d.BranchId == branchId)
            .Where(d => kind == null || d.Kind == kind)
            .OrderBy(d => d.Name)
            .ThenBy(d => d.CreatedAtUtc)
            .Select(d => new StaffDeviceSummary(
                d.Id,
                d.Name,
                d.BranchId,
                d.CreatedAtUtc,
                d.LastSeenAtUtc,
                d.RevokedAtUtc != null,
                d.Kind,
                d.BoundStaffMember == null
                    ? null
                    : new BoundStaffMemberView(d.BoundStaffMember.Id, d.BoundStaffMember.FullName, d.BoundStaffMember.Role),
                d.RevokedAtUtc,
                d.Platform,
                d.Browser))
            .ToListAsync(cancellationToken);

    public async Task<bool> RevokeDeviceAsync(
        Guid branchId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var device = await db.StaffDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.BranchId == branchId, cancellationToken);

        if (device is null)
        {
            return false;
        }

        var nowUtc = clock.UtcNow;
        var wasLive = !device.IsRevoked;
        device.Revoke(nowUtc);

        // Ending the open sessions matters as much as revoking the device: a tablet in a taxi may
        // already hold a session token, and the session is what can act.
        var open = await db.StaffSessions
            .Where(s => s.StaffDeviceId == device.Id && s.EndedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var session in open)
        {
            session.End(nowUtc);
        }

        // Its push subscription goes with it: the send already skips a revoked device, and an endpoint kept for a
        // device nobody can use again is a capability to that browser stored for nothing.
        await StaffPhones.ForgetPushSubscriptionsAsync(db, [device.Id], cancellationToken);

        // A phone is somebody's way in, so taking it away is a staff admin action with an author, as issuing its
        // code was. A tablet's revocation stays what it was: a log line.
        if (wasLive && device.Kind == StaffDeviceKind.Phone)
        {
            await AuditUnpairAsync(device, byItsOwner: false, cancellationToken);
        }

        // The History's entry in the same save: a tablet revoked, or somebody's phone unpaired from the console.
        if (wasLive)
        {
            await RecordDeviceGoneAsync(device, PhoneUnpairedBy.Console, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Before returning, so the very next request from that tablet is refused. The cache in
        // front of the authority check exists to save a round trip, not to delay a revocation.
        authority.InvalidateDevice(device.Id);

        logger.LogWarning(
            "Device {DeviceId} revoked at branch {BranchId}. {SessionCount} open session(s) ended.",
            device.Id, branchId, open.Count);

        return true;
    }

    // ---------------------------------------------------------------------------------------
    // Personal phones: a StaffDevice bound to one person, paired by a code issued for them.
    // ---------------------------------------------------------------------------------------

    public async Task<PhonePairingCodeResult> CreatePhonePairingCodeAsync(
        Guid branchId,
        Guid staffMemberId,
        Guid actingStaffMemberId,
        CancellationToken cancellationToken = default)
    {
        const string operation = "Pair a phone";

        var branch = await db.Branches
            .Where(b => b.Id == branchId)
            .Select(b => new { b.Id, b.VenueId, b.AllowedFeatureBits })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} does not exist.");

        // Staff phones are a feature the platform allows per branch (docs/branch-features.md).
        BranchFeatures.Require(branch.AllowedFeatureBits, BranchFeature.StaffPhone, branch.Id);

        // The caller's stored row, not the token: a manager demoted since their token was minted must not keep a
        // manager's reach - the rule every staff admin action follows.
        var acting = await db.StaffMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == actingStaffMemberId, cancellationToken);

        if (acting is not { IsActive: true }
            || (!acting.IsPlatformAdmin
                && (acting.VenueId != branch.VenueId || !StaffRoleRules.ManagesAtVenue(acting.Role))))
        {
            throw new StaffPermissionException(operation, acting?.Role ?? actor.Role, StaffRole.Manager);
        }

        // Only somebody this branch's devices sign in - at it, or venue-wide. Anybody else is not here to pair;
        // their own branch's manager pairs them there.
        var staff = await db.StaffMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == staffMemberId
                     && s.VenueId == branch.VenueId
                     && (s.BranchId == null || s.BranchId == branchId),
                cancellationToken)
            ?? throw new KeyNotFoundException($"Staff member {staffMemberId} does not sign in at this branch.");

        // You may pair the people you could have created, and your own phone. The phone still opens only with the
        // person's own PIN, so this is not taking over their account - but it is staff admin, and follows its rules.
        if (staff.Id != acting.Id && !StaffRoleRules.MayManage(acting.Role, staff.Role))
        {
            throw new StaffPermissionException(
                $"Pairing a {staff.Role}'s phone", acting.Role, RequiredRoleFor(staff.Role));
        }

        // A branch manager pairs at their own branch, and only their own branch's people.
        if (!acting.IsPlatformAdmin
            && !StaffRoleRules.CoversWholeVenue(acting.Role)
            && (acting.BranchId != branchId || (staff.Id != acting.Id && staff.BranchId != acting.BranchId)))
        {
            throw new StaffPermissionException(
                $"Pairing a {staff.Role}'s phone at another branch", acting.Role, StaffRole.GeneralManager);
        }

        if (!staff.IsActive)
        {
            throw new DomainStateException(
                $"{staff.FullName} is deactivated. Reactivate them first, then pair their phone.");
        }

        if (staff.Role == StaffRole.Kitchen)
        {
            throw new KitchenPhoneNotAllowedException(staff.Id);
        }

        var nowUtc = clock.UtcNow;

        // "Show a new code" takes the old one back: whatever the person had and did not use stops working now.
        var unused = await db.StaffEnrolmentCodes
            .Where(c => c.ForStaffMemberId == staff.Id && c.RedeemedAtUtc == null && c.SupersededAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var earlier in unused)
        {
            earlier.Supersede(nowUtc);
        }

        var code = Secrets.NewEnrolmentCode();
        var entity = new StaffEnrolmentCode(
            branch.VenueId, branch.Id, Secrets.Hash(code), acting.Id, nowUtc, forStaffMemberId: staff.Id);

        db.StaffEnrolmentCodes.Add(entity);

        // Who gave whom a way onto a phone, and where - never the code or its hash.
        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            PhonePairingIssuedAuditAction,
            new AuditTarget(StaffTargetType, staff.Id, staff.FullName, branch.VenueId),
            new { branchId = branch.Id, expiresAtUtc = entity.ExpiresAtUtc, supersededUnused = unused.Count },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Phone pairing code issued for staff member {StaffMemberId} at branch {BranchId} by {ActorId}.",
            staff.Id, branch.Id, acting.Id);

        // The only time the plaintext exists, as for a tablet's code.
        var shown = $"{code[..5]}-{code[5..]}";

        return new PhonePairingCodeResult(shown, entity.ExpiresAtUtc, PairingUrl(shown), staff.Id, branch.Id);
    }

    public async Task<PhonePairingPreview> PreviewPhonePairingAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var pairing = await FindPairingCodeAsync(code, clock.UtcNow, cancellationToken);

        // A code issued before the platform blocked staff phones here is good for nothing now.
        await BranchFeatureGate.RequireAsync(db, pairing.BranchId, BranchFeature.StaffPhone, cancellationToken);

        var person = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == pairing.ForStaffMemberId)
            .Where(SignsInOn(pairing.VenueId, pairing.BranchId))
            .Select(s => new { s.Id, s.FullName, s.Role })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw PairingPersonGone();

        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == pairing.BranchId)
            .Select(b => new { b.Name, VenueName = b.Venue.Name })
            .FirstAsync(cancellationToken);

        return new PhonePairingPreview(
            person.Id, person.FullName, person.Role, pairing.BranchId, branch.Name, branch.VenueName, pairing.ExpiresAtUtc);
    }

    public async Task<PhonePairingResult> PairPhoneAsync(
        string code,
        string pin,
        string clientDeviceId,
        string deviceName,
        StaffDevicePlatform? platform = null,
        StaffDeviceBrowser? browser = null,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;
        var pairing = await FindPairingCodeAsync(code, nowUtc, cancellationToken);

        // Before the PIN is checked: a code issued before the platform blocked staff phones here pairs nothing.
        await BranchFeatureGate.RequireAsync(db, pairing.BranchId, BranchFeature.StaffPhone, cancellationToken);

        var staff = await db.StaffMembers
            .Where(s => s.Id == pairing.ForStaffMemberId)
            .Where(SignsInOn(pairing.VenueId, pairing.BranchId))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw PairingPersonGone();

        // Moved to the kitchen in the minutes since the code was issued.
        if (staff.Role == StaffRole.Kitchen)
        {
            throw new KitchenPhoneNotAllowedException(staff.Id);
        }

        // What this browser already is: one of the branch's tablets (refused - a manager revokes it first), this
        // person's phone here (reused), somebody else's phone here or this person's phone at another branch (both
        // unpaired below: the browser holds one device token, and from now on it is this one).
        var live = await db.StaffDevices
            .Where(d => d.ClientDeviceId == clientDeviceId
                        && d.RevokedAtUtc == null
                        && (d.BranchId == pairing.BranchId || d.BoundStaffMemberId == staff.Id))
            .ToListAsync(cancellationToken);

        if (live.Any(d => d.BranchId == pairing.BranchId && d.Kind == StaffDeviceKind.Tablet))
        {
            throw new DomainStateException(
                "This device is enrolled as one of the branch's tablets. Ask a manager to revoke the tablet, then "
                + "pair the phone.");
        }

        // The person's PIN, with the lockout every PIN sign-in has. A wrong one leaves the code usable until it
        // expires: the ten minutes and the lockout bound the guessing, not the code.
        await VerifyPinAsync(staff, pin, deviceId: null, nowUtc, cancellationToken);

        var device = live.FirstOrDefault(d => d.BranchId == pairing.BranchId && d.BoundStaffMemberId == staff.Id);
        var unpaired = live.Where(d => d != device).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The browser's old bindings go first, in their own statement: the new phone takes the same client id
        // at the branch, which the live-device index allows only once the old row is revoked.
        if (unpaired.Count > 0)
        {
            await RevokeWithSessionsAsync(unpaired, nowUtc, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var repaired = device is not null;

        if (device is null)
        {
            device = StaffDevice.Phone(pairing.VenueId, pairing.BranchId, deviceName, clientDeviceId, staff.Id, nowUtc);
            db.StaffDevices.Add(device);
        }
        else
        {
            device.Rename(deviceName);
        }

        // The parts the console words the device column from, in its reader's language.
        device.Describe(platform, browser);

        // Concurrency token on RedeemedAtUtc, as for a tablet: two phones racing for one code cannot both win.
        pairing.Redeem(device.Id, nowUtc);

        // No token names anybody yet: the PIN just checked is who paired it, on the phone it paired - and who took
        // the browser's earlier bindings away, recorded before the save that commits the pairing with them.
        var provedBy = new ActivityProvedActor(staff.Id, device.Id);

        foreach (var gone in unpaired)
        {
            await StaffPhones.RecordGoneAsync(db, activity, gone, PhoneUnpairedBy.Replaced, cancellationToken, provedBy);
        }

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Device, ActivityActions.DevicePhonePaired, ActivitySubjects.Device, device.Id)
            {
                BranchId = device.BranchId,
                ProvedBy = provedBy,
                Summary = new
                {
                    name = device.Name,
                    staffMemberId = staff.Id,
                    staffName = staff.FullName,
                    repaired,
                    codeCreatedByStaffMemberId = pairing.CreatedByStaffMemberId,
                },
            },
            cancellationToken);

        var (session, renewalToken) = await OpenSessionAsync(staff, device, nowUtc, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.LiveDevicePerClientId))
        {
            throw new DomainStateException(
                "This device was set up by another request at the same moment. Open the staff app again.");
        }

        await transaction.CommitAsync(cancellationToken);

        foreach (var gone in unpaired)
        {
            authority.InvalidateDevice(gone.Id);
        }

        if (unpaired.Count > 0)
        {
            logger.LogWarning(
                "Pairing device {DeviceId} to staff member {StaffMemberId} unpaired {Count} earlier binding(s) of the same browser.",
                device.Id, staff.Id, unpaired.Count);
        }

        var (deviceToken, expiresAtUtc) = tokens.IssueDeviceToken(device.Id, device.BranchId, device.VenueId);

        logger.LogInformation(
            "Phone {DeviceId} paired to staff member {StaffMemberId} at branch {BranchId}.",
            device.Id, staff.Id, device.BranchId);

        return new PhonePairingResult(
            new DeviceEnrolmentResult(deviceToken, device.Id, device.BranchId, device.Name, expiresAtUtc),
            await SignedInAsync(staff, device, session, renewalToken, cancellationToken));
    }

    public async Task UnpairOwnPhoneAsync(
        Guid deviceId,
        Guid staffMemberId,
        CancellationToken cancellationToken = default)
    {
        var device = await db.StaffDevices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

        // Unpairing must not fail for a phone that is already gone: that is the outcome asked for.
        if (device is null || device.IsRevoked)
        {
            return;
        }

        if (device.Kind != StaffDeviceKind.Phone)
        {
            throw new DomainStateException(
                "This is one of the branch's tablets, not a personal phone. A manager revokes it from the console.");
        }

        // A session on a phone is always its own person's; anything else is a token this phone did not mint.
        if (device.BoundStaffMemberId != staffMemberId)
        {
            throw new UnauthorizedAccessException("Only the phone's own person can unpair it.");
        }

        var nowUtc = clock.UtcNow;

        await RevokeWithSessionsAsync([device], nowUtc, cancellationToken);
        await AuditUnpairAsync(device, byItsOwner: true, cancellationToken);
        await RecordDeviceGoneAsync(device, PhoneUnpairedBy.ItsOwner, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        authority.InvalidateDevice(device.Id);

        logger.LogInformation("Phone {DeviceId} unpaired by its own staff member {StaffMemberId}.", device.Id, staffMemberId);
    }

    /// <summary>
    /// The pairing code a phone typed or scanned, still usable - or the refusal that says which way it is not.
    /// </summary>
    /// <remarks>
    /// Tracked, so <see cref="PairPhoneAsync"/> can spend it. A tablet's enrolment code is not a pairing code and
    /// is answered exactly as an unknown one.
    /// </remarks>
    private async Task<StaffEnrolmentCode> FindPairingCodeAsync(
        string code,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var hash = Secrets.Hash(Secrets.NormalizeEnrolmentCode(code));

        var pairing = await db.StaffEnrolmentCodes.FirstOrDefaultAsync(c => c.CodeHash == hash, cancellationToken);

        if (pairing is null || !pairing.IsPhonePairing)
        {
            throw new AuthenticationFailedException("pairing-code-invalid", "That pairing code is not valid.");
        }

        // Spent is a conflict, as for a tablet's code: it worked once, so somebody paired a phone with it.
        if (pairing.RedeemedAtUtc is not null)
        {
            throw new DomainStateException(
                "That pairing code has already been used. Ask your manager for a new one, and to check the "
                + "phones list for one nobody paired.");
        }

        if (!pairing.IsUsableAt(nowUtc))
        {
            throw new AuthenticationFailedException(
                "pairing-code-expired",
                "That pairing code has expired or a newer one replaced it. Ask your manager for a new one.");
        }

        return pairing;
    }

    /// <summary>The code's person can no longer sign in at its branch: deactivated, or moved elsewhere.</summary>
    private static DomainStateException PairingPersonGone() =>
        new("The person this code was issued for can no longer sign in at this branch. Ask your manager for a new code.");

    /// <summary>Revokes devices and ends every session open on them. The caller saves and invalidates the cache.</summary>
    private async Task RevokeWithSessionsAsync(
        IReadOnlyList<StaffDevice> devices,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var ids = devices.Select(d => d.Id).ToList();

        foreach (var device in devices)
        {
            device.Revoke(nowUtc);
        }

        var open = await db.StaffSessions
            .Where(s => ids.Contains(s.StaffDeviceId) && s.EndedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var session in open)
        {
            session.End(nowUtc);
        }

        await StaffPhones.ForgetPushSubscriptionsAsync(db, ids, cancellationToken);
    }

    /// <summary>Who took a phone away from whom - the console, or the phone's own person. Never a token.</summary>
    private async Task AuditUnpairAsync(StaffDevice device, bool byItsOwner, CancellationToken cancellationToken)
    {
        if (actor.StaffMemberId is null)
        {
            return;
        }

        var person = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == device.BoundStaffMemberId)
            .Select(s => new { s.Id, s.FullName })
            .FirstOrDefaultAsync(cancellationToken);

        if (person is null)
        {
            return;
        }

        await PlatformAudit.RecordAsync(
            db,
            actor,
            clock,
            PhoneUnpairedAuditAction,
            new AuditTarget(StaffTargetType, person.Id, person.FullName, device.VenueId),
            new { deviceId = device.Id, branchId = device.BranchId, deviceName = device.Name, byItsOwner },
            cancellationToken);
    }

    /// <summary>
    /// The History's entry for a device that stopped working, staged in the caller's save: <c>device.revoked</c> for a
    /// tablet, <c>device.phone-unpaired</c> for somebody's phone - with who it was and which way it went.
    /// </summary>
    private Task RecordDeviceGoneAsync(StaffDevice device, string unpairedBy, CancellationToken cancellationToken) =>
        StaffPhones.RecordGoneAsync(db, activity, device, unpairedBy, cancellationToken);

    /// <summary>
    /// What a pairing QR carries: the staff app's pairing page, on the console's own origin - the staff app and
    /// the console are one web app - with the code in the fragment, which never reaches a host's access log.
    /// </summary>
    /// <remarks>
    /// The origin comes from <c>Auth:PasswordResetUrlTemplate</c>, the setting that already names where the
    /// console is and that startup refuses to leave pointing at loopback outside Development.
    /// </remarks>
    private string PairingUrl(string code)
    {
        var sample = _options.PasswordResetUrlTemplate.Replace("{token}", "t", StringComparison.Ordinal);
        var origin = Uri.TryCreate(sample, UriKind.Absolute, out var url)
            ? url.GetLeftPart(UriPartial.Authority)
            : "http://localhost:5173";

        return $"{origin}/staff/pair#code={Uri.EscapeDataString(code)}";
    }

    private const string StaffTargetType = "StaffMember";

    /// <summary>The lowest rank that may manage <paramref name="target"/>, for the 403 - the staff service's table.</summary>
    private static StaffRole RequiredRoleFor(StaffRole target) => target switch
    {
        StaffRole.Owner or StaffRole.GeneralManager => StaffRole.Owner,
        StaffRole.Manager => StaffRole.GeneralManager,
        StaffRole.PlatformAdmin => StaffRole.PlatformAdmin,
        _ => StaffRole.Manager,
    };

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
