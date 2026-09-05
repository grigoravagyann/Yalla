using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reservation policy, opening hours and the floor plan for one branch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Settings changes never touch bookings.</b> Shortening the booking window or the turn time
/// applies to future bookings; the ones already made are counted and reported so the owner knows,
/// and are otherwise left exactly as they were.
/// </para>
/// <para>
/// <b>The floor plan is replaced whole.</b> Canvas, areas and tables land in one <c>SaveChanges</c>
/// and so one transaction; a partially applied plan is a broken room. A table left out of the
/// plan is deleted only if nothing ever happened at it - otherwise it is deactivated, because a
/// reservation, session or tab that pointed at it must still resolve.
/// </para>
/// <para>
/// <b>A QR token never changes on edit.</b> Nothing in the replace path writes it. The one way it
/// changes is <see cref="RegenerateQrTokenAsync"/>, which is audited.
/// </para>
/// </remarks>
internal sealed class BranchSettingsService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ILogger<BranchSettingsService> logger) : IBranchSettingsService
{
    // ------------------------------------------------------------ reservation policy

    public async Task<ReservationPolicyView> GetReservationPolicyAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        var branch = await LoadBranchAsync(branchId, cancellationToken);

        return ReservationPolicyView.From(branch.ReservationPolicy);
    }

    public async Task<ReservationPolicyChangeResult> UpdateReservationPolicyAsync(
        Guid branchId,
        ReservationPolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var branch = await LoadBranchAsync(branchId, cancellationToken);

        // Constructor checks, then the editing bounds. Refuses; never clamps.
        var policy = command.ToPolicy();
        var nowUtc = clock.UtcNow;

        // Which live bookings the new rules would not have allowed. Counted, reported, and left
        // alone: a settings change must never rewrite or cancel a reservation as a side effect.
        var windowEndsUtc = nowUtc.AddDays(policy.BookingWindowDays);

        var affected = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branch.Id
                        && r.StartUtc > nowUtc
                        && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.PendingApproval))
            .Where(r => r.StartUtc > windowEndsUtc
                        || EF.Functions.DateDiffMinute(r.StartUtc, r.EndUtc) != policy.TurnTimeMinutes)
            .OrderBy(r => r.StartUtc)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        branch.UpdateReservationPolicy(policy);
        await db.SaveChangesAsync(cancellationToken);

        if (affected.Count > 0)
        {
            logger.LogInformation(
                "Reservation policy on branch {BranchId} changed; {Count} existing booking(s) fall outside the new rules and were left unchanged.",
                branch.Id, affected.Count);
        }

        return new ReservationPolicyChangeResult(ReservationPolicyView.From(policy), affected.Count, affected);
    }

    // ------------------------------------------------------------ opening hours

    public async Task<IReadOnlyList<OpeningHoursView>> GetOpeningHoursAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        await db.OpeningHours
            .AsNoTracking()
            .Where(h => h.BranchId == branchId)
            .OrderBy(h => h.Day)
            .ThenBy(h => h.OpensAt)
            .Select(h => new OpeningHoursView(h.Day, h.OpensAt, h.ClosesAt, h.ClosesNextDay))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OpeningHoursView>> ReplaceOpeningHoursAsync(
        Guid branchId,
        IReadOnlyList<OpeningHoursBlock> blocks,
        CancellationToken cancellationToken = default)
    {
        var branch = await LoadBranchAsync(branchId, cancellationToken);

        // Derives ClosesNextDay and refuses overlaps before anything is touched.
        var normalised = OpeningHoursRules.Normalise(blocks);

        var existing = await db.OpeningHours.Where(h => h.BranchId == branch.Id).ToListAsync(cancellationToken);
        db.OpeningHours.RemoveRange(existing);

        foreach (var block in normalised)
        {
            db.OpeningHours.Add(new OpeningHours(branch.Id, block.Day, block.OpensAt, block.ClosesAt, block.ClosesNextDay));
        }

        // Delete and insert in one transaction: the branch never has half a week.
        await db.SaveChangesAsync(cancellationToken);

        return normalised;
    }

    // ------------------------------------------------------------ floor plan

    public async Task<FloorPlanView> GetFloorPlanAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new { b.Id, b.FloorWidth, b.FloorHeight })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var areas = await db.FloorAreas
            .AsNoTracking()
            .Where(a => a.BranchId == branchId)
            .OrderBy(a => a.DisplayOrder)
            .Select(a => new FloorAreaView(a.Id, a.Name, a.DisplayOrder))
            .ToListAsync(cancellationToken);

        var tables = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.BranchId == branchId)
            .OrderBy(t => t.Label)
            .Select(TableProjection)
            .ToListAsync(cancellationToken);

        return new FloorPlanView(branch.Id, branch.FloorWidth, branch.FloorHeight, areas, tables);
    }

    public async Task<FloorPlanReplaceResult> ReplaceFloorPlanAsync(
        Guid branchId,
        ReplaceFloorPlanCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Two collections on one root: split, so the areas do not multiply the tables on the wire.
        var branch = await db.Branches
            .Include(b => b.FloorAreas)
            .Include(b => b.DiningTables)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var areasInput = command.Areas ?? [];
        var tablesInput = command.Tables ?? [];

        // The pure rules first: unique labels, inside the canvas (errors); overlaps (warnings).
        var validation = FloorPlanRules.Validate(command.FloorWidth, command.FloorHeight, tablesInput);

        var errors = validation.Errors.ToList();

        var unnamedAreas = areasInput.Count(a => string.IsNullOrWhiteSpace(a.Name));

        if (unnamedAreas > 0)
        {
            errors.Add($"{unnamedAreas} area(s) in the plan have no name.");
        }

        // Shape problems are thrown before anything below reads a label or a name, so a payload
        // with a missing field answers "that field is missing" rather than faulting on it.
        if (errors.Count > 0)
        {
            throw new FloorPlanInvalidException(errors, validation.TablesOutsideCanvas, validation.DuplicateLabels);
        }

        var duplicateAreas = areasInput
            .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateAreas.Count > 0)
        {
            errors.Add($"Area names must be unique within a branch. Repeated: {string.Join(", ", duplicateAreas)}.");
        }

        var unknownAreas = tablesInput
            .Where(t => t.FloorAreaName is not null
                        && !areasInput.Any(a => string.Equals(a.Name.Trim(), t.FloorAreaName.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Select(t => $"{t.Label} -> {t.FloorAreaName}")
            .ToList();

        if (unknownAreas.Count > 0)
        {
            errors.Add($"Tables reference areas that are not in the plan: {string.Join(", ", unknownAreas)}.");
        }

        // ---- areas
        var areasByName = new Dictionary<string, FloorArea>(StringComparer.OrdinalIgnoreCase);
        var keptAreaIds = new HashSet<Guid>();

        foreach (var input in areasInput)
        {
            var area = (input.Id is { } id ? branch.FloorAreas.FirstOrDefault(a => a.Id == id) : null)
                       ?? branch.FloorAreas.FirstOrDefault(a => string.Equals(a.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase));

            if (area is null)
            {
                area = new FloorArea(branch.Id, input.Name, input.DisplayOrder);
                db.FloorAreas.Add(area);
            }
            else
            {
                area.Rename(input.Name);
                area.SetDisplayOrder(input.DisplayOrder);
            }

            areasByName[area.Name] = area;
            keptAreaIds.Add(area.Id);
        }

        // ---- tables: match by id, then by label; anything else is new.
        var existingTables = branch.DiningTables.ToList();
        var matched = new HashSet<Guid>();
        var newTables = new List<(FloorTableInput Input, DiningTable Table)>();
        var updatedTables = new List<(FloorTableInput Input, DiningTable Table)>();

        foreach (var input in tablesInput)
        {
            var label = input.Label.Trim();

            var table = (input.Id is { } id ? existingTables.FirstOrDefault(t => t.Id == id) : null)
                        ?? existingTables.FirstOrDefault(t => string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase));

            if (table is not null && !matched.Add(table.Id))
            {
                errors.Add($"Table {label} is matched by more than one entry in the plan.");
                continue;
            }

            if (table is null)
            {
                newTables.Add((input, null!));
            }
            else
            {
                updatedTables.Add((input, table));
            }
        }

        // ---- tables left out: history decides delete versus deactivate.
        var omitted = existingTables.Where(t => !matched.Contains(t.Id)).ToList();
        var omittedIds = omitted.Select(t => t.Id).ToList();
        var used = omittedIds.Count == 0 ? [] : await TablesWithHistoryAsync(omittedIds, cancellationToken);

        // A label about to be reused by a NEW table while a deactivated table keeps it would hit
        // the unique index. Name it rather than let the index answer.
        var retainedLabels = omitted
            .Where(t => used.Contains(t.Id))
            .Select(t => t.Label)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var clashing = newTables
            .Where(n => retainedLabels.Contains(n.Input.Label.Trim()))
            .Select(n => n.Input.Label.Trim())
            .ToList();

        if (clashing.Count > 0)
        {
            errors.Add(
                $"Label(s) {string.Join(", ", clashing)} belong to tables with history that this plan omits. "
                + "Those tables are deactivated, not deleted, and keep their labels - reuse them by keeping the table (with its id) instead.");
        }

        // A table with diners at it must not vanish from the plan. DeleteTableAsync already refuses
        // this one table at a time; a plan that leaves the table out is the same act, and a floor
        // view that stops showing an occupied table is the failure the whole state machine exists
        // to prevent.
        var seated = omitted
            .Where(t => t.Status == TableStatus.Occupied)
            .Select(t => t.Label)
            .ToList();

        if (seated.Count > 0)
        {
            errors.Add(
                $"Table(s) {string.Join(", ", seated)} have a party seated at them and cannot be removed from the plan. "
                + "Free them first, or keep them in the plan.");
        }

        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            throw new FloorPlanInvalidException(errors, validation.TablesOutsideCanvas, validation.DuplicateLabels);
        }

        var deactivated = new List<string>();
        var removed = new List<string>();

        foreach (var table in omitted)
        {
            if (used.Contains(table.Id))
            {
                table.SetActive(false);

                // Its area may be leaving with this plan; a deactivated table need not point at one.
                if (table.FloorAreaId is { } areaId && !keptAreaIds.Contains(areaId))
                {
                    table.AssignToArea(null);
                }

                deactivated.Add(table.Label);
            }
            else
            {
                db.DiningTables.Remove(table);
                removed.Add(table.Label);
            }
        }

        // Two tables trading labels - renumbering a room during onboarding - is a legal plan that a
        // single pass cannot write: the unique index on (branch, label) is checked per statement, so
        // "table A becomes B" lands while the real B still holds the name. Park the movers on
        // throwaway labels, flush, then write the real ones. One transaction either way, so a plan
        // still applies whole or not at all.
        var currentLabels = existingTables.ToDictionary(t => t.Id, t => t.Label);

        var movers = updatedTables
            .Where(u => !string.Equals(currentLabels[u.Table.Id], u.Input.Label.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(u => existingTables.Any(other => other.Id != u.Table.Id
                                                   && string.Equals(other.Label, u.Input.Label.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        IDbContextTransaction? transaction = null;

        if (movers.Count > 0)
        {
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            foreach (var (_, table) in movers)
            {
                table.Relabel(StagingLabel());
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var (input, table) in updatedTables)
        {
            // Geometry, label, seats, area, bookability. Never the QR token.
            table.Relabel(input.Label);
            table.SetSeats(input.Seats);
            table.MoveTo(input.X, input.Y, input.RotationDegrees);
            table.Resize(input.Width, input.Height);
            table.SetShape(input.Shape);
            table.AssignToArea(AreaId(areasByName, input.FloorAreaName));
            table.SetBookable(input.IsBookable);
            table.SetActive(true);
        }

        foreach (var (input, _) in newTables)
        {
            db.DiningTables.Add(new DiningTable(
                branch.Id,
                input.Label,
                input.Seats,
                input.X,
                input.Y,
                input.Width,
                input.Height,
                input.Shape,
                AreaId(areasByName, input.FloorAreaName),
                input.RotationDegrees,
                input.IsBookable));
        }

        // Areas the plan dropped. Any table still pointing at one is a deactivated table whose
        // area we just cleared, so the delete is safe.
        foreach (var area in branch.FloorAreas.Where(a => !keptAreaIds.Contains(a.Id)).ToList())
        {
            foreach (var straggler in existingTables.Where(t => t.FloorAreaId == area.Id))
            {
                straggler.AssignToArea(null);
            }

            db.FloorAreas.Remove(area);
        }

        branch.ResizeFloor(command.FloorWidth, command.FloorHeight);

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TableLabelPerBranch))
        {
            db.ChangeTracker.Clear();
            throw new FloorPlanInvalidException(
                ["A table label in this plan is already used by another table in the branch."], [], []);
        }
        finally
        {
            // Disposing an uncommitted transaction rolls it back, so a failure between the two
            // phases leaves the staging labels nowhere.
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        logger.LogInformation(
            "Floor plan on branch {BranchId} replaced: {Updated} updated, {Added} added, {Deactivated} deactivated, {Removed} removed, {Warnings} warning(s).",
            branch.Id, updatedTables.Count, newTables.Count, deactivated.Count, removed.Count, validation.Warnings.Count);

        return new FloorPlanReplaceResult(
            await GetFloorPlanAsync(branch.Id, cancellationToken),
            validation.Warnings,
            deactivated,
            removed);
    }

    public async Task<FloorAreaView> CreateFloorAreaAsync(
        Guid branchId,
        FloorAreaCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var branch = await LoadBranchAsync(branchId, cancellationToken);

        var area = new FloorArea(branch.Id, command.Name, command.DisplayOrder);
        db.FloorAreas.Add(area);
        await db.SaveChangesAsync(cancellationToken);

        return new FloorAreaView(area.Id, area.Name, area.DisplayOrder);
    }

    public async Task<FloorAreaView> UpdateFloorAreaAsync(
        Guid branchId,
        Guid areaId,
        FloorAreaCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var area = await db.FloorAreas.FirstOrDefaultAsync(a => a.Id == areaId && a.BranchId == branchId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Floor area {areaId} was not found at this branch.");

        area.Rename(command.Name);
        area.SetDisplayOrder(command.DisplayOrder);
        await db.SaveChangesAsync(cancellationToken);

        return new FloorAreaView(area.Id, area.Name, area.DisplayOrder);
    }

    public async Task DeleteFloorAreaAsync(Guid branchId, Guid areaId, CancellationToken cancellationToken = default)
    {
        var area = await db.FloorAreas.FirstOrDefaultAsync(a => a.Id == areaId && a.BranchId == branchId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Floor area {areaId} was not found at this branch.");

        // Tables stay; they just stop belonging to a zone.
        var tables = await db.DiningTables.Where(t => t.FloorAreaId == area.Id).ToListAsync(cancellationToken);

        foreach (var table in tables)
        {
            table.AssignToArea(null);
        }

        db.FloorAreas.Remove(area);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TableDeletionResult> DeleteTableAsync(
        Guid branchId,
        Guid tableId,
        CancellationToken cancellationToken = default)
    {
        var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == tableId && t.BranchId == branchId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Table {tableId} was not found at this branch.");

        if (table.Status == TableStatus.Occupied)
        {
            throw new DomainStateException($"Table {table.Label} has a party seated at it. Free it first.");
        }

        var used = await TablesWithHistoryAsync([table.Id], cancellationToken);

        if (used.Contains(table.Id))
        {
            table.SetActive(false);
            await db.SaveChangesAsync(cancellationToken);

            return new TableDeletionResult(
                table.Id,
                table.Label,
                Deleted: false,
                Deactivated: true,
                $"Table {table.Label} has reservations, seatings or tabs in its history, so it was deactivated rather than deleted. "
                + "Its records are kept; it no longer appears on the floor plan or takes bookings.");
        }

        db.DiningTables.Remove(table);
        await db.SaveChangesAsync(cancellationToken);

        return new TableDeletionResult(
            table.Id, table.Label, Deleted: true, Deactivated: false, $"Table {table.Label} was never used and has been removed.");
    }

    public async Task<FloorTableView> RegenerateQrTokenAsync(Guid tableId, CancellationToken cancellationToken = default)
    {
        var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == tableId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Table {tableId} was not found.");

        var previous = table.QrToken;
        table.RegenerateQrToken();

        // The printed code on the physical table just stopped working. That is worth a record of
        // who did it and when, whatever their role.
        // The previous token only - it is dead the moment this commits, and recording it is what
        // lets someone answer "which code stopped working". The NEW token is a live credential:
        // anyone who can read it can open a tab on that table anonymously, so it does not go into
        // an append-only log that reporting and backups can reach.
        PlatformAudit.Record(db, actor, clock, "table.regenerate-qr", "DiningTable", table.Id, new
        {
            table.BranchId,
            table.Label,
            previousQrToken = previous,
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "QR token regenerated for table {TableId} ({Label}) at branch {BranchId} by staff member {StaffMemberId}.",
            table.Id, table.Label, table.BranchId, actor.StaffMemberId);

        return await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == table.Id)
            .Select(TableProjection)
            .FirstAsync(cancellationToken);
    }

    // ------------------------------------------------------------ helpers

    private async Task<Branch> LoadBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
        ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

    /// <summary>
    /// Which of these tables anything ever happened at: a booking, a seating, a bill - or an entry
    /// in the state-change log.
    /// </summary>
    /// <remarks>
    /// The audit log counts, and it is the easy one to forget. A table that was held for a late
    /// party, or marked out of service for a wobbly leg, has no reservation, no session and no tab,
    /// but it does have <c>TableStateChange</c> rows - and that foreign key is <c>Restrict</c>, so
    /// deleting the table would fail in the database with a constraint error nobody can act on
    /// instead of quietly deactivating it here.
    /// </remarks>
    private async Task<HashSet<Guid>> TablesWithHistoryAsync(IReadOnlyList<Guid> tableIds, CancellationToken cancellationToken)
    {
        var reserved = db.Reservations.Where(r => tableIds.Contains(r.DiningTableId)).Select(r => r.DiningTableId);
        var seated = db.TableSessions.Where(s => tableIds.Contains(s.DiningTableId)).Select(s => s.DiningTableId);
        var billed = db.Tabs.Where(t => tableIds.Contains(t.DiningTableId)).Select(t => t.DiningTableId);
        var audited = db.TableStateChanges.Where(c => tableIds.Contains(c.DiningTableId)).Select(c => c.DiningTableId);

        var used = await reserved.Union(seated).Union(billed).Union(audited).Distinct().ToListAsync(cancellationToken);

        return [.. used];
    }

    /// <summary>A label nothing else can be holding, for the middle of a two-phase rename.</summary>
    private static string StagingLabel() => $"~{Guid.NewGuid():N}"[..9];

    private static Guid? AreaId(Dictionary<string, FloorArea> areasByName, string? areaName) =>
        areaName is null ? null : areasByName[areaName.Trim()].Id;

    /// <summary>
    /// A table as the editor reads it, including whether deleting it would actually work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IsDeletable</c> is four <c>EXISTS</c> subqueries rather than the three the floor plan
    /// obviously needs, and the fourth is the one that matters: <see cref="TablesWithHistoryAsync"/>
    /// counts <c>TableStateChange</c> rows too, because that foreign key is <c>Restrict</c>. A table
    /// held for a late party has no booking, no seating and no bill, but it does have audit rows -
    /// and if this flag disagreed with the rule the delete actually applies, the editor would offer
    /// a delete that then refuses, which is exactly the confusion the flag exists to remove. The two
    /// must be read as one rule; changing either alone is a bug.
    /// </para>
    /// <para>
    /// An instance expression rather than a static one, because it closes over the context. It runs
    /// once per editing session, not on the floor screen's refresh loop, so four correlated
    /// subqueries against indexed foreign keys are the right trade for one round trip.
    /// </para>
    /// </remarks>
    private System.Linq.Expressions.Expression<Func<DiningTable, FloorTableView>> TableProjection =>
        t => new FloorTableView(
            t.Id, t.Label, t.Seats, t.X, t.Y, t.Width, t.Height, t.RotationDegrees, t.Shape,
            t.FloorAreaId, t.IsBookable, t.IsActive, t.QrToken, t.Status,
            !db.Reservations.Any(r => r.DiningTableId == t.Id)
            && !db.TableSessions.Any(ts => ts.DiningTableId == t.Id)
            && !db.Tabs.Any(tb => tb.DiningTableId == t.Id)
            && !db.TableStateChanges.Any(c => c.DiningTableId == t.Id));
}
