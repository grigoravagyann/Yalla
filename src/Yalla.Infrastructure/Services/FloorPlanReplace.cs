using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;
using static Yalla.Infrastructure.Services.BranchSettingsLookups;
using static Yalla.Infrastructure.Services.FloorPlanMatching;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Replacing the floor plan whole: rooms, their photos and every table's pin, in one transaction under the branch
/// lock. The rules that decide the plan without the database are <see cref="FloorPlanMatching"/>'s.
/// </summary>
internal sealed class FloorPlanReplace(
    YallaDbContext db,
    ICurrentActor actor,
    IStaffBranchGuard branchGuard,
    ILogger logger,
    BranchSettingsLookups lookups,
    FloorPlanTables floorTables,
    FloorPlanReads reads)
{
    public async Task<FloorPlanReplaceResult> ReplaceFloorPlanAsync(
        Guid branchId,
        ReplaceFloorPlanCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Replace the floor plan", cancellationToken);

        if (string.IsNullOrWhiteSpace(command.ExpectedVersion))
        {
            throw new FieldValidationException(new FieldViolation(
                "expectedVersion",
                "Send the version of the floor plan you loaded, so a save cannot overwrite somebody else's.",
                FieldBounds.Required));
        }

        // Held to the end: the version is compared, the plan written and the version bumped as one
        // step, so two editors who loaded the same revision cannot both save.
        await using var writeLock = await lookups.LockBranchAsync(branchId, cancellationToken);

        // Two collections on one root, and the rooms' photos under one of them: split, so the areas
        // and photos do not multiply the tables on the wire.
        var branch = await db.Branches
            .Include(b => b.FloorAreas)
            .ThenInclude(a => a.Photos)
            .Include(b => b.DiningTables)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        if (!IsVersion(command.ExpectedVersion, branch.FloorPlanVersion))
        {
            throw new FloorPlanChangedException(VersionOf(branch.FloorPlanVersion));
        }

        var areasInput = command.Areas ?? [];
        var tablesInput = command.Tables ?? [];

        // The pure rules first: every table labelled, labels unique (errors); unpinned bookable
        // tables (a warning).
        var validation = FloorPlanRules.Validate(tablesInput);

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
            throw new FloorPlanInvalidException(errors, validation.DuplicateLabels);
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
            .Where(t => t.FloorAreaName is not null && AreaIndex(areasInput, t.FloorAreaName) < 0)
            .Select(t => $"{t.Label} -> {t.FloorAreaName}")
            .ToList();

        if (unknownAreas.Count > 0)
        {
            errors.Add($"Tables reference areas that are not in the plan: {string.Join(", ", unknownAreas)}.");
        }

        // Which stored room each entry is, decided once for the whole plan - so the photo and pin
        // rules below and the writes after them agree on it.
        var storedAreas = MatchAreas(areasInput, branch.FloorAreas, errors);

        // Thrown here rather than with the rest below: the photo and pin rules that follow find a
        // table's room by name, which only means something once the names are unique and known.
        if (errors.Count > 0)
        {
            throw new FloorPlanInvalidException(errors, validation.DuplicateLabels);
        }

        // ---- photos and pins: every field-named rule, then whose photos they are.
        var photoRoom = PlanRoomPhotos(areasInput);

        var currentRoomPhotos = branch.FloorAreas
            .SelectMany(a => a.Photos)
            .Select(p => p.PhotoId)
            .ToHashSet();

        // What each room in the plan showed before this save: none for a new one.
        var previousPhotos = storedAreas
            .Select(a => (IReadOnlySet<Guid>)(a?.Photos.Select(p => p.PhotoId).ToHashSet() ?? []))
            .ToList();

        var pins = PlanPins(tablesInput, areasInput, photoRoom, previousPhotos, currentRoomPhotos);

        // Refused the way the gallery refuses one: a photo uploaded for another branch is simply not
        // found here. Pins need no check of their own - each was just held to its room's photos.
        if (photoRoom.Count > 0 && await CountOurPhotosLockedAsync(branchId, [.. photoRoom.Keys], cancellationToken) != photoRoom.Count)
        {
            throw new KeyNotFoundException("A room photo was not found at this branch.");
        }

        // ---- areas, and their photos in order
        var areasByName = new Dictionary<string, FloorArea>(StringComparer.OrdinalIgnoreCase);
        var keptAreaIds = new HashSet<Guid>();
        var photosByArea = new Dictionary<Guid, HashSet<Guid>>();

        for (var index = 0; index < areasInput.Count; index++)
        {
            var input = areasInput[index];
            var area = storedAreas[index];

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

            var wanted = input.PhotoIds ?? [];
            photosByArea[area.Id] = [.. wanted];

            // Replace-whole, row by row: a photo the room keeps keeps its row and only moves; one it
            // drops loses its row; a new one gets one. Moving a photo between rooms is a drop here and
            // an add there - the unique index is per room, so the two never collide.
            foreach (var dropped in area.Photos.Where(p => !wanted.Contains(p.PhotoId)).ToList())
            {
                db.FloorAreaPhotos.Remove(dropped);
            }

            for (var position = 0; position < wanted.Count; position++)
            {
                var kept = area.Photos.FirstOrDefault(p => p.PhotoId == wanted[position]);

                if (kept is null)
                {
                    db.FloorAreaPhotos.Add(new FloorAreaPhoto(area.Id, wanted[position], position));
                }
                else
                {
                    kept.MoveTo(position);
                }
            }
        }

        // ---- tables: by id for every entry, then by label among the active tables no id claimed.
        var existingTables = branch.DiningTables.ToList();
        var storedTables = MatchTables(tablesInput, existingTables, errors);
        var matched = storedTables.OfType<DiningTable>().Select(t => t.Id).ToHashSet();
        var newTables = new List<(int Index, FloorTableInput Input)>();
        var updatedTables = new List<(int Index, FloorTableInput Input, DiningTable Table)>();

        for (var index = 0; index < tablesInput.Count; index++)
        {
            if (storedTables[index] is { } table)
            {
                updatedTables.Add((index, tablesInput[index], table));
            }
            else
            {
                newTables.Add((index, tablesInput[index]));
            }
        }

        // ---- tables left out: history decides delete versus deactivate. The editor never sends an
        // out-of-service table, so every save leaves those out too; they stay as they are.
        var omitted = existingTables.Where(t => !matched.Contains(t.Id)).ToList();
        var omittedIds = omitted.Select(t => t.Id).ToList();
        var used = omittedIds.Count == 0 ? [] : await floorTables.TablesWithHistoryAsync(omittedIds, cancellationToken);

        errors.AddRange(LabelsHeldByOmittedTables(tablesInput, storedTables, omitted, used));

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
            throw new FloorPlanInvalidException(errors, validation.DuplicateLabels);
        }

        // A table somebody has booked is not left out either: deactivating it would strand the
        // booking on a table the floor no longer shows. Its own 409, after the plan's own errors,
        // naming the tables and the bookings so the manager knows what to move.
        var usedIds = omitted.Where(t => used.Contains(t.Id)).Select(t => t.Id).ToList();

        if (usedIds.Count > 0)
        {
            await floorTables.RefuseUpcomingBookingsAsync(usedIds, cancellationToken);
        }

        var deactivated = new List<string>();
        var removed = new List<string>();

        foreach (var table in omitted)
        {
            if (used.Contains(table.Id))
            {
                // Reported only when this save is the one that took it out of service: an
                // out-of-service table is left out of every save, and listing it each time tells
                // the manager something happened that did not.
                if (table.IsActive)
                {
                    deactivated.Add(table.Label);
                }

                table.SetActive(false);

                // Its area may be leaving with this plan; a deactivated table need not point at one.
                if (table.FloorAreaId is { } areaId && !keptAreaIds.Contains(areaId))
                {
                    table.AssignToArea(null);
                }

                // Nor at a photo its room no longer shows: the pin is a foreign key, and a stale one
                // would keep a picture the venue removed from ever being swept.
                if (table.PhotoId is { } photoId
                    && (table.FloorAreaId is not { } roomId
                        || !photosByArea.TryGetValue(roomId, out var roomPhotos)
                        || !roomPhotos.Contains(photoId)))
                {
                    table.TakeOffPhoto();
                }
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
        // throwaway labels, flush, then write the real ones. Both flushes are inside the branch
        // lock's transaction, so a plan still applies whole or not at all.
        //
        // Every renamed table is parked, not only one whose new label another table holds: renaming
        // 5 to 13 and adding a new 5 frees a label that an insert in the same flush takes, and
        // nothing here should rest on the order the provider writes an update and an insert in.
        var movers = updatedTables
            .Where(u => !string.Equals(u.Table.Label, u.Input.Label.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (movers.Count > 0)
        {
            foreach (var (_, _, table) in movers)
            {
                table.Relabel(StagingLabel());
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var (index, input, table) in updatedTables)
        {
            // Label, seats, room, bookability and pin. Never the QR token.
            table.Relabel(input.Label);
            table.SetSeats(input.Seats);
            table.AssignToArea(AreaId(areasByName, input.FloorAreaName));
            table.SetBookable(input.IsBookable);
            table.SetActive(true);
            table.PlaceOnPhoto(pins[index].PhotoId, pins[index].X, pins[index].Y);
        }

        foreach (var (index, input) in newTables)
        {
            var table = new DiningTable(
                branch.Id,
                input.Label,
                input.Seats,
                AreaId(areasByName, input.FloorAreaName),
                input.IsBookable);

            table.PlaceOnPhoto(pins[index].PhotoId, pins[index].X, pins[index].Y);
            db.DiningTables.Add(table);
        }

        // Areas the plan dropped. Their photo rows go with them (cascade). Any table still pointing
        // at one is a deactivated table whose area we just cleared, so the delete is safe.
        foreach (var area in branch.FloorAreas.Where(a => !keptAreaIds.Contains(a.Id)).ToList())
        {
            foreach (var straggler in existingTables.Where(t => t.FloorAreaId == area.Id))
            {
                straggler.AssignToArea(null);
                straggler.TakeOffPhoto();
            }

            db.FloorAreas.Remove(area);
        }

        // With the room routes (add, rename, delete), the places the version moves.
        branch.BumpFloorPlanVersion();

        try
        {
            await SaveUnderLockAsync(writeLock, branchId, cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TableLabelPerBranch))
        {
            db.ChangeTracker.Clear();
            throw new FloorPlanInvalidException(
                ["A table label in this plan is already used by another table in the branch."], []);
        }

        var warnings = validation.Warnings.ToList();

        // Every pin the client sent that PlanPins took off rather than refused: on a photo the plan
        // dropped from every room, or moved out of the table's room.
        var unpinned = tablesInput
            .Select((input, index) => (input, index))
            .Where(x => x.input.PhotoId is not null && pins[x.index].PhotoId is null)
            .ToList();

        var unpinnedByRemoval = unpinned
            .Where(x => !photoRoom.ContainsKey(x.input.PhotoId!.Value))
            .Select(x => x.input.Label.Trim())
            .ToList();

        var unpinnedByMove = unpinned
            .Where(x => photoRoom.ContainsKey(x.input.PhotoId!.Value))
            .Select(x => x.input.Label.Trim())
            .ToList();

        if (unpinnedByRemoval.Count > 0)
        {
            warnings.Add(
                $"Table(s) {string.Join(", ", unpinnedByRemoval)} were taken off photos this plan removed from every room.");
        }

        if (unpinnedByMove.Count > 0)
        {
            warnings.Add(
                $"Table(s) {string.Join(", ", unpinnedByMove)} were taken off photos this plan moved to another room.");
        }

        logger.LogInformation(
            "Floor plan on branch {BranchId} replaced at version {Version} by staff member {StaffMemberId}: {Rooms} room(s) with {Photos} photo(s); {Updated} table(s) updated, {Added} added, {Deactivated} deactivated, {Removed} removed, {Warnings} warning(s).",
            branch.Id, branch.FloorPlanVersion, actor.StaffMemberId, areasInput.Count, photoRoom.Count, updatedTables.Count, newTables.Count, deactivated.Count, removed.Count, warnings.Count);

        return new FloorPlanReplaceResult(
            await reads.ReadFloorPlanAsync(branch.Id, cancellationToken),
            warnings,
            deactivated,
            removed);
    }

    /// <summary>
    /// How many of these photos are this branch's, holding each one found until the save commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hold is for the orphan sweep. A photo just uploaded and not yet in any room is an orphan
    /// to it, and if the sweep deleted the row between this check and the save, the save's insert of
    /// the room's photo row would fail on the foreign key - a 500 for a plan that was valid when it
    /// was checked. <c>UPDLOCK</c> is held to the end of the transaction (the branch lock's), so the
    /// sweep's delete waits for the save; by then the room shows the photo, and the sweep's own
    /// foreign-key handling keeps it. A sweep that got there first leaves a row this count does not
    /// find: 404, as for any photo that is not here.
    /// </para>
    /// <para>
    /// One parameter per id rather than a JSON list, so each id is a seek on the primary key and only
    /// those rows are held. <c>UPDLOCK</c> does not block readers.
    /// </para>
    /// </remarks>
    private async Task<int> CountOurPhotosLockedAsync(Guid branchId, IReadOnlyList<Guid> photoIds, CancellationToken cancellationToken)
    {
        var parameters = photoIds
            .Select((id, i) => new SqlParameter($"@photo{i}", id))
            .ToList();

        var sql =
            "SELECT [Id] AS [Value] FROM [Photos] WITH (UPDLOCK, ROWLOCK) "
            + $"WHERE [BranchId] = @branchId AND [Id] IN ({string.Join(", ", parameters.Select(p => p.ParameterName))})";

        parameters.Add(new SqlParameter("@branchId", branchId));

        var found = await db.Database
            .SqlQueryRaw<Guid>(sql, [.. parameters])
            .ToListAsync(cancellationToken);

        return found.Count;
    }
}
