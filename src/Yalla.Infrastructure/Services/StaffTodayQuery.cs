using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Messages;
using Yalla.Application.Overview;
using Yalla.Application.Reservations;
using Yalla.Application.Staff;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The manager's Today: the console's Overview of the branch, plus the few things the phone's board shows that the
/// Overview does not. See <see cref="StaffToday"/>.
/// </summary>
/// <remarks>
/// Built on the Overview rather than beside it, so the two screens can never disagree about a count: the bookings, the
/// room and the hall's takings are read by <see cref="IBranchOverviewQuery"/>, under its own guard, and the unanswered
/// messages by the Messages tab's own read.
/// </remarks>
internal sealed class StaffTodayQuery(
    YallaDbContext db,
    ICurrentActor actor,
    IStaffBranchGuard branchGuard,
    IBranchOverviewQuery overviews,
    IGuestMessageService messages) : IStaffTodayQuery
{
    private const string Operation = "Read today's summary";

    public async Task<StaffToday> GetAsync(
        Guid? branchId,
        Guid? sessionDeviceId,
        Guid? tokenBranchId,
        CancellationToken cancellationToken = default)
    {
        if (actor.Type != ActorType.Staff || actor.StaffMemberId is not { } staffId)
        {
            throw new StaffPermissionException(Operation, actor.Role, StaffRole.Manager);
        }

        var acting = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.Role, s.VenueId, s.BranchId, s.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (acting is not { IsActive: true }
            || !(acting.Role == StaffRole.PlatformAdmin || StaffRoleRules.ManagesAtVenue(acting.Role)))
        {
            throw new StaffPermissionException(Operation, acting?.Role ?? actor.Role, StaffRole.Manager);
        }

        // A PIN session on a shared tablet is the tablet's branch's, whoever signed in - the rule BranchScoped holds
        // every PIN session to. A phone is its person's own, so it reaches what their stored row reaches.
        Guid? confinedTo = null;

        if (sessionDeviceId is { } deviceId)
        {
            var device = await db.StaffDevices
                .AsNoTracking()
                .Where(d => d.Id == deviceId)
                .Select(d => new { d.Kind, d.BranchId })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new StaffPermissionException(Operation, acting.Role, StaffRole.Manager);

            if (device.Kind == StaffDeviceKind.Tablet)
            {
                confinedTo = device.BranchId;
            }
        }

        var target = branchId
                     ?? confinedTo
                     ?? tokenBranchId
                     ?? acting.BranchId
                     ?? await db.Branches
                         .AsNoTracking()
                         .Where(b => b.VenueId == acting.VenueId && b.IsActive)
                         .OrderBy(b => b.Name)
                         .Select(b => (Guid?)b.Id)
                         .FirstOrDefaultAsync(cancellationToken)
                     ?? throw new KeyNotFoundException("There is no branch to summarise.");

        if (confinedTo is { } tabletBranch && target != tabletBranch)
        {
            throw new StaffBranchScopeException("branch");
        }

        // The stored row's reach: a branch manager their own branch, an owner or general manager the venue's.
        await branchGuard.RequireAtBranchAsync(target, Operation, cancellationToken);

        var overview = await overviews.GetAsync(target, cancellationToken);
        var nowUtc = overview.AsOfUtc;
        var sinceUtc = overview.Revenue.SinceUtc;

        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == target)
            .Select(b => new { b.Name, b.VenueId, VenueName = b.Venue.Name })
            .FirstAsync(cancellationToken);

        var branches = confinedTo is not null
                       || !(acting.Role == StaffRole.PlatformAdmin || StaffRoleRules.CoversWholeVenue(acting.Role))
            ? [new TodayBranch(target, branch.Name)]
            : await db.Branches
                .AsNoTracking()
                .Where(b => b.VenueId == branch.VenueId && (b.IsActive || b.Id == target))
                .OrderBy(b => b.Name)
                .Select(b => new TodayBranch(b.Id, b.Name))
                .ToListAsync(cancellationToken);

        var takeaway = await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.BranchId == target
                        && o.Status == TakeawayStatus.Collected
                        && o.CollectedAtUtc >= sinceUtc
                        && o.CollectedAtUtc <= nowUtc)
            .GroupBy(_ => 1)
            .Select(g => new { Amd = g.Sum(o => o.PaidAmountAmd ?? 0L), Count = g.Count() })
            .FirstOrDefaultAsync(cancellationToken);

        var seated = await db.TableSessions
            .AsNoTracking()
            .Where(s => s.BranchId == target && s.SeatedAtUtc >= sinceUtc && s.SeatedAtUtc <= nowUtc)
            .GroupBy(s => s.ReservationId != null)
            .Select(g => new { Booked = g.Key, People = g.Sum(s => s.PartySize) })
            .ToListAsync(cancellationToken);

        var withBooking = seated.Where(s => s.Booked).Sum(s => s.People);
        var walkIn = seated.Where(s => !s.Booked).Sum(s => s.People);

        // The floor's awaiting list's rule (ReservationService.ListAwaitingApprovalAsync), counted rather than listed.
        var awaiting = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == target
                        && r.Status == ReservationStatus.PendingApproval
                        && r.StartUtc > nowUtc
                        && (r.ApprovalExpiresAtUtc == null || r.ApprovalExpiresAtUtc > nowUtc)
                        && !db.TableShareRequests.Any(q => q.LaterReservationId == r.Id
                                                           && q.Status == TableShareRequestStatus.Pending
                                                           && q.AsksEarlierGuest))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Oldest = g.Min(r => (DateTime?)r.CreatedAtUtc),
                Soonest = g.Min(r => r.ApprovalExpiresAtUtc),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var inbox = await messages.ListForBranchAsync(target, cancellationToken);
        var unanswered = inbox.Items.Where(i => i.OldestUnansweredGuestMessageUtc is not null).ToList();

        var shifts = await ShiftsAsync(target, overview.TimeZoneId, overview.ServiceDate, nowUtc, cancellationToken);
        var c = overview.Bookings;

        return new StaffToday(
            target,
            branch.Name,
            branch.VenueName,
            overview.TimeZoneId,
            nowUtc,
            overview.ServiceDate,
            overview.LocalTime,
            branches,
            new TodayRevenue(
                overview.Revenue.TodayAmd + (takeaway?.Amd ?? 0L),
                overview.Revenue.TodayAmd,
                takeaway?.Amd ?? 0L,
                takeaway?.Count ?? 0,
                sinceUtc),
            new TodayGuests(withBooking + walkIn, withBooking, walkIn),
            new TodayBookings(c.Total, c.Seated + c.Completed, c.Confirmed, c.Late, c.NoShow, c.PendingApproval),
            new TodayRoom(overview.InRoom.OccupiedTables, overview.InRoom.Tables, overview.InRoom.People),
            new TodayNeedsYou(
                awaiting?.Count ?? 0,
                awaiting?.Oldest,
                awaiting?.Soonest,
                unanswered.Count,
                unanswered.Min(i => i.OldestUnansweredGuestMessageUtc)),
            shifts);
    }

    /// <summary>
    /// The service date's shifts at the branch and last night's still running, with whether each person is signed in
    /// on one of the branch's devices now.
    /// </summary>
    private async Task<IReadOnlyList<TodayShift>> ShiftsAsync(
        Guid branchId,
        string timeZoneId,
        DateOnly serviceDate,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var zone = BranchZone.For(timeZoneId);
        var all = await ShiftInstants.ForAsync(db, branchId, zone, serviceDate.AddDays(-1), serviceDate, cancellationToken);
        var today = all.Where(s => s.Shift.Date == serviceDate || s.RunsAt(nowUtc)).ToList();

        if (today.Count == 0)
        {
            return [];
        }

        var ids = today.Select(s => s.Shift.StaffMemberId).Distinct().ToList();

        var people = await db.StaffMembers
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.FullName, s.Role })
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        var idleSince = nowUtc - StaffSession.IdleTimeout;

        var signedIn = (await db.StaffSessions
            .AsNoTracking()
            .Where(s => ids.Contains(s.StaffMemberId)
                        && s.EndedAtUtc == null
                        && s.LastActivityAtUtc > idleSince
                        && s.AbsoluteExpiresAtUtc > nowUtc
                        && s.StaffDevice.BranchId == branchId
                        && s.StaffDevice.RevokedAtUtc == null)
            .Select(s => s.StaffMemberId)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();

        return
        [
            .. today
                .Where(s => people.ContainsKey(s.Shift.StaffMemberId))
                .OrderBy(s => s.StartUtc)
                .ThenBy(s => people[s.Shift.StaffMemberId].FullName, StringComparer.CurrentCulture)
                .Select(s =>
                {
                    var person = people[s.Shift.StaffMemberId];

                    return new TodayShift(
                        person.Id,
                        person.FullName,
                        person.Role,
                        s.Shift.StartsAt!.Value,
                        s.Shift.EndsAt!.Value,
                        s.Shift.EndsNextDay,
                        s.StartUtc,
                        s.EndUtc,
                        s.RunsAt(nowUtc),
                        signedIn.Contains(person.Id));
                }),
        ];
    }
}
