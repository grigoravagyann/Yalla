using Yalla.Domain;
using Yalla.Domain.Venues;

namespace Yalla.Application.BranchSettings;

// ------------------------------------------------------------------ special opening days

/// <summary>One special day as the console sends it.</summary>
/// <param name="Date">The branch's local date, <c>YYYY-MM-DD</c>. From today at the branch, at most a year and a day out.</param>
/// <param name="Note">What a diner is told - "New Year". Optional; trimmed, blank is none, at most 60 characters.</param>
/// <param name="Blocks">
/// The opening blocks that open on the date. <b>Empty is closed all day.</b> At most four; they may touch but
/// not overlap. A closing time at or before the opening time means after midnight, as in the weekly hours.
/// </param>
public sealed record SpecialOpeningDayInput(DateOnly Date, string? Note, IReadOnlyList<OpeningBlockInput> Blocks);

/// <summary>One opening block as the client sends it. <c>closesNextDay</c> is derived, not sent.</summary>
public sealed record OpeningBlockInput(TimeOnly OpensAt, TimeOnly ClosesAt);

/// <summary>One special day as every read returns it - the console's list, the public pages, the day's slots.</summary>
/// <param name="Date">The branch's local date.</param>
/// <param name="Note">What a diner is told; absent when there is none.</param>
/// <param name="Closed">Closed all day: <paramref name="Blocks"/> is empty.</param>
/// <param name="Blocks">The blocks that open on the date, earliest first. They replace the weekday's hours.</param>
public sealed record SpecialOpeningDayView(
    DateOnly Date,
    string? Note,
    bool Closed,
    IReadOnlyList<OpeningBlockView> Blocks)
{
    public static SpecialOpeningDayView From(SpecialDay day)
    {
        ArgumentNullException.ThrowIfNull(day);

        return new SpecialOpeningDayView(
            day.Date,
            day.Note,
            day.IsClosed,
            [.. day.Blocks.Select(b => new OpeningBlockView(b.OpensAt, b.ClosesAt, b.ClosesNextDay))]);
    }
}

/// <summary>One block of a special day.</summary>
/// <param name="OpensAt">Wall clock.</param>
/// <param name="ClosesAt">Wall clock; on the next date when <paramref name="ClosesNextDay"/>.</param>
/// <param name="ClosesNextDay">Closes after midnight - "(next day)".</param>
public sealed record OpeningBlockView(TimeOnly OpensAt, TimeOnly ClosesAt, bool ClosesNextDay);

/// <summary>What saving the special days did.</summary>
/// <param name="Days">The special days from today at the branch on, as saved, earliest first.</param>
/// <param name="AffectedExistingReservations">
/// How many live bookings - confirmed, or waiting for approval - fall on a saved special day and no longer fit its
/// hours. They were <b>not</b> changed or cancelled: a settings edit never touches a booking. Somebody has to call
/// those guests.
/// </param>
/// <param name="AffectedReservationIds">Which ones, earliest first.</param>
public sealed record SpecialOpeningDaysChangeResult(
    IReadOnlyList<SpecialOpeningDayView> Days,
    int AffectedExistingReservations,
    IReadOnlyList<Guid> AffectedReservationIds);

/// <summary>
/// The rules for a branch's list of special days, as a pure function.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list is everything from today on.</b> Days before today at the branch are history: last night's
/// special block may still be running past midnight, and the Overview's yesterday still reads it, so a save
/// never touches them and the list never names them. A date before today is refused rather than ignored, so a
/// console left open overnight does not quietly drop a day.
/// </para>
/// <para>
/// Every problem is reported, indexed into the bare array as the weekly hours are - <c>[1].date</c>,
/// <c>[0].blocks[1].opensAt</c> - with the bound it broke.
/// </para>
/// </remarks>
public static class SpecialOpeningDayRules
{
    /// <summary>The most special days one list may hold.</summary>
    public const int MaxDays = 100;

    /// <summary>How far ahead a special day may be set, in days from today at the branch.</summary>
    public const int HorizonDays = 366;

    /// <summary>How far ahead the public pages name special days, in days from today at the branch.</summary>
    public const int PublicLookaheadDays = 60;

    /// <exception cref="FieldValidationException">Anything in the list breaks a rule; every problem is named.</exception>
    public static IReadOnlyList<SpecialDay> Normalise(IReadOnlyList<SpecialOpeningDayInput> days, DateOnly localToday)
    {
        ArgumentNullException.ThrowIfNull(days);

        var violations = new List<FieldViolation>();
        var horizon = localToday.AddDays(HorizonDays);
        var seen = new HashSet<DateOnly>();
        var result = new List<SpecialDay>();

        if (days.Count > MaxDays)
        {
            violations.Add(new FieldViolation(
                $"[{MaxDays}]",
                $"At most {MaxDays} special days; this list has {days.Count}.",
                FieldBounds.Max,
                Max: MaxDays,
                Value: days.Count));
        }

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];

            if (day is null)
            {
                violations.Add(new FieldViolation($"[{i}]", "A special day is missing.", FieldBounds.Required));
                continue;
            }

            var dateOk = false;

            if (day.Date == default)
            {
                violations.Add(new FieldViolation($"[{i}].date", "A special day needs a date.", FieldBounds.Required));
            }
            else if (day.Date < localToday)
            {
                violations.Add(new FieldViolation(
                    $"[{i}].date",
                    $"{day.Date:yyyy-MM-dd} is before today at the branch ({localToday:yyyy-MM-dd}).",
                    FieldBounds.Min,
                    Min: localToday,
                    Value: day.Date));
            }
            else if (day.Date > horizon)
            {
                violations.Add(new FieldViolation(
                    $"[{i}].date",
                    $"{day.Date:yyyy-MM-dd} is more than {HorizonDays} days ahead.",
                    FieldBounds.Max,
                    Max: horizon,
                    Value: day.Date));
            }
            else if (!seen.Add(day.Date))
            {
                violations.Add(new FieldViolation(
                    $"[{i}].date",
                    $"{day.Date:yyyy-MM-dd} is in the list twice.",
                    FieldBounds.Conflict,
                    Value: day.Date));
            }
            else
            {
                dateOk = true;
            }

            var note = string.IsNullOrWhiteSpace(day.Note) ? null : day.Note.Trim();

            if (note is { Length: > SpecialOpeningDay.MaxNoteLength })
            {
                violations.Add(new FieldViolation(
                    $"[{i}].note",
                    $"A note is at most {SpecialOpeningDay.MaxNoteLength} characters.",
                    FieldBounds.Max,
                    Max: SpecialOpeningDay.MaxNoteLength,
                    Value: note.Length));
            }

            var blocks = Blocks(day.Blocks, i, violations);

            if (dateOk && blocks is not null && note is not { Length: > SpecialOpeningDay.MaxNoteLength })
            {
                result.Add(new SpecialDay(day.Date, note, blocks));
            }
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        return [.. result.OrderBy(d => d.Date)];
    }

    /// <summary>One day's blocks, derived and checked; null when any is refused.</summary>
    private static List<OpeningBlock>? Blocks(
        IReadOnlyList<OpeningBlockInput>? blocks,
        int dayIndex,
        List<FieldViolation> violations)
    {
        var field = $"[{dayIndex}].blocks";

        if (blocks is null)
        {
            violations.Add(new FieldViolation(
                field, "Send the day's opening blocks; an empty list means closed all day.", FieldBounds.Required));
            return null;
        }

        if (blocks.Count > SpecialOpeningDay.MaxBlocks)
        {
            violations.Add(new FieldViolation(
                field,
                $"A special day has at most {SpecialOpeningDay.MaxBlocks} opening blocks.",
                FieldBounds.Max,
                Max: SpecialOpeningDay.MaxBlocks,
                Value: blocks.Count));
            return null;
        }

        var before = violations.Count;
        var indexed = new List<(int Index, OpeningBlock Block)>();

        for (var j = 0; j < blocks.Count; j++)
        {
            var block = blocks[j];

            if (block is null)
            {
                violations.Add(new FieldViolation($"{field}[{j}]", "An opening block is missing.", FieldBounds.Required));
                continue;
            }

            if (block.OpensAt == block.ClosesAt)
            {
                violations.Add(new FieldViolation(
                    $"{field}[{j}].closesAt",
                    $"Opening and closing time are both {block.OpensAt:HH\\:mm}. A block must have a length.",
                    FieldBounds.Conflict,
                    Value: block.ClosesAt));
                continue;
            }

            indexed.Add((j, OpeningBlock.Between(block.OpensAt, block.ClosesAt)));
        }

        var ordered = indexed.OrderBy(b => b.Block.OpensAt).ToList();

        for (var k = 1; k < ordered.Count; k++)
        {
            var previous = ordered[k - 1].Block;
            var current = ordered[k];

            if (Minutes(current.Block.OpensAt) < ClosingMinutes(previous))
            {
                violations.Add(new FieldViolation(
                    $"{field}[{current.Index}].opensAt",
                    $"Opening blocks overlap: {previous.OpensAt:HH\\:mm}-{previous.ClosesAt:HH\\:mm} "
                    + $"and {current.Block.OpensAt:HH\\:mm}-{current.Block.ClosesAt:HH\\:mm}.",
                    FieldBounds.Conflict,
                    Min: previous.ClosesAt,
                    Value: current.Block.OpensAt));
            }
        }

        return violations.Count == before ? [.. ordered.Select(b => b.Block)] : null;
    }

    private static int Minutes(TimeOnly time) => (int)time.ToTimeSpan().TotalMinutes;

    private static int ClosingMinutes(OpeningBlock block) =>
        Minutes(block.ClosesAt) + (block.ClosesNextDay ? 24 * 60 : 0);
}
