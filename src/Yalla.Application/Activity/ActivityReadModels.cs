using System.Globalization;
using System.Text;
using System.Text.Json;
using Yalla.Domain;
using Yalla.Domain.Audit;

namespace Yalla.Application.Activity;

// ---------------------------------------------------------------- the History feed

/// <summary>
/// The query string of <c>GET /api/branches/{branchId}/activity</c> (and its <c>.csv</c>), as it came - every value a
/// string, so a value that does not parse is named in the one 422 with the rest.
/// </summary>
/// <param name="Date">One branch-local day, <c>yyyy-MM-dd</c> - the same as <c>from</c> and <c>to</c> both that day.</param>
/// <param name="From">The first branch-local date, <c>yyyy-MM-dd</c>.</param>
/// <param name="To">The last, inclusive.</param>
/// <param name="StaffMemberId">Only what this person did.</param>
/// <param name="Category"><c>booking</c>, <c>table</c>, <c>order</c>, <c>payment</c>, <c>takeaway</c>, <c>message</c>, <c>settings</c>, <c>staff</c> or <c>device</c>.</param>
/// <param name="Q">The table (exact), the guest (contains), a code (<c>T-482</c>, a booking's) or a dish (contains).</param>
/// <param name="Cursor">A <c>nextCursor</c> from the page before.</param>
/// <param name="Limit">1 to 100.</param>
/// <param name="ActorKind">Only what this kind of actor did: <c>staff</c>, <c>guest</c>, <c>system</c> or <c>platform</c>.</param>
public sealed record ActivityFeedQuery(
    string? Date = null,
    string? From = null,
    string? To = null,
    string? StaffMemberId = null,
    string? Category = null,
    string? Q = null,
    string? Cursor = null,
    string? Limit = null,
    string? ActorKind = null);

/// <summary>A feed query, checked, its defaults filled in.</summary>
public sealed record ActivityFeedFilter(
    DateOnly From,
    DateOnly To,
    Guid? StaffMemberId,
    Domain.Enums.ActivityCategory? Category,
    string? Search,
    ActivityCursor? Cursor,
    int Limit,
    Domain.Enums.ActivityActorKind? ActorKind = null)
{
    /// <summary>The search as a takeaway code - "T-482" for "t-482", "T-482" or "482".</summary>
    public string? SearchAsTakeawayCode =>
        Search is null
            ? null
            : Search.StartsWith(Domain.Takeaway.TakeawayRules.CodePrefix, StringComparison.OrdinalIgnoreCase)
                ? Domain.Takeaway.TakeawayRules.CodePrefix + Search[Domain.Takeaway.TakeawayRules.CodePrefix.Length..].ToUpperInvariant()
                : Domain.Takeaway.TakeawayRules.CodePrefix + Search.ToUpperInvariant();
}

/// <summary>Where a page ended: the last entry's instant and id, newest first - a total order, so pages neither repeat nor skip.</summary>
public sealed record ActivityCursor(DateTime OccurredAtUtc, Guid EntryId)
{
    public string Encode() =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{OccurredAtUtc.Ticks}.{EntryId:N}")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>The cursor, or null when it was not one this list issued.</summary>
    public static ActivityCursor? TryDecode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            var base64 = value.Trim().Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');

            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(base64)).Split('.');

            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTime.MaxValue.Ticks
                && Guid.TryParseExact(parts[1], "N", out var id))
            {
                return new ActivityCursor(new DateTime(ticks, DateTimeKind.Utc), id);
            }
        }
        catch (FormatException)
        {
            // Not base64 - not ours.
        }

        return null;
    }
}

/// <summary>One entry of the History, as the console renders it.</summary>
/// <param name="Id">The entry.</param>
/// <param name="OccurredAtUtc">When.</param>
/// <param name="Local">The same instant on the branch's clock, <c>yyyy-MM-dd HH:mm</c>.</param>
/// <param name="ActorKind"><c>staff</c>, <c>guest</c>, <c>system</c> or <c>platform</c>.</param>
/// <param name="StaffMemberId">Who, when staff.</param>
/// <param name="ActorName">Their name then - staff's, or the guest's; absent for the system.</param>
/// <param name="DeviceId">The tablet or phone it came from; absent from the console.</param>
/// <param name="DeviceLabel">That device's name then, or <c>Console</c>; absent for the guest and the system.</param>
/// <param name="Category">The filter word.</param>
/// <param name="Action">The stable code - see <c>docs/activity.md</c>.</param>
/// <param name="SubjectType">What it was done to: <c>reservation</c>, <c>tab</c>, <c>takeaway</c>, <c>menu-item</c>...</param>
/// <param name="SubjectId">Its id - the detail link.</param>
/// <param name="SubjectCode">Its short code: <c>T-482</c>, a booking's code.</param>
/// <param name="TableSessionId">The sitting it belongs to - the bill's timeline.</param>
/// <param name="TableLabel">The table, as it was called then.</param>
/// <param name="GuestName">The guest.</param>
/// <param name="ItemNames">The dishes it names, comma-joined.</param>
/// <param name="AmountAmd">The amount it is about.</param>
/// <param name="IsMoney">About money (kept three years).</param>
/// <param name="Before">What it was, for a change - shape per action.</param>
/// <param name="After">What it became.</param>
/// <param name="Reason">Why, as somebody typed it.</param>
/// <param name="Summary">The parameters to render the line from - shape per action, see <c>docs/activity.md</c>.</param>
/// <param name="Seconds">
/// How long it took, when the action measures it: a call's claim from the call, a reply from the guest's text, a
/// booking's decision from its request.
/// </param>
public sealed record ActivityItem(
    Guid Id,
    DateTime OccurredAtUtc,
    string Local,
    string ActorKind,
    Guid? StaffMemberId,
    string? ActorName,
    Guid? DeviceId,
    string? DeviceLabel,
    string Category,
    string Action,
    string SubjectType,
    Guid SubjectId,
    string? SubjectCode,
    Guid? TableSessionId,
    string? TableLabel,
    string? GuestName,
    string? ItemNames,
    long? AmountAmd,
    bool IsMoney,
    JsonElement? Before,
    JsonElement? After,
    string? Reason,
    JsonElement Summary,
    int? Seconds);

/// <summary>A page of the History, newest first.</summary>
/// <param name="Items">This page.</param>
/// <param name="NextCursor">Pass back as <c>cursor</c>; absent on the last page.</param>
/// <param name="From">The first date read, the default filled in.</param>
/// <param name="To">The last, inclusive.</param>
public sealed record ActivityPage(IReadOnlyList<ActivityItem> Items, string? NextCursor, DateOnly From, DateOnly To);

/// <summary>Somebody on a timeline, for its filter chips.</summary>
public sealed record ActivityPerson(Guid StaffMemberId, string Name);

/// <summary>A bill's header on its timeline.</summary>
/// <param name="TabId">The tab.</param>
/// <param name="TableSessionId">Its sitting.</param>
/// <param name="TableLabel">The table.</param>
/// <param name="PartySize">How many sat.</param>
/// <param name="OpenedAtUtc">When the tab opened.</param>
/// <param name="SeatedAtUtc">When the party sat - before the tab, often.</param>
/// <param name="ClosedAtUtc">When it closed; absent while open.</param>
/// <param name="Status">1 Open, 2 Closing, 3 Closed, 4 Abandoned.</param>
/// <param name="SubtotalAmd">The lines, less adjustments.</param>
/// <param name="ServiceChargeAmd">The service charge.</param>
/// <param name="TotalAmd">What the bill came to - the reports' figure for it.</param>
/// <param name="PaidAmd">Paid so far.</param>
/// <param name="TipsAmd">Tips on its payments, outside the bill.</param>
/// <param name="Servers">The table's waiters in turn: who took it from when - the first from the seat, each transfer after.</param>
public sealed record TabTimelineHeader(
    Guid TabId,
    Guid TableSessionId,
    string TableLabel,
    int PartySize,
    DateTime OpenedAtUtc,
    DateTime SeatedAtUtc,
    DateTime? ClosedAtUtc,
    int Status,
    long SubtotalAmd,
    long ServiceChargeAmd,
    long TotalAmd,
    long PaidAmd,
    long TipsAmd,
    IReadOnlyList<TimelineServer> Servers);

/// <summary>A waiter's turn at a table.</summary>
/// <param name="StaffMemberId">The waiter.</param>
/// <param name="Name">Their name as stored now.</param>
/// <param name="FromUtc">When they took it.</param>
public sealed record TimelineServer(Guid StaffMemberId, string Name, DateTime FromUtc);

/// <summary>A line on a bill with who did what to it.</summary>
/// <param name="LineId">The line.</param>
/// <param name="OrderId">Its order.</param>
/// <param name="Name">The dish, as ordered.</param>
/// <param name="Quantity">How many.</param>
/// <param name="UnitPriceAmd">At the price then.</param>
/// <param name="LineTotalAmd">Quantity times price - zero once voided.</param>
/// <param name="AddedAtUtc">When the order went in.</param>
/// <param name="AddedByStaffMemberId">The waiter who keyed it; absent for a guest's phone.</param>
/// <param name="AddedByName">That waiter's name, or the guest's display name.</param>
/// <param name="AddedByGuest">A guest ordered it from their phone.</param>
/// <param name="ServerStaffMemberId">The table's waiter when it was ordered - whose sale it is.</param>
/// <param name="VoidedAtUtc">When it was voided.</param>
/// <param name="VoidedByStaffMemberId">Who voided it.</param>
/// <param name="VoidedByName">Their name.</param>
/// <param name="VoidReason">Why.</param>
public sealed record TimelineLine(
    Guid LineId,
    Guid OrderId,
    string Name,
    int Quantity,
    long UnitPriceAmd,
    long LineTotalAmd,
    DateTime AddedAtUtc,
    Guid? AddedByStaffMemberId,
    string? AddedByName,
    bool AddedByGuest,
    Guid? ServerStaffMemberId,
    DateTime? VoidedAtUtc,
    Guid? VoidedByStaffMemberId,
    string? VoidedByName,
    string? VoidReason);

/// <summary>A booking's header on its timeline.</summary>
public sealed record BookingTimelineHeader(
    Guid ReservationId,
    string Code,
    string GuestName,
    int PartySize,
    string LocalDate,
    string LocalTime,
    int Status,
    string TableLabel,
    Guid? TabId);

/// <summary>A takeaway order's header on its timeline.</summary>
public sealed record TakeawayTimelineHeader(
    Guid OrderId,
    string Code,
    string GuestName,
    int Status,
    long TotalAmd,
    long? PaidAmd,
    int? PaymentMethod,
    DateTime CreatedAtUtc,
    DateTime? CollectedAtUtc);

/// <summary>
/// Everything that happened to one bill, booking or takeaway order, oldest first - the Check board - with the bill's
/// lines and who added and voided each. Exactly one header is present for those three subjects; any other subject has
/// its entries and no header.
/// </summary>
public sealed record ActivityTimeline(
    string SubjectType,
    Guid SubjectId,
    TabTimelineHeader? Tab,
    BookingTimelineHeader? Booking,
    TakeawayTimelineHeader? Takeaway,
    IReadOnlyList<TimelineLine> Lines,
    IReadOnlyList<ActivityItem> Entries,
    IReadOnlyList<ActivityPerson> People);

/// <summary>The History's reads. Manager or above at the branch.</summary>
public interface IActivityQuery
{
    /// <summary>A page of the branch's history, newest first.</summary>
    Task<ActivityPage> ListAsync(Guid branchId, ActivityFeedQuery query, CancellationToken cancellationToken = default);

    /// <summary>The same filters, every entry in the window, for the CSV - at most <see cref="ActivityFeedRules.MaxExportRows"/>.</summary>
    /// <exception cref="ActivityExportTooLargeException">More than that: narrow the window.</exception>
    Task<ActivityExport> ExportAsync(Guid branchId, ActivityFeedQuery query, CancellationToken cancellationToken = default);

    /// <summary>One subject's timeline.</summary>
    Task<ActivityTimeline> TimelineAsync(
        Guid branchId, string subjectType, Guid subjectId, CancellationToken cancellationToken = default);
}

/// <summary>The rows of a CSV export and the window they cover.</summary>
public sealed record ActivityExport(IReadOnlyList<ActivityItem> Items, DateOnly From, DateOnly To);

/// <summary>An export would carry more rows than a file is allowed: 422 <c>activity-export-too-large</c>.</summary>
public sealed class ActivityExportTooLargeException(int rows)
    : Exception($"That window holds {rows} or more entries; an export carries at most {ActivityFeedRules.MaxExportRows}. Narrow the dates or add a filter.")
{
    public int Rows { get; } = rows;

    public int MaxRows => ActivityFeedRules.MaxExportRows;
}

/// <summary>The History's bounds and its query check. Pure, so each rule is a unit test. See <c>docs/activity.md</c>.</summary>
public static class ActivityFeedRules
{
    /// <summary>The longest window one read covers, both ends counted.</summary>
    public const int MaxRangeDays = 366;

    /// <summary>How far back the History reaches: money entries are kept three years.</summary>
    public const int MaxDaysBack = ActivityEntry.MoneyRetentionDays;

    public const int DefaultLimit = 50;

    public const int MaxLimit = 100;

    public const int MaxSearchLength = 60;

    /// <summary>The most rows a CSV carries.</summary>
    public const int MaxExportRows = 20_000;

    public static class Fields
    {
        public const string Date = "date";
        public const string From = "from";
        public const string To = "to";
        public const string StaffMemberId = "staffMemberId";
        public const string Category = "category";
        public const string Q = "q";
        public const string Cursor = "cursor";
        public const string Limit = "limit";
        public const string ActorKind = "actorKind";
    }

    /// <summary>
    /// The query checked against the branch's today: <c>date</c>, or <c>from</c>/<c>to</c> (default today, both);
    /// never both kinds; the window at most <see cref="MaxRangeDays"/> and not before <see cref="MaxDaysBack"/> days
    /// ago. Every bad field is named in one 422 <c>validation-failed</c>.
    /// </summary>
    public static ActivityFeedFilter Check(ActivityFeedQuery query, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(query);

        var violations = new List<FieldViolation>();
        var date = DateRules.Parse(query.Date, Fields.Date, violations);
        var fromSent = DateRules.Parse(query.From, Fields.From, violations);
        var toSent = DateRules.Parse(query.To, Fields.To, violations);

        if (date is not null && (fromSent is not null || toSent is not null))
        {
            violations.Add(new FieldViolation(
                Fields.Date, "Send date, or from and to - not both.", FieldBounds.Conflict, Value: query.Date));
        }

        var to = date ?? toSent ?? today;
        var from = date ?? fromSent ?? to;

        DateRules.CheckWindow(from, to, today, MaxDaysBack, MaxRangeDays, violations, Fields.From, Fields.To);

        Guid? staffMemberId = null;

        if (!string.IsNullOrWhiteSpace(query.StaffMemberId))
        {
            if (Guid.TryParse(query.StaffMemberId.Trim(), out var parsed) && parsed != Guid.Empty)
            {
                staffMemberId = parsed;
            }
            else
            {
                violations.Add(new FieldViolation(
                    Fields.StaffMemberId, "staffMemberId is a staff member's id.", FieldBounds.Required, Value: query.StaffMemberId));
            }
        }

        Domain.Enums.ActivityCategory? category = null;

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            category = ActivityWords.CategoryFrom(query.Category.Trim());

            if (category is null)
            {
                violations.Add(new FieldViolation(
                    Fields.Category,
                    "category is booking, table, order, payment, takeaway, message, settings, staff or device.",
                    FieldBounds.Range,
                    Value: query.Category));
            }
        }

        Domain.Enums.ActivityActorKind? actorKind = null;

        if (!string.IsNullOrWhiteSpace(query.ActorKind))
        {
            actorKind = ActivityWords.ActorKindFrom(query.ActorKind.Trim());

            if (actorKind is null)
            {
                violations.Add(new FieldViolation(
                    Fields.ActorKind, "actorKind is staff, guest, system or platform.", FieldBounds.Range, Value: query.ActorKind));
            }
        }

        var search = string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim();

        if (search is { Length: > MaxSearchLength })
        {
            violations.Add(new FieldViolation(
                Fields.Q, $"A search is at most {MaxSearchLength} characters.", FieldBounds.Max, Max: MaxSearchLength, Value: search.Length));
        }

        ActivityCursor? cursor = null;

        if (!string.IsNullOrWhiteSpace(query.Cursor) && (cursor = ActivityCursor.TryDecode(query.Cursor)) is null)
        {
            violations.Add(new FieldViolation(
                Fields.Cursor, "That cursor was not issued by this list. Start again without it.", FieldBounds.Required));
        }

        var limit = DefaultLimit;

        if (!string.IsNullOrWhiteSpace(query.Limit)
            && (!int.TryParse(query.Limit.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > MaxLimit))
        {
            violations.Add(new FieldViolation(
                Fields.Limit, $"limit is 1 to {MaxLimit}.", FieldBounds.Range, Min: 1, Max: MaxLimit, Value: query.Limit));
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        return new ActivityFeedFilter(from, to, staffMemberId, category, search, cursor, limit, actorKind);
    }

    /// <summary>
    /// How long the entry's action measured, from its summary: a call's <c>responseSeconds</c>, a reply's
    /// <c>replySeconds</c>, a booking decision's <c>extra.decisionSeconds</c>. Null for everything else.
    /// </summary>
    public static int? SecondsOf(JsonElement summary)
    {
        if (summary.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in (string[])["responseSeconds", "replySeconds"])
        {
            if (summary.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetInt32();
            }
        }

        return summary.TryGetProperty("extra", out var extra)
               && extra.ValueKind == JsonValueKind.Object
               && extra.TryGetProperty("decisionSeconds", out var decision)
               && decision.ValueKind == JsonValueKind.Number
            ? decision.GetInt32()
            : null;
    }
}

/// <summary>Date parameters as the History and the performance pages take them.</summary>
public static class DateRules
{
    public static DateOnly? Parse(string? text, string field, List<FieldViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        violations.Add(new FieldViolation(field, $"{field} is a date, yyyy-MM-dd.", FieldBounds.Required, Value: text));

        return null;
    }

    /// <summary>Names a window that ends before it starts, runs too long, or reaches before the oldest day kept.</summary>
    public static void CheckWindow(
        DateOnly from,
        DateOnly to,
        DateOnly today,
        int maxDaysBack,
        int maxRangeDays,
        List<FieldViolation> violations,
        string fromField,
        string toField)
    {
        ArgumentNullException.ThrowIfNull(violations);

        if (violations.Count > 0)
        {
            return;
        }

        var oldest = today.AddDays(-maxDaysBack);

        if (from < oldest)
        {
            violations.Add(new FieldViolation(
                fromField, $"The history goes back {maxDaysBack} days, to {Iso(oldest)}.", FieldBounds.Min, Min: Iso(oldest), Value: Iso(from)));
        }

        if (from > to)
        {
            violations.Add(new FieldViolation(
                fromField, $"from ({Iso(from)}) is after to ({Iso(to)}).", FieldBounds.Conflict, Max: Iso(to), Value: Iso(from)));
        }
        else if (to.DayNumber - from.DayNumber + 1 > maxRangeDays)
        {
            violations.Add(new FieldViolation(
                toField, $"A read covers at most {maxRangeDays} days.", FieldBounds.Max, Max: maxRangeDays, Value: to.DayNumber - from.DayNumber + 1));
        }
    }

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
