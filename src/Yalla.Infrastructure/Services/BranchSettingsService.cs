using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
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
/// <b>The floor plan is replaced whole.</b> Rooms, their photos and every table's pin land in one
/// transaction; a partially applied plan is a room whose pins point at pictures it no longer shows. A table left out of the plan is deleted only if nothing
/// ever happened at it - otherwise it is deactivated, because a reservation, session or tab that
/// pointed at it must still resolve.
/// </para>
/// <para>
/// <b>A QR token never changes on edit.</b> Nothing in the replace path writes it. The one way it
/// changes is <see cref="RegenerateQrTokenAsync"/>, which is audited.
/// </para>
/// <para>
/// <b>Who (K4).</b> Every method checks the caller again from the stored staff row through
/// <see cref="IStaffBranchGuard"/>, underneath the route's <c>BranchScoped</c> policy: a manager
/// with a home branch is confined to it even while their token still says otherwise.
/// </para>
/// <para>
/// <b>One writer of pins.</b> A table's pin is a place on one of its room's photos, and the floor-plan
/// replace is the only call that sets one. It takes an update lock on the branch row first and reads
/// everything it decides on inside it; deleting a room, which takes pins off, takes the same lock.
/// The branch's cover photo has nothing to do with pins and changes freely.
/// </para>
/// </remarks>
internal sealed class BranchSettingsService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IStaffBranchGuard branchGuard,
    ILogger<BranchSettingsService> logger) : IBranchSettingsService
{
    // ------------------------------------------------------------ the public page

    public async Task<PublicProfileView> GetPublicProfileAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the public profile", cancellationToken);

        var branch = await LoadBranchAsync(branchId, cancellationToken);

        return await PublicProfileOfAsync(branch, cancellationToken);
    }

    public async Task<PublicProfileView> UpdatePublicProfileAsync(
        Guid branchId,
        PublicProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Save the public profile", cancellationToken);

        var branch = await LoadBranchAsync(branchId, cancellationToken);

        // The photo id is caller-supplied and not secret - one venue's card URL names it. Checked
        // before anything is written, so a foreign id refuses the whole form and leaves the old
        // phone and switch as they were. Refused the way a menu item refuses a foreign photo: the
        // id is simply not found at this branch.
        if (command.CoverPhotoId is { } photoId
            && !await db.Photos.AnyAsync(p => p.Id == photoId && p.BranchId == branchId, cancellationToken))
        {
            throw new KeyNotFoundException($"Photo {photoId} was not found at this branch.");
        }

        // The number first: a bad one must refuse the whole form rather than leave the branch
        // switched on for bookings with its old phone still published.
        branch.SetPhoneE164(command.PhoneE164);
        branch.SetAcceptsWebBookings(command.AcceptsWebBookings);

        // The card's picture only. Tables are pinned on room photos, not on the cover, so changing it
        // moves nothing else.
        branch.SetCoverPhoto(command.CoverPhotoId);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Branch {BranchId} public profile saved; web bookings {State}; cover photo {Cover}.",
            branch.Id, command.AcceptsWebBookings ? "on" : "off", command.CoverPhotoId?.ToString() ?? "none");

        return await PublicProfileOfAsync(branch, cancellationToken);
    }

    /// <summary>
    /// The branch's public settings with its cover photo loaded - the branch row alone carries only
    /// the id, and a view built from that would report the card as having no picture.
    /// </summary>
    private async Task<PublicProfileView> PublicProfileOfAsync(Branch branch, CancellationToken cancellationToken)
    {
        var cover = branch.CoverPhotoId is { } photoId
            ? await db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == photoId, cancellationToken)
            : null;

        return new PublicProfileView(
            branch.PhoneE164,
            branch.AcceptsWebBookings,
            cover is null ? null : PhotoView.From(cover));
    }

    // ------------------------------------------------------------ reservation policy

    public async Task<ReservationPolicyView> GetReservationPolicyAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the reservation policy", cancellationToken);

        var branch = await LoadBranchAsync(branchId, cancellationToken);

        return ReservationPolicyView.From(branch.ReservationPolicy);
    }

    public async Task<ReservationPolicyChangeResult> UpdateReservationPolicyAsync(
        Guid branchId,
        ReservationPolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Save the reservation policy", cancellationToken);

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

        // Saving the form is what counts as having reviewed it, which is the line the onboarding
        // checklist reads. A branch ships with defaults, so "has a policy" was never the question.
        branch.UpdateReservationPolicy(policy, nowUtc);
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

    // ------------------------------------------------------------ floor plan: rooms, photos, pins

    public async Task<FloorPlanView> GetFloorPlanAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the floor plan", cancellationToken);

        return await ReadFloorPlanAsync(branchId, cancellationToken);
    }

    private async Task<FloorPlanView> ReadFloorPlanAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new { b.Id, b.FloorPlanVersion })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var rooms = await db.FloorAreas
            .AsNoTracking()
            .Where(a => a.BranchId == branchId)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.Name)
            .Select(a => new { a.Id, a.Name, a.DisplayOrder })
            .ToListAsync(cancellationToken);

        var photos = await RoomPhotosAsync(branchId, cancellationToken);

        var areas = rooms
            .Select(a => new FloorAreaView(a.Id, a.Name, a.DisplayOrder, photos.GetValueOrDefault(a.Id, [])))
            .ToList();

        var tables = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.BranchId == branchId)
            .OrderBy(t => t.Label)
            .Select(TableProjection)
            .ToListAsync(cancellationToken);

        return new FloorPlanView(branch.Id, areas, tables, VersionOf(branch.FloorPlanVersion));
    }

    /// <summary>Every room's photos at this branch, each room's in its own order.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<PhotoView>>> RoomPhotosAsync(
        Guid branchId, CancellationToken cancellationToken)
    {
        var rows = await db.FloorAreaPhotos
            .AsNoTracking()
            .Where(p => p.FloorArea.BranchId == branchId)
            .OrderBy(p => p.Position)
            .Select(p => new { p.FloorAreaId, p.Photo })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.FloorAreaId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PhotoView>)[.. g.Select(r => PhotoView.From(r.Photo))]);
    }

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
        await using var writeLock = await LockBranchAsync(branchId, cancellationToken);

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

        // ---- tables: match by id, then by label; anything else is new.
        var existingTables = branch.DiningTables.ToList();
        var matched = new HashSet<Guid>();
        var newTables = new List<(int Index, FloorTableInput Input)>();
        var updatedTables = new List<(int Index, FloorTableInput Input, DiningTable Table)>();

        for (var index = 0; index < tablesInput.Count; index++)
        {
            var input = tablesInput[index];
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
                newTables.Add((index, input));
            }
            else
            {
                updatedTables.Add((index, input, table));
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
            throw new FloorPlanInvalidException(errors, validation.DuplicateLabels);
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

                // Nor at a photo its room no longer shows: the pin is a foreign key, and a stale one
                // would keep a picture the venue removed from ever being swept.
                if (table.PhotoId is { } photoId
                    && (table.FloorAreaId is not { } roomId
                        || !photosByArea.TryGetValue(roomId, out var roomPhotos)
                        || !roomPhotos.Contains(photoId)))
                {
                    table.TakeOffPhoto();
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
        // throwaway labels, flush, then write the real ones. Both flushes are inside the branch
        // lock's transaction, so a plan still applies whole or not at all.
        var currentLabels = existingTables.ToDictionary(t => t.Id, t => t.Label);

        var movers = updatedTables
            .Where(u => !string.Equals(currentLabels[u.Table.Id], u.Input.Label.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(u => existingTables.Any(other => other.Id != u.Table.Id
                                                   && string.Equals(other.Label, u.Input.Label.Trim(), StringComparison.OrdinalIgnoreCase)))
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
            await ReadFloorPlanAsync(branch.Id, cancellationToken),
            warnings,
            deactivated,
            removed);
    }

    /// <summary>
    /// Checks every room's photo list and says which room each photo is in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At most <see cref="FloorAreaPhoto.MaxPhotos"/> a room, no photo twice, and <b>a photo in at
    /// most one room</b>: a table is pinned on a photo, and a photo shown in two rooms would put the
    /// same table in both. The plan is the whole branch, so checking it here is checking the branch.
    /// </para>
    /// <para>
    /// Every problem at once, named against the payload - <c>areas[1].photoIds[3]</c> - so the editor
    /// can mark the thumbnail rather than the whole form.
    /// </para>
    /// </remarks>
    /// <exception cref="FieldValidationException">One or more of the rules broken.</exception>
    private static Dictionary<Guid, int> PlanRoomPhotos(IReadOnlyList<FloorAreaInput> areas)
    {
        var violations = new List<FieldViolation>();
        var roomOf = new Dictionary<Guid, int>();

        for (var i = 0; i < areas.Count; i++)
        {
            var photoIds = areas[i].PhotoIds ?? [];

            if (photoIds.Count > FloorAreaPhoto.MaxPhotos)
            {
                violations.Add(new FieldViolation(
                    $"areas[{i}].photoIds",
                    $"A room holds at most {FloorAreaPhoto.MaxPhotos} photos.",
                    FieldBounds.Max,
                    Max: FloorAreaPhoto.MaxPhotos,
                    Value: photoIds.Count));
            }

            for (var j = 0; j < photoIds.Count; j++)
            {
                var photoId = photoIds[j];
                var field = $"areas[{i}].photoIds[{j}]";

                if (photoId == Guid.Empty)
                {
                    violations.Add(new FieldViolation(field, "Name the photo.", FieldBounds.Required));
                }
                else if (roomOf.TryGetValue(photoId, out var other))
                {
                    violations.Add(new FieldViolation(
                        field,
                        other == i
                            ? "This photo is in the room more than once."
                            : $"This photo is already in {areas[other].Name.Trim()}. A photo belongs to one room.",
                        FieldBounds.Conflict,
                        Value: photoId));
                }
                else
                {
                    roomOf[photoId] = i;
                }
            }
        }

        return violations.Count > 0 ? throw new FieldValidationException(violations) : roomOf;
    }

    /// <summary>
    /// Every table's pin as it will be stored, checked against the rooms the plan sends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pin is a photo and both coordinates, or none of them, and its photo must be one of <b>the
    /// table's own room's</b> - a pin on the terrace's picture for a table in the hall is a table the
    /// diner finds in the wrong room.
    /// </para>
    /// <para>
    /// <b>A pin the save's own photo changes invalidate is taken off, not refused</b>, with a warning:
    /// on a photo this plan drops from every room, or on one it moves out of the table's room (the
    /// photo was the room's before this save and is another room's after it). Removing or moving a
    /// picture is the manager's decision about the rooms; an editor that still sends the pins it
    /// loaded must not turn that into an error the manager cannot see the cause of - and removing and
    /// moving must answer the same way, or the same intent is a warning one way and a 422 the other.
    /// </para>
    /// <para>
    /// A pin on another room's photo that was <b>not</b> the table's room's before either is the
    /// client placing it there, and is refused; so is a photo that was never one of this branch's
    /// rooms.
    /// </para>
    /// <para>
    /// <c>previousPhotos</c> is, per plan room by index, what its stored room showed before this save
    /// (nothing for a new room).
    /// </para>
    /// </remarks>
    /// <exception cref="FieldValidationException">A half pin, a coordinate outside 0-1, or a photo not the table's room's.</exception>
    private static (Guid? PhotoId, double? X, double? Y)[] PlanPins(
        IReadOnlyList<FloorTableInput> tables,
        IReadOnlyList<FloorAreaInput> areas,
        IReadOnlyDictionary<Guid, int> photoRoom,
        IReadOnlyList<IReadOnlySet<Guid>> previousPhotos,
        IReadOnlySet<Guid> currentRoomPhotos)
    {
        var violations = new List<FieldViolation>();
        var pins = new (Guid? PhotoId, double? X, double? Y)[tables.Count];

        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            var label = table.Label.Trim();

            (Guid? PhotoId, double? X, double? Y) pin;

            try
            {
                pin = BranchListingRules.PhotoPosition(table.PhotoId, table.PhotoX, table.PhotoY, label, $"tables[{i}].");
            }
            catch (FieldValidationException ex)
            {
                violations.AddRange(ex.Violations);
                continue;
            }

            if (pin.PhotoId is not { } photoId)
            {
                pins[i] = (null, null, null);
                continue;
            }

            var room = table.FloorAreaName is null ? -1 : AreaIndex(areas, table.FloorAreaName);

            if (photoRoom.TryGetValue(photoId, out var photoIn))
            {
                if (photoIn == room)
                {
                    pins[i] = pin;
                }
                else if (room >= 0 && previousPhotos[room].Contains(photoId))
                {
                    // The photo was this table's room's and the save moved it away.
                    pins[i] = (null, null, null);
                }
                else
                {
                    violations.Add(new FieldViolation(
                        $"tables[{i}].photoId",
                        room < 0
                            ? $"Table {label} is in no room, so it cannot be pinned on {areas[photoIn].Name.Trim()}'s photo."
                            : $"Table {label} is in {areas[room].Name.Trim()}; this photo is {areas[photoIn].Name.Trim()}'s. Pin a table on one of its own room's photos.",
                        FieldBounds.Conflict,
                        Value: photoId));
                }
            }
            else if (currentRoomPhotos.Contains(photoId))
            {
                pins[i] = (null, null, null);
            }
            else
            {
                violations.Add(new FieldViolation(
                    $"tables[{i}].photoId",
                    $"Table {label} is pinned on a photo that is not one of its room's photos.",
                    FieldBounds.Conflict,
                    Value: photoId));
            }
        }

        return violations.Count > 0 ? throw new FieldValidationException(violations) : pins;
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

    /// <summary>Which area in the plan a table's <c>floorAreaName</c> names, or -1.</summary>
    private static int AreaIndex(IReadOnlyList<FloorAreaInput> areas, string areaName)
    {
        for (var i = 0; i < areas.Count; i++)
        {
            if (string.Equals(areas[i].Name?.Trim(), areaName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The stored room each entry in the plan is, or null for a new one: by id first, for every
    /// entry, and only then by name among the rooms no id claimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the point. Matching entry by entry, id-then-name, let a name reach a room before
    /// its own id did: rename Hall to Lounge and add a new Hall in one save, the new Hall listed
    /// first, and the new Hall took the stored room by name, Lounge took it again by id, the new
    /// room was never created and its photos and pins landed on a room that did not show them.
    /// </para>
    /// <para>
    /// A name that matches only a room an id claimed is a new room - the entry holding that id
    /// renamed it, since two entries cannot share a name. An id claimed by two entries has no honest
    /// reading, and is refused naming both, the way a table matched twice is.
    /// </para>
    /// </remarks>
    private static FloorArea?[] MatchAreas(
        IReadOnlyList<FloorAreaInput> areas,
        IReadOnlyCollection<FloorArea> stored,
        List<string> errors)
    {
        var matched = new FloorArea?[areas.Count];
        var claimedBy = new Dictionary<Guid, int>();

        for (var i = 0; i < areas.Count; i++)
        {
            if (areas[i].Id is not { } id || stored.FirstOrDefault(a => a.Id == id) is not { } area)
            {
                continue;
            }

            if (claimedBy.TryGetValue(area.Id, out var first))
            {
                errors.Add(
                    $"Room {area.Name} is matched by more than one entry in the plan: "
                    + $"{areas[first].Name.Trim()} and {areas[i].Name.Trim()}.");
                continue;
            }

            claimedBy[area.Id] = i;
            matched[i] = area;
        }

        for (var i = 0; i < areas.Count; i++)
        {
            if (matched[i] is not null || (areas[i].Id is { } id && claimedBy.ContainsKey(id)))
            {
                continue;
            }

            var name = areas[i].Name.Trim();
            var area = stored.FirstOrDefault(a => !claimedBy.ContainsKey(a.Id)
                                                  && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

            if (area is not null)
            {
                claimedBy[area.Id] = i;
                matched[i] = area;
            }
        }

        return matched;
    }

    // ------------------------------------------------------------ areas and tables

    public async Task<FloorAreaView> CreateFloorAreaAsync(
        Guid branchId,
        FloorAreaCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Add a floor area", cancellationToken);

        // The plan's lock and the plan's version: a room added here is a change to the plan, and an
        // editor that loaded it before must hear 409 rather than save over it and delete the room.
        await using var writeLock = await LockBranchAsync(branchId, cancellationToken);

        var branch = await LoadBranchAsync(branchId, cancellationToken);

        await RequireUnusedAreaNameAsync(branchId, command.Name, null, cancellationToken);

        var area = new FloorArea(branch.Id, command.Name, command.DisplayOrder);
        db.FloorAreas.Add(area);
        branch.BumpFloorPlanVersion();
        await SaveUnderLockAsync(writeLock, branchId, cancellationToken);

        // A new room has no photos; they are added through the floor-plan save.
        return new FloorAreaView(area.Id, area.Name, area.DisplayOrder, []);
    }

    public async Task<FloorAreaView> UpdateFloorAreaAsync(
        Guid branchId,
        Guid areaId,
        FloorAreaCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Edit a floor area", cancellationToken);

        // As for an added room: a stale editor's save would undo the rename, so it moves the version.
        await using var writeLock = await LockBranchAsync(branchId, cancellationToken);

        var branch = await LoadBranchAsync(branchId, cancellationToken);

        var area = await db.FloorAreas.FirstOrDefaultAsync(a => a.Id == areaId && a.BranchId == branchId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Floor area {areaId} was not found at this branch.");

        await RequireUnusedAreaNameAsync(branchId, command.Name, area.Id, cancellationToken);

        area.Rename(command.Name);
        area.SetDisplayOrder(command.DisplayOrder);
        branch.BumpFloorPlanVersion();
        await SaveUnderLockAsync(writeLock, branchId, cancellationToken);

        var photos = await db.FloorAreaPhotos
            .AsNoTracking()
            .Where(p => p.FloorAreaId == area.Id)
            .OrderBy(p => p.Position)
            .Select(p => p.Photo)
            .ToListAsync(cancellationToken);

        return new FloorAreaView(area.Id, area.Name, area.DisplayOrder, [.. photos.Select(PhotoView.From)]);
    }

    /// <summary>
    /// Refuses a room name another room at the branch already has, ignoring case and spaces at the
    /// ends - the rule the plan save applies to the whole plan.
    /// </summary>
    /// <remarks>
    /// Without it these routes made the very plan the save refuses: two rooms called Terrace, and an
    /// editor that loads them, sends them back, and is told its names repeat with no way to see why
    /// it is stuck. Checked inside the branch lock, so two adds cannot both pass it. A blank name is
    /// left to the room's own guard.
    /// </remarks>
    /// <exception cref="FieldValidationException">The name is taken: field <c>name</c>, bound <c>conflict</c>.</exception>
    private async Task RequireUnusedAreaNameAsync(Guid branchId, string? name, Guid? exceptAreaId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var trimmed = name.Trim();

        var names = await db.FloorAreas
            .AsNoTracking()
            .Where(a => a.BranchId == branchId && a.Id != exceptAreaId)
            .Select(a => a.Name)
            .ToListAsync(cancellationToken);

        if (names.Any(n => string.Equals(n.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new FieldValidationException(new FieldViolation(
                "name",
                $"Area names must be unique within a branch. {trimmed} is already used.",
                FieldBounds.Conflict,
                Value: trimmed));
        }
    }

    public async Task DeleteFloorAreaAsync(Guid branchId, Guid areaId, CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Remove a floor area", cancellationToken);

        // The same lock as the plan save: this changes pins, and a save validating pins against the
        // room's photos must not interleave with the room going. And the same version: an editor that
        // loaded the plan with this room in it would otherwise save it back, photos and pins and all.
        await using var writeLock = await LockBranchAsync(branchId, cancellationToken);

        var branch = await LoadBranchAsync(branchId, cancellationToken);

        var area = await db.FloorAreas.FirstOrDefaultAsync(a => a.Id == areaId && a.BranchId == branchId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Floor area {areaId} was not found at this branch.");

        // Tables stay; they just stop belonging to a room - and come off its photos, which go with
        // it (cascade). A pin on a picture no room shows is a table nobody can find.
        var tables = await db.DiningTables.Where(t => t.FloorAreaId == area.Id).ToListAsync(cancellationToken);

        foreach (var table in tables)
        {
            table.AssignToArea(null);
            table.TakeOffPhoto();
        }

        db.FloorAreas.Remove(area);
        branch.BumpFloorPlanVersion();
        await SaveUnderLockAsync(writeLock, branchId, cancellationToken);
    }

    public async Task<TableDeletionResult> DeleteTableAsync(
        Guid branchId,
        Guid tableId,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Delete a table", cancellationToken);

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

        await branchGuard.RequireAtBranchAsync(table.BranchId, "Replace a table's QR code", cancellationToken);

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

    // ------------------------------------------------------------ the branch write lock

    /// <summary>How long a layout save waits for another one on the same branch.</summary>
    /// <remarks>
    /// A plan replace is a handful of statements; five seconds is many of them. Longer than that and
    /// somebody's save is stuck, which the editor should hear about as "try again" rather than hang.
    /// </remarks>
    private const string SetLockTimeout = "SET LOCK_TIMEOUT 5000;";

    /// <summary>
    /// Opens a transaction and takes an update lock on the branch row, for the writers that change
    /// the plan and move its version: the plan replace and the room add, rename and delete.
    /// </summary>
    /// <remarks>
    /// <c>UPDLOCK</c> serialises those writers against each other without blocking readers, and
    /// <c>HOLDLOCK</c> keeps it to the end of the transaction, so what each reads inside the lock is
    /// still true when it commits. The same protocol as <see cref="TableLock"/>, one level up.
    /// </remarks>
    /// <exception cref="DbUpdateConcurrencyException">
    /// Another save held the lock for longer than the wait allows. Answers 409
    /// <c>concurrent-update</c>: reload and try again. Nothing was changed.
    /// </exception>
    private async Task<BranchWriteLock> LockBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var scope = new BranchWriteLock(db, transaction);

        try
        {
            await db.Database.ExecuteSqlRawAsync(SetLockTimeout, cancellationToken);

            await db.Database.ExecuteSqlRawAsync(
                "SELECT Id FROM Branches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @branchId",
                [new SqlParameter("@branchId", branchId)],
                cancellationToken);

            return scope;
        }
        catch (Exception ex) when (TableLock.IsTimeout(ex))
        {
            await scope.DisposeAsync();
            throw Busy(branchId);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>Saves and commits inside the branch lock, answering a lock wait as a concurrent update.</summary>
    private async Task SaveUnderLockAsync(BranchWriteLock writeLock, Guid branchId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await writeLock.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (TableLock.IsTimeout(ex))
        {
            // A table row held by a booking or a seating for longer than the wait. Nothing was
            // decided, so the honest answer is the retryable one.
            db.ChangeTracker.Clear();
            throw Busy(branchId);
        }
    }

    private static DbUpdateConcurrencyException Busy(Guid branchId) =>
        new($"Branch {branchId} is being saved by somebody else right now. Reload and try again.");

    /// <summary>A held branch lock. Commit to keep the work; disposing without committing rolls it back.</summary>
    private sealed class BranchWriteLock(YallaDbContext db, IDbContextTransaction transaction) : IAsyncDisposable
    {
        private bool committed;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!committed)
                {
                    await transaction.RollbackAsync();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or SqlException)
            {
                // Already rolled back by the provider, or the connection went. Nothing left to undo.
                _ = ex;
            }
            finally
            {
                await transaction.DisposeAsync();

                // LOCK_TIMEOUT belongs to the pooled connection; put it back for the rest of the request.
                try
                {
                    await db.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT -1;");
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException or ObjectDisposedException)
                {
                    _ = ex;
                }
            }
        }
    }

    // ------------------------------------------------------------ helpers

    private async Task<Branch> LoadBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
        ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

    /// <summary>The wire form of a plan revision. Opaque to clients.</summary>
    private static string VersionOf(int floorPlanVersion) =>
        floorPlanVersion.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the version the editor sent is the one stored. Anything unreadable is stale.</summary>
    private static bool IsVersion(string expected, int current) =>
        int.TryParse(expected.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed == current;

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
            t.Id, t.Label, t.Seats, t.FloorAreaId, t.IsBookable, t.IsActive, t.QrToken, t.Status,
            !db.Reservations.Any(r => r.DiningTableId == t.Id)
            && !db.TableSessions.Any(ts => ts.DiningTableId == t.Id)
            && !db.Tabs.Any(tb => tb.DiningTableId == t.Id)
            && !db.TableStateChanges.Any(c => c.DiningTableId == t.Id),
            t.PhotoId,
            t.PhotoX,
            t.PhotoY);
}
