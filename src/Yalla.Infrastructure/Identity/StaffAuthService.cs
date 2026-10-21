using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Reservations;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Identity;
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
    ILogger<StaffAuthService> logger) : IStaffAuthService
{
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

        if (enrolment is null)
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
                staff.Id, device.Id, staff.PinFailedAttempts, lockedOut);

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

        await db.SaveChangesAsync(cancellationToken);

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
            device.BranchId);
    }

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
            session.BranchId);
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
                d.LastSeenAtUtc))
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
            .Select(d => new { d.VenueId, d.BranchId, d.Branch.TimeZoneId, Revoked = d.RevokedAtUtc != null })
            .FirstOrDefaultAsync(cancellationToken);

        // Refused exactly as GetEnrolledDeviceAsync refuses: the PIN screen this feeds must not
        // render for a killed tablet, let alone list the names of who works there.
        if (device is null || device.Revoked)
        {
            throw DeviceNoLongerEnrolled();
        }

        var people = await db.StaffMembers
            .AsNoTracking()
            .Where(SignsInOn(device.VenueId, device.BranchId))
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
        CancellationToken cancellationToken = default) =>
        await db.StaffDevices
            .AsNoTracking()
            .Where(d => d.BranchId == branchId)
            .OrderBy(d => d.Name)
            .Select(d => new StaffDeviceSummary(
                d.Id, d.Name, d.BranchId, d.CreatedAtUtc, d.LastSeenAtUtc, d.RevokedAtUtc != null))
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

        await db.SaveChangesAsync(cancellationToken);

        // Before returning, so the very next request from that tablet is refused. The cache in
        // front of the authority check exists to save a round trip, not to delay a revocation.
        authority.InvalidateDevice(device.Id);

        logger.LogWarning(
            "Device {DeviceId} revoked at branch {BranchId}. {SessionCount} open session(s) ended.",
            device.Id, branchId, open.Count);

        return true;
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
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("PIN lockout cleared for staff member {StaffMemberId}.", staffMemberId);

        return true;
    }
}
