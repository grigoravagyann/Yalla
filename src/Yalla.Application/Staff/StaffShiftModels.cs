using Yalla.Domain;
using Yalla.Domain.Staff;

namespace Yalla.Application.Staff;

// ------------------------------------------------------------------ shifts

/// <summary>A new shift, as the console's shift editor sends it.</summary>
/// <param name="StaffMemberId">Whose shift. They must work at the branch - its own people, or an owner or general manager.</param>
/// <param name="Date">The branch's local date the shift starts on.</param>
/// <param name="StartsAt">Wall clock. Required unless <paramref name="DayOff"/>.</param>
/// <param name="EndsAt">Wall clock; at or before <paramref name="StartsAt"/> means after midnight. Required unless <paramref name="DayOff"/>.</param>
/// <param name="DayOff">A day off on the date: no times.</param>
public sealed record CreateShiftCommand(
    Guid StaffMemberId,
    DateOnly? Date,
    TimeOnly? StartsAt,
    TimeOnly? EndsAt,
    bool DayOff = false);

/// <summary>A shift moved or retimed: the whole shift again. The person does not change.</summary>
public sealed record UpdateShiftCommand(
    DateOnly? Date,
    TimeOnly? StartsAt,
    TimeOnly? EndsAt,
    bool DayOff = false);

/// <summary>One shift as every read returns it.</summary>
/// <param name="Id">The shift.</param>
/// <param name="StaffMemberId">Whose.</param>
/// <param name="BranchId">Where.</param>
/// <param name="Date">The branch's local date it starts on.</param>
/// <param name="DayOff">A day off on the date; no times then.</param>
/// <param name="StartsAt">Wall clock; absent on a day off.</param>
/// <param name="EndsAt">Wall clock, on the next date when <paramref name="EndsNextDay"/>; absent on a day off.</param>
/// <param name="EndsNextDay">Ends after midnight - including at exactly 00:00. False on a day off.</param>
/// <param name="StartsAtUtc">The start as an instant; absent on a day off.</param>
/// <param name="EndsAtUtc">The end as an instant; absent on a day off.</param>
public sealed record StaffShiftView(
    Guid Id,
    Guid StaffMemberId,
    Guid BranchId,
    DateOnly Date,
    bool DayOff,
    TimeOnly? StartsAt,
    TimeOnly? EndsAt,
    bool EndsNextDay,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc);

/// <summary>A branch's shifts over a window, with what the Staff screen's header counts.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="ServiceDate">
/// "Today" at the branch: its own date, or yesterday while last night's opening block still runs past midnight - the
/// Overview's service day. The shift line reads the shifts dated this.
/// </param>
/// <param name="From">First date answered.</param>
/// <param name="To">Last date answered.</param>
/// <param name="OnShiftToday">
/// "On shift today: N people" - the active people with a working shift (not a day off) at this branch dated
/// <paramref name="ServiceDate"/>.
/// </param>
/// <param name="Shifts">
/// This branch's shifts dated <paramref name="From"/> to <paramref name="To"/>, active people only, by date, then
/// start (days off first), then person.
/// </param>
public sealed record BranchShiftsView(
    Guid BranchId,
    DateOnly ServiceDate,
    DateOnly From,
    DateOnly To,
    int OnShiftToday,
    IReadOnlyList<StaffShiftView> Shifts);

/// <summary>
/// The shifts on a branch's Staff screen - the minimal model: one person, one branch, one date, a from and a to
/// time or a day off.
/// </summary>
/// <remarks>
/// Read by anybody who may open the branch's console. Written by whoever may manage the person - the staff edit's
/// rank check: owner, general manager, branch manager, waiter and kitchen in that order, a branch manager at their
/// own branch only - or by the person themselves.
/// </remarks>
public interface IStaffShiftService
{
    /// <param name="branchId">The branch.</param>
    /// <param name="from">First date; the service date when absent.</param>
    /// <param name="to">Last date; two weeks from <paramref name="from"/> when absent. At most <see cref="StaffShiftRules.MaxWindowDays"/> days.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="FieldValidationException">The window ends before it starts, or is too long.</exception>
    Task<BranchShiftsView> ListAsync(
        Guid branchId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default);

    /// <exception cref="FieldValidationException">A field breaks a rule, or the shift clashes with another of the person's.</exception>
    /// <exception cref="StaffPermissionException">The caller may not manage this person.</exception>
    /// <exception cref="KeyNotFoundException">The person does not work at this branch.</exception>
    /// <exception cref="DomainStateException">The person is deactivated.</exception>
    Task<StaffShiftView> CreateAsync(
        Guid branchId, CreateShiftCommand command, CancellationToken cancellationToken = default);

    /// <exception cref="FieldValidationException">A field breaks a rule, or the shift clashes with another of the person's.</exception>
    /// <exception cref="StaffPermissionException">The caller may not manage this person.</exception>
    /// <exception cref="KeyNotFoundException">No such shift at this branch.</exception>
    /// <exception cref="DomainStateException">The person is deactivated.</exception>
    Task<StaffShiftView> UpdateAsync(
        Guid branchId, Guid shiftId, UpdateShiftCommand command, CancellationToken cancellationToken = default);

    /// <exception cref="StaffPermissionException">The caller may not manage this person.</exception>
    /// <exception cref="KeyNotFoundException">No such shift at this branch.</exception>
    Task DeleteAsync(Guid branchId, Guid shiftId, CancellationToken cancellationToken = default);
}

/// <summary>One shift's date and times, checked: both times, or neither for a day off.</summary>
/// <param name="Date">The local date it starts on.</param>
/// <param name="StartsAt">Null on a day off.</param>
/// <param name="EndsAt">Null on a day off; at or before <paramref name="StartsAt"/> is after midnight.</param>
public sealed record ShiftSlot(DateOnly Date, TimeOnly? StartsAt, TimeOnly? EndsAt)
{
    public bool IsDayOff => StartsAt is null;

    /// <summary>
    /// The start as an instant, in its branch's zone; null on a day off or when not read in a zone. Two shifts that both
    /// carry instants are compared by them, so shifts at branches in two zones clash by time, not by wall clock.
    /// </summary>
    public DateTime? StartUtc { get; init; }

    /// <summary>The end as an instant; see <see cref="StartUtc"/>.</summary>
    public DateTime? EndUtc { get; init; }

    /// <summary>Local start; null on a day off.</summary>
    public DateTime? LocalStart => StartsAt is { } start ? Date.ToDateTime(start) : null;

    /// <summary>Local end, the next date when it is at or before the start; null on a day off.</summary>
    public DateTime? LocalEnd => StartsAt is { } start && EndsAt is { } end
        ? (end <= start ? Date.AddDays(1) : Date).ToDateTime(end)
        : null;

    public override string ToString() =>
        IsDayOff ? $"{Date:yyyy-MM-dd} day off" : $"{Date:yyyy-MM-dd} {StartsAt:HH\\:mm}-{EndsAt:HH\\:mm}";
}

/// <summary>
/// The rules for one shift and between one person's shifts, as pure functions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Past dates are history.</b> A shift is planned from the branch's service date on and at most
/// <see cref="HorizonDays"/> ahead.
/// </para>
/// <para>
/// <b>One person, one place at a time.</b> Two of a person's shifts may touch - 10:00-16:00 and 16:00-22:00 - but not
/// overlap, at this branch or another of the venue's - compared as instants when the slots carry them, so branches in
/// two zones clash by time - and a day off has nothing else on its date. Every problem is
/// named by its field with the bound it broke, as the other console forms are.
/// </para>
/// </remarks>
public static class StaffShiftRules
{
    /// <summary>How far ahead a shift may be planned, in days from the service date.</summary>
    public const int HorizonDays = 92;

    /// <summary>The longest window one read answers, in days.</summary>
    public const int MaxWindowDays = 92;

    /// <summary>How many days a read covers when it names no end: two weeks.</summary>
    public const int DefaultWindowDays = 14;

    /// <summary>Checks one shift's own fields. Times are whole minutes: seconds are dropped.</summary>
    /// <param name="date">The local date it starts on.</param>
    /// <param name="startsAt">Wall clock; required unless <paramref name="dayOff"/>.</param>
    /// <param name="endsAt">Wall clock; required unless <paramref name="dayOff"/>.</param>
    /// <param name="dayOff">A day off: no times.</param>
    /// <param name="serviceDate">Today at the branch. The horizon counts from it.</param>
    /// <param name="earliest">
    /// The earliest date allowed when it is before <paramref name="serviceDate"/>: a shift from last night still running,
    /// which may be corrected on its own date. The service date otherwise.
    /// </param>
    /// <exception cref="FieldValidationException">Every problem, named.</exception>
    public static ShiftSlot Normalise(
        DateOnly? date, TimeOnly? startsAt, TimeOnly? endsAt, bool dayOff, DateOnly serviceDate, DateOnly? earliest = null)
    {
        var violations = new List<FieldViolation>();
        var horizon = serviceDate.AddDays(HorizonDays);
        var minimum = earliest is { } allowed && allowed < serviceDate ? allowed : serviceDate;

        // Whole minutes, so 10:00:30-10:00:45 is no length rather than fifteen seconds, and 16 hours means 960 minutes.
        startsAt = startsAt is { } rawStart ? new TimeOnly(rawStart.Hour, rawStart.Minute) : null;
        endsAt = endsAt is { } rawEnd ? new TimeOnly(rawEnd.Hour, rawEnd.Minute) : null;

        if (date is not { } day || day == default)
        {
            violations.Add(new FieldViolation("date", "A shift needs a date.", FieldBounds.Required));
        }
        else if (day < minimum)
        {
            violations.Add(new FieldViolation(
                "date",
                $"{day:yyyy-MM-dd} has passed at the branch; shifts are planned from {minimum:yyyy-MM-dd} on.",
                FieldBounds.Min,
                Min: minimum,
                Value: day));
        }
        else if (day > horizon)
        {
            violations.Add(new FieldViolation(
                "date",
                $"{day:yyyy-MM-dd} is more than {HorizonDays} days ahead.",
                FieldBounds.Max,
                Max: horizon,
                Value: day));
        }

        if (dayOff)
        {
            if (startsAt is not null || endsAt is not null)
            {
                violations.Add(new FieldViolation(
                    startsAt is not null ? "startsAt" : "endsAt",
                    "A day off has no times. Send neither, or make it a working shift.",
                    FieldBounds.Conflict));
            }
        }
        else
        {
            if (startsAt is null)
            {
                violations.Add(new FieldViolation("startsAt", "A shift needs a start time.", FieldBounds.Required));
            }

            if (endsAt is null)
            {
                violations.Add(new FieldViolation("endsAt", "A shift needs an end time.", FieldBounds.Required));
            }

            if (startsAt is { } start && endsAt is { } end)
            {
                var minutes = StaffShift.LengthMinutes(start, end);

                if (minutes == 0)
                {
                    violations.Add(new FieldViolation(
                        "endsAt",
                        $"The shift starts and ends at {start:HH\\:mm}. A shift must have a length.",
                        FieldBounds.Conflict,
                        Value: end));
                }
                else if (minutes > StaffShift.MaxLengthMinutes)
                {
                    violations.Add(new FieldViolation(
                        "endsAt",
                        $"{start:HH\\:mm}-{end:HH\\:mm} is {minutes / 60} h {minutes % 60} min; a shift is at most "
                        + $"{StaffShift.MaxLengthMinutes / 60} hours.",
                        FieldBounds.Max,
                        Max: StaffShift.MaxLengthMinutes,
                        Value: minutes));
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        return new ShiftSlot(date!.Value, dayOff ? null : startsAt, dayOff ? null : endsAt);
    }

    /// <summary>
    /// Refuses <paramref name="candidate"/> when it clashes with one of the same person's <paramref name="others"/>:
    /// an overlapping shift, or a day off meeting anything on its date.
    /// </summary>
    /// <param name="candidate">The shift being saved.</param>
    /// <param name="others">The person's other shifts around its date, at every branch; never the shift itself.</param>
    /// <exception cref="FieldValidationException">The first clash, named.</exception>
    public static void RequireNoClash(ShiftSlot candidate, IEnumerable<ShiftSlot> others)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(others);

        foreach (var other in others.OrderBy(o => o.Date).ThenBy(o => o.StartsAt))
        {
            if (candidate.IsDayOff || other.IsDayOff)
            {
                if (other.Date == candidate.Date)
                {
                    throw new FieldValidationException(new FieldViolation(
                        "date",
                        candidate.IsDayOff
                            ? $"{candidate.Date:yyyy-MM-dd} already has {Describe(other)} for this person; a day off has nothing else on it."
                            : $"{candidate.Date:yyyy-MM-dd} is this person's day off.",
                        FieldBounds.Conflict,
                        Value: candidate.Date));
                }

                continue;
            }

            // As instants when both carry them - two branches, two zones - and on the wall clock otherwise.
            var (start, end, otherStart, otherEnd) =
                candidate.StartUtc is not null && candidate.EndUtc is not null
                && other.StartUtc is not null && other.EndUtc is not null
                    ? (candidate.StartUtc, candidate.EndUtc, other.StartUtc, other.EndUtc)
                    : (candidate.LocalStart, candidate.LocalEnd, other.LocalStart, other.LocalEnd);

            if (start < otherEnd && otherStart < end)
            {
                throw new FieldValidationException(new FieldViolation(
                    "startsAt",
                    $"Overlaps this person's shift {Describe(other)}. One person works one shift at a time.",
                    FieldBounds.Conflict,
                    Value: candidate.StartsAt));
            }
        }
    }

    /// <summary>The window a read answers, checked.</summary>
    /// <exception cref="FieldValidationException">It ends before it starts, or is longer than <see cref="MaxWindowDays"/>.</exception>
    public static (DateOnly From, DateOnly To) Window(DateOnly? from, DateOnly? to, DateOnly serviceDate)
    {
        var first = from ?? serviceDate;
        var last = to ?? first.AddDays(DefaultWindowDays - 1);

        if (last < first)
        {
            throw new FieldValidationException(new FieldViolation(
                "to", "The window ends before it starts.", FieldBounds.Min, Min: first, Value: last));
        }

        var days = last.DayNumber - first.DayNumber + 1;

        return days > MaxWindowDays
            ? throw new FieldValidationException(new FieldViolation(
                "to",
                $"A read covers at most {MaxWindowDays} days; this one is {days}.",
                FieldBounds.Max,
                Max: MaxWindowDays,
                Value: days))
            : (first, last);
    }

    private static string Describe(ShiftSlot slot) =>
        slot.IsDayOff ? $"a day off on {slot.Date:yyyy-MM-dd}" : $"on {slot.Date:yyyy-MM-dd} {slot.StartsAt:HH\\:mm}-{slot.EndsAt:HH\\:mm}";
}
