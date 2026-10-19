using Yalla.Domain;
using Yalla.Domain.Venues;

namespace Yalla.Application.BranchSettings;

// ------------------------------------------------------------------ special opening days

/// <summary>One special day as the console sends it.</summary>
/// <param name="Date">The branch's local date, <c>YYYY-MM-DD</c>. At most a year and a day out; an entry before today at the branch is history and is ignored.</param>
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

/// <summary>The branch's special days as the console edits them, with the revision it is editing.</summary>
/// <param name="Version">
/// The list's revision. Opaque: send it back as <c>expectedVersion</c> with the next save, which is refused with
/// <c>409 concurrent-update</c> if somebody saved in between.
/// </param>
/// <param name="Days">
/// The special days from the branch's service date on, earliest first: today's on - and, while last night's special block
/// is still running past midnight, yesterday's first. That one is read-only: a save replaces today on, so it is kept.
/// </param>
public sealed record SpecialOpeningDaysView(string Version, IReadOnlyList<SpecialOpeningDayView> Days);

/// <summary>The console's save of the special days: the whole list from today on, against the revision it loaded.</summary>
/// <param name="Days">
/// Every special day from today at the branch on - <c>[]</c> clears them. An entry dated before today is history
/// and is <b>ignored</b>, not refused: a console left open past midnight still saves the rest.
/// </param>
/// <param name="ExpectedVersion">
/// <b>Required.</b> The <c>version</c> the editor loaded. A list saved since then is refused with
/// <c>409 concurrent-update</c> and <c>context.currentVersion</c>, and nothing is written.
/// </param>
public sealed record ReplaceSpecialOpeningDaysCommand(
    IReadOnlyList<SpecialOpeningDayInput> Days,
    string? ExpectedVersion = null);

/// <summary>What saving the special days did.</summary>
/// <param name="Version">The list's revision now; the next save sends it as <c>expectedVersion</c>.</param>
/// <param name="Days">The special days as saved, from the service date on as <see cref="SpecialOpeningDaysView.Days"/>.</param>
/// <param name="AffectedExistingReservations">
/// How many live future bookings - confirmed, or waiting for approval - this save put outside the hours: on a date
/// whose special day was added, removed or given other blocks, or just after midnight following one, that fitted
/// the hours before the save and do not now. They were <b>not</b> changed or cancelled: a settings edit never
/// touches a booking. Somebody has to call those guests.
/// </param>
/// <param name="AffectedReservationIds">Which ones, earliest first.</param>
public sealed record SpecialOpeningDaysChangeResult(
    string Version,
    IReadOnlyList<SpecialOpeningDayView> Days,
    int AffectedExistingReservations,
    IReadOnlyList<Guid> AffectedReservationIds);

/// <summary>
/// The special days were saved by somebody else since this editor loaded them. Answers 409
/// <c>concurrent-update</c> with <c>context.currentVersion</c>; nothing is written.
/// </summary>
/// <remarks>
/// The list is replaced whole, so without this the second of two editors silently undoes the first one's holiday.
/// </remarks>
public sealed class SpecialOpeningDaysChangedException(string currentVersion)
    : Exception("The special days were changed since you loaded them. Reload them, then make your change again.")
{
    /// <summary>The revision now stored, for the editor to reload against.</summary>
    public string CurrentVersion { get; } = currentVersion;
}

/// <summary>
/// The rules for a branch's list of special days, as a pure function.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list is everything from today on.</b> Days before today at the branch are history: last night's
/// special block may still be running past midnight, and the Overview's yesterday still reads it, so a save
/// never touches them and the list never names them. An entry dated before today is <b>ignored</b> - a console
/// left open past midnight still holds yesterday's holiday, and refusing the whole list for it would stop the
/// owner saving tomorrow's.
/// </para>
/// <para>
/// Every problem is reported, indexed into <c>days</c> - <c>days[1].date</c>, <c>days[0].blocks[1].opensAt</c> -
/// with the bound it broke.
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

    /// <summary>The body's list, which every refused field is named under.</summary>
    private const string Prefix = "days";

    /// <exception cref="FieldValidationException">Anything in the list breaks a rule; every problem is named.</exception>
    public static IReadOnlyList<SpecialDay> Normalise(IReadOnlyList<SpecialOpeningDayInput> days, DateOnly localToday)
    {
        ArgumentNullException.ThrowIfNull(days);

        var violations = new List<FieldViolation>();
        var horizon = localToday.AddDays(HorizonDays);
        var seen = new HashSet<DateOnly>();
        var result = new List<SpecialDay>();

        // History, ignored whole: nothing about a past entry can refuse the list.
        var current = days.Count(d => d is null || d.Date == default || d.Date >= localToday);

        if (current > MaxDays)
        {
            violations.Add(new FieldViolation(
                $"{Prefix}[{MaxDays}]",
                $"At most {MaxDays} special days; this list has {current}.",
                FieldBounds.Max,
                Max: MaxDays,
                Value: current));
        }

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];

            if (day is null)
            {
                violations.Add(new FieldViolation($"{Prefix}[{i}]", "A special day is missing.", FieldBounds.Required));
                continue;
            }

            if (day.Date != default && day.Date < localToday)
            {
                continue;
            }

            var dateOk = false;

            if (day.Date == default)
            {
                violations.Add(new FieldViolation($"{Prefix}[{i}].date", "A special day needs a date.", FieldBounds.Required));
            }
            else if (day.Date > horizon)
            {
                violations.Add(new FieldViolation(
                    $"{Prefix}[{i}].date",
                    $"{day.Date:yyyy-MM-dd} is more than {HorizonDays} days ahead.",
                    FieldBounds.Max,
                    Max: horizon,
                    Value: day.Date));
            }
            else if (!seen.Add(day.Date))
            {
                violations.Add(new FieldViolation(
                    $"{Prefix}[{i}].date",
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
                    $"{Prefix}[{i}].note",
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
        var field = $"{Prefix}[{dayIndex}].blocks";

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
