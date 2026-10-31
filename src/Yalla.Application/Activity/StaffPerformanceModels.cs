using System.Globalization;
using Yalla.Domain;
using Yalla.Domain.Enums;

namespace Yalla.Application.Activity;

// ---------------------------------------------------------------- staff performance

/// <summary>The query string of the performance reads, as it came. See <see cref="StaffPerformanceRules.Check"/>.</summary>
/// <param name="From">The first branch-local date, <c>yyyy-MM-dd</c>; default today.</param>
/// <param name="To">The last, inclusive; default <c>from</c>, or today.</param>
/// <param name="List">A person's recent list: <c>orders</c> (default), <c>messages</c>, <c>bookings</c>, <c>cash</c> or <c>calls</c>.</param>
/// <param name="Cursor">The list's <c>nextCursor</c> from the page before.</param>
/// <param name="Limit">1 to 100, the list's page.</param>
/// <param name="BranchId">Only on <c>/api/staff/me/performance</c>, for an owner or general manager, who has no home branch.</param>
public sealed record StaffPerformanceQuery(
    string? From = null,
    string? To = null,
    string? List = null,
    string? Cursor = null,
    string? Limit = null,
    string? BranchId = null);

/// <summary>A person's recent list.</summary>
public enum RecentList
{
    Orders = 1,
    Messages = 2,
    Bookings = 3,
    Cash = 4,
    Calls = 5,
}

/// <summary>A performance query, checked.</summary>
public sealed record StaffPerformanceFilter(
    DateOnly From,
    DateOnly To,
    RecentList List,
    ActivityCursor? Cursor,
    int Limit,
    Guid? BranchId);

/// <summary>
/// One person's numbers over a window. Sales, guests, tables and tips are the table's waiter's: a sale is theirs from
/// the order placed while the table was theirs, so a transfer moves what comes after and leaves what came before. See
/// <c>docs/activity.md</c>.
/// </summary>
/// <param name="StaffMemberId">The person.</param>
/// <param name="Name">Their name now.</param>
/// <param name="Role">0 Unknown ... 6 GeneralManager - the stored role now.</param>
/// <param name="IsActive">Still works here.</param>
/// <param name="Tables">Closed bills in the window with a share of their sale theirs, or that were theirs at the close.</param>
/// <param name="Guests">Those bills' party sizes.</param>
/// <param name="SalesAmd">Their share of those bills' totals, service charge included - whole dram, in the proportion of the lines ordered while the table was theirs.</param>
/// <param name="AverageCheckAmd">Sales over tables, rounded; 0 with no tables.</param>
/// <param name="TipsAmd">Tips on payments taken while the table was theirs.</param>
/// <param name="CallsClaimed">Calls and bill requests they claimed ("I'm on it").</param>
/// <param name="CallClaimMedianSeconds">The median seconds from the call to their claim; absent with none.</param>
/// <param name="MessagesAnswered">Guest texts they answered first.</param>
/// <param name="MessageReplyMedianSeconds">The median seconds from the guest's text to their reply; absent with none.</param>
/// <param name="Approvals">Bookings they approved.</param>
/// <param name="Declines">Bookings they declined.</param>
/// <param name="CashAmd">Cash they took: on bills that closed in the window, and at the takeaway counter.</param>
/// <param name="CashPayments">How many cash payments that was.</param>
/// <param name="Discounts">Discounts they gave, still standing.</param>
/// <param name="Comps">Comps they gave, still standing.</param>
/// <param name="AdjustmentsAmd">What those took off, as the bills charged it.</param>
/// <param name="Voids">Lines they voided.</param>
/// <param name="BookingsTaken">
/// Bookings they took by hand (2026-10-31) whose start falls in the window - whatever became of them: came, missed,
/// cancelled, still to come.
/// </param>
/// <param name="BookingsCame">Of those, the parties that sat down (seated or completed).</param>
/// <param name="BookingsNoShow">Of those, the parties that never came (a no-show that stands; one withdrawn does not count).</param>
public sealed record StaffPerformanceRow(
    Guid StaffMemberId,
    string Name,
    StaffRole Role,
    bool IsActive,
    int Tables,
    long Guests,
    long SalesAmd,
    long AverageCheckAmd,
    long TipsAmd,
    int CallsClaimed,
    int? CallClaimMedianSeconds,
    int MessagesAnswered,
    int? MessageReplyMedianSeconds,
    int Approvals,
    int Declines,
    long CashAmd,
    int CashPayments,
    int Discounts,
    int Comps,
    long AdjustmentsAmd,
    int Voids,
    int BookingsTaken = 0,
    int BookingsCame = 0,
    int BookingsNoShow = 0);

/// <summary>
/// The branch's totals over the window - counted once, not summed from the rows: a transferred table is one table, and a
/// median of everyone is not the sum of medians. The money matches the revenue report for the same dates.
/// </summary>
/// <param name="Tables">Closed bills - the revenue report's <c>tabs</c>.</param>
/// <param name="Guests">Their party sizes.</param>
/// <param name="SalesAmd">Their totals - the revenue report's <c>hallAmd</c>.</param>
/// <param name="UnattributedSalesAmd">The part of that no waiter held: a table nobody on staff served.</param>
/// <param name="AverageCheckAmd">The revenue report's <c>averageTabAmd</c>.</param>
/// <param name="TakeawayAmd">Takeaway collected - the revenue report's <c>takeawayAmd</c>; nobody's sale.</param>
/// <param name="TipsAmd">All tips on those bills.</param>
/// <param name="CallsClaimed">Calls claimed.</param>
/// <param name="CallClaimMedianSeconds">The median over every claim.</param>
/// <param name="MessagesAnswered">Guest texts answered.</param>
/// <param name="MessageReplyMedianSeconds">The median over every reply.</param>
/// <param name="Approvals">Bookings approved by staff.</param>
/// <param name="Declines">Bookings declined by staff.</param>
/// <param name="CashAmd">All cash - the revenue report's <c>cashAmd</c>.</param>
/// <param name="CashPayments">Cash payments.</param>
/// <param name="Discounts">Discounts standing.</param>
/// <param name="Comps">Comps standing.</param>
/// <param name="AdjustmentsAmd">The revenue report's <c>adjustmentsTotalAmd</c>.</param>
/// <param name="Voids">Lines voided.</param>
/// <param name="BookingsTaken">Bookings staff took by hand whose start falls in the window.</param>
/// <param name="BookingsCame">Of those, the parties that sat down.</param>
/// <param name="BookingsNoShow">Of those, the parties that never came.</param>
public sealed record StaffPerformanceTotals(
    int Tables,
    long Guests,
    long SalesAmd,
    long UnattributedSalesAmd,
    long AverageCheckAmd,
    long TakeawayAmd,
    long TipsAmd,
    int CallsClaimed,
    int? CallClaimMedianSeconds,
    int MessagesAnswered,
    int? MessageReplyMedianSeconds,
    int Approvals,
    int Declines,
    long CashAmd,
    int CashPayments,
    int Discounts,
    int Comps,
    long AdjustmentsAmd,
    int Voids,
    int BookingsTaken = 0,
    int BookingsCame = 0,
    int BookingsNoShow = 0);

/// <summary>Staff → Results: everybody's numbers over the window, and the branch's.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="From">The first date.</param>
/// <param name="To">The last, inclusive.</param>
/// <param name="Staff">One row per person with anything in the window, and every active waiter and manager of the branch.</param>
/// <param name="Totals">The branch's, counted once.</param>
public sealed record StaffPerformanceReport(
    Guid BranchId,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<StaffPerformanceRow> Staff,
    StaffPerformanceTotals Totals);

/// <summary>A day's sales on a person's chart.</summary>
public sealed record DailySales(DateOnly Date, long SalesAmd, int Tables);

/// <summary>A page of one of a person's recent lists, newest first.</summary>
/// <param name="List"><c>orders</c>, <c>messages</c>, <c>bookings</c>, <c>cash</c> or <c>calls</c>.</param>
/// <param name="Items">This page - History entries, with <c>seconds</c> where the action measures it.</param>
/// <param name="NextCursor">Pass back as <c>cursor</c>; absent on the last page.</param>
public sealed record RecentPage(string List, IReadOnlyList<ActivityItem> Items, string? NextCursor);

/// <summary>One person's page: their numbers, a day-by-day chart, and their recent work.</summary>
/// <param name="BranchId">The branch read.</param>
/// <param name="From">The first date.</param>
/// <param name="To">The last, inclusive.</param>
/// <param name="Kpis">Their row, as on Staff → Results.</param>
/// <param name="DailySales">One entry per local date from six days before <c>to</c> (or <c>from</c>, when earlier) to <c>to</c>, zero days included.</param>
/// <param name="Recent">The list asked for.</param>
public sealed record PersonPerformance(
    Guid BranchId,
    DateOnly From,
    DateOnly To,
    StaffPerformanceRow Kpis,
    IReadOnlyList<DailySales> DailySales,
    RecentPage Recent);

/// <summary>
/// The first page of each of a person's recent lists, read together for My results. Each page is the one
/// <c>/api/staff/me/performance?list=…</c> answers, cursor included, so "more" continues there.
/// </summary>
/// <param name="Orders">Orders sent, pre-orders sent, voids, discounts, comps, bills closed in cash.</param>
/// <param name="Messages">Replies and "are you coming?".</param>
/// <param name="Bookings">Approved, declined, seated, no-show.</param>
/// <param name="Cash">Cash at tables and at the takeaway counter.</param>
/// <param name="Calls">Calls claimed.</param>
public sealed record RecentLists(
    RecentPage Orders,
    RecentPage Messages,
    RecentPage Bookings,
    RecentPage Cash,
    RecentPage Calls);

/// <summary>
/// My results in one read: the caller's numbers, their chart and the first page of every recent list - what the
/// staff phone's screen shows for a period, which took one <c>/api/staff/me/performance</c> call per list before.
/// </summary>
/// <param name="BranchId">The branch read.</param>
/// <param name="From">The first date.</param>
/// <param name="To">The last, inclusive.</param>
/// <param name="Kpis">Their row, as on Staff → Results.</param>
/// <param name="DailySales">As on a person's page: one entry per local date, a week ending on <c>to</c> at least.</param>
/// <param name="Recent">Every list's first page, <c>limit</c> entries each.</param>
public sealed record MyResults(
    Guid BranchId,
    DateOnly From,
    DateOnly To,
    StaffPerformanceRow Kpis,
    IReadOnlyList<DailySales> DailySales,
    RecentLists Recent);

/// <summary>The performance reads.</summary>
public interface IStaffPerformanceQuery
{
    /// <summary>Everybody at the branch. Manager or above at it.</summary>
    Task<StaffPerformanceReport> GetBranchAsync(Guid branchId, StaffPerformanceQuery query, CancellationToken cancellationToken = default);

    /// <summary>One person at the branch. Manager or above at it; the person on the venue's staff (404 otherwise).</summary>
    Task<PersonPerformance> GetPersonAsync(
        Guid branchId, Guid staffMemberId, StaffPerformanceQuery query, CancellationToken cancellationToken = default);

    /// <summary>The caller's own, at their home branch (or <c>branchId</c> for the venue-wide). A waiter or above.</summary>
    Task<PersonPerformance> GetMineAsync(StaffPerformanceQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's own with every recent list at once: the same person, branch and checks as <see cref="GetMineAsync"/>;
    /// <see cref="StaffPerformanceQuery.List"/> and <see cref="StaffPerformanceQuery.Cursor"/> are not read, and the
    /// limit is per list (default <see cref="StaffPerformanceRules.ResultsDefaultLimit"/>).
    /// </summary>
    Task<MyResults> GetMyResultsAsync(StaffPerformanceQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The performance reads' bounds and their query check.</summary>
public static class StaffPerformanceRules
{
    public const int MaxRangeDays = 366;

    /// <summary>Sales and cash are read off the bills, kept for good; approvals off the History, kept a year.</summary>
    public const int MaxDaysBack = Domain.Audit.ActivityEntry.MoneyRetentionDays;

    /// <summary>The chart's least reach: a week ending on <c>to</c>.</summary>
    public const int ChartDays = 7;

    /// <summary>My results' page of each list when no <c>limit</c> is sent: a few of each, five lists at once.</summary>
    public const int ResultsDefaultLimit = 10;

    public static class Fields
    {
        public const string From = "from";
        public const string To = "to";
        public const string List = "list";
        public const string Cursor = "cursor";
        public const string Limit = "limit";
        public const string BranchId = "branchId";
    }

    public static string Word(RecentList list) => list switch
    {
        RecentList.Orders => "orders",
        RecentList.Messages => "messages",
        RecentList.Bookings => "bookings",
        RecentList.Cash => "cash",
        RecentList.Calls => "calls",
        _ => throw new ArgumentOutOfRangeException(nameof(list), list, "Not a list."),
    };

    /// <summary>The action codes each recent list shows.</summary>
    public static IReadOnlyList<string> ActionsOf(RecentList list) => list switch
    {
        RecentList.Orders =>
        [
            ActivityActions.OrderSent, ActivityActions.OrderPreOrderSent, ActivityActions.OrderLineVoided,
            ActivityActions.PaymentCash, ActivityActions.PaymentDiscount, ActivityActions.PaymentComp,
        ],
        RecentList.Messages => [ActivityActions.MessageSent, ActivityActions.MessageAskedOnMyWay],
        RecentList.Bookings =>
        [
            ActivityActions.BookingApproved, ActivityActions.BookingDeclined, ActivityActions.BookingSeated,
            ActivityActions.BookingNoShow,
        ],
        RecentList.Cash => [ActivityActions.PaymentCash, ActivityActions.TakeawayCollected],
        RecentList.Calls => [ActivityActions.TableCallClaimed],
        _ => throw new ArgumentOutOfRangeException(nameof(list), list, "Not a list."),
    };

    /// <summary>The query checked against the branch's today; every bad field named in one 422.</summary>
    public static StaffPerformanceFilter Check(StaffPerformanceQuery query, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(query);

        var violations = new List<FieldViolation>();
        var fromSent = DateRules.Parse(query.From, Fields.From, violations);
        var toSent = DateRules.Parse(query.To, Fields.To, violations);
        var to = toSent ?? (fromSent is { } f && f > today ? f : today);
        var from = fromSent ?? to;

        DateRules.CheckWindow(from, to, today, MaxDaysBack, MaxRangeDays, violations, Fields.From, Fields.To);

        var list = RecentList.Orders;

        if (!string.IsNullOrWhiteSpace(query.List))
        {
            var word = query.List.Trim().ToLowerInvariant();
            var match = Enum.GetValues<RecentList>().Where(l => Word(l) == word).ToList();

            if (match.Count == 1)
            {
                list = match[0];
            }
            else
            {
                violations.Add(new FieldViolation(
                    Fields.List, "list is orders, messages, bookings, cash or calls.", FieldBounds.Range, Value: query.List));
            }
        }

        ActivityCursor? cursor = null;

        if (!string.IsNullOrWhiteSpace(query.Cursor) && (cursor = ActivityCursor.TryDecode(query.Cursor)) is null)
        {
            violations.Add(new FieldViolation(
                Fields.Cursor, "That cursor was not issued by this list. Start again without it.", FieldBounds.Required));
        }

        var limit = ActivityFeedRules.DefaultLimit;

        if (!string.IsNullOrWhiteSpace(query.Limit)
            && (!int.TryParse(query.Limit.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > ActivityFeedRules.MaxLimit))
        {
            violations.Add(new FieldViolation(
                Fields.Limit, $"limit is 1 to {ActivityFeedRules.MaxLimit}.", FieldBounds.Range, Min: 1, Max: ActivityFeedRules.MaxLimit, Value: query.Limit));
        }

        Guid? branchId = null;

        if (!string.IsNullOrWhiteSpace(query.BranchId))
        {
            if (Guid.TryParse(query.BranchId.Trim(), out var parsed) && parsed != Guid.Empty)
            {
                branchId = parsed;
            }
            else
            {
                violations.Add(new FieldViolation(
                    Fields.BranchId, "branchId is a branch's id.", FieldBounds.Required, Value: query.BranchId));
            }
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        return new StaffPerformanceFilter(from, to, list, cursor, limit, branchId);
    }

    /// <summary>
    /// The median of whole seconds: the middle one of an odd count, the mean of the middle two of an even count rounded
    /// half away from zero (31 and 32 → 32). Null for none.
    /// </summary>
    public static int? Median(IReadOnlyCollection<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (int)Math.Round((sorted[middle - 1] + (long)sorted[middle]) / 2m, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Splits a bill's total over the waiters who held its lines, in proportion to their lines' worth, so the shares sum to
    /// the total exactly: each gets the whole dram of their share, and the dram left over go one each to the largest
    /// remainders (ties to the earlier-listed waiter). With nothing to weigh by, the whole goes to <paramref name="fallback"/>.
    /// </summary>
    /// <param name="totalAmd">The bill's total.</param>
    /// <param name="weights">Each waiter's lines - null for lines nobody held, in order of who held the table first.</param>
    /// <param name="fallback">Who gets it all when no line weighs anything: the waiter at the close, or nobody (null).</param>
    public static IReadOnlyList<(Guid? Server, long Amd)> Split(
        long totalAmd, IReadOnlyList<(Guid? Server, long Weight)> weights, Guid? fallback)
    {
        ArgumentNullException.ThrowIfNull(weights);

        var positive = weights.Where(w => w.Weight > 0).ToList();
        var whole = positive.Sum(w => w.Weight);

        if (whole <= 0 || totalAmd == 0)
        {
            return [(fallback, totalAmd)];
        }

        var shares = positive
            .Select((w, index) =>
            {
                var exact = (decimal)totalAmd * w.Weight / whole;
                var floor = (long)Math.Floor(exact);

                return (w.Server, Amd: floor, Remainder: exact - floor, Index: index);
            })
            .ToList();

        var left = totalAmd - shares.Sum(s => s.Amd);

        foreach (var index in shares.OrderByDescending(s => s.Remainder).ThenBy(s => s.Index).Take((int)left).Select(s => s.Index).ToList())
        {
            shares[index] = shares[index] with { Amd = shares[index].Amd + 1 };
        }

        return [.. shares.Select(s => (s.Server, s.Amd))];
    }
}
