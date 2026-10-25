using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.BranchSettings;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;
using static Yalla.Infrastructure.Services.BranchSettingsLookups;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The weekly opening hours and the special opening days that override them on a date.
/// </summary>
/// <remarks>
/// Like the policy, a save never touches a booking: the live ones the new hours no longer fit are counted and
/// reported. The special days are replaced whole under the branch lock and carry a revision, so a save made from
/// an older list is refused rather than allowed to undo somebody else's.
/// </remarks>
internal sealed class BranchHoursSettings(
    YallaDbContext db,
    IClock clock,
    IStaffBranchGuard branchGuard,
    BranchSettingsHistory history,
    ILogger logger,
    BranchSettingsLookups lookups)
{
    public async Task<IReadOnlyList<OpeningHoursView>> GetOpeningHoursAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the opening hours", cancellationToken);

        return await db.OpeningHours
            .AsNoTracking()
            .Where(h => h.BranchId == branchId)
            .OrderBy(h => h.Day)
            .ThenBy(h => h.OpensAt)
            .Select(h => new OpeningHoursView(h.Day, h.OpensAt, h.ClosesAt, h.ClosesNextDay))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OpeningHoursView>> ReplaceOpeningHoursAsync(
        Guid branchId,
        IReadOnlyList<OpeningHoursBlock> blocks,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Save the opening hours", cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        // Derives ClosesNextDay and refuses overlaps before anything is touched.
        var normalised = OpeningHoursRules.Normalise(blocks);

        var existing = await db.OpeningHours.Where(h => h.BranchId == branch.Id).ToListAsync(cancellationToken);

        await history.RecordAsync(
            branch,
            ActivityActions.SettingsHours,
            existing.OrderBy(h => h.Day).ThenBy(h => h.OpensAt)
                .Select(h => new OpeningHoursView(h.Day, h.OpensAt, h.ClosesAt, h.ClosesNextDay)).ToList(),
            normalised,
            new { blocks = normalised.Count },
            cancellationToken);

        db.OpeningHours.RemoveRange(existing);

        foreach (var block in normalised)
        {
            db.OpeningHours.Add(new OpeningHours(branch.Id, block.Day, block.OpensAt, block.ClosesAt, block.ClosesNextDay));
        }

        // Delete and insert in one transaction: the branch never has half a week.
        await db.SaveChangesAsync(cancellationToken);

        return normalised;
    }

    public async Task<SpecialOpeningDaysView> GetSpecialOpeningDaysAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the special opening days", cancellationToken);

        // Read, not tracked: a tracked branch would answer a later save in the same context with this revision.
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new { b.TimeZoneId, b.SpecialDaysVersion })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var localNow = BranchZone.For(branch.TimeZoneId).ToLocal(clock.UtcNow);
        var today = DateOnly.FromDateTime(localNow);
        var calendar = await OpeningCalendars.ForBranchAsync(
            db, branchId, today.AddDays(-1), today.AddDays(SpecialOpeningDayRules.HorizonDays), cancellationToken);

        return new SpecialOpeningDaysView(VersionOf(branch.SpecialDaysVersion), FromServiceDate(calendar, localNow));
    }

    public async Task<SpecialOpeningDaysChangeResult> ReplaceSpecialOpeningDaysAsync(
        Guid branchId,
        ReplaceSpecialOpeningDaysCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Save the special opening days", cancellationToken);

        // Without the revision a save cannot tell it is about to undo somebody else's holiday.
        if (string.IsNullOrWhiteSpace(command.ExpectedVersion))
        {
            throw new FieldValidationException(new FieldViolation(
                "expectedVersion",
                "Send the version you loaded the special days at, so a save cannot undo somebody else's.",
                FieldBounds.Required));
        }

        if (command.Days is null)
        {
            throw new FieldValidationException(new FieldViolation(
                "days", "Send every special day from today on; an empty list clears them.", FieldBounds.Required));
        }

        // Two editors saving at once are serialised by the branch lock, and the one that loaded an older list is
        // refused rather than allowed to replace the other's whole.
        await using var writeLock = await lookups.LockBranchAsync(branchId, cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        // The row as it is now, inside the lock - not a copy this context tracked before another save committed.
        await db.Entry(branch).ReloadAsync(cancellationToken);

        if (!IsVersion(command.ExpectedVersion, branch.SpecialDaysVersion))
        {
            throw new SpecialOpeningDaysChangedException(VersionOf(branch.SpecialDaysVersion));
        }

        var zone = BranchZone.For(branch.TimeZoneId);
        var nowUtc = clock.UtcNow;
        var today = zone.LocalDateAt(nowUtc);

        // Dates and blocks checked before anything is touched; every problem named, history ignored.
        var normalised = SpecialOpeningDayRules.Normalise(command.Days, today);

        // The hours as they were, from the evening before today to the day after the horizon: every date a
        // changed day or a booking just after one can fall on.
        var windowFrom = today.AddDays(-1);
        var windowTo = today.AddDays(SpecialOpeningDayRules.HorizonDays + 1);
        var before = await OpeningCalendars.ForBranchAsync(db, branchId, windowFrom, windowTo, cancellationToken);

        // From today on, replaced whole. Earlier days are history the Overview's yesterday still reads.
        await writeLock.RunAsync(
            branchId,
            () => db.SpecialOpeningDays
                .Where(d => d.BranchId == branchId && d.Date >= today)
                .ExecuteDeleteAsync(cancellationToken));

        foreach (var day in normalised)
        {
            db.SpecialOpeningDays.Add(new SpecialOpeningDay(branchId, day.Date, day.Note, day.Blocks));
        }

        branch.BumpSpecialDaysVersion();

        await history.RecordAsync(
            branch,
            ActivityActions.SettingsSpecialDays,
            before.SpecialDays.Where(d => d.Date >= today).Select(SpecialOpeningDayView.From).ToList(),
            normalised.Select(d => new { date = d.Date, note = d.Note, blocks = d.Blocks }).ToList(),
            new { days = normalised.Count },
            cancellationToken);

        await SaveUnderLockAsync(writeLock, branchId, cancellationToken);

        var after = await OpeningCalendars.ForBranchAsync(db, branchId, windowFrom, windowTo, cancellationToken);
        var changed = ChangedDates(before.SpecialDays.Where(d => d.Date >= today), normalised);
        var affected = await AffectedBySpecialDaysAsync(branchId, zone, changed, before, after, nowUtc, cancellationToken);

        if (affected.Count > 0)
        {
            logger.LogInformation(
                "Special opening days on branch {BranchId} saved; {Count} live booking(s) no longer fit the hours and were left unchanged.",
                branchId, affected.Count);
        }

        return new SpecialOpeningDaysChangeResult(
            VersionOf(branch.SpecialDaysVersion),
            FromServiceDate(after, zone.ToLocal(nowUtc)),
            affected.Count,
            affected);
    }

    /// <summary>
    /// The console's list: from the service date, as the public page's - so at 01:00 on New Year's Day the Eve whose
    /// 18:00-03:00 is still running is first, and the console's "what a guest sees now" reads the block diners do.
    /// </summary>
    /// <remarks>
    /// That entry is shown, never saved: a save replaces only today on, so sending it back - edited, or left out -
    /// changes nothing. It drops off the list when its block closes.
    /// </remarks>
    private static IReadOnlyList<SpecialOpeningDayView> FromServiceDate(OpeningCalendar calendar, DateTime localNow)
    {
        var from = calendar.ServiceDateOf(localNow);

        return [.. calendar.SpecialDays.Where(d => d.Date >= from).Select(SpecialOpeningDayView.From)];
    }

    /// <summary>
    /// The dates whose hours this save changed: a special day added, removed, or given other blocks. A new note
    /// alone changes no hours.
    /// </summary>
    private static List<DateOnly> ChangedDates(IEnumerable<SpecialDay> before, IEnumerable<SpecialDay> after)
    {
        var old = before.ToDictionary(d => d.Date);
        var now = after.ToDictionary(d => d.Date);

        return [.. old.Keys.Union(now.Keys)
            .Where(date => !old.TryGetValue(date, out var was)
                           || !now.TryGetValue(date, out var @is)
                           || !was.Blocks.SequenceEqual(@is.Blocks))
            .Order()];
    }

    /// <summary>
    /// Live future bookings this save put outside the hours: starting on a changed date, or on the day after one -
    /// inside its block past midnight - that fitted the calendar before the save and do not fit it now.
    /// </summary>
    /// <remarks>
    /// "Newly" is the point. A booking that already did not fit was not put there by this save, and the day after
    /// an unchanged date is not looked at, so adding 6 January does not report the 1 January booking an earlier
    /// save already reported.
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> AffectedBySpecialDaysAsync(
        Guid branchId,
        BranchZone zone,
        IReadOnlyList<DateOnly> changed,
        OpeningCalendar before,
        OpeningCalendar after,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (changed.Count == 0)
        {
            return [];
        }

        var asked = changed.Concat(changed.Select(d => d.AddDays(1))).Distinct().ToList();

        var bookings = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branchId
                        && asked.Contains(r.LocalDate)
                        && r.StartUtc > nowUtc
                        && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.PendingApproval))
            .OrderBy(r => r.StartUtc)
            .Select(r => new { r.Id, r.LocalDate, r.LocalStartTime, r.EndUtc })
            .ToListAsync(cancellationToken);

        return [.. bookings
            .Where(r =>
            {
                var start = r.LocalDate.ToDateTime(r.LocalStartTime);
                var end = zone.ToLocal(r.EndUtc);

                return before.Fits(start, end) && !after.Fits(start, end);
            })
            .Select(r => r.Id)];
    }
}
