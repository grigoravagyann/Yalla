using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Application.BranchSettings;

// ------------------------------------------------------------------ the public page

/// <summary>
/// The two settings that decide what a branch publishes to anybody with its link.
/// </summary>
/// <remarks>
/// Their own form rather than fields on the reservation policy, because they answer a different
/// question. The policy is "how do we run our floor"; this is "what do we say to strangers on the
/// internet", and the second is a decision an owner makes once during onboarding.
/// </remarks>
/// <param name="PhoneE164">
/// The published contact number in E.164, or null when there is none.
/// </param>
/// <param name="AcceptsWebBookings">
/// Whether the public page offers booking. See
/// <see cref="Yalla.Domain.Venues.Branch.AcceptsWebBookings"/> for why this is false until somebody
/// says otherwise.
/// </param>
/// <param name="CoverPhoto">
/// The venue card's picture - the first thing a stranger sees on the public page and in the diner
/// app's list - or null when there is none yet.
/// </param>
public sealed record PublicProfileView(
    string? PhoneE164,
    bool AcceptsWebBookings,
    Media.PhotoView? CoverPhoto);

/// <summary>The same settings, as written. All are replaced at once.</summary>
/// <param name="PhoneE164">
/// E.164, e.g. <c>+37411223344</c>. Spaces, dashes and brackets are stripped before validation.
/// Null or blank clears the number.
/// </param>
/// <param name="AcceptsWebBookings">Whether to offer booking on the public page.</param>
/// <param name="CoverPhotoId">
/// A photo uploaded for <b>this</b> branch, or null to clear the picture. One uploaded for another
/// branch is not found here, the same answer a menu item gets for a foreign photo.
/// </param>
public sealed record PublicProfileCommand(string? PhoneE164, bool AcceptsWebBookings, Guid? CoverPhotoId = null);

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
    int? ApprovalRequiredAbovePartySize,
    int WalkInHoldbackMinutes)
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
        p.ApprovalRequiredAbovePartySize,
        p.WalkInHoldbackMinutes);
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
    int? ApprovalRequiredAbovePartySize,
    int WalkInHoldbackMinutes = 30)
{
    /// <summary>
    /// Builds the policy, applying the editing bounds first.
    /// </summary>
    /// <remarks>
    /// <b>Bounds before the constructor, deliberately.</b> The constructor throws on the first bad
    /// number it meets, so validating afterwards could only ever report one problem out of six -
    /// and it would report it as an <see cref="ArgumentOutOfRangeException"/> naming a C# parameter
    /// rather than a wire field. Checking the raw values first is what lets a form show every
    /// broken bound at once, each against its own input.
    /// </remarks>
    /// <exception cref="Yalla.Domain.FieldValidationException">
    /// One or more fields are outside their bounds. Every one of them is named.
    /// </exception>
    public ReservationPolicy ToPolicy()
    {
        ReservationPolicyLimits.Validate(
            TurnTimeMinutes,
            BufferMinutes,
            GraceMinutes,
            LateNudgeAfterMinutes,
            GraceExtensionMinutes,
            MinLeadMinutes,
            BookingWindowDays,
            CancellationDeadlineMinutes,
            ServiceChargePercent,
            MaxSeatOverhang,
            ApprovalRequiredAbovePartySize,
            WalkInHoldbackMinutes);

        return new ReservationPolicy(
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
            ApprovalRequiredAbovePartySize,
            WalkInHoldbackMinutes);
    }
}

/// <summary>
/// The policy after the change, and what the change did <i>not</i> do.
/// </summary>
/// <param name="Policy">The policy now in force, as stored.</param>
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
    /// <para>
    /// A closing time at or before the opening time means the branch closes after midnight -
    /// 18:00 to 01:00 - which is the only reading that is not bad data. Two blocks on one day may
    /// touch (12:00-15:00 then 15:00-23:00) but not overlap.
    /// </para>
    /// <para>
    /// <b>Every bad block is reported, each against its own input.</b> The payload is an array, so
    /// the field name is indexed - <c>[2].closesAt</c> - which is what a form rendering a row per
    /// block needs in order to put the message on the right row. Refusing the first problem and
    /// stopping would make an owner fixing a week's hours submit once per mistake.
    /// </para>
    /// </remarks>
    /// <exception cref="FieldValidationException">
    /// Two blocks on the same day overlap, or a block opens and closes at the same minute.
    /// </exception>
    public static IReadOnlyList<OpeningHoursView> Normalise(IReadOnlyList<OpeningHoursBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var violations = new List<FieldViolation>();

        // Equal times would derive as "closes after midnight" and then be refused by the entity
        // with a message about after-midnight closing, which is not what the caller got wrong.
        // A branch that never shuts says 00:00-23:59; there is no 24-hour block.
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];

            if (block.OpensAt == block.ClosesAt)
            {
                violations.Add(new FieldViolation(
                    Field(i, nameof(OpeningHoursBlock.ClosesAt)),
                    $"Opening and closing time are both {block.OpensAt:HH\\:mm} on {block.Day}. A block must have a "
                    + "length; for a branch that never closes use 00:00 to 23:59.",
                    FieldBounds.Conflict,
                    Value: block.ClosesAt));
            }
        }

        // Indexed against the payload as sent, so a violation can name the row the client drew.
        // Ordering for the overlap sweep happens on a copy that keeps the original position.
        var indexed = blocks
            .Select((b, index) => (Index: index, View: new OpeningHoursView(
                b.Day, b.OpensAt, b.ClosesAt, ClosesNextDay: b.ClosesAt <= b.OpensAt)))
            .OrderBy(b => b.View.Day)
            .ThenBy(b => b.View.OpensAt)
            .ToList();

        foreach (var day in indexed.GroupBy(b => b.View.Day))
        {
            var ordered = day.ToList();

            for (var i = 1; i < ordered.Count; i++)
            {
                var previous = ordered[i - 1].View;
                var current = ordered[i];

                if (Minutes(current.View.OpensAt) < ClosingMinutes(previous))
                {
                    violations.Add(new FieldViolation(
                        Field(current.Index, nameof(OpeningHoursBlock.OpensAt)),
                        $"Opening hours overlap on {day.Key}: {previous.OpensAt:HH\\:mm}-{previous.ClosesAt:HH\\:mm} "
                        + $"and {current.View.OpensAt:HH\\:mm}-{current.View.ClosesAt:HH\\:mm}.",
                        FieldBounds.Conflict,
                        Min: previous.ClosesAt,
                        Value: current.View.OpensAt));
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        return [.. indexed.Select(b => b.View)];
    }

    /// <summary>
    /// The wire name of one property of one block, e.g. <c>[2].closesAt</c>.
    /// </summary>
    /// <remarks>
    /// The request body is a bare array, so there is no object to prefix with. Index first, then
    /// the property camel-cased exactly as the schema spells it.
    /// </remarks>
    private static string Field(int index, string property) =>
        $"[{index}].{char.ToLowerInvariant(property[0])}{property[1..]}";

    private static int Minutes(TimeOnly time) => (int)time.ToTimeSpan().TotalMinutes;

    private static int ClosingMinutes(OpeningHoursView block) =>
        Minutes(block.ClosesAt) + (block.ClosesNextDay ? 24 * 60 : 0);
}

// ------------------------------------------------------------------ floor plan: rooms, photos, pins

/// <summary>A room as the editor reads it, with its photos in the order a diner swipes them.</summary>
/// <param name="Id">The room.</param>
/// <param name="Name">Hall, Terrace, VIP.</param>
/// <param name="DisplayOrder">Lowest first.</param>
/// <param name="Photos">The room's photos, in order. Empty for a room nobody has photographed yet.</param>
public sealed record FloorAreaView(Guid Id, string Name, int DisplayOrder, IReadOnlyList<Media.PhotoView> Photos);

/// <summary>A table as the editor reads it.</summary>
/// <param name="Id">The table.</param>
/// <param name="Label">What is printed on it.</param>
/// <param name="Seats">How many it seats.</param>
/// <param name="FloorAreaId">Its room, or null.</param>
/// <param name="IsBookable">False for tables that only ever take walk-ins.</param>
/// <param name="IsActive">False for a table deactivated because it has history.</param>
/// <param name="QrToken">The token behind the QR code on the table.</param>
/// <param name="Status">Its physical state.</param>
/// <param name="IsDeletable">Whether a delete would remove it rather than deactivate it.</param>
/// <param name="PhotoId">The room photo it is pinned on, or null. Always one of its own room's photos.</param>
/// <param name="PhotoX">Where the pin sits on that photo, 0 (left) to 1 (right); null with no pin.</param>
/// <param name="PhotoY">0 (top) to 1 (bottom); null with no pin.</param>
public sealed record FloorTableView(
    Guid Id,
    string Label,
    int Seats,
    Guid? FloorAreaId,
    bool IsBookable,
    bool IsActive,
    string QrToken,
    TableStatus Status,
    bool IsDeletable,
    Guid? PhotoId,
    double? PhotoX,
    double? PhotoY);

/// <summary>
/// The rooms, their photos, and every table with its pin.
/// </summary>
/// <remarks>
/// <b>Inactive tables are included</b>, flagged by <c>isActive</c>. The editor has to be able to
/// show a table someone just tried to delete as deactivated; dropping it from the response would
/// look like the delete succeeded, and the next save would recreate it under a new id and a new QR
/// code. The diner-facing reads - availability and the public rooms - exclude them, which is the
/// opposite requirement on the same flag and the reason both directions have a test.
/// </remarks>
/// <param name="BranchId">The branch.</param>
/// <param name="Areas">The rooms, lowest <c>displayOrder</c> first, each with its photos.</param>
/// <param name="Tables">Every table, inactive ones included.</param>
/// <param name="Version">
/// The plan's revision, opaque. Send it back as <c>expectedVersion</c> on the replace; a save against
/// an older revision is refused with <c>floor-plan-changed</c>.
/// </param>
public sealed record FloorPlanView(
    Guid BranchId,
    IReadOnlyList<FloorAreaView> Areas,
    IReadOnlyList<FloorTableView> Tables,
    string Version);

/// <summary>A room as the editor sends it. <c>Id</c> matches an existing room; null means new.</summary>
/// <param name="Id">The existing room this entry edits, or null for one matched by name or created.</param>
/// <param name="Name">Unique within the branch, and required.</param>
/// <param name="DisplayOrder">Lowest first.</param>
/// <param name="PhotoIds">
/// The room's photos, in the order a diner swipes them - the whole list, replacing what was there.
/// At most <see cref="FloorAreaPhoto.MaxPhotos"/>, each a photo uploaded for <b>this</b> branch
/// (another branch's is not found here, the gallery's answer), and each in at most one room. Null or
/// absent means none. A photo dropped from every room takes the tables pinned on it off the photo.
/// </param>
public sealed record FloorAreaInput(Guid? Id, string Name, int DisplayOrder, IReadOnlyList<Guid>? PhotoIds = null);

/// <summary>
/// A table as the editor sends it. <c>Id</c> matches an existing table; null means new - unless a
/// table with that label already exists in the branch, in which case it is that table, so its QR
/// code survives an editor that lost the id.
/// </summary>
/// <param name="Id">The existing table this entry edits, or null for one matched by label or created.</param>
/// <param name="Label">What is printed on the table. Unique within the branch, and required.</param>
/// <param name="Seats">How many people it seats.</param>
/// <param name="FloorAreaName">Names a room in the same plan, by name. Null for no room.</param>
/// <param name="IsBookable">False for tables that only ever take walk-ins, e.g. bar stools.</param>
/// <param name="PhotoId">
/// The photo the table is pinned on - one of <b>its own room's</b> <c>photoIds</c> in this plan - or
/// null for no pin. Sent with <paramref name="PhotoX"/> and <paramref name="PhotoY"/>, or all three
/// null. A diner is only offered a bookable table that has a pin.
/// </param>
/// <param name="PhotoX">Where the pin sits, 0 (left) to 1 (right).</param>
/// <param name="PhotoY">Where the pin sits, 0 (top) to 1 (bottom).</param>
public sealed record FloorTableInput(
    Guid? Id,
    string Label,
    int Seats,
    string? FloorAreaName = null,
    bool IsBookable = true,
    Guid? PhotoId = null,
    double? PhotoX = null,
    double? PhotoY = null);

/// <summary>The whole plan - rooms, their photos and every table's pin - replaced in one atomic call.</summary>
/// <param name="Areas">Every room in the plan, each with its photos.</param>
/// <param name="Tables">Every table in the plan, each with its pin or none.</param>
/// <param name="ExpectedVersion">
/// <b>Required.</b> The <c>version</c> the editor loaded. A plan changed since then is refused with
/// <c>409 floor-plan-changed</c> and <c>context.currentVersion</c>, and nothing is written.
/// </param>
public sealed record ReplaceFloorPlanCommand(
    IReadOnlyList<FloorAreaInput> Areas,
    IReadOnlyList<FloorTableInput> Tables,
    string? ExpectedVersion = null);

/// <summary>
/// The floor plan was saved by somebody else since the editor loaded it. Answers 409
/// <c>floor-plan-changed</c> with <c>context.currentVersion</c>.
/// </summary>
public sealed class FloorPlanChangedException(string currentVersion)
    : Exception("The floor plan was changed since you loaded it. Reload it, then make your changes again.")
{
    /// <summary>The revision now stored, for the editor to reload against.</summary>
    public string CurrentVersion { get; } = currentVersion;
}

/// <summary>
/// A table that somebody has booked and not yet sat down at cannot be deleted or left out of the
/// plan. Answers 409 <c>table-has-future-bookings</c> with <c>context.tables</c> and
/// <c>context.bookings</c>. Nothing is written.
/// </summary>
/// <remarks>
/// Deactivating it instead - what history otherwise earns a table - would leave a confirmed booking
/// on a table the floor no longer shows. A booking counts while it is Confirmed or PendingApproval
/// and has not ended; past, cancelled and completed ones are history, as before.
/// </remarks>
public sealed class TableHasFutureBookingsException(IReadOnlyList<string> tables, IReadOnlyList<string> bookings)
    : Exception(Describe(tables, bookings))
{
    /// <summary>The labels of the tables refused, in label order.</summary>
    public IReadOnlyList<string> Tables { get; } = tables;

    /// <summary>The codes of the bookings that hold them, in code order.</summary>
    public IReadOnlyList<string> Bookings { get; } = bookings;

    private static string Describe(IReadOnlyList<string> tables, IReadOnlyList<string> bookings) =>
        (tables.Count == 1 ? $"Table {tables[0]} has" : $"Tables {string.Join(", ", tables)} have")
        + $" upcoming bookings ({string.Join(", ", bookings)}). Move or cancel them first.";
}

/// <summary>
/// The plan as applied, plus what the editor should know: warnings that did not stop it, and which
/// omitted tables were deactivated rather than deleted because they have history.
/// </summary>
/// <param name="Plan">The plan now stored.</param>
/// <param name="Warnings">
/// Things worth fixing that are not errors - today, a bookable table with no pin, which diners are
/// not offered.
/// </param>
/// <param name="DeactivatedTables">
/// Labels of tables this save took out of service: active before, left out, kept inactive because
/// they have history. Not the tables already inactive - the editor leaves those out of every save,
/// and reporting them each time would announce a change nobody made.
/// </param>
/// <param name="RemovedTables">Labels of omitted tables deleted outright.</param>
public sealed record FloorPlanReplaceResult(
    FloorPlanView Plan,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> DeactivatedTables,
    IReadOnlyList<string> RemovedTables);

public sealed record FloorAreaCommand(string Name, int DisplayOrder);

/// <summary>What deleting a table did: removed outright, or deactivated because it has history.</summary>
/// <param name="TableId">The table.</param>
/// <param name="Label">Its label.</param>
/// <param name="Deleted">Removed outright: it had never been used.</param>
/// <param name="Deactivated">Kept and switched off: it has reservations, seatings or tabs.</param>
/// <param name="Message">Which of the two happened, for the manager.</param>
/// <param name="FloorPlanVersion">
/// The plan's version after the delete. Either outcome changes the plan and moves it, so an editor
/// that loaded the plan before is refused rather than saving the table back; the editor that made
/// the delete adopts this to keep its own draft current.
/// </param>
public sealed record TableDeletionResult(
    Guid TableId,
    string Label,
    bool Deleted,
    bool Deactivated,
    string Message,
    string FloorPlanVersion);

/// <summary>The outcome of checking a plan: hard errors, and the soft warnings that do not stop it.</summary>
public sealed record FloorPlanValidation(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> DuplicateLabels)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The rules for a floor plan's tables that need nothing but the payload, as a pure function.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Every table labelled, labels unique</b> within the plan - errors naming the labels.</item>
/// <item><b>A bookable table with no pin is a warning, not an error.</b> Staff still seat it; diners
/// are not offered it, and the editor should say so rather than let the manager find out from an
/// empty booking screen.</item>
/// </list>
/// The rules that need the rooms - a pin on one of the table's own room's photos, a photo in one
/// room, ten photos a room - are the service's, because they read what the plan replaces.
/// </remarks>
public static class FloorPlanRules
{
    public static FloorPlanValidation Validate(IReadOnlyList<FloorTableInput> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var errors = new List<string>();
        var warnings = new List<string>();

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

        var unpinned = tables
            .Where(t => t.IsBookable && t.PhotoId is null && !string.IsNullOrWhiteSpace(t.Label))
            .Select(t => t.Label.Trim())
            .ToList();

        if (unpinned.Count > 0)
        {
            warnings.Add(
                $"Bookable table(s) {string.Join(", ", unpinned)} are not pinned on a room photo, so diners are not offered them.");
        }

        return new FloorPlanValidation(errors, warnings, duplicates);
    }
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
    /// <summary>The branch's public-page settings.</summary>
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    Task<PublicProfileView> GetPublicProfileAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the branch's public-page settings.</summary>
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    /// <exception cref="ArgumentException">The phone number is not valid E.164.</exception>
    Task<PublicProfileView> UpdatePublicProfileAsync(
        Guid branchId,
        PublicProfileCommand command,
        CancellationToken cancellationToken = default);

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

    /// <summary>The special opening days from the branch's service date on (yesterday's while its block still runs past midnight), earliest first, with the list's revision.</summary>
    Task<SpecialOpeningDaysView> GetSpecialOpeningDaysAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces every special day from today at the branch on, atomically and under the branch lock. Earlier
    /// days are kept. <b>Never touches a booking</b>: live ones that no longer fit are counted and reported.
    /// </summary>
    /// <exception cref="FieldValidationException">No expected version; a date too far out or twice; a note too long; a block overlaps.</exception>
    /// <exception cref="SpecialOpeningDaysChangedException">Saved by somebody else since <c>ExpectedVersion</c>.</exception>
    Task<SpecialOpeningDaysChangeResult> ReplaceSpecialOpeningDaysAsync(
        Guid branchId,
        ReplaceSpecialOpeningDaysCommand command,
        CancellationToken cancellationToken = default);

    Task<FloorPlanView> GetFloorPlanAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces rooms, their photos and every table's pin in one atomic call. See
    /// <see cref="FloorPlanRules"/> for the payload-only rules.
    /// </summary>
    /// <exception cref="Yalla.Domain.FieldValidationException">
    /// <c>ExpectedVersion</c> is missing; or a room has too many photos, a photo twice, or a photo
    /// another room also has; or a pin is half sent, out of range, or on a photo that is not its own
    /// room's.
    /// </exception>
    /// <exception cref="KeyNotFoundException">A room photo that was not uploaded for this branch.</exception>
    /// <exception cref="FloorPlanChangedException">The plan was saved since <c>ExpectedVersion</c>.</exception>
    Task<FloorPlanReplaceResult> ReplaceFloorPlanAsync(
        Guid branchId,
        ReplaceFloorPlanCommand command,
        CancellationToken cancellationToken = default);

    Task<FloorAreaView> CreateFloorAreaAsync(Guid branchId, FloorAreaCommand command, CancellationToken cancellationToken = default);

    Task<FloorAreaView> UpdateFloorAreaAsync(Guid branchId, Guid areaId, FloorAreaCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a room and its photos. Tables in it stay, with no room and off their photos.
    /// </summary>
    Task DeleteFloorAreaAsync(Guid branchId, Guid areaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a table that has never been used, or deactivates one that has any reservation,
    /// session or tab - and says which it did.
    /// </summary>
    Task<TableDeletionResult> DeleteTableAsync(Guid branchId, Guid tableId, CancellationToken cancellationToken = default);

    /// <summary>The one deliberate way a QR code changes. Audited.</summary>
    Task<FloorTableView> RegenerateQrTokenAsync(Guid tableId, CancellationToken cancellationToken = default);
}
