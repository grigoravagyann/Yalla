using Yalla.Application.BranchSettings;
using Yalla.Domain;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The floor-plan save's rules that need no database: which stored room and table each entry is, which room each
/// photo is in, every table's pin, and the labels a left-out table still holds.
/// </summary>
internal static class FloorPlanMatching
{
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
    public static Dictionary<Guid, int> PlanRoomPhotos(IReadOnlyList<FloorAreaInput> areas)
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
    public static (Guid? PhotoId, double? X, double? Y)[] PlanPins(
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

    /// <summary>Which area in the plan a table's <c>floorAreaName</c> names, or -1.</summary>
    public static int AreaIndex(IReadOnlyList<FloorAreaInput> areas, string areaName)
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
    public static FloorArea?[] MatchAreas(
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

    /// <summary>
    /// The stored table each entry in the plan is, or null for a new one: by id first, for every
    /// entry, and only then by label among the <b>active</b> tables no id claimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same order as <see cref="MatchAreas"/>, for the same reason. Matched entry by entry,
    /// renumbering table 5 to 13 and adding a new table 5 in one save let the new 5 reach the stored
    /// table 5 by label before 13 reached it by id, and the save was refused as one table matched
    /// twice. A label that matches only a table an id claimed is a new table.
    /// </para>
    /// <para>
    /// Out-of-service tables are never matched by label. The editor does not send them, so a label
    /// that equals one is a manager adding a table, and matching it would bring the old one back -
    /// its printed QR code working again, its history attached to a table the manager thinks is new.
    /// Putting a table back is its own act, and it sends the table's id; an id still matches any
    /// table, active or not. <see cref="LabelsHeldByOmittedTables"/> refuses the label instead.
    /// </para>
    /// <para>An id sent by two entries has no honest reading, and is refused naming both.</para>
    /// </remarks>
    public static DiningTable?[] MatchTables(
        IReadOnlyList<FloorTableInput> tables,
        IReadOnlyCollection<DiningTable> stored,
        List<string> errors)
    {
        var matched = new DiningTable?[tables.Count];
        var claimedBy = new Dictionary<Guid, int>();

        for (var i = 0; i < tables.Count; i++)
        {
            if (tables[i].Id is not { } id || stored.FirstOrDefault(t => t.Id == id) is not { } table)
            {
                continue;
            }

            if (claimedBy.TryGetValue(table.Id, out var first))
            {
                errors.Add(
                    $"Table {table.Label} is matched by more than one entry in the plan: "
                    + $"{tables[first].Label.Trim()} and {tables[i].Label.Trim()}.");
                continue;
            }

            claimedBy[table.Id] = i;
            matched[i] = table;
        }

        for (var i = 0; i < tables.Count; i++)
        {
            if (matched[i] is not null || (tables[i].Id is { } id && claimedBy.ContainsKey(id)))
            {
                continue;
            }

            var label = tables[i].Label.Trim();
            var table = stored.FirstOrDefault(t => t.IsActive
                                                   && !claimedBy.ContainsKey(t.Id)
                                                   && string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase));

            if (table is not null)
            {
                claimedBy[table.Id] = i;
                matched[i] = table;
            }
        }

        return matched;
    }

    /// <summary>
    /// Entries that take the label of a table this save leaves out but that keeps its label: one
    /// already out of service, or one with history that this save deactivates.
    /// </summary>
    /// <remarks>
    /// The unique index on (branch, label) would refuse these anyway, with a message that names
    /// neither table. Named here, the manager is told which table holds the label and what to do:
    /// put an out-of-service table back (by id, which <see cref="MatchTables"/> honours) rather than
    /// add a stranger under its number.
    /// </remarks>
    public static List<string> LabelsHeldByOmittedTables(
        IReadOnlyList<FloorTableInput> tables,
        IReadOnlyList<DiningTable?> storedTables,
        IReadOnlyList<DiningTable> omitted,
        IReadOnlySet<Guid> used)
    {
        var errors = new List<string>();

        for (var i = 0; i < tables.Count; i++)
        {
            var label = tables[i].Label.Trim();
            var holder = omitted.FirstOrDefault(t => (!t.IsActive || used.Contains(t.Id))
                                                     && string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase));

            if (holder is null)
            {
                continue;
            }

            errors.Add((holder.IsActive, storedTables[i]) switch
            {
                (false, null) =>
                    $"Table {holder.Label} is out of service. Put it back instead of adding a new table with the same label.",
                (false, { } renamed) =>
                    $"Table {holder.Label} is out of service and keeps its label. Put it back, or give table {renamed.Label} another label.",
                (true, _) =>
                    $"Table {holder.Label} has history, so leaving it out of the plan takes it out of service with its label. "
                    + "Keep it in the plan, or give the other table another label.",
            });
        }

        return errors;
    }

    /// <summary>A label nothing else can be holding, for the middle of a two-phase rename.</summary>
    public static string StagingLabel() => $"~{Guid.NewGuid():N}"[..9];

    public static Guid? AreaId(Dictionary<string, FloorArea> areasByName, string? areaName) =>
        areaName is null ? null : areasByName[areaName.Trim()].Id;
}
