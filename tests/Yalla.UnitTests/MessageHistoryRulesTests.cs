using Yalla.Application.Messages;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Domain.Takeaway;

namespace Yalla.UnitTests;

/// <summary>
/// The console's message history without a database: the window's defaults and bounds, every bad field named at once, the
/// cursor, the search as a takeaway code, and the outcomes. See <c>docs/messages.md</c> §history.
/// </summary>
public class MessageHistoryRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 24);

    [Fact]
    public void Nothing_sent_is_the_last_seven_days_of_both_kinds_fifty_at_a_time()
    {
        var filter = MessageHistoryRules.Check(new MessageHistoryQuery(), Today);

        Assert.Equal(Today.AddDays(-6), filter.From);
        Assert.Equal(Today, filter.To);
        Assert.Equal(MessageHistoryKind.All, filter.Kind);
        Assert.Null(filter.Search);
        Assert.False(filter.UnansweredOnly);
        Assert.Null(filter.Cursor);
        Assert.Equal(50, filter.Limit);
    }

    [Fact]
    public void An_unsent_from_follows_to_and_never_reaches_past_the_oldest_day()
    {
        Assert.Equal(new DateOnly(2026, 10, 4), MessageHistoryRules.Check(new MessageHistoryQuery(To: "2026-10-10"), Today).From);

        // Three days from the oldest kept: from stops there rather than refusing a value nobody sent.
        var nearOldest = MessageHistoryRules.Check(new MessageHistoryQuery(To: Iso(Today.AddDays(-362))), Today);
        Assert.Equal(Today.AddDays(-365), nearOldest.From);

        // An unsent to is today.
        Assert.Equal(Today, MessageHistoryRules.Check(new MessageHistoryQuery(From: "2026-10-01"), Today).To);
    }

    [Fact]
    public void The_whole_year_is_one_page_and_a_day_more_is_refused()
    {
        var year = MessageHistoryRules.Check(new MessageHistoryQuery(From: Iso(Today.AddDays(-365)), To: Iso(Today)), Today);
        Assert.Equal(366, year.To.DayNumber - year.From.DayNumber + 1);

        var before = Assert.Throws<FieldValidationException>(
            () => MessageHistoryRules.Check(new MessageHistoryQuery(From: Iso(Today.AddDays(-366))), Today));
        Assert.Equal("from", before.Field);
        Assert.Equal(FieldBounds.Min, before.Violations[0].Bound);

        // That from with today's to is also 367 days long - but only from is wrong, so only from is named.
        Assert.Equal(["from"], before.Violations.Select(v => v.Field).ToArray());
        var beforeWithTo = Assert.Throws<FieldValidationException>(
            () => MessageHistoryRules.Check(new MessageHistoryQuery(From: Iso(Today.AddDays(-366)), To: Iso(Today)), Today));
        Assert.Equal(["from"], beforeWithTo.Violations.Select(v => v.Field).ToArray());

        var tooLong = Assert.Throws<FieldValidationException>(
            () => MessageHistoryRules.Check(new MessageHistoryQuery(From: Iso(Today.AddDays(-365)), To: Iso(Today.AddDays(1))), Today));
        Assert.Equal("to", tooLong.Field);
        Assert.Equal(FieldBounds.Max, tooLong.Violations[0].Bound);
        Assert.Equal(MessageHistoryRules.MaxRangeDays, tooLong.Violations[0].Max);

        var reversed = Assert.Throws<FieldValidationException>(
            () => MessageHistoryRules.Check(new MessageHistoryQuery(From: "2026-10-20", To: "2026-10-19"), Today));
        Assert.Equal("from", reversed.Field);
        Assert.Equal(FieldBounds.Conflict, reversed.Violations[0].Bound);

        // A to before the oldest day, with no from: named on to alone.
        var old = Assert.Throws<FieldValidationException>(
            () => MessageHistoryRules.Check(new MessageHistoryQuery(To: Iso(Today.AddDays(-400))), Today));
        Assert.Equal(["to"], old.Violations.Select(v => v.Field).ToArray());
    }

    [Fact]
    public void Every_bad_field_is_named_in_one_refusal()
    {
        var refused = Assert.Throws<FieldValidationException>(() => MessageHistoryRules.Check(
            new MessageHistoryQuery(
                From: "24.10.2026",
                To: "2026-02-30",
                Kind: "walk-in",
                Q: new string('x', 61),
                UnansweredOnly: "yes",
                Cursor: "not-a-cursor",
                Limit: "500"),
            Today));

        Assert.Equal(
            ["from", "to", "kind", "q", "unansweredOnly", "cursor", "limit"],
            refused.Violations.Select(v => v.Field).ToArray());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("ten")]
    [InlineData("-1")]
    public void A_limit_is_one_to_a_hundred(string limit)
    {
        var refused = Assert.Throws<FieldValidationException>(
            () => MessageHistoryRules.Check(new MessageHistoryQuery(Limit: limit), Today));
        Assert.Equal("limit", refused.Field);
    }

    [Fact]
    public void Values_are_read_as_the_wire_writes_them()
    {
        var filter = MessageHistoryRules.Check(
            new MessageHistoryQuery(Kind: "Takeaway", Q: "  Ani  ", UnansweredOnly: "TRUE", Limit: "100"),
            Today);

        Assert.Equal(MessageHistoryKind.Takeaway, filter.Kind);
        Assert.Equal("Ani", filter.Search);
        Assert.True(filter.UnansweredOnly);
        Assert.Equal(100, filter.Limit);

        // A blank kind is no kind, as every other blank parameter: both kinds.
        Assert.Equal(MessageHistoryKind.All, MessageHistoryRules.Check(new MessageHistoryQuery(Kind: ""), Today).Kind);
        Assert.Equal(MessageHistoryKind.All, MessageHistoryRules.Check(new MessageHistoryQuery(Kind: "  "), Today).Kind);

        // A blank search is no search; sixty characters is the longest.
        Assert.Null(MessageHistoryRules.Check(new MessageHistoryQuery(Q: "   "), Today).Search);
        Assert.Equal(60, MessageHistoryRules.Check(new MessageHistoryQuery(Q: new string('x', 60)), Today).Search!.Length);
    }

    [Theory]
    [InlineData("482", "T-482")]
    [InlineData("T-482", "T-482")]
    [InlineData("t-482", "T-482")]
    [InlineData("Ani", "T-ANI")]
    public void A_search_is_also_read_as_a_takeaway_code(string q, string code) =>
        Assert.Equal(code, MessageHistoryRules.Check(new MessageHistoryQuery(Q: q), Today).SearchAsTakeawayCode);

    [Fact]
    public void A_cursor_carries_its_instant_to_the_tick_and_its_thread()
    {
        var cursor = new MessageHistoryCursor(new DateTime(2026, 10, 24, 9, 30, 0, DateTimeKind.Utc).AddTicks(7), Guid.CreateVersion7());

        var decoded = MessageHistoryCursor.TryDecode(cursor.Encode());

        Assert.Equal(cursor, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded!.LastMessageUtc.Kind);
        Assert.DoesNotContain('=', cursor.Encode());
        Assert.Equal(cursor, MessageHistoryRules.Check(new MessageHistoryQuery(Cursor: cursor.Encode()), Today).Cursor);

        Assert.Null(MessageHistoryCursor.TryDecode("%%%"));
        Assert.Null(MessageHistoryCursor.TryDecode(Convert.ToBase64String("123.not-a-guid"u8.ToArray())));
    }

    [Theory]
    [InlineData(ReservationStatus.PendingApproval, false, "upcoming")]
    [InlineData(ReservationStatus.Confirmed, false, "upcoming")]
    [InlineData(ReservationStatus.Seated, false, "came")]
    [InlineData(ReservationStatus.Completed, false, "came")]
    [InlineData(ReservationStatus.NoShow, false, "no-show")]
    [InlineData(ReservationStatus.CancelledByDiner, false, "cancelled")]
    [InlineData(ReservationStatus.CancelledByVenue, false, "cancelled")]
    [InlineData(ReservationStatus.CancelledByVenue, true, "declined")]
    public void A_bookings_outcome_comes_from_its_status(ReservationStatus status, bool declined, string outcome) =>
        Assert.Equal(outcome, MessageHistoryRules.BookingOutcome(status, declined));

    [Theory]
    [InlineData(TakeawayStatus.Sent, "in-progress")]
    [InlineData(TakeawayStatus.Accepted, "in-progress")]
    [InlineData(TakeawayStatus.Ready, "in-progress")]
    [InlineData(TakeawayStatus.Collected, "collected")]
    [InlineData(TakeawayStatus.NoShow, "no-show")]
    [InlineData(TakeawayStatus.Declined, "declined")]
    [InlineData(TakeawayStatus.Cancelled, "cancelled")]
    public void An_orders_outcome_comes_from_its_status(TakeawayStatus status, string outcome) =>
        Assert.Equal(outcome, MessageHistoryRules.TakeawayOutcome(status));

    [Fact]
    public void The_history_reaches_back_as_far_as_threads_are_kept() =>
        Assert.Equal(ConversationRules.RetentionDays, MessageHistoryRules.MaxDaysBack);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
