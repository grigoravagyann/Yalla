using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Application.BranchSettings;

// ------------------------------------------------------------------ reservation policy

/// <summary>Every field of the owned <see cref="ReservationPolicy"/>, as read.</summary>
public sealed record ReservationPolicyView(
    int TurnTimeMinutes,
    int BufferMinutes,
    int GraceMinutes,
    int LateNudgeAfterMinutes,
    int GraceExtensionMinutes,
    int MinLeadMinutes,
    int BookingWindowDays,
    int CancellationDeadlineMinutes,
    bool AutoConfirm,
    decimal ServiceChargePercent,
    bool PricesIncludeVat,
    int? MaxSeatOverhang,
    int? ApprovalRequiredAbovePartySize)
{
    public static ReservationPolicyView From(ReservationPolicy p) => new(
        p.TurnTimeMinutes,
        p.BufferMinutes,
        p.GraceMinutes,
        p.LateNudgeAfterMinutes,
        p.GraceExtensionMinutes,
        p.MinLeadMinutes,
        p.BookingWindowDays,
        p.CancellationDeadlineMinutes,
        p.AutoConfirm,
        p.ServiceChargePercent,
        p.PricesIncludeVat,
        p.MaxSeatOverhang,
        p.ApprovalRequiredAbovePartySize);
}

/// <summary>Every field of the policy, as written. The whole form is replaced at once.</summary>
public sealed record ReservationPolicyCommand(
    int TurnTimeMinutes,
    int BufferMinutes,
    int GraceMinutes,
    int LateNudgeAfterMinutes,
    int GraceExtensionMinutes,
    int MinLeadMinutes,
    int BookingWindowDays,
    int CancellationDeadlineMinutes,
    bool AutoConfirm,
    decimal ServiceChargePercent,
    bool PricesIncludeVat,
    int? MaxSeatOverhang,
    int? ApprovalRequiredAbovePartySize)
{
    /// <summary>Builds the policy, applying the constructor's own checks and then the editing bounds.</summary>
    public ReservationPolicy ToPolicy()
    {
        var policy = new ReservationPolicy(
            TurnTimeMinutes,
            BufferMinutes,
            GraceMinutes,
            LateNudgeAfterMinutes,
            GraceExtensionMinutes,
            MinLeadMinutes,
            BookingWindowDays,
            CancellationDeadlineMinutes,
            AutoConfirm,
            ServiceChargePercent,
            PricesIncludeVat,
            MaxSeatOverhang,
            ApprovalRequiredAbovePartySize);

        ReservationPolicyLimits.Validate(policy);

        return policy;
    }
}

/// <summary>
/// The policy after the change, and what the change did <i>not</i> do.
/// </summary>
/// <param name="AffectedExistingReservations">
/// How many live bookings now fall outside the new rules - beyond the shortened window, or
/// booked under a longer turn time. They were <b>not</b> changed: a settings edit never rewrites or
/// cancels a booking. The new rules apply to future bookings only.
/// </param>
/// <param name="AffectedReservationIds">Which ones, so the panel can show them.</param>
public sealed record ReservationPolicyChangeResult(
    ReservationPolicyView Policy,
    int AffectedExistingReservations,
    IReadOnlyList<Guid> AffectedReservationIds);

// ------------------------------------------------------------------ opening hours

/// <summary>One opening block as the client supplies it. <c>ClosesNextDay</c> is derived, not sent.</summary>
public sealed record OpeningHoursBlock(DayOfWeek Day, TimeOnly OpensAt, TimeOnly ClosesAt);

public sealed record OpeningHoursView(DayOfWeek Day, TimeOnly OpensAt, TimeOnly ClosesAt, bool ClosesNextDay);

/// <summary>
/// The rules for a weekly set of opening hours, as a pure function.
/// </summary>
public static class OpeningHoursRules
{
    /// <summary>
    /// Derives <c>ClosesNextDay</c> and refuses blocks that overlap within a day.
    /// </summary>
    /// <remarks>
    /// A closing time at or before the opening time means the branch closes after midnight -
    /// 18:00 to 01:00 - which is the only reading that is not bad data. Two blocks on one day may
    /// touch (12:00-15:00 then 15:00-23:00) but not overlap.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Two blocks on the same day overlap, or a block opens and closes at the same minute.
    /// </exception>
    public static IReadOnlyList<OpeningHoursView> Normalise(IReadOnlyList<OpeningHoursBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        // Equal times would derive as "closes after midnight" and then be refused by the entity
        // with a message about after-midnight closing, which is not what the caller got wrong.
        // A branch that never shuts says 00:00-23:59; there is no 24-hour block.
        foreach (var same in blocks.Where(b => b.OpensAt == b.ClosesAt))
        {
            throw new ArgumentException(
                $"Opening and closing time are both {same.OpensAt:HH\\:mm} on {same.Day}. A block must have a "
                + "length; for a branch that never closes use 00:00 to 23:59.",
                nameof(blocks));
        }

        var normalised = blocks
            .Select(b => new OpeningHoursView(b.Day, b.OpensAt, b.ClosesAt, ClosesNextDay: b.ClosesAt <= b.OpensAt))
            .OrderBy(b => b.Day)
            .ThenBy(b => b.OpensAt)
            .ToList();

        foreach (var day in normalised.GroupBy(b => b.Day))
        {
            var ordered = day.ToList();

            for (var i = 1; i < ordered.Count; i++)
            {
                var previous = ordered[i - 1];
                var current = ordered[i];

                if (Minutes(current.OpensAt) < ClosingMinutes(previous))
                {
                    throw new ArgumentException(
                        $"Opening hours overlap on {day.Key}: {previous.OpensAt:HH\\:mm}-{previous.ClosesAt:HH\\:mm} "
                        + $"and {current.OpensAt:HH\\:mm}-{current.ClosesAt:HH\\:mm}.",
                        nameof(blocks));
                }
            }
        }

        return normalised;
    }

    private static int Minutes(TimeOnly time) => (int)time.ToTimeSpan().TotalMinutes;

    private static int ClosingMinutes(OpeningHoursView block) =>
        Minutes(block.ClosesAt) + (block.ClosesNextDay ? 24 * 60 : 0);
}

// ------------------------------------------------------------------ floor plan

public sealed record FloorAreaView(Guid Id, string Name, int DisplayOrder);

public sealed record FloorTableView(
    Guid Id,
    string Label,
    int Seats,
    int X,
    int Y,
    int Width,
    int Height,
    double RotationDegrees,
    TableShape Shape,
    Guid? FloorAreaId,
    bool IsBookable,
    bool IsActive,
    string QrToken,
    TableStatus Status);

/// <summary>Canvas size, areas, and every table with its geometry. Inactive tables are included, flagged.</summary>
public sealed record FloorPlanView(
    Guid BranchId,
    int FloorWidth,
    int FloorHeight,
    IReadOnlyList<FloorAreaView> Areas,
    IReadOnlyList<FloorTableView> Tables);

/// <summary>An area as the editor sends it. <c>Id</c> matches an existing area; null means new.</summary>
public sealed record FloorAreaInput(Guid? Id, string Name, int DisplayOrder);

/// <summary>
/// A table as the editor sends it. <c>Id</c> matches an existing table; null means new - unless a
/// table with that label already exists in the branch, in which case it is that table, so its QR
/// code survives an editor that lost the id.
/// </summary>
/// <param name="FloorAreaName">Names an area in the same plan, by name. Null for no area.</param>
public sealed record FloorTableInput(
    Guid? Id,
    string Label,
    int Seats,
    int X,
    int Y,
    int Width,
    int Height,
    double RotationDegrees,
    TableShape Shape,
    string? FloorAreaName = null,
    bool IsBookable = true);

/// <summary>The whole plan, replaced in one atomic call.</summary>
public sealed record ReplaceFloorPlanCommand(
    int FloorWidth,
    int FloorHeight,
    IReadOnlyList<FloorAreaInput> Areas,
    IReadOnlyList<FloorTableInput> Tables);

/// <summary>
/// The plan as applied, plus what the editor should know: overlaps (warnings, not errors), and
/// which omitted tables were deactivated rather than deleted because they have history.
/// </summary>
public sealed record FloorPlanReplaceResult(
    FloorPlanView Plan,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> DeactivatedTables,
    IReadOnlyList<string> RemovedTables);

public sealed record FloorAreaCommand(string Name, int DisplayOrder);

/// <summary>What deleting a table did: removed outright, or deactivated because it has history.</summary>
public sealed record TableDeletionResult(Guid TableId, string Label, bool Deleted, bool Deactivated, string Message);

/// <summary>The outcome of checking a plan: hard errors, and the soft warnings that do not stop it.</summary>
public sealed record FloorPlanValidation(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> TablesOutsideCanvas,
    IReadOnlyList<string> DuplicateLabels)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The rules for a floor plan, as a pure function over the payload.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Labels unique</b> within the plan - an error naming the label.</item>
/// <item><b>Every table inside the canvas</b> - an error naming the tables. A table drawn off the
/// edge of the room is a table the diner app cannot show.</item>
/// <item><b>Overlapping tables are a warning, not an error.</b> Real rooms have benches against
/// tables and stools tucked under bars; the plan is a map, not a physics simulation.</item>
/// </list>
/// Overlap is tested on axis-aligned bounds, ignoring rotation. It is a warning, so being
/// approximate is fine.
/// </remarks>
public static class FloorPlanRules
{
    public static FloorPlanValidation Validate(int floorWidth, int floorHeight, IReadOnlyList<FloorTableInput> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var errors = new List<string>();
        var warnings = new List<string>();

        if (floorWidth <= 0 || floorHeight <= 0)
        {
            errors.Add("The canvas must have a positive width and height.");
        }

        // Checked before anything dereferences a label: a table object with no "label" member
        // binds to null, and the service would otherwise fault on it rather than say what is wrong.
        var unlabelled = tables.Count(t => string.IsNullOrWhiteSpace(t.Label));

        if (unlabelled > 0)
        {
            errors.Add($"{unlabelled} table(s) in the plan have no label. Every table needs the label printed on it.");
        }

        var duplicates = tables
            .GroupBy(t => (t.Label ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 && g.Key.Length > 0)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            errors.Add($"Table labels must be unique within a branch. Repeated: {string.Join(", ", duplicates)}.");
        }

        var outside = tables
            .Where(t => t.X < 0 || t.Y < 0 || t.X + t.Width > floorWidth || t.Y + t.Height > floorHeight)
            .Select(t => t.Label)
            .ToList();

        if (outside.Count > 0)
        {
            errors.Add(
                $"Every table must sit inside the {floorWidth}x{floorHeight} canvas. Outside it: {string.Join(", ", outside)}.");
        }

        for (var i = 0; i < tables.Count; i++)
        {
            for (var j = i + 1; j < tables.Count; j++)
            {
                if (Overlaps(tables[i], tables[j]))
                {
                    warnings.Add($"Tables {tables[i].Label} and {tables[j].Label} overlap.");
                }
            }
        }

        return new FloorPlanValidation(errors, warnings, outside, duplicates);
    }

    private static bool Overlaps(FloorTableInput a, FloorTableInput b) =>
        a.X < b.X + b.Width
        && b.X < a.X + a.Width
        && a.Y < b.Y + b.Height
        && b.Y < a.Y + a.Height;
}

// ------------------------------------------------------------------ the service

/// <summary>
/// Configuring one branch: its reservation policy, its opening hours and its floor plan.
/// </summary>
/// <remarks>
/// Manager or owner within scope, or a platform admin. Scope is enforced by the policies at the
/// endpoint; these methods trust the branch id they are given.
/// </remarks>
public interface IBranchSettingsService
{
    Task<ReservationPolicyView> GetReservationPolicyAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the policy. <b>Never touches an existing reservation</b>: bookings that fall
    /// outside the new rules are counted and reported, not rewritten or cancelled.
    /// </summary>
    Task<ReservationPolicyChangeResult> UpdateReservationPolicyAsync(
        Guid branchId,
        ReservationPolicyCommand command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpeningHoursView>> GetOpeningHoursAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the full weekly set atomically.</summary>
    Task<IReadOnlyList<OpeningHoursView>> ReplaceOpeningHoursAsync(
        Guid branchId,
        IReadOnlyList<OpeningHoursBlock> blocks,
        CancellationToken cancellationToken = default);

    Task<FloorPlanView> GetFloorPlanAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Replaces canvas, areas and tables in one atomic call. See <see cref="FloorPlanRules"/>.</summary>
    Task<FloorPlanReplaceResult> ReplaceFloorPlanAsync(
        Guid branchId,
        ReplaceFloorPlanCommand command,
        CancellationToken cancellationToken = default);

    Task<FloorAreaView> CreateFloorAreaAsync(Guid branchId, FloorAreaCommand command, CancellationToken cancellationToken = default);

    Task<FloorAreaView> UpdateFloorAreaAsync(Guid branchId, Guid areaId, FloorAreaCommand command, CancellationToken cancellationToken = default);

    /// <summary>Removes an area. Tables in it are left in place with no area.</summary>
    Task DeleteFloorAreaAsync(Guid branchId, Guid areaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a table that has never been used, or deactivates one that has any reservation,
    /// session or tab - and says which it did.
    /// </summary>
    Task<TableDeletionResult> DeleteTableAsync(Guid branchId, Guid tableId, CancellationToken cancellationToken = default);

    /// <summary>The one deliberate way a QR code changes. Audited.</summary>
    Task<FloorTableView> RegenerateQrTokenAsync(Guid tableId, CancellationToken cancellationToken = default);
}
