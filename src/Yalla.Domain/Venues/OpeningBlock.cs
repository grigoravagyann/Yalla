namespace Yalla.Domain.Venues;

/// <summary>
/// One stretch of opening on a day: it opens on that day and closes on it, or after midnight on the next.
/// </summary>
/// <remarks>
/// The value a weekly <see cref="OpeningHours"/> row and a <see cref="SpecialOpeningDay"/> block have in common,
/// so <see cref="OpeningCalendar"/> can answer "the hours on this date" without caring which of the two it came
/// from. Wall-clock values, never UTC, for the reason <see cref="OpeningHours"/> gives.
/// </remarks>
public sealed record OpeningBlock
{
    public OpeningBlock(TimeOnly opensAt, TimeOnly closesAt, bool closesNextDay)
    {
        if (!closesNextDay && closesAt <= opensAt)
        {
            throw new ArgumentException(
                "Closing time must be after opening time unless the block closes after midnight.",
                nameof(closesAt));
        }

        if (closesNextDay && closesAt >= opensAt)
        {
            throw new ArgumentException(
                "A block closing after midnight must have a closing time earlier in the clock than its opening time.",
                nameof(closesAt));
        }

        OpensAt = opensAt;
        ClosesAt = closesAt;
        ClosesNextDay = closesNextDay;
    }

    public TimeOnly OpensAt { get; }

    public TimeOnly ClosesAt { get; }

    /// <summary>The block closes after midnight, on the next calendar day - "(next day)".</summary>
    public bool ClosesNextDay { get; }

    /// <summary>
    /// Whether any of the block falls on the next calendar day. A block closing at 00:00 is flagged
    /// <see cref="ClosesNextDay"/> but runs into nothing.
    /// </summary>
    public bool RunsPastMidnight => ClosesNextDay && ClosesAt != TimeOnly.MinValue;

    /// <summary>The block as the client sends it: a closing time at or before the opening one means after midnight.</summary>
    public static OpeningBlock Between(TimeOnly opensAt, TimeOnly closesAt) => new(opensAt, closesAt, closesAt <= opensAt);
}
