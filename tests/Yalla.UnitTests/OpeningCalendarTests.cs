using Yalla.Application.BranchSettings;
using Yalla.Domain;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The hours on a date: the weekly week with special days laid over it, and the rules for a list of them.
/// </summary>
/// <remarks>
/// Open 10:00-23:00 every day of the week. 31 December 2026 is a Thursday open 18:00-03:00, and 1 January 2027 a
/// Friday shut all day.
/// </remarks>
public class OpeningCalendarTests
{
    private static readonly DateOnly Eve = new(2026, 12, 31);

    private static readonly DateOnly NewYear = new(2027, 1, 1);

    private static readonly OpeningBlock Day = OpeningBlock.Between(new TimeOnly(10, 0), new TimeOnly(23, 0));

    private static readonly OpeningBlock LateNight = OpeningBlock.Between(new TimeOnly(18, 0), new TimeOnly(3, 0));

    private static OpeningCalendar Calendar(params SpecialDay[] special) =>
        new(
            Enum.GetValues<DayOfWeek>().Select(d => (d, Day)),
            special,
            Eve.AddDays(-7),
            NewYear.AddDays(7));

    private static OpeningCalendar Holidays() =>
        Calendar(new SpecialDay(Eve, "New Year's Eve", [LateNight]), new SpecialDay(NewYear, "New Year", []));

    private static DateTime At(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute));

    [Fact]
    public void A_special_day_replaces_its_weekdays_blocks_and_an_ordinary_day_keeps_them()
    {
        var hours = Holidays();

        Assert.Equal([LateNight], hours.BlocksOn(Eve));
        Assert.Empty(hours.BlocksOn(NewYear));
        Assert.Equal([Day], hours.BlocksOn(NewYear.AddDays(1)));

        Assert.Equal("New Year", hours.SpecialDayOn(NewYear)!.Note);
        Assert.True(hours.SpecialDayOn(NewYear)!.IsClosed);
        Assert.Null(hours.SpecialDayOn(NewYear.AddDays(1)));
        Assert.Equal([Eve, NewYear], hours.SpecialDays.Select(d => d.Date));
    }

    [Fact]
    public void A_sitting_fits_the_special_days_hours_and_not_the_weekdays()
    {
        var hours = Holidays();

        // Lunch on a closed New Year's Day, though Fridays are open at noon.
        Assert.Equal(
            ReservationRejectionReason.OutsideOpeningHours,
            ReservationRules.CheckOpeningHours(hours, At(NewYear, 12), At(NewYear, 13, 30)));

        // Late on the Eve, though Thursdays close at 23:00 - and after midnight, inside the Eve's block, on a
        // date that is itself shut.
        Assert.Null(ReservationRules.CheckOpeningHours(hours, At(Eve, 23, 30), At(NewYear, 1)));
        Assert.Null(ReservationRules.CheckOpeningHours(hours, At(NewYear, 1), At(NewYear, 2, 30)));

        // But not past the Eve's 03:00, and not at the weekday's noon on the Eve either.
        Assert.NotNull(ReservationRules.CheckOpeningHours(hours, At(NewYear, 2), At(NewYear, 3, 30)));
        Assert.NotNull(ReservationRules.CheckOpeningHours(hours, At(Eve, 12), At(Eve, 13, 30)));
    }

    [Fact]
    public void A_weekly_block_past_midnight_still_runs_into_a_special_day()
    {
        var hours = new OpeningCalendar(
            Enum.GetValues<DayOfWeek>().Select(d => (d, OpeningBlock.Between(new TimeOnly(10, 0), new TimeOnly(1, 0)))),
            [new SpecialDay(NewYear, null, [])],
            Eve.AddDays(-7),
            NewYear.AddDays(7));

        Assert.True(hours.IsOpenAt(At(NewYear, 0, 30)));
        Assert.False(hours.IsOpenAt(At(NewYear, 1)));
        Assert.False(hours.IsOpenAt(At(NewYear, 12)));
        Assert.Equal(Eve, hours.ServiceDateOf(At(NewYear, 0, 30)));
    }

    [Fact]
    public void Open_now_and_the_service_day_follow_the_special_days()
    {
        var hours = Holidays();

        Assert.False(hours.IsOpenAt(At(Eve, 12)));
        Assert.True(hours.IsOpenAt(At(Eve, 23, 30)));

        var running = hours.OpenAt(At(NewYear, 1));
        Assert.NotNull(running);
        Assert.Equal(Eve, running.Value.Date);
        Assert.Equal(At(NewYear, 3), running.Value.Closes);

        Assert.Equal(Eve, hours.ServiceDateOf(At(NewYear, 2, 59)));
        Assert.Equal(NewYear, hours.ServiceDateOf(At(NewYear, 3)));
        Assert.False(hours.IsOpenAt(At(NewYear, 12)));
    }

    [Fact]
    public void A_date_outside_the_window_the_special_days_were_read_for_is_refused_rather_than_guessed()
    {
        var hours = Holidays();

        Assert.Throws<InvalidOperationException>(() => hours.BlocksOn(Eve.AddDays(-8)));
        Assert.Throws<InvalidOperationException>(() => hours.IsOpenAt(At(NewYear.AddDays(8), 12)));

        // A calendar read for every date has no special days to miss, so it answers for any date.
        Assert.Single(TestCalendars.Weekly([new OpeningHours(Guid.CreateVersion7(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0), false)])
            .BlocksOn(new DateOnly(2030, 1, 7)));
    }

    // ---------------------------------------------------------------- the list's rules

    private static readonly DateOnly Today = new(2026, 12, 30);

    private static SpecialOpeningDayInput Input(DateOnly date, string? note, params (string Opens, string Closes)[] blocks) =>
        new(date, note, [.. blocks.Select(b => new OpeningBlockInput(TimeOnly.Parse(b.Opens), TimeOnly.Parse(b.Closes)))]);

    [Fact]
    public void A_list_is_derived_ordered_and_trimmed()
    {
        var days = SpecialOpeningDayRules.Normalise(
            [Input(NewYear, "   "), Input(Eve, "  New Year's Eve ", ("18:00", "03:00"))],
            Today);

        Assert.Equal([Eve, NewYear], days.Select(d => d.Date));
        Assert.Equal("New Year's Eve", days[0].Note);
        Assert.True(days[0].Blocks.Single().ClosesNextDay);
        Assert.Null(days[1].Note);
        Assert.True(days[1].IsClosed);

        // Today itself is allowed: closing tonight at short notice is the commonest special day there is.
        Assert.Single(SpecialOpeningDayRules.Normalise([Input(Today, null)], Today));
    }

    [Fact]
    public void Every_bad_day_is_reported_against_its_own_field_with_its_bound()
    {
        var refused = Assert.Throws<FieldValidationException>(() => SpecialOpeningDayRules.Normalise(
            [
                Input(Today.AddDays(-1), null),
                Input(Today.AddDays(SpecialOpeningDayRules.HorizonDays + 1), null),
                Input(Eve, null),
                Input(Eve, null),
                Input(NewYear, new string('a', SpecialOpeningDay.MaxNoteLength + 1)),
                Input(NewYear.AddDays(1), null, ("12:00", "16:00"), ("15:00", "20:00")),
                Input(NewYear.AddDays(2), null, ("12:00", "12:00")),
                new SpecialOpeningDayInput(NewYear.AddDays(3), null, null!),
                Input(NewYear.AddDays(4), null, ("08:00", "09:00"), ("10:00", "11:00"), ("12:00", "13:00"), ("14:00", "15:00"), ("16:00", "17:00")),
                new SpecialOpeningDayInput(default, null, []),
            ],
            Today));

        var byField = refused.Violations.ToDictionary(v => v.Field, v => v.Bound);

        // Yesterday is history: ignored, not refused.
        Assert.DoesNotContain(byField.Keys, k => k.StartsWith("days[0]", StringComparison.Ordinal));
        Assert.Equal(FieldBounds.Max, byField["days[1].date"]);
        Assert.False(byField.ContainsKey("days[2].date"));
        Assert.Equal(FieldBounds.Conflict, byField["days[3].date"]);
        Assert.Equal(FieldBounds.Max, byField["days[4].note"]);
        Assert.Equal(FieldBounds.Conflict, byField["days[5].blocks[1].opensAt"]);
        Assert.Equal(FieldBounds.Conflict, byField["days[6].blocks[0].closesAt"]);
        Assert.Equal(FieldBounds.Required, byField["days[7].blocks"]);
        Assert.Equal(FieldBounds.Max, byField["days[8].blocks"]);
        Assert.Equal(FieldBounds.Required, byField["days[9].date"]);
        Assert.Equal(8, refused.Violations.Count);
    }

    [Fact]
    public void Blocks_may_touch_and_an_overnight_block_overlapping_a_later_one_is_caught()
    {
        Assert.Equal(
            2,
            SpecialOpeningDayRules.Normalise([Input(Eve, null, ("12:00", "15:00"), ("15:00", "23:00"))], Today)[0].Blocks.Count);

        var refused = Assert.Throws<FieldValidationException>(() => SpecialOpeningDayRules.Normalise(
            [Input(Eve, null, ("22:00", "02:00"), ("10:00", "23:00"))], Today));

        Assert.Equal("days[0].blocks[0].opensAt", refused.Violations.Single().Field);
    }

    [Fact]
    public void More_than_a_hundred_days_is_one_refusal()
    {
        var many = Enumerable.Range(0, SpecialOpeningDayRules.MaxDays + 1).Select(i => Input(Today.AddDays(i), null)).ToList();

        var refused = Assert.Throws<FieldValidationException>(() => SpecialOpeningDayRules.Normalise(many, Today));

        Assert.Equal($"days[{SpecialOpeningDayRules.MaxDays}]", refused.Violations.Single().Field);
        Assert.Equal(FieldBounds.Max, refused.Violations.Single().Bound);
    }
}
