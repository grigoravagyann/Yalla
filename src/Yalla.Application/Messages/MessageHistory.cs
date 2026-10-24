using System.Globalization;
using System.Text;
using Yalla.Domain;
using Yalla.Domain.Messages;

namespace Yalla.Application.Messages;

// ---------------------------------------------------------------- the console's message history

/// <summary>
/// The query string of <c>GET /api/branches/{branchId}/conversations/history</c>, as it came - every value a string, so a
/// value that does not parse is named in the one 422 with the rest rather than refused by the binder on its own.
/// </summary>
/// <param name="From">The first branch-local date, <c>yyyy-MM-dd</c>.</param>
/// <param name="To">The last branch-local date, <c>yyyy-MM-dd</c>, inclusive.</param>
/// <param name="Kind"><c>all</c>, <c>reservation</c> or <c>takeaway</c>.</param>
/// <param name="Q">The guest's name (contains), the table (exact) or the <c>T-</c> code.</param>
/// <param name="UnansweredOnly"><c>true</c> or <c>false</c>.</param>
/// <param name="Cursor">A <c>nextCursor</c> from the page before.</param>
/// <param name="Limit">1 to 100.</param>
public sealed record MessageHistoryQuery(
    string? From = null,
    string? To = null,
    string? Kind = null,
    string? Q = null,
    string? UnansweredOnly = null,
    string? Cursor = null,
    string? Limit = null);

/// <summary>A thread's subject on a history row.</summary>
/// <param name="Kind"><c>reservation</c> or <c>takeaway</c>.</param>
/// <param name="Id">The booking's or the order's id - the thread's route.</param>
/// <param name="Label">The table's label for a booking, the code ("T-485") for an order.</param>
/// <param name="GuestName">The guest, as the booking or order names them.</param>
/// <param name="LocalDate">A booking's day, or an order's ready day, <c>yyyy-MM-dd</c> on the branch's clock.</param>
/// <param name="LocalTime">A booking's start, or an order's ready time, <c>HH:mm</c> on the branch's clock.</param>
/// <param name="PartySize">A booking's party. Absent on an order.</param>
/// <param name="ItemCount">An order's dishes in all. Absent on a booking.</param>
/// <param name="TotalAmd">An order's total. Absent on a booking.</param>
/// <param name="Outcome">
/// What became of it. A booking: <c>came</c>, <c>no-show</c>, <c>cancelled</c>, <c>declined</c> or <c>upcoming</c>. An
/// order: <c>collected</c>, <c>no-show</c>, <c>declined</c>, <c>cancelled</c> or <c>in-progress</c>.
/// </param>
public sealed record MessageHistorySubject(
    string Kind,
    Guid Id,
    string Label,
    string GuestName,
    string LocalDate,
    string LocalTime,
    int? PartySize,
    int? ItemCount,
    long? TotalAmd,
    string? Outcome);

/// <summary>One thread on the console's Messages page.</summary>
/// <param name="ConversationId">The thread.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="MessageCount">The guest's and the venue's messages - the system's status lines are not counted.</param>
/// <param name="FirstAtUtc">When the first message still in the thread was written.</param>
/// <param name="LastAtUtc">When the last was - the row's "when", and the order of the list.</param>
/// <param name="LastLocal">The same instant on the branch's clock, <c>yyyy-MM-dd HH:mm</c>.</param>
/// <param name="StaffNames">Who at the venue wrote, as they were named then - each once, in the order they first wrote.</param>
/// <param name="Unanswered">The guest's last typed message has no venue message after it.</param>
/// <param name="Open">The thread still takes messages - from the tablet; nobody answers from here.</param>
public sealed record MessageHistoryItem(
    Guid ConversationId,
    MessageHistorySubject Subject,
    int MessageCount,
    DateTime FirstAtUtc,
    DateTime LastAtUtc,
    string LastLocal,
    IReadOnlyList<string> StaffNames,
    bool Unanswered,
    bool Open);

/// <summary>A page of the branch's message history, newest last message first.</summary>
/// <param name="Items">This page.</param>
/// <param name="NextCursor">Pass back as <c>cursor</c> for the next page; absent on the last.</param>
/// <param name="From">The first date the page was read for - the default filled in when none was sent.</param>
/// <param name="To">The last date, inclusive.</param>
public sealed record MessageHistoryPage(
    IReadOnlyList<MessageHistoryItem> Items,
    string? NextCursor,
    DateOnly From,
    DateOnly To);

/// <summary>Which threads a history page reads.</summary>
public enum MessageHistoryKind
{
    All = 0,
    Reservation = 1,
    Takeaway = 2,
}

/// <summary>Where a history page ended: the last thread's last-message instant and its id.</summary>
/// <remarks>
/// <c>(LastMessageUtc, Id)</c> newest first is a total order, so a page boundary between two threads written to in the
/// same instant neither repeats nor skips one. A thread written to after the first page was read moves above the cursor:
/// it is not repeated further down, and shows on a fresh read.
/// </remarks>
public sealed record MessageHistoryCursor(DateTime LastMessageUtc, Guid ConversationId)
{
    public string Encode() =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{LastMessageUtc.Ticks}.{ConversationId:N}")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>The cursor, or null when it was not one this list issued.</summary>
    public static MessageHistoryCursor? TryDecode(string value)
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
                return new MessageHistoryCursor(new DateTime(ticks, DateTimeKind.Utc), id);
            }
        }
        catch (FormatException)
        {
            // Not base64 - not ours.
        }

        return null;
    }
}

/// <summary>A history query, checked and with its defaults filled in.</summary>
/// <param name="From">The first branch-local date.</param>
/// <param name="To">The last, inclusive.</param>
/// <param name="Kind">Bookings, orders or both.</param>
/// <param name="Search">The search, trimmed; null for none.</param>
/// <param name="UnansweredOnly">Only threads whose guest waits for an answer.</param>
/// <param name="Cursor">Where the page before ended; null for the first page.</param>
/// <param name="Limit">How many threads on this page.</param>
public sealed record MessageHistoryFilter(
    DateOnly From,
    DateOnly To,
    MessageHistoryKind Kind,
    string? Search,
    bool UnansweredOnly,
    MessageHistoryCursor? Cursor,
    int Limit)
{
    /// <summary>The search as a <c>T-</c> code - "T-482" for "t-482", "T-482" or "482".</summary>
    public string? SearchAsTakeawayCode =>
        Search is null
            ? null
            : Search.StartsWith(Domain.Takeaway.TakeawayRules.CodePrefix, StringComparison.OrdinalIgnoreCase)
                ? Domain.Takeaway.TakeawayRules.CodePrefix + Search[Domain.Takeaway.TakeawayRules.CodePrefix.Length..].ToUpperInvariant()
                : Domain.Takeaway.TakeawayRules.CodePrefix + Search.ToUpperInvariant();
}

/// <summary>
/// The rules of the console's message history: the window it reads, its defaults and its bounds. Pure, so each is a unit
/// test. See <c>docs/messages.md</c> §history.
/// </summary>
public static class MessageHistoryRules
{
    /// <summary>The default window: today and the six days before.</summary>
    public const int DefaultDays = 7;

    /// <summary>The longest window one page reads, both ends counted.</summary>
    public const int MaxRangeDays = 366;

    /// <summary>
    /// How far back the history reaches - <see cref="ConversationRules.RetentionDays"/>, the sweep's: nothing older is
    /// kept to find.
    /// </summary>
    public const int MaxDaysBack = ConversationRules.RetentionDays;

    public const int DefaultLimit = 50;

    public const int MaxLimit = 100;

    /// <summary>The longest search.</summary>
    public const int MaxSearchLength = 60;

    /// <summary>The wire names a refusal names.</summary>
    public static class Fields
    {
        public const string From = "from";
        public const string To = "to";
        public const string Kind = "kind";
        public const string Q = "q";
        public const string UnansweredOnly = "unansweredOnly";
        public const string Cursor = "cursor";
        public const string Limit = "limit";
    }

    /// <summary>The wire names of <c>kind</c>.</summary>
    public static class Kinds
    {
        public const string All = "all";
        public const string Reservation = ConversationRules.SubjectKinds.Reservation;
        public const string Takeaway = ConversationRules.SubjectKinds.Takeaway;
    }

    /// <summary>
    /// The query checked against the branch's today, its defaults filled in: <c>to</c> today, <c>from</c> six days before
    /// <c>to</c> (never before the oldest day kept), <c>kind</c> all, <c>limit</c> 50. Every bad field is named in one
    /// 422 <c>validation-failed</c>.
    /// </summary>
    /// <param name="query">The query string as it came.</param>
    /// <param name="today">The branch's local date now.</param>
    /// <exception cref="FieldValidationException">Anything out of bounds or not parseable - every one named.</exception>
    public static MessageHistoryFilter Check(MessageHistoryQuery query, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(query);

        var violations = new List<FieldViolation>();
        var oldest = today.AddDays(-MaxDaysBack);

        var fromSent = ParseDate(query.From, Fields.From, violations);
        var to = ParseDate(query.To, Fields.To, violations) ?? today;
        var from = fromSent ?? Max(to.AddDays(-(DefaultDays - 1)), oldest);
        var datesParsed = violations.Count == 0;

        if (datesParsed)
        {
            if (fromSent is not null && from < oldest)
            {
                violations.Add(new FieldViolation(
                    Fields.From,
                    $"The history goes back {MaxDaysBack} days, to {Iso(oldest)}.",
                    FieldBounds.Min,
                    Min: Iso(oldest),
                    Value: Iso(from)));
            }

            if (to < oldest)
            {
                violations.Add(new FieldViolation(
                    Fields.To,
                    $"The history goes back {MaxDaysBack} days, to {Iso(oldest)}.",
                    FieldBounds.Min,
                    Min: Iso(oldest),
                    Value: Iso(to)));
            }

            // An unsent from follows to, so it is after to only when to is before the oldest day - named above.
            if (from > to && fromSent is not null)
            {
                violations.Add(new FieldViolation(
                    Fields.From,
                    $"from ({Iso(from)}) is after to ({Iso(to)}).",
                    FieldBounds.Conflict,
                    Max: Iso(to),
                    Value: Iso(from)));
            }
            else if (from <= to && to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
            {
                violations.Add(new FieldViolation(
                    Fields.To,
                    $"A page reads at most {MaxRangeDays} days.",
                    FieldBounds.Max,
                    Max: MaxRangeDays,
                    Value: to.DayNumber - from.DayNumber + 1));
            }
        }

        var kind = MessageHistoryKind.All;

        if (query.Kind is { } kindText)
        {
            switch (kindText.Trim().ToLowerInvariant())
            {
                case Kinds.All:
                    kind = MessageHistoryKind.All;
                    break;
                case Kinds.Reservation:
                    kind = MessageHistoryKind.Reservation;
                    break;
                case Kinds.Takeaway:
                    kind = MessageHistoryKind.Takeaway;
                    break;
                default:
                    violations.Add(new FieldViolation(
                        Fields.Kind,
                        $"kind is {Kinds.All}, {Kinds.Reservation} or {Kinds.Takeaway}.",
                        FieldBounds.Range,
                        Value: kindText));
                    break;
            }
        }

        var search = string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim();

        if (search is { Length: > MaxSearchLength })
        {
            violations.Add(new FieldViolation(
                Fields.Q,
                $"A search is at most {MaxSearchLength} characters.",
                FieldBounds.Max,
                Max: MaxSearchLength,
                Value: search.Length));
        }

        var unansweredOnly = false;

        if (!string.IsNullOrWhiteSpace(query.UnansweredOnly) && !bool.TryParse(query.UnansweredOnly.Trim(), out unansweredOnly))
        {
            violations.Add(new FieldViolation(
                Fields.UnansweredOnly, "unansweredOnly is true or false.", FieldBounds.Required, Value: query.UnansweredOnly));
        }

        MessageHistoryCursor? cursor = null;

        if (!string.IsNullOrWhiteSpace(query.Cursor) && (cursor = MessageHistoryCursor.TryDecode(query.Cursor)) is null)
        {
            violations.Add(new FieldViolation(
                Fields.Cursor, "That cursor was not issued by this list. Start again without it.", FieldBounds.Required));
        }

        var limit = DefaultLimit;

        if (!string.IsNullOrWhiteSpace(query.Limit))
        {
            if (!int.TryParse(query.Limit.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > MaxLimit)
            {
                violations.Add(new FieldViolation(
                    Fields.Limit,
                    $"limit is 1 to {MaxLimit}.",
                    FieldBounds.Range,
                    Min: 1,
                    Max: MaxLimit,
                    Value: query.Limit));
            }
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        return new MessageHistoryFilter(from, to, kind, search, unansweredOnly, cursor, limit);
    }

    /// <summary>
    /// A booking's outcome on the history row: <c>came</c> (seated or completed), <c>no-show</c>, <c>declined</c> (the
    /// venue's no, or nobody's answer in time), <c>cancelled</c> (by the guest or the venue), <c>upcoming</c> (pending or
    /// confirmed).
    /// </summary>
    public static string BookingOutcome(Domain.Enums.ReservationStatus status, bool declined) => status switch
    {
        Domain.Enums.ReservationStatus.Seated or Domain.Enums.ReservationStatus.Completed => "came",
        Domain.Enums.ReservationStatus.NoShow => "no-show",
        Domain.Enums.ReservationStatus.CancelledByVenue when declined => "declined",
        Domain.Enums.ReservationStatus.CancelledByVenue or Domain.Enums.ReservationStatus.CancelledByDiner => "cancelled",
        _ => "upcoming",
    };

    /// <summary>
    /// A takeaway order's outcome: <c>collected</c>, <c>no-show</c> (not collected), <c>declined</c> (the venue's no, or
    /// nobody's answer in time), <c>cancelled</c> (by the guest), <c>in-progress</c> (sent, accepted or ready).
    /// </summary>
    public static string TakeawayOutcome(Domain.Takeaway.TakeawayStatus status) => status switch
    {
        Domain.Takeaway.TakeawayStatus.Collected => "collected",
        Domain.Takeaway.TakeawayStatus.NoShow => "no-show",
        Domain.Takeaway.TakeawayStatus.Declined => "declined",
        Domain.Takeaway.TakeawayStatus.Cancelled => "cancelled",
        _ => "in-progress",
    };

    private static DateOnly? ParseDate(string? text, string field, List<FieldViolation> violations)
    {
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

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
