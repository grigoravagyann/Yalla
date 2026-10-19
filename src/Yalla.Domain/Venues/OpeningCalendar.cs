namespace Yalla.Domain.Venues;

/// <summary>
/// A branch's opening hours on real dates: the weekly week, with its special days laid over it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one answer to "the hours on this date".</b> Every reader of opening hours - availability, the day's
/// slots, booking and changing a booking, the console's Overview, "open now" on every listing - asks this type,
/// so a holiday closes the branch everywhere at once. Before special days each reader read the weekly rows by
/// weekday itself; a reader that still did would offer New Year's Day to a diner the branch is shut for.
/// </para>
/// <para>
/// <b>A date's blocks are the ones that open on it.</b> A <see cref="SpecialOpeningDay"/> on the date replaces the
/// weekday's blocks; otherwise the weekday's apply. A block running past midnight belongs to the day it opened
/// on, so whether a moment is open looks at its own date and the one before.
/// </para>
/// <para>
/// <b>It knows which dates it was loaded for.</b> Special days are read for a window, not for all time, and a
/// date outside it would silently fall back to the weekly hours - the exact bug this type exists to prevent. So
/// asking about a date outside the window throws rather than guesses.
/// </para>
/// </remarks>
public sealed class OpeningCalendar
{
    private readonly ILookup<DayOfWeek, OpeningBlock> _weekly;
    private readonly Dictionary<DateOnly, SpecialDay> _special;
    private readonly DateOnly _from;
    private readonly DateOnly _to;

    /// <param name="weekly">Every weekly block, by the weekday it opens on.</param>
    /// <param name="specialDays">The special days the branch has between <paramref name="from"/> and <paramref name="to"/>.</param>
    /// <param name="from">The first date the special days were read for.</param>
    /// <param name="to">The last date the special days were read for.</param>
    public OpeningCalendar(
        IEnumerable<(DayOfWeek Day, OpeningBlock Block)> weekly,
        IEnumerable<SpecialDay> specialDays,
        DateOnly from,
        DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(weekly);
        ArgumentNullException.ThrowIfNull(specialDays);

        if (to < from)
        {
            throw new ArgumentException("The window ends before it starts.", nameof(to));
        }

        _weekly = weekly.OrderBy(w => w.Block.OpensAt).ToLookup(w => w.Day, w => w.Block);
        _special = specialDays.Where(d => d.Date >= from && d.Date <= to).ToDictionary(d => d.Date);
        _from = from;
        _to = to;
    }

    /// <summary>The weekly hours alone, for every date - a branch with no special days at all.</summary>
    public static OpeningCalendar Weekly(IEnumerable<OpeningHours> hours)
    {
        ArgumentNullException.ThrowIfNull(hours);

        return new OpeningCalendar(
            hours.Select(h => (h.Day, new OpeningBlock(h.OpensAt, h.ClosesAt, h.ClosesNextDay))),
            [],
            DateOnly.MinValue,
            DateOnly.MaxValue);
    }

    /// <summary>The special days in the window, earliest first.</summary>
    public IReadOnlyList<SpecialDay> SpecialDays => [.. _special.Values.OrderBy(d => d.Date)];

    /// <summary>The blocks that open on <paramref name="date"/>, earliest first: its special day's, else its weekday's.</summary>
    /// <exception cref="InvalidOperationException">The date is outside the window the special days were read for.</exception>
    public IReadOnlyList<OpeningBlock> BlocksOn(DateOnly date)
    {
        Covered(date);

        return _special.TryGetValue(date, out var special) ? special.Blocks : [.. _weekly[date.DayOfWeek]];
    }

    /// <summary>The special day on <paramref name="date"/>, or null when it keeps its weekday's hours.</summary>
    public SpecialDay? SpecialDayOn(DateOnly date)
    {
        Covered(date);

        return _special.GetValueOrDefault(date);
    }

    /// <summary>The blocks that open on <paramref name="date"/> as wall-clock intervals, earliest first.</summary>
    public IReadOnlyList<OpeningInterval> IntervalsOn(DateOnly date) =>
        [.. BlocksOn(date).Select(block => new OpeningInterval(
            date,
            block,
            date.ToDateTime(block.OpensAt),
            (block.ClosesNextDay ? date.AddDays(1) : date).ToDateTime(block.ClosesAt)))];

    /// <summary>
    /// Whether the whole of <paramref name="localStart"/>-<paramref name="localEnd"/> falls inside one block - of
    /// its own date, or of the date before when that one ran past midnight.
    /// </summary>
    public bool Fits(DateTime localStart, DateTime localEnd)
    {
        var date = DateOnly.FromDateTime(localStart);

        return new[] { date.AddDays(-1), date }
            .SelectMany(IntervalsOn)
            .Any(i => localStart >= i.Opens && localEnd <= i.Closes);
    }

    /// <summary>The block open at <paramref name="local"/>, today's before last night's, or null when shut.</summary>
    public OpeningInterval? OpenAt(DateTime local)
    {
        var date = DateOnly.FromDateTime(local);

        foreach (var interval in IntervalsOn(date).Concat(IntervalsOn(date.AddDays(-1))))
        {
            if (interval.Contains(local))
            {
                return interval;
            }
        }

        return null;
    }

    /// <summary>Whether the branch is inside an opening block at <paramref name="local"/>.</summary>
    public bool IsOpenAt(DateTime local) => OpenAt(local) is not null;

    /// <summary>
    /// The service day a moment belongs to: the date before while that date's block is still running past
    /// midnight, else its own date.
    /// </summary>
    public DateOnly ServiceDateOf(DateTime local)
    {
        var date = DateOnly.FromDateTime(local);
        var dayBefore = date.AddDays(-1);

        return IntervalsOn(dayBefore).Any(i => i.Block.RunsPastMidnight && i.Contains(local)) ? dayBefore : date;
    }

    private void Covered(DateOnly date)
    {
        if (date < _from || date > _to)
        {
            throw new InvalidOperationException(
                $"The special days were read for {_from:yyyy-MM-dd} to {_to:yyyy-MM-dd}, not {date:yyyy-MM-dd}; "
                + "read them for the dates this question needs.");
        }
    }
}

/// <summary>A special day as the calendar holds it.</summary>
/// <param name="Date">The branch's local date.</param>
/// <param name="Note">What a diner is told, or null.</param>
/// <param name="Blocks">The blocks opening on it, earliest first. Empty: closed all day.</param>
public sealed record SpecialDay(DateOnly Date, string? Note, IReadOnlyList<OpeningBlock> Blocks)
{
    public bool IsClosed => Blocks.Count == 0;
}

/// <summary>One block on one date, as wall-clock instants.</summary>
/// <param name="Date">The date it opens on.</param>
/// <param name="Block">The block.</param>
/// <param name="Opens">When it opens.</param>
/// <param name="Closes">When it closes - on the next date for a block past midnight.</param>
public readonly record struct OpeningInterval(DateOnly Date, OpeningBlock Block, DateTime Opens, DateTime Closes)
{
    /// <summary>Open at <paramref name="local"/>: from the opening, up to but not at the close.</summary>
    public bool Contains(DateTime local) => local >= Opens && local < Closes;
}
