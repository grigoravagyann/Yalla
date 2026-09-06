namespace Yalla.Domain.Venues;

/// <summary>
/// Turning a branch's wall clock into UTC and back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every stored instant is UTC and every date a person names is local.</b> "Yesterday's covers",
/// "we open at nine", "the report for Tuesday" are all statements in the venue's own clock, and a
/// report that takes UTC midnight as the day boundary is wrong by four hours in Yerevan - every
/// day, quietly, in the direction that moves the late sittings into the wrong day.
/// </para>
/// <para>
/// One place, because the alternative is each report doing its own conversion and one of them
/// getting the exclusive end wrong. A local day runs from its own midnight to the <i>next</i> day's
/// midnight, exclusive - see <see cref="RangeToUtc"/>, where the +1 day is the entire subtlety.
/// </para>
/// <para>
/// The IANA identifier is resolved against the host's time-zone database. .NET on Windows and on
/// Linux both accept IANA ids since .NET 6, which is why <c>Branch.TimeZoneId</c> stores one rather
/// than a Windows zone name.
/// </para>
/// </remarks>
public static class BranchTime
{
    /// <summary>The zone, or UTC when the identifier is not one this host knows.</summary>
    /// <remarks>
    /// Falling back rather than throwing: a branch configured with a bad zone should produce a
    /// report that is off by a few hours, not a 500 that hides the fact anything is wrong. The
    /// branch's zone is validated when it is set.
    /// </remarks>
    public static TimeZoneInfo Zone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>A UTC instant as the branch's wall clock reads it.</summary>
    public static DateTime ToLocal(DateTime utc, string timeZoneId) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone(timeZoneId));

    /// <summary>A local wall-clock instant as the UTC the database stores.</summary>
    public static DateTime ToUtc(DateTime local, string timeZoneId) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone(timeZoneId));

    /// <summary>
    /// A range of <b>local dates</b>, inclusive of both ends, as a half-open UTC interval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The end is the day <i>after</i> <paramref name="toLocalDate"/> at local midnight, so the
    /// whole of the last day is inside the range and the first instant of the next is not. A report
    /// for a local Tuesday therefore excludes a sitting that started at 01:30 on Wednesday, which is
    /// exactly the case a UTC-boundary report gets wrong.
    /// </para>
    /// <para>
    /// Half-open rather than inclusive-inclusive because there is no last instant of a day to be
    /// inclusive of: 23:59:59.999 leaves a millisecond of the evening unreported, and every attempt
    /// to name it precisely is a rounding argument nobody wins.
    /// </para>
    /// </remarks>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) RangeToUtc(
        DateOnly fromLocalDate,
        DateOnly toLocalDate,
        string timeZoneId) =>
        (
            ToUtc(fromLocalDate.ToDateTime(TimeOnly.MinValue), timeZoneId),
            ToUtc(toLocalDate.AddDays(1).ToDateTime(TimeOnly.MinValue), timeZoneId));
}
