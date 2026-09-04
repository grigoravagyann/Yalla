using Yalla.Domain.Common;

namespace Yalla.Domain.Venues;

/// <summary>
/// When a branch is open on one day of the week.
/// </summary>
/// <remarks>
/// These are genuinely wall-clock values and are stored as <see cref="TimeOnly"/>, not converted
/// to UTC. "We open at nine" stays nine o'clock across a daylight-saving change; converting it
/// once at configuration time would silently shift the branch's opening by an hour.
/// </remarks>
public sealed class OpeningHours : Entity
{
    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    public DayOfWeek Day { get; private set; }

    public TimeOnly OpensAt { get; private set; }

    public TimeOnly ClosesAt { get; private set; }

    /// <summary>
    /// True when closing time falls after midnight, e.g. 10:00-01:00. Without this flag a
    /// closing time earlier than the opening time is indistinguishable from bad data.
    /// </summary>
    public bool ClosesNextDay { get; private set; }

    private OpeningHours()
    {
    }

    public OpeningHours(Guid branchId, DayOfWeek day, TimeOnly opensAt, TimeOnly closesAt, bool closesNextDay)
        : base(Guid.CreateVersion7())
    {
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        Day = Guard.Defined(day, nameof(day));
        OpensAt = opensAt;
        ClosesAt = closesAt;
        ClosesNextDay = closesNextDay;

        if (!closesNextDay && closesAt <= opensAt)
        {
            throw new ArgumentException(
                "Closing time must be after opening time unless the branch closes after midnight.",
                nameof(closesAt));
        }

        if (closesNextDay && closesAt >= opensAt)
        {
            throw new ArgumentException(
                "A branch closing after midnight must have a closing time earlier in the clock than its opening time.",
                nameof(closesAt));
        }
    }

    /// <summary>Length of this opening block, spanning midnight where applicable.</summary>
    public TimeSpan Duration => ClosesNextDay
        ? TimeSpan.FromDays(1) - (OpensAt - ClosesAt)
        : ClosesAt - OpensAt;
}
