using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>A shift with the instants it starts and ends, read in its branch's zone.</summary>
internal sealed record ShiftInstant(StaffShift Shift, DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>Running at <paramref name="nowUtc"/>: started, not yet over.</summary>
    public bool RunsAt(DateTime nowUtc) => StartUtc <= nowUtc && nowUtc < EndUtc;
}

/// <summary>
/// A branch's shifts as instants, for the readers that ask "who is on shift now" - staff push's "only during my
/// shift", and the manager's Today.
/// </summary>
/// <remarks>
/// The PIN roster's rule (<c>StaffAuthService.ShiftsToShowAsync</c>, docs/auth.md "Across a clock change"): each
/// shift's start and end are read in the branch's zone, the earlier instant of a repeated hour and the clock from
/// before the change for a skipped one (<see cref="BranchZone.ToUtcLenient"/>), and "running" is decided on those
/// instants, never on the wall clock. Days off are not shifts and are left out.
/// </remarks>
internal static class ShiftInstants
{
    /// <summary>The branch's shifts dated <paramref name="from"/> to <paramref name="to"/>, with their instants.</summary>
    public static async Task<List<ShiftInstant>> ForAsync(
        YallaDbContext db,
        Guid branchId,
        BranchZone zone,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var shifts = await db.StaffShifts
            .AsNoTracking()
            .Where(s => s.BranchId == branchId && s.Date >= from && s.Date <= to && s.StartsAt != null)
            .ToListAsync(cancellationToken);

        return [.. shifts.Select(s => new ShiftInstant(s, Instant(s.LocalStart!.Value), Instant(s.LocalEnd!.Value)))];

        DateTime Instant(DateTime local) => zone.ToUtcLenient(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
    }

    /// <summary>
    /// Who is on a shift at the branch at <paramref name="nowUtc"/> - last night's still running past midnight
    /// included. Empty for a branch whose zone this host cannot read, rather than a failure.
    /// </summary>
    public static async Task<HashSet<Guid>> OnShiftAsync(
        YallaDbContext db,
        Guid branchId,
        string timeZoneId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        BranchZone zone;

        try
        {
            zone = BranchZone.For(timeZoneId);
        }
        catch (BranchTimeZoneMisconfiguredException)
        {
            return [];
        }

        var today = zone.LocalDateAt(nowUtc);
        var shifts = await ForAsync(db, branchId, zone, today.AddDays(-1), today, cancellationToken);

        return [.. shifts.Where(s => s.RunsAt(nowUtc)).Select(s => s.Shift.StaffMemberId)];
    }
}
