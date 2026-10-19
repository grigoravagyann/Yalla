using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Staff;

namespace Yalla.UnitTests;

/// <summary>
/// One shift's own fields, and the rules between one person's shifts: touching is fine, overlapping is not, and a
/// day off has nothing else on its date.
/// </summary>
/// <remarks>The branch's service date is Monday 19 October 2026.</remarks>
public class StaffShiftRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 19);

    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    private static ShiftSlot Shift(DateOnly date, int from, int to) => new(date, T(from), T(to));

    private static ShiftSlot DayOff(DateOnly date) => new(date, null, null);

    private static IEnumerable<string> Fields(FieldValidationException e) =>
        e.Violations.Select(v => $"{v.Field}:{v.Bound}").Order(StringComparer.Ordinal);

    [Fact]
    public void A_working_shift_and_a_day_off_come_back_as_given_and_an_overnight_end_is_the_next_day()
    {
        var overnight = StaffShiftRules.Normalise(Today, T(16), T(0), dayOff: false, Today);

        Assert.Equal(new ShiftSlot(Today, T(16), T(0)), overnight);
        Assert.Equal(Today.AddDays(1).ToDateTime(T(0)), overnight.LocalEnd);

        var late = StaffShiftRules.Normalise(Today.AddDays(1), T(18), T(2), dayOff: false, Today);

        Assert.Equal(Today.AddDays(2).ToDateTime(T(2)), late.LocalEnd);

        var off = StaffShiftRules.Normalise(Today, null, null, dayOff: true, Today);

        Assert.True(off.IsDayOff);
        Assert.Null(off.LocalStart);
    }

    [Fact]
    public void Every_bad_field_is_named_with_its_bound()
    {
        Assert.Equal(
            ["date:required", "endsAt:required", "startsAt:required"],
            Fields(Assert.Throws<FieldValidationException>(
                () => StaffShiftRules.Normalise(null, null, null, dayOff: false, Today))));

        var past = Assert.Throws<FieldValidationException>(
            () => StaffShiftRules.Normalise(Today.AddDays(-1), T(10), T(18), dayOff: false, Today));

        var min = Assert.Single(past.Violations);
        Assert.Equal(("date", FieldBounds.Min), (min.Field, min.Bound));
        Assert.Equal(Today, min.Min);

        var far = Assert.Throws<FieldValidationException>(
            () => StaffShiftRules.Normalise(Today.AddDays(StaffShiftRules.HorizonDays + 1), T(10), T(18), dayOff: false, Today));

        Assert.Equal(("date", FieldBounds.Max), (far.Violations[0].Field, far.Violations[0].Bound));

        // The horizon itself is allowed.
        StaffShiftRules.Normalise(Today.AddDays(StaffShiftRules.HorizonDays), T(10), T(18), dayOff: false, Today);

        Assert.Equal(
            ["endsAt:conflict"],
            Fields(Assert.Throws<FieldValidationException>(
                () => StaffShiftRules.Normalise(Today, T(10), T(10), dayOff: false, Today))));

        // 08:00 to 07:00 is 23 hours: a slip, not a shift. 10:00 to 02:00 is 16 and is fine.
        var tooLong = Assert.Throws<FieldValidationException>(
            () => StaffShiftRules.Normalise(Today, T(8), T(7), dayOff: false, Today));

        Assert.Equal(("endsAt", FieldBounds.Max), (tooLong.Violations[0].Field, tooLong.Violations[0].Bound));
        Assert.Equal(StaffShift.MaxLengthMinutes, tooLong.Violations[0].Max);
        Assert.Equal(23 * 60, tooLong.Violations[0].Value);

        StaffShiftRules.Normalise(Today, T(10), T(2), dayOff: false, Today);

        Assert.Equal(
            ["startsAt:conflict"],
            Fields(Assert.Throws<FieldValidationException>(
                () => StaffShiftRules.Normalise(Today, T(10), T(18), dayOff: true, Today))));
    }

    [Fact]
    public void Touching_shifts_are_fine_and_overlapping_ones_are_refused_across_midnight_too()
    {
        StaffShiftRules.RequireNoClash(Shift(Today, 16, 22), [Shift(Today, 10, 16), Shift(Today.AddDays(1), 10, 16)]);

        var overlap = Assert.Throws<FieldValidationException>(
            () => StaffShiftRules.RequireNoClash(Shift(Today, 15, 22), [Shift(Today, 10, 16)]));

        Assert.Equal(["startsAt:conflict"], Fields(overlap));

        // Last night's 18:00-02:00 runs into this morning's 01:00 start.
        Assert.Throws<FieldValidationException>(
            () => StaffShiftRules.RequireNoClash(Shift(Today, 1, 9), [Shift(Today.AddDays(-1), 18, 2)]));

        // And ending at 00:00 touches a shift starting at 00:00 the next day without overlapping it.
        StaffShiftRules.RequireNoClash(Shift(Today.AddDays(1), 0, 6), [Shift(Today, 16, 0)]);
    }

    [Fact]
    public void A_day_off_has_nothing_else_on_its_date()
    {
        Assert.Equal(
            ["date:conflict"],
            Fields(Assert.Throws<FieldValidationException>(
                () => StaffShiftRules.RequireNoClash(DayOff(Today), [Shift(Today, 10, 16)]))));

        Assert.Equal(
            ["date:conflict"],
            Fields(Assert.Throws<FieldValidationException>(
                () => StaffShiftRules.RequireNoClash(Shift(Today, 10, 16), [DayOff(Today)]))));

        Assert.Throws<FieldValidationException>(() => StaffShiftRules.RequireNoClash(DayOff(Today), [DayOff(Today)]));

        // Yesterday's late shift is yesterday's, so today can still be a day off.
        StaffShiftRules.RequireNoClash(DayOff(Today), [Shift(Today.AddDays(-1), 18, 2), DayOff(Today.AddDays(1))]);
    }

    [Fact]
    public void A_read_window_defaults_to_two_weeks_from_the_service_date_and_is_capped()
    {
        Assert.Equal((Today, Today.AddDays(13)), StaffShiftRules.Window(null, null, Today));
        Assert.Equal((Today.AddDays(2), Today.AddDays(15)), StaffShiftRules.Window(Today.AddDays(2), null, Today));
        Assert.Equal((Today, Today.AddDays(91)), StaffShiftRules.Window(Today, Today.AddDays(91), Today));

        var backwards = Assert.Throws<FieldValidationException>(() => StaffShiftRules.Window(Today, Today.AddDays(-1), Today));
        Assert.Equal(["to:min"], Fields(backwards));

        var tooLong = Assert.Throws<FieldValidationException>(() => StaffShiftRules.Window(Today, Today.AddDays(92), Today));
        Assert.Equal(["to:max"], Fields(tooLong));
        Assert.Equal(StaffShiftRules.MaxWindowDays, tooLong.Violations[0].Max);
    }

    [Fact]
    public void The_entity_refuses_what_the_rules_refuse_and_derives_the_next_day_end()
    {
        var staff = Guid.CreateVersion7();
        var branch = Guid.CreateVersion7();

        var shift = new StaffShift(staff, branch, Today, T(16), T(0));

        Assert.True(shift.EndsNextDay);
        Assert.False(shift.IsDayOff);
        Assert.Equal(Today.AddDays(1).ToDateTime(T(0)), shift.LocalEnd);

        shift.Reschedule(Today, null, null);

        Assert.True(shift.IsDayOff);
        Assert.False(shift.EndsNextDay);

        Assert.Throws<ArgumentException>(() => new StaffShift(staff, branch, Today, T(10), null));
        Assert.Throws<ArgumentException>(() => new StaffShift(staff, branch, Today, T(10), T(10)));
        Assert.Throws<ArgumentException>(() => new StaffShift(staff, branch, Today, T(8), T(7)));
    }
}
