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

/// <summary>Personal phones: pairing codes, the pairing itself, unpairing and the audit of it.</summary>
internal sealed partial class StaffAuthService
{
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
}
