using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Staff;

/// <summary>
/// One person's shift at one branch on one date - or their day off on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Minimal on purpose.</b> A date and a from and to time, in the branch's own wall clock: the console's Staff
/// screen draws "today 16:00-00:00" from it and counts who is on shift today. No rota templates, no pay, no
/// clocking in - the floor has none of those and the console was asked for none.
/// </para>
/// <para>
/// <b>The date is the one it starts on.</b> A closing time at or before the starting time means after midnight, as
/// in the opening hours: 18:00-02:00 on Friday is Friday's shift and runs into Saturday. A day off carries no
/// times at all.
/// </para>
/// <para>
/// The rules between shifts - one person in one place at a time, a day off with nothing else on it - span
/// branches and people, so they are checked by the service under the person's lock, not here.
/// </para>
/// </remarks>
public sealed class StaffShift : Entity
{
    /// <summary>The longest shift, in minutes. Longer is a typing slip - 08:00-07:00 - not a working day.</summary>
    public const int MaxLengthMinutes = 16 * 60;

    private StaffShift()
    {
    }

    /// <param name="staffMemberId">Whose shift it is.</param>
    /// <param name="branchId">Where they work it.</param>
    /// <param name="date">The branch's local date it starts on.</param>
    /// <param name="startsAt">Wall clock; null with <paramref name="endsAt"/> for a day off.</param>
    /// <param name="endsAt">Wall clock; at or before <paramref name="startsAt"/> is after midnight.</param>
    public StaffShift(Guid staffMemberId, Guid branchId, DateOnly date, TimeOnly? startsAt, TimeOnly? endsAt)
        : base(Guid.CreateVersion7())
    {
        StaffMemberId = Guard.NotEmpty(staffMemberId, nameof(staffMemberId));
        BranchId = Guard.NotEmpty(branchId, nameof(branchId));
        Reschedule(date, startsAt, endsAt);
    }

    public Guid StaffMemberId { get; private set; }

    public StaffMember StaffMember { get; private set; } = null!;

    public Guid BranchId { get; private set; }

    public Branch Branch { get; private set; } = null!;

    /// <summary>The branch's local date the shift starts on.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>Wall clock. Null on a day off.</summary>
    public TimeOnly? StartsAt { get; private set; }

    /// <summary>Wall clock, on the next date when <see cref="EndsNextDay"/>. Null on a day off.</summary>
    public TimeOnly? EndsAt { get; private set; }

    /// <summary>Ends on the date after <see cref="Date"/> - including at exactly 00:00.</summary>
    public bool EndsNextDay { get; private set; }

    /// <summary>A day off on <see cref="Date"/>: no times.</summary>
    public bool IsDayOff => StartsAt is null;

    /// <summary>
    /// Moves the shift to another date or other times. Both times, or neither for a day off.
    /// </summary>
    /// <exception cref="ArgumentException">One time without the other, no length, or longer than <see cref="MaxLengthMinutes"/>.</exception>
    public void Reschedule(DateOnly date, TimeOnly? startsAt, TimeOnly? endsAt)
    {
        if (date == default)
        {
            throw new ArgumentException("A shift needs a date.", nameof(date));
        }

        if (startsAt is null != endsAt is null)
        {
            throw new ArgumentException("A shift has both a start and an end, or neither for a day off.", nameof(endsAt));
        }

        if (startsAt is { } start && endsAt is { } end)
        {
            var minutes = LengthMinutes(start, end);

            if (minutes == 0)
            {
                throw new ArgumentException("A shift must have a length.", nameof(endsAt));
            }

            if (minutes > MaxLengthMinutes)
            {
                throw new ArgumentException($"A shift is at most {MaxLengthMinutes / 60} hours.", nameof(endsAt));
            }
        }

        Date = date;
        StartsAt = startsAt;
        EndsAt = endsAt;
        EndsNextDay = startsAt is { } s && endsAt is { } e && e <= s;
    }

    /// <summary>The shift's local start, or null on a day off.</summary>
    public DateTime? LocalStart => StartsAt is { } start ? Date.ToDateTime(start) : null;

    /// <summary>The shift's local end, or null on a day off.</summary>
    public DateTime? LocalEnd => EndsAt is { } end ? (EndsNextDay ? Date.AddDays(1) : Date).ToDateTime(end) : null;

    /// <summary>
    /// Minutes from <paramref name="startsAt"/> to <paramref name="endsAt"/>, past midnight when the end is at or
    /// before the start. Zero only when they are equal.
    /// </summary>
    public static int LengthMinutes(TimeOnly startsAt, TimeOnly endsAt)
    {
        if (startsAt == endsAt)
        {
            return 0;
        }

        var minutes = (int)(endsAt.ToTimeSpan() - startsAt.ToTimeSpan()).TotalMinutes;

        return minutes > 0 ? minutes : minutes + (24 * 60);
    }
}
