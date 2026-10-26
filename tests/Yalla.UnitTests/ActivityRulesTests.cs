using System.Text.Json;
using Yalla.Application.Activity;
using Yalla.Domain;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests;

/// <summary>
/// The pure rules of the activity history and the staff results: medians, how a bill's sale splits over its waiters,
/// the History's query check, and the entry's own guards. See <c>docs/activity.md</c>.
/// </summary>
public class ActivityRulesTests
{
    // ---------------------------------------------------------------- medians

    [Fact]
    public void A_median_of_an_odd_count_is_the_middle_value()
    {
        Assert.Equal(48, StaffPerformanceRules.Median([95, 12, 48]));
        Assert.Equal(7, StaffPerformanceRules.Median([7]));
    }

    /// <summary>
    /// An even count is the mean of the middle two - not the lower one, not the upper one - rounded half away from zero
    /// on a whole second: 30 and 61 is 45.5, so 46.
    /// </summary>
    [Fact]
    public void A_median_of_an_even_count_is_the_mean_of_the_middle_two_rounded_half_up()
    {
        Assert.Equal(45, StaffPerformanceRules.Median([70, 30, 50, 40]));
        Assert.Equal(46, StaffPerformanceRules.Median([30, 61]));
        Assert.Equal(91, StaffPerformanceRules.Median([60, 121]));
        Assert.Equal(10, StaffPerformanceRules.Median([10, 10]));
    }

    [Fact]
    public void A_median_of_nothing_is_absent_not_zero() =>
        Assert.Null(StaffPerformanceRules.Median([]));

    // ---------------------------------------------------------------- splitting a sale

    /// <summary>
    /// The shares add up to the bill exactly, whatever the proportions: each gets the whole dram of its share and the dram
    /// left over go to the largest remainders.
    /// </summary>
    [Theory]
    [InlineData(17_490L, 6_400L, 9_500L)]
    [InlineData(10_001L, 1L, 2L)]
    [InlineData(3L, 1L, 1L)]
    [InlineData(100L, 33L, 67L)]
    public void A_bill_split_over_its_waiters_adds_up_to_the_bill(long total, long first, long second)
    {
        var lilit = Guid.CreateVersion7();
        var aram = Guid.CreateVersion7();

        var split = StaffPerformanceRules.Split(total, [(lilit, first), (aram, second)], fallback: aram);

        Assert.Equal(total, split.Sum(s => s.Amd));
        Assert.All(split, s => Assert.InRange(s.Amd, 0L, total));
    }

    [Fact]
    public void A_split_follows_the_lines_and_breaks_a_tie_toward_the_earlier_waiter()
    {
        var lilit = Guid.CreateVersion7();
        var aram = Guid.CreateVersion7();

        // 17,490 over 6,400 and 9,500: 7,039.77 and 10,450.22 - one dram over, to the larger remainder.
        var split = StaffPerformanceRules.Split(17_490L, [(lilit, 6_400L), (aram, 9_500L)], fallback: aram);
        Assert.Equal(7_040L, split.Single(s => s.Server == lilit).Amd);
        Assert.Equal(10_450L, split.Single(s => s.Server == aram).Amd);

        // 3 over 1 and 1: 1.5 each - the dram left goes to the one who held the table first.
        var tie = StaffPerformanceRules.Split(3L, [(lilit, 1L), (aram, 1L)], fallback: aram);
        Assert.Equal(2L, tie.Single(s => s.Server == lilit).Amd);
        Assert.Equal(1L, tie.Single(s => s.Server == aram).Amd);
    }

    [Fact]
    public void A_bill_with_no_lines_to_weigh_is_the_closing_waiters_or_nobodys()
    {
        var aram = Guid.CreateVersion7();

        Assert.Equal([(aram, 1_000L)], StaffPerformanceRules.Split(1_000L, [], fallback: aram));
        Assert.Equal([((Guid?)null, 1_000L)], StaffPerformanceRules.Split(1_000L, [(aram, 0L)], fallback: null));
    }

    // ---------------------------------------------------------------- the History's query

    private static readonly DateOnly Today = new(2026, 10, 24);

    [Fact]
    public void The_history_reads_today_by_default_and_a_date_alone_as_that_day()
    {
        var defaults = ActivityFeedRules.Check(new ActivityFeedQuery(), Today);
        Assert.Equal(Today, defaults.From);
        Assert.Equal(Today, defaults.To);
        Assert.Equal(ActivityFeedRules.DefaultLimit, defaults.Limit);

        var day = ActivityFeedRules.Check(new ActivityFeedQuery(Date: "2026-10-20"), Today);
        Assert.Equal(new DateOnly(2026, 10, 20), day.From);
        Assert.Equal(new DateOnly(2026, 10, 20), day.To);
    }

    [Fact]
    public void Every_bad_history_parameter_is_named_in_one_refusal()
    {
        var refused = Assert.Throws<FieldValidationException>(() => ActivityFeedRules.Check(
            new ActivityFeedQuery(
                Date: "2026-10-20",
                From: "2026-10-19",
                StaffMemberId: "aram",
                Category: "everything",
                Q: new string('x', 61),
                Cursor: "not-ours",
                Limit: "0"),
            Today));

        Assert.Equal(
            ["date", "staffMemberId", "category", "q", "cursor", "limit"],
            refused.Violations.Select(v => v.Field).ToArray());
    }

    [Fact]
    public void The_history_reaches_back_as_far_as_money_is_kept_and_no_further()
    {
        var oldest = Today.AddDays(-ActivityEntry.MoneyRetentionDays);

        var atTheEdge = ActivityFeedRules.Check(
            new ActivityFeedQuery(From: DateRules.Iso(oldest), To: DateRules.Iso(oldest.AddDays(10))), Today);
        Assert.Equal(oldest, atTheEdge.From);

        var tooOld = Assert.Throws<FieldValidationException>(() => ActivityFeedRules.Check(
            new ActivityFeedQuery(From: DateRules.Iso(oldest.AddDays(-1)), To: DateRules.Iso(oldest)), Today));
        Assert.Equal("from", Assert.Single(tooOld.Violations).Field);

        var dayTooOld = Assert.Throws<FieldValidationException>(() => ActivityFeedRules.Check(
            new ActivityFeedQuery(Date: DateRules.Iso(oldest.AddDays(-1))), Today));
        Assert.Equal("date", Assert.Single(dayTooOld.Violations).Field);

        var tooLong = Assert.Throws<FieldValidationException>(() => ActivityFeedRules.Check(
            new ActivityFeedQuery(From: DateRules.Iso(Today.AddDays(-366)), To: DateRules.Iso(Today)), Today));
        Assert.Equal("to", Assert.Single(tooLong.Violations).Field);
    }

    /// <summary>
    /// A far-future date is a 422, not the 500 the day after <c>9999-12-31</c> used to throw - in the History and the
    /// staff results alike.
    /// </summary>
    [Fact]
    public void A_window_far_in_the_future_is_refused_by_name_not_by_overflow()
    {
        // Named as the field sent: a date alone is refused as date, from and to as to.
        var feed = Assert.Throws<FieldValidationException>(() => ActivityFeedRules.Check(
            new ActivityFeedQuery(Date: "9999-12-31"), Today));
        Assert.Equal("date", Assert.Single(feed.Violations).Field);

        var feedTo = Assert.Throws<FieldValidationException>(() => ActivityFeedRules.Check(
            new ActivityFeedQuery(To: "9999-12-31"), Today));
        Assert.Equal("to", Assert.Single(feedTo.Violations).Field);

        var results = Assert.Throws<FieldValidationException>(() => StaffPerformanceRules.Check(
            new StaffPerformanceQuery(From: "9999-12-31"), Today));
        Assert.Equal("to", Assert.Single(results.Violations).Field);

        // A year ahead is still a window (empty, but asked for).
        var ahead = ActivityFeedRules.Check(new ActivityFeedQuery(Date: DateRules.Iso(Today.AddDays(366))), Today);
        Assert.Equal(Today.AddDays(366), ahead.To);
    }

    [Fact]
    public void A_cursor_survives_its_round_trip()
    {
        var cursor = new ActivityCursor(new DateTime(2026, 10, 24, 18, 31, 0, DateTimeKind.Utc), Guid.CreateVersion7());

        Assert.Equal(cursor, ActivityCursor.TryDecode(cursor.Encode()));
        Assert.Null(ActivityCursor.TryDecode("abc"));
    }

    [Fact]
    public void Seconds_come_from_the_summary_key_the_action_measures()
    {
        Assert.Equal(31, ActivityFeedRules.SecondsOf(Json("""{"responseSeconds":31}""")));
        Assert.Equal(95, ActivityFeedRules.SecondsOf(Json("""{"replySeconds":95,"text":"Yes"}""")));
        Assert.Equal(240, ActivityFeedRules.SecondsOf(Json("""{"extra":{"decisionSeconds":240}}""")));
        Assert.Null(ActivityFeedRules.SecondsOf(Json("""{"replySeconds":null}""")));
        Assert.Null(ActivityFeedRules.SecondsOf(Json("""{"amountAmd":5000}""")));
    }

    [Fact]
    public void A_performance_window_defaults_to_today_and_names_a_bad_list()
    {
        var defaults = StaffPerformanceRules.Check(new StaffPerformanceQuery(), Today);
        Assert.Equal(Today, defaults.From);
        Assert.Equal(Today, defaults.To);
        Assert.Equal(RecentList.Orders, defaults.List);

        var refused = Assert.Throws<FieldValidationException>(() =>
            StaffPerformanceRules.Check(new StaffPerformanceQuery(From: "2026-10-25", To: "2026-10-20", List: "tips"), Today));
        Assert.Equal(["from", "list"], refused.Violations.Select(v => v.Field).ToArray());
    }

    // ---------------------------------------------------------------- the entry and the sitting

    [Fact]
    public void An_entry_keeps_its_labels_clipped_and_its_json_whole()
    {
        var entry = new ActivityEntry(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new DateTime(2026, 10, 24, 14, 0, 0, DateTimeKind.Utc),
            ActivityActorKind.Staff,
            Guid.CreateVersion7(),
            new string('A', 300),
            null,
            "Console",
            ActivityCategory.Order,
            ActivityActions.OrderSent,
            ActivitySubjects.Tab,
            Guid.CreateVersion7(),
            "{}",
            beforeJson: "{\"x\":\"" + new string('y', 5000) + "\"}");

        Assert.Equal(200, entry.ActorName!.Length);
        Assert.Equal("""{"truncated":true}""", entry.BeforeJson);
        Assert.Equal("{}", entry.SummaryJson);
    }

    /// <summary>
    /// The device as parts: a kind only beside a device, a person only for a phone - and on the wire the console is a
    /// kind of its own, read from an entry with staff and no device, so entries written before the parts have it too.
    /// </summary>
    [Fact]
    public void An_entry_keeps_its_device_as_parts_and_the_wire_words_them()
    {
        var phone = Entry(Guid.CreateVersion7(), StaffDeviceKind.Phone, "Aram Petrosyan");
        Assert.Equal((StaffDeviceKind.Phone, "Aram Petrosyan"), (phone.DeviceKind!.Value, phone.DeviceOwnerName));

        var tablet = Entry(Guid.CreateVersion7(), StaffDeviceKind.Tablet, "Aram Petrosyan");
        Assert.Equal(StaffDeviceKind.Tablet, tablet.DeviceKind);
        Assert.Null(tablet.DeviceOwnerName);

        var console = Entry(deviceId: null, StaffDeviceKind.Phone, "Aram Petrosyan");
        Assert.Null(console.DeviceKind);
        Assert.Null(console.DeviceOwnerName);

        Assert.Equal("phone", ActivityWords.DeviceKindOf(ActivityActorKind.Staff, Guid.CreateVersion7(), StaffDeviceKind.Phone));
        Assert.Equal("tablet", ActivityWords.DeviceKindOf(ActivityActorKind.Staff, Guid.CreateVersion7(), StaffDeviceKind.Tablet));
        Assert.Equal("console", ActivityWords.DeviceKindOf(ActivityActorKind.Staff, null, null));
        Assert.Equal("console", ActivityWords.DeviceKindOf(ActivityActorKind.PlatformAdmin, null, null));
        Assert.Null(ActivityWords.DeviceKindOf(ActivityActorKind.Staff, Guid.CreateVersion7(), null));
        Assert.Null(ActivityWords.DeviceKindOf(ActivityActorKind.System, null, null));
        Assert.Null(ActivityWords.DeviceKindOf(ActivityActorKind.Diner, null, null));

        static ActivityEntry Entry(Guid? deviceId, StaffDeviceKind kind, string owner) => new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new DateTime(2026, 10, 26, 14, 0, 0, DateTimeKind.Utc),
            ActivityActorKind.Staff,
            Guid.CreateVersion7(),
            "Aram Petrosyan",
            deviceId,
            "Aram's phone",
            ActivityCategory.Order,
            ActivityActions.OrderSent,
            ActivitySubjects.Tab,
            Guid.CreateVersion7(),
            "{}",
            deviceKind: kind,
            deviceOwnerName: owner);
    }

    [Fact]
    public void An_entry_by_staff_must_name_them() =>
        Assert.Throws<ArgumentException>(() => new ActivityEntry(
            Guid.CreateVersion7(),
            null,
            new DateTime(2026, 10, 24, 14, 0, 0, DateTimeKind.Utc),
            ActivityActorKind.Staff,
            null,
            null,
            null,
            null,
            ActivityCategory.Staff,
            ActivityActions.StaffPinSet,
            ActivitySubjects.StaffMember,
            Guid.CreateVersion7(),
            "{}"));

    [Fact]
    public void A_sitting_is_served_by_who_seated_it_and_moves_only_by_transfer()
    {
        var lilit = Guid.CreateVersion7();
        var aram = Guid.CreateVersion7();
        var seatedAt = new DateTime(2026, 10, 24, 13, 12, 0, DateTimeKind.Utc);

        var session = TableSession.SeatWalkIn(Guid.CreateVersion7(), Guid.CreateVersion7(), 4, seatedAt, lilit);
        Assert.Equal(lilit, session.ServerStaffMemberId);
        Assert.False(session.ClaimServerIfNone(aram, seatedAt.AddMinutes(3)));

        Assert.Equal(lilit, session.TransferServer(aram, Guid.CreateVersion7(), seatedAt.AddMinutes(28)));
        Assert.Equal(aram, session.ServerStaffMemberId);
        Assert.Equal(seatedAt.AddMinutes(28), session.ServerAssignedAtUtc);
        Assert.Throws<DomainStateException>(() => session.TransferServer(aram, Guid.CreateVersion7(), seatedAt.AddMinutes(29)));

        session.Close(seatedAt.AddHours(1));
        Assert.Throws<DomainStateException>(() => session.TransferServer(lilit, Guid.CreateVersion7(), seatedAt.AddHours(2)));

        var scanned = TableSession.SeatWalkIn(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, seatedAt);
        Assert.Null(scanned.ServerStaffMemberId);
        Assert.True(scanned.ClaimServerIfNone(aram, seatedAt.AddMinutes(5)));
        Assert.Equal(aram, scanned.ServerStaffMemberId);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
