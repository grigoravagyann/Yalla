using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Takeaway;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The takeaway rules that need no database: when orders are taken and which pickup times are offered, when the kitchen
/// is full, the code, the settings' bounds and the shape of an order. See <c>docs/takeaway.md</c>.
/// </summary>
public class TakeawayRulesTests
{
    /// <summary>Thursday 22 October 2026, on the branch's wall clock.</summary>
    private static readonly DateOnly Thursday = new(2026, 10, 22);

    private static OpeningCalendar Calendar(TimeOnly opens, TimeOnly closes, bool closesNextDay = false) =>
        TestCalendars.Weekly(Enum.GetValues<DayOfWeek>().Select(d => new OpeningHours(Guid.CreateVersion7(), d, opens, closes, closesNextDay)));

    private static DateTime At(int hour, int minute, DateOnly? date = null) =>
        (date ?? Thursday).ToDateTime(new TimeOnly(hour, minute));

    /// <summary>
    /// Open 10:00-23:00, 20 minutes' prep, last order 30 minutes before closing: at 18:52 ASAP is ready at 19:12, the
    /// slots run on the quarter hour from 19:15 to 22:30, and the last pickup is 22:30.
    /// </summary>
    [Fact]
    public void Slots_run_on_the_quarter_hour_from_now_plus_prep_to_closing_minus_the_last_order()
    {
        var window = TakeawayRules.Window(Calendar(new(10, 0), new(23, 0)), At(18, 52), 20, 30);

        Assert.True(window.Open);
        Assert.Equal(At(19, 12), window.AsapReadyLocal);
        Assert.Equal(At(22, 30), window.LastPickupLocal);
        Assert.Equal(At(19, 15), window.SlotsLocal[0]);
        Assert.Equal(At(22, 30), window.SlotsLocal[^1]);
        Assert.Equal(14, window.SlotsLocal.Count);
        Assert.All(window.SlotsLocal, s => Assert.Equal(0, s.Minute % 15));
        Assert.Null(window.OpensAtLocal);
    }

    /// <summary>A time on the quarter hour is itself the first slot; seconds round up to the next minute first.</summary>
    [Fact]
    public void A_time_on_the_quarter_is_the_first_slot_and_seconds_round_up()
    {
        Assert.Equal(At(19, 15), TakeawayRules.CeilToStep(At(19, 15)));
        Assert.Equal(At(19, 30), TakeawayRules.CeilToStep(At(19, 15).AddSeconds(1)));
        Assert.Equal(At(19, 0), TakeawayRules.CeilToStep(At(18, 46)));

        var window = TakeawayRules.Window(Calendar(new(10, 0), new(23, 0)), At(18, 55), 20, 30);
        Assert.Equal(At(19, 15), window.SlotsLocal[0]);
    }

    /// <summary>
    /// Ordering stops once now plus prep would pass the last pickup: at 22:10 with 20 minutes' prep the last pickup 22:30
    /// is still reachable; at 22:11 it is not, and the window says when it opens next - tomorrow at 10:00.
    /// </summary>
    [Fact]
    public void Ordering_closes_when_the_earliest_pickup_passes_the_last_and_names_the_next_opening()
    {
        var calendar = Calendar(new(10, 0), new(23, 0));

        var last = TakeawayRules.Window(calendar, At(22, 10), 20, 30);
        Assert.True(last.Open);
        Assert.Equal([At(22, 30)], last.SlotsLocal);

        var closed = TakeawayRules.Window(calendar, At(22, 11), 20, 30);
        Assert.False(closed.Open);
        Assert.Empty(closed.SlotsLocal);
        Assert.Null(closed.AsapReadyLocal);
        Assert.Equal(At(10, 0, Thursday.AddDays(1)), closed.OpensAtLocal);
    }

    /// <summary>Before opening, closed, opening later the same day.</summary>
    [Fact]
    public void Before_opening_it_is_closed_and_opens_at_the_opening()
    {
        var window = TakeawayRules.Window(Calendar(new(10, 0), new(23, 0)), At(8, 30), 20, 30);

        Assert.False(window.Open);
        Assert.Equal(At(10, 0), window.OpensAtLocal);
    }

    /// <summary>
    /// A block running past midnight belongs to the day it opened on: at 23:40 a branch open 12:00-01:00 offers 00:00
    /// and 00:15 with no last-order margin - slots on the next calendar date.
    /// </summary>
    [Fact]
    public void A_block_past_midnight_offers_slots_after_midnight()
    {
        var window = TakeawayRules.Window(Calendar(new(12, 0), new(1, 0), closesNextDay: true), At(23, 40), 20, 45);

        Assert.True(window.Open);
        Assert.Equal(At(0, 15, Thursday.AddDays(1)), window.LastPickupLocal);
        Assert.Equal([At(0, 0, Thursday.AddDays(1)), At(0, 15, Thursday.AddDays(1))], window.SlotsLocal);
    }

    /// <summary>A day with no hours at all: closed, and the next opening is found days on.</summary>
    [Fact]
    public void A_closed_day_looks_ahead_for_the_next_opening()
    {
        var calendar = TestCalendars.Weekly(
            [new OpeningHours(Guid.CreateVersion7(), DayOfWeek.Saturday, new TimeOnly(11, 0), new TimeOnly(22, 0), false)]);

        var window = TakeawayRules.Window(calendar, At(12, 0), 20, 30);

        Assert.False(window.Open);
        Assert.Equal(At(11, 0, new DateOnly(2026, 10, 24)), window.OpensAtLocal);
    }

    /// <summary>The kitchen is full at MaxInProgress orders sent or accepted, not before.</summary>
    [Fact]
    public void Busy_is_max_in_progress_or_more()
    {
        Assert.False(TakeawayRules.IsBusy(4, 5));
        Assert.True(TakeawayRules.IsBusy(5, 5));
        Assert.True(TakeawayRules.IsBusy(6, 5));

        Assert.True(TakeawayRules.IsInProgress(TakeawayStatus.Sent));
        Assert.True(TakeawayRules.IsInProgress(TakeawayStatus.Accepted));
        Assert.False(TakeawayRules.IsInProgress(TakeawayStatus.Ready));
        Assert.False(TakeawayRules.IsInProgress(TakeawayStatus.Collected));
    }

    /// <summary>
    /// A slot is offered while fewer than MaxInProgress orders are cooked in a window overlapping its own: with two of
    /// two at 19:15, 19:15 and 19:30 are full and 19:45 (20 minutes' prep) is free again.
    /// </summary>
    [Fact]
    public void A_slot_is_left_out_when_the_orders_cooked_beside_it_would_exceed_the_maximum()
    {
        var slot = new DateTime(2026, 10, 22, 15, 15, 0, DateTimeKind.Utc);
        var pickups = new[] { slot, slot };

        Assert.False(TakeawayRules.SlotFits(slot, 20, pickups, 2));
        Assert.False(TakeawayRules.SlotFits(slot.AddMinutes(15), 20, pickups, 2));
        Assert.True(TakeawayRules.SlotFits(slot.AddMinutes(30), 20, pickups, 2));
        Assert.True(TakeawayRules.SlotFits(slot, 20, pickups, 3));
    }

    /// <summary>"T-" and three digits; a fresh code is never one already used today; a full day has none left.</summary>
    [Fact]
    public void Codes_are_T_and_three_digits_and_never_repeat_within_a_day()
    {
        Assert.Equal("T-007", TakeawayRules.CodeFor(7));
        Assert.Equal("T-482", TakeawayRules.CodeFor(482));
        Assert.Throws<ArgumentOutOfRangeException>(() => TakeawayRules.CodeFor(1000));

        var used = Enumerable.Range(0, 999).Select(TakeawayRules.CodeFor).ToHashSet();
        Assert.Equal("T-999", TakeawayRules.FreshCode(used));

        used.Add("T-999");
        Assert.Null(TakeawayRules.FreshCode(used));

        var code = TakeawayRules.FreshCode(new HashSet<string>());
        Assert.Matches("^T-[0-9]{3}$", code);
    }

    /// <summary>The settings' bounds and steps, every field named.</summary>
    [Fact]
    public void Settings_out_of_bounds_or_off_their_step_are_named()
    {
        Assert.Empty(TakeawayRules.CheckSettings(20, 30, 5));
        Assert.Empty(TakeawayRules.CheckSettings(5, 0, 1));
        Assert.Empty(TakeawayRules.CheckSettings(90, 120, 30));

        var violations = TakeawayRules.CheckSettings(4, 135, 0);
        Assert.Equal(["prepMinutes", "lastOrderMinutes", "maxInProgress"], violations.Select(v => v.Field));

        var steps = TakeawayRules.CheckSettings(22, 20, 31);
        Assert.Equal(["prepMinutes", "lastOrderMinutes", "maxInProgress"], steps.Select(v => v.Field));
    }

    /// <summary>The branch refuses settings out of bounds whole, and defaults to off, manual, 20, 30, 5.</summary>
    [Fact]
    public void A_branch_starts_off_with_the_defaults_and_refuses_bad_settings_whole()
    {
        var venue = new Venue("Lava", VenueType.Restaurant, "lava");
        var branch = new Branch(venue, "Kentron", "kentron", "Abovyan 12", 40.18, 44.51, "Asia/Yerevan");

        Assert.False(branch.TakeawayEnabled);
        Assert.False(branch.TakeawayAutoAccept);
        Assert.Equal((20, 30, 5), (branch.TakeawayPrepMinutes, branch.TakeawayLastOrderMinutes, branch.TakeawayMaxInProgress));

        Assert.Throws<FieldValidationException>(() => branch.SetTakeaway(true, true, 95, 30, 5));
        Assert.False(branch.TakeawayEnabled);

        branch.SetTakeaway(true, true, 25, 45, 8);
        Assert.Equal((true, true, 25, 45, 8), (branch.TakeawayEnabled, branch.TakeawayAutoAccept, branch.TakeawayPrepMinutes, branch.TakeawayLastOrderMinutes, branch.TakeawayMaxInProgress));
    }

    /// <summary>The shape of an order: lines, quantities, total and note, every one named - never an overflow.</summary>
    [Fact]
    public void An_orders_shape_is_checked_field_by_field()
    {
        Assert.Empty(TakeawayRules.Check([1, 2], "no onion"));
        Assert.Contains(TakeawayRules.Check([], null), v => v.Field == "lines");
        Assert.Contains(TakeawayRules.Check([0, 21], null), v => v.Field == "lines[0].quantity");
        Assert.Contains(TakeawayRules.Check([0, 21], null), v => v.Field == "lines[1].quantity");
        Assert.Contains(TakeawayRules.Check([20, 20, 20, 1], null), v => v.Field == "lines" && v.Bound == "max");
        Assert.Contains(TakeawayRules.Check([int.MaxValue, int.MaxValue], null), v => v.Field == "lines" && v.Bound == "max");
        Assert.Contains(TakeawayRules.Check([1], new string('x', 501)), v => v.Field == "note");
    }

    /// <summary>No-show is thirty minutes after the later of the promised time and the kitchen's "ready".</summary>
    [Fact]
    public void No_show_counts_from_the_later_of_promised_and_marked_ready()
    {
        var promised = new DateTime(2026, 10, 22, 15, 10, 0, DateTimeKind.Utc);

        Assert.Equal(promised.AddMinutes(30), TakeawayRules.NoShowFromUtc(promised, null));
        Assert.Equal(promised.AddMinutes(30), TakeawayRules.NoShowFromUtc(promised, promised.AddMinutes(-5)));
        Assert.Equal(promised.AddMinutes(42), TakeawayRules.NoShowFromUtc(promised, promised.AddMinutes(12)));
    }

    /// <summary>The persisted numbers of takeaway's enums, pinned.</summary>
    [Fact]
    public void Takeaway_enum_values_are_pinned()
    {
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7],
            new[]
            {
                TakeawayStatus.Sent, TakeawayStatus.Accepted, TakeawayStatus.Ready, TakeawayStatus.Collected,
                TakeawayStatus.Declined, TakeawayStatus.Cancelled, TakeawayStatus.NoShow,
            }.Select(s => (int)s));
        Assert.Equal(1, (int)TakeawayPaymentMethod.Cash);
        Assert.Equal(2, (int)TakeawayPaymentMethod.Card);
        Assert.Equal(1, (int)TakeawayDeclineCause.StaffDeclined);
        Assert.Equal(2, (int)TakeawayDeclineCause.NoAnswer);
        Assert.Equal(1, (int)TakeawayItemRefusal.NotOnMenu);
        Assert.Equal(2, (int)TakeawayItemRefusal.SoldOut);
        Assert.Equal(3, (int)TakeawayItemRefusal.NotForTakeaway);
    }
}
