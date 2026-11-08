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

/// <summary>The enrolled devices: what a tablet is, the branch roster it shows, listing and revoking.</summary>
internal sealed partial class StaffAuthService
{
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
}
