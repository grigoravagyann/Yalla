using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The shifts on a branch's Staff screen: the minimal model - one person, one branch, one date, a from and a to time
/// or a day off.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who.</b> Every call goes through <see cref="IStaffBranchGuard"/> first, underneath the route's
/// <c>BranchScoped</c> policy: an owner or general manager at any branch of their venue, a branch manager at their own,
/// a platform admin anywhere. A write then asks the staff edit's questions from the stored rows: <b>rank</b> - may the
/// caller manage this person, or is it the person themselves - and <b>reach</b>, as <c>StaffManagementService</c> asks
/// it: a branch manager plans only the people of their own branch, never somebody who works the whole venue.
/// </para>
/// <para>
/// <b>Today is the service day</b>, the Overview's: the branch's own date, or yesterday while last night's opening
/// block still runs past midnight. Shifts before it are history: not planned, changed or removed - except one still
/// running, whose times may be corrected on its own date.
/// </para>
/// <para>
/// <b>One person, one place at a time.</b> A write takes the person's lock - the update lock on their staff row the
/// account lock already uses - and checks their other shifts, at every branch of the venue, inside it, as instants:
/// each shift in its own branch's zone. Two managers adding overlapping shifts for the same waiter at once are
/// serialised, and the second is refused by name.
/// </para>
/// <para>
/// <b>Only the branches a person covers.</b> A shift counts - listed, counted, clashed with - only while its person
/// still works at its branch (their branch, or the whole venue). Moving somebody takes their future shifts elsewhere off
/// in the same save (<c>StaffManagementService</c>); one left behind some other way is ignored, and can be removed.
/// </para>
/// <para>
/// Not audited, like the other staff edits short of creating a person or giving them a sign-in; logged.
/// </para>
/// </remarks>
internal sealed class StaffShiftService(
    YallaDbContext db,
    IClock clock,
    IStaffBranchGuard branchGuard,
    ILogger<StaffShiftService> logger) : IStaffShiftService
{
    public async Task<BranchShiftsView> ListAsync(
        Guid branchId,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the shifts", cancellationToken);

        var branch = await LoadBranchAsync(branchId, cancellationToken);
        var (serviceDate, _) = await ServiceDayAsync(branch, cancellationToken);
        var (first, last) = StaffShiftRules.Window(from, to, serviceDate);

        var shifts = await Covered(db.StaffShifts.AsNoTracking())
            .Where(s => s.BranchId == branchId && s.Date >= first && s.Date <= last)
            .ToListAsync(cancellationToken);

        var onShiftToday = await Covered(db.StaffShifts.AsNoTracking())
            .Where(s => s.BranchId == branchId && s.Date == serviceDate && s.StartsAt != null)
            .Select(s => s.StaffMemberId)
            .Distinct()
            .CountAsync(cancellationToken);

        var zone = BranchZone.For(branch.TimeZoneId);

        return new BranchShiftsView(
            branchId,
            serviceDate,
            first,
            last,
            onShiftToday,
            [
                .. shifts
                    .OrderBy(s => s.Date)
                    .ThenBy(s => s.StartsAt.HasValue)
                    .ThenBy(s => s.StartsAt)
                    .ThenBy(s => s.StaffMemberId)
                    .Select(s => ToView(s, zone)),
            ]);
    }

    public async Task<StaffShiftView> CreateAsync(
        Guid branchId,
        CreateShiftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var actingId = await branchGuard.RequireAtBranchAsync(branchId, "Plan a shift", cancellationToken);

        if (command.StaffMemberId == Guid.Empty)
        {
            throw new FieldValidationException(new FieldViolation(
                "staffMemberId", "Say whose shift it is.", FieldBounds.Required));
        }

        var branch = await LoadBranchAsync(branchId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPersonAsync(command.StaffMemberId, cancellationToken);

        var person = await LoadPersonAsync(branch, command.StaffMemberId, cancellationToken);
        await RequireMayPlanAsync(actingId, person, "Planning a shift", requireReach: true, cancellationToken);

        var (serviceDate, _) = await ServiceDayAsync(branch, cancellationToken);
        var slot = InZone(
            StaffShiftRules.Normalise(command.Date, command.StartsAt, command.EndsAt, command.DayOff, serviceDate),
            BranchZone.For(branch.TimeZoneId));

        StaffShiftRules.RequireNoClash(slot, await OthersAroundAsync(person, slot.Date, exceptShiftId: null, cancellationToken));

        var shift = new StaffShift(person.Id, branchId, slot.Date, slot.StartsAt, slot.EndsAt);
        db.StaffShifts.Add(shift);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Shift {ShiftId} ({Slot}) planned for staff member {StaffMemberId} at branch {BranchId} by {ActorId}.",
            shift.Id, slot, person.Id, branchId, actingId);

        return ToView(shift, BranchZone.For(branch.TimeZoneId));
    }

    public async Task<StaffShiftView> UpdateAsync(
        Guid branchId,
        Guid shiftId,
        UpdateShiftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var actingId = await branchGuard.RequireAtBranchAsync(branchId, "Change a shift", cancellationToken);
        var branch = await LoadBranchAsync(branchId, cancellationToken);
        var personId = await ShiftOwnerAsync(branchId, shiftId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPersonAsync(personId, cancellationToken);

        // Read again under the lock: deleted, or moved to another day, while this waited.
        var shift = await db.StaffShifts.FirstOrDefaultAsync(s => s.Id == shiftId && s.BranchId == branchId, cancellationToken)
                    ?? throw ShiftNotFound(shiftId);

        var person = await LoadPersonAsync(branch, shift.StaffMemberId, cancellationToken);
        await RequireMayPlanAsync(actingId, person, "Changing a shift", requireReach: true, cancellationToken);

        var (serviceDate, localNow) = await ServiceDayAsync(branch, cancellationToken);
        RequireNotHistory(shift, serviceDate, localNow, "changed");

        // A shift from before the service day that is still running may be corrected - on its own date, no earlier.
        var slot = InZone(
            StaffShiftRules.Normalise(
                command.Date, command.StartsAt, command.EndsAt, command.DayOff, serviceDate, earliest: shift.Date),
            BranchZone.For(branch.TimeZoneId));

        StaffShiftRules.RequireNoClash(slot, await OthersAroundAsync(person, slot.Date, shift.Id, cancellationToken));

        shift.Reschedule(slot.Date, slot.StartsAt, slot.EndsAt);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Shift {ShiftId} changed to {Slot} for staff member {StaffMemberId} at branch {BranchId} by {ActorId}.",
            shift.Id, slot, person.Id, branchId, actingId);

        return ToView(shift, BranchZone.For(branch.TimeZoneId));
    }

    public async Task DeleteAsync(Guid branchId, Guid shiftId, CancellationToken cancellationToken = default)
    {
        var actingId = await branchGuard.RequireAtBranchAsync(branchId, "Remove a shift", cancellationToken);
        var branch = await LoadBranchAsync(branchId, cancellationToken);

        var shift = await db.StaffShifts.FirstOrDefaultAsync(s => s.Id == shiftId && s.BranchId == branchId, cancellationToken)
                    ?? throw ShiftNotFound(shiftId);

        // A deactivated person's shifts are hidden, not locked; one left at a branch its person no longer works at is
        // ignored everywhere. Taking either off is harmless, so the person need only be of this venue and within the
        // caller's rank - and within their reach only while the shift is one the branch still counts.
        var person = await LoadPersonAsync(branch, shift.StaffMemberId, cancellationToken, requireActive: false, requireCovers: false);
        var stale = person.BranchId is { } home && home != branchId;

        await RequireMayPlanAsync(actingId, person, "Removing a shift", requireReach: !stale, cancellationToken);

        var (serviceDate, localNow) = await ServiceDayAsync(branch, cancellationToken);
        RequireNotHistory(shift, serviceDate, localNow, "removed");

        db.StaffShifts.Remove(shift);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Shift {ShiftId} removed for staff member {StaffMemberId} at branch {BranchId} by {ActorId}.",
            shift.Id, person.Id, branchId, actingId);
    }

    // ------------------------------------------------------------ helpers

    private sealed record BranchRow(Guid Id, Guid VenueId, string TimeZoneId);

    /// <summary>The shifts whose person still works at their branch: venue-wide, or that very branch.</summary>
    private static IQueryable<StaffShift> Covered(IQueryable<StaffShift> shifts) =>
        shifts.Where(s => s.StaffMember.IsActive
                          && (s.StaffMember.BranchId == null || s.StaffMember.BranchId == s.BranchId));

    private async Task<BranchRow> LoadBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new BranchRow(b.Id, b.VenueId, b.TimeZoneId))
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

    private Task<(DateOnly ServiceDate, DateTime LocalNow)> ServiceDayAsync(BranchRow branch, CancellationToken cancellationToken) =>
        OpeningCalendars.ServiceDayAsync(db, branch.Id, branch.TimeZoneId, clock.UtcNow, cancellationToken);

    /// <summary>
    /// Refuses a shift from before the service day that has ended: history. One still running - last night's
    /// 18:00-02:00 at 00:30 after the branch shut - may still be corrected or taken off.
    /// </summary>
    /// <exception cref="DomainStateException">The shift is past and over. Answers 409 <c>conflicting-state</c>.</exception>
    private static void RequireNotHistory(StaffShift shift, DateOnly serviceDate, DateTime localNow, string what)
    {
        if (shift.Date >= serviceDate || shift.LocalEnd > localNow)
        {
            return;
        }

        throw new DomainStateException(
            $"The shift on {shift.Date:yyyy-MM-dd} is over, and past shifts are history: it cannot be {what}.");
    }

    /// <summary>
    /// The person's lock: the update lock on their staff row, held to the commit - the one the account lock takes, so
    /// every writer of things about one person queues on the same row.
    /// </summary>
    private async Task LockPersonAsync(Guid staffMemberId, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<Guid>($"SELECT Id AS [Value] FROM StaffMembers WITH (UPDLOCK, HOLDLOCK) WHERE Id = {staffMemberId}")
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The person, if they are of this branch's venue and - when <paramref name="requireCovers"/> - work at this
    /// branch: its own people, or an owner or general manager of the venue.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Not of this venue, or (when <paramref name="requireCovers"/>) at another of its branches.</exception>
    /// <exception cref="DomainStateException">Deactivated, when <paramref name="requireActive"/>.</exception>
    private async Task<StaffMember> LoadPersonAsync(
        BranchRow branch,
        Guid staffMemberId,
        CancellationToken cancellationToken,
        bool requireActive = true,
        bool requireCovers = true)
    {
        var person = await db.StaffMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == staffMemberId && s.VenueId == branch.VenueId, cancellationToken);

        if (person is null
            || person.Role == StaffRole.PlatformAdmin
            || (requireCovers && person.BranchId is { } home && home != branch.Id))
        {
            throw new KeyNotFoundException($"Staff member {staffMemberId} does not work at this branch.");
        }

        if (requireActive && !person.IsActive)
        {
            throw new DomainStateException(
                $"{person.FullName} is deactivated. Reactivate them first, then plan their shifts.");
        }

        return person;
    }

    /// <summary>
    /// The staff edit's questions, from the stored rows: you plan the shifts of the people you could have created, and
    /// your own - and, below the venue-wide ranks, only the people of your own branch.
    /// </summary>
    /// <remarks>
    /// The reach half mirrors <c>StaffManagementService.RequireReach</c>: a branch manager is confined to their branch,
    /// and somebody with no branch on their row - a venue-wide waiter, on every branch's tablets - is further than
    /// that, though the guard let the manager in to this branch.
    /// </remarks>
    private async Task RequireMayPlanAsync(
        Guid actingId,
        StaffMember person,
        string operation,
        bool requireReach,
        CancellationToken cancellationToken)
    {
        if (person.Id == actingId)
        {
            return;
        }

        // The guard has read this row already and found it active and in reach of the branch.
        var acting = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == actingId)
            .Select(s => new { s.Role, s.BranchId })
            .FirstAsync(cancellationToken);

        if (!StaffRoleRules.MayManage(acting.Role, person.Role))
        {
            throw new StaffPermissionException(
                $"{operation} for a {person.Role}", acting.Role, RequiredRoleFor(person.Role));
        }

        var reachesWholeVenue = acting.Role == StaffRole.PlatformAdmin || StaffRoleRules.CoversWholeVenue(acting.Role);

        if (requireReach && !reachesWholeVenue && (acting.BranchId is not { } home || person.BranchId != home))
        {
            throw new StaffPermissionException(
                $"{operation} for a {person.Role} beyond your branch", acting.Role, StaffRole.GeneralManager);
        }
    }

    /// <summary>
    /// The person's other shifts around <paramref name="date"/> - two days either side, so a shift at a branch in
    /// another zone is not missed - at the branches they still work at, each as instants in its own branch's zone.
    /// </summary>
    private async Task<IReadOnlyList<ShiftSlot>> OthersAroundAsync(
        StaffMember person,
        DateOnly date,
        Guid? exceptShiftId,
        CancellationToken cancellationToken)
    {
        var from = date.AddDays(-2);
        var to = date.AddDays(2);
        var home = person.BranchId;

        var rows = await db.StaffShifts
            .AsNoTracking()
            .Where(s => s.StaffMemberId == person.Id
                        && s.Date >= from
                        && s.Date <= to
                        && (home == null || s.BranchId == home)
                        && (exceptShiftId == null || s.Id != exceptShiftId))
            .Select(s => new { s.Date, s.StartsAt, s.EndsAt, s.Branch.TimeZoneId })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => InZone(new ShiftSlot(r.Date, r.StartsAt, r.EndsAt), BranchZone.For(r.TimeZoneId)))];
    }

    /// <summary>The slot with its instants, read in <paramref name="zone"/>.</summary>
    private static ShiftSlot InZone(ShiftSlot slot, BranchZone zone) =>
        slot with { StartUtc = Instant(zone, slot.LocalStart), EndUtc = Instant(zone, slot.LocalEnd) };

    private async Task<Guid> ShiftOwnerAsync(Guid branchId, Guid shiftId, CancellationToken cancellationToken)
    {
        var owner = await db.StaffShifts
            .AsNoTracking()
            .Where(s => s.Id == shiftId && s.BranchId == branchId)
            .Select(s => (Guid?)s.StaffMemberId)
            .FirstOrDefaultAsync(cancellationToken);

        return owner ?? throw ShiftNotFound(shiftId);
    }

    private static KeyNotFoundException ShiftNotFound(Guid shiftId) =>
        new($"Shift {shiftId} was not found at this branch.");

    /// <summary>The lowest rank that may manage <paramref name="target"/>, for the 403 - as the staff edit names it.</summary>
    private static StaffRole RequiredRoleFor(StaffRole target) => target switch
    {
        StaffRole.Owner or StaffRole.GeneralManager => StaffRole.Owner,
        StaffRole.Manager => StaffRole.GeneralManager,
        _ => StaffRole.Manager,
    };

    /// <summary>
    /// One shift as every read returns it, its instants read in its branch's <paramref name="zone"/>. Shared with the
    /// tablet's PIN roster, which reports a person's shift in these same fields.
    /// </summary>
    internal static StaffShiftView ToView(StaffShift shift, BranchZone zone) => new(
        shift.Id,
        shift.StaffMemberId,
        shift.BranchId,
        shift.Date,
        shift.IsDayOff,
        shift.StartsAt,
        shift.EndsAt,
        shift.EndsNextDay,
        Instant(zone, shift.LocalStart),
        Instant(zone, shift.LocalEnd));

    /// <summary>A local wall-clock moment as an instant; null on a day off, or for an hour a clock change skips.</summary>
    private static DateTime? Instant(BranchZone zone, DateTime? local) =>
        local is { } at && zone.TryToUtc(DateOnly.FromDateTime(at), TimeOnly.FromDateTime(at), out var utc) ? utc : null;
}
