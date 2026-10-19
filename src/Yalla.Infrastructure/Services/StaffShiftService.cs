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
/// a platform admin anywhere. A write then asks the staff edit's rank question from the stored rows - may the caller
/// manage this person - or is it the person themselves.
/// </para>
/// <para>
/// <b>Today is the service day</b>, the Overview's: the branch's own date, or yesterday while last night's opening
/// block still runs past midnight. Shifts before it are history and are not planned.
/// </para>
/// <para>
/// <b>One person, one place at a time.</b> A write takes the person's lock - the update lock on their staff row the
/// account lock already uses - and checks their other shifts, at every branch of the venue, inside it. Two managers
/// adding overlapping shifts for the same waiter at once are serialised, and the second is refused by name.
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
        var serviceDate = await ServiceDateAsync(branch, cancellationToken);
        var (first, last) = StaffShiftRules.Window(from, to, serviceDate);

        var shifts = await db.StaffShifts
            .AsNoTracking()
            .Where(s => s.BranchId == branchId && s.Date >= first && s.Date <= last && s.StaffMember.IsActive)
            .ToListAsync(cancellationToken);

        var onShiftToday = await db.StaffShifts
            .AsNoTracking()
            .Where(s => s.BranchId == branchId
                        && s.Date == serviceDate
                        && s.StartsAt != null
                        && s.StaffMember.IsActive)
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
        await RequireMayPlanAsync(actingId, person, "Planning a shift", cancellationToken);

        var serviceDate = await ServiceDateAsync(branch, cancellationToken);
        var slot = StaffShiftRules.Normalise(command.Date, command.StartsAt, command.EndsAt, command.DayOff, serviceDate);

        StaffShiftRules.RequireNoClash(slot, await OthersAroundAsync(person.Id, slot.Date, exceptShiftId: null, cancellationToken));

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
        await RequireMayPlanAsync(actingId, person, "Changing a shift", cancellationToken);

        var serviceDate = await ServiceDateAsync(branch, cancellationToken);
        var slot = StaffShiftRules.Normalise(command.Date, command.StartsAt, command.EndsAt, command.DayOff, serviceDate);

        StaffShiftRules.RequireNoClash(slot, await OthersAroundAsync(person.Id, slot.Date, shift.Id, cancellationToken));

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

        // A deactivated person's shifts are hidden, not locked: taking one off is harmless, so only the rank applies.
        var person = await LoadPersonAsync(branch, shift.StaffMemberId, cancellationToken, requireActive: false);
        await RequireMayPlanAsync(actingId, person, "Removing a shift", cancellationToken);

        db.StaffShifts.Remove(shift);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Shift {ShiftId} removed for staff member {StaffMemberId} at branch {BranchId} by {ActorId}.",
            shift.Id, person.Id, branchId, actingId);
    }

    // ------------------------------------------------------------ helpers

    private sealed record BranchRow(Guid Id, Guid VenueId, string TimeZoneId);

    private async Task<BranchRow> LoadBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new BranchRow(b.Id, b.VenueId, b.TimeZoneId))
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

    /// <summary>The branch's service date now: today, or yesterday while last night's block still runs past midnight.</summary>
    private async Task<DateOnly> ServiceDateAsync(BranchRow branch, CancellationToken cancellationToken)
    {
        var localNow = BranchZone.For(branch.TimeZoneId).ToLocal(clock.UtcNow);
        var today = DateOnly.FromDateTime(localNow);
        var calendar = await OpeningCalendars.ForBranchAsync(db, branch.Id, today.AddDays(-1), today, cancellationToken);

        return calendar.ServiceDateOf(localNow);
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
    /// The person, if they work at this branch: its own people, or an owner or general manager of its venue.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Not of this venue, or at another of its branches.</exception>
    /// <exception cref="DomainStateException">Deactivated, when <paramref name="requireActive"/>.</exception>
    private async Task<StaffMember> LoadPersonAsync(
        BranchRow branch,
        Guid staffMemberId,
        CancellationToken cancellationToken,
        bool requireActive = true)
    {
        var person = await db.StaffMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == staffMemberId && s.VenueId == branch.VenueId, cancellationToken);

        if (person is null
            || person.Role == StaffRole.PlatformAdmin
            || (person.BranchId is { } home && home != branch.Id))
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
    /// The staff edit's rank question, from the stored rows: you plan the shifts of the people you could have created,
    /// and your own.
    /// </summary>
    private async Task RequireMayPlanAsync(
        Guid actingId,
        StaffMember person,
        string operation,
        CancellationToken cancellationToken)
    {
        if (person.Id == actingId)
        {
            return;
        }

        // The guard has read this row already and found it active and in reach; its role is what is asked here.
        var actingRole = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == actingId)
            .Select(s => s.Role)
            .FirstAsync(cancellationToken);

        if (!StaffRoleRules.MayManage(actingRole, person.Role))
        {
            throw new StaffPermissionException(
                $"{operation} for a {person.Role}", actingRole, RequiredRoleFor(person.Role));
        }
    }

    /// <summary>The person's other shifts from the day before to the day after - every one that could clash.</summary>
    private async Task<IReadOnlyList<ShiftSlot>> OthersAroundAsync(
        Guid staffMemberId,
        DateOnly date,
        Guid? exceptShiftId,
        CancellationToken cancellationToken)
    {
        var from = date.AddDays(-1);
        var to = date.AddDays(1);

        var rows = await db.StaffShifts
            .AsNoTracking()
            .Where(s => s.StaffMemberId == staffMemberId
                        && s.Date >= from
                        && s.Date <= to
                        && (exceptShiftId == null || s.Id != exceptShiftId))
            .Select(s => new { s.Date, s.StartsAt, s.EndsAt })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => new ShiftSlot(r.Date, r.StartsAt, r.EndsAt))];
    }

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

    private static StaffShiftView ToView(StaffShift shift, BranchZone zone) => new(
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
