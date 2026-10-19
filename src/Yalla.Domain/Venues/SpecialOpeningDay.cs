using Yalla.Domain.Common;

namespace Yalla.Domain.Venues;

/// <summary>
/// One date on which a branch keeps different hours from its week: a holiday, a closure, a late New Year's Eve.
/// </summary>
/// <remarks>
/// <para>
/// <b>It replaces the date's weekly blocks entirely.</b> No blocks at all is closed all day. A date's blocks are
/// the ones that <i>open</i> on it, as in the week: 31 December 18:00-03:00 runs into 1 January even when
/// 1 January is itself a closed special day, and a Friday's weekly block running to 01:00 still runs into a
/// Saturday that is a special day. <see cref="OpeningCalendar"/> is the one reader of that rule.
/// </para>
/// <para>
/// One row per branch and date (a unique index). The note is what a diner is told - "New Year" - and is
/// optional.
/// </para>
/// </remarks>
public sealed class SpecialOpeningDay : Entity
{
    /// <summary>The longest note, after trimming.</summary>
    public const int MaxNoteLength = 60;

    /// <summary>The most opening blocks one special day may have.</summary>
    public const int MaxBlocks = 4;

    private readonly List<SpecialOpeningBlock> _blocks = [];

    private SpecialOpeningDay()
    {
    }

    public SpecialOpeningDay(Guid branchId, DateOnly date, string? note, IEnumerable<OpeningBlock> blocks)
        : base(Guid.CreateVersion7())
    {
        ArgumentNullException.ThrowIfNull(blocks);

        BranchId = Guard.NotEmpty(branchId, nameof(branchId));

        if (date == default)
        {
            throw new ArgumentException("A special day needs a date.", nameof(date));
        }

        Date = date;

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        if (trimmed is { Length: > MaxNoteLength })
        {
            throw new ArgumentException($"A special day's note is at most {MaxNoteLength} characters.", nameof(note));
        }

        Note = trimmed;

        foreach (var block in blocks.OrderBy(b => b.OpensAt))
        {
            _blocks.Add(new SpecialOpeningBlock(Id, block));
        }

        if (_blocks.Count > MaxBlocks)
        {
            throw new ArgumentException($"A special day has at most {MaxBlocks} opening blocks.", nameof(blocks));
        }
    }

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    /// <summary>The branch's local calendar date.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>What a diner is told, e.g. "New Year". Null when there is none.</summary>
    public string? Note { get; private set; }

    /// <summary>The blocks that open on this date, earliest first. Empty: closed all day.</summary>
    public IReadOnlyCollection<SpecialOpeningBlock> Blocks => _blocks;
}

/// <summary>One opening block of a <see cref="SpecialOpeningDay"/>.</summary>
public sealed class SpecialOpeningBlock : Entity
{
    private SpecialOpeningBlock()
    {
    }

    public SpecialOpeningBlock(Guid specialOpeningDayId, OpeningBlock block)
        : base(Guid.CreateVersion7())
    {
        ArgumentNullException.ThrowIfNull(block);

        SpecialOpeningDayId = Guard.NotEmpty(specialOpeningDayId, nameof(specialOpeningDayId));
        OpensAt = block.OpensAt;
        ClosesAt = block.ClosesAt;
        ClosesNextDay = block.ClosesNextDay;
    }

    public Guid SpecialOpeningDayId { get; private set; }

    public TimeOnly OpensAt { get; private set; }

    public TimeOnly ClosesAt { get; private set; }

    public bool ClosesNextDay { get; private set; }

    public OpeningBlock ToBlock() => new(OpensAt, ClosesAt, ClosesNextDay);
}
