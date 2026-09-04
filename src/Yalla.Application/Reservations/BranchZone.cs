using Yalla.Domain.Occupancy;

namespace Yalla.Application.Reservations;

/// <summary>
/// A branch's IANA time zone, and the only place local wall-clock and UTC are converted into each
/// other.
/// </summary>
/// <remarks>
/// <para>
/// The diner picks a local date and a local time. Everything is stored as a UTC instant plus the
/// wall-clock values they saw. Converting between the two is where booking systems go wrong, so it
/// happens here and nowhere else.
/// </para>
/// <para>
/// Two local times are not simply instants, and both are handled rather than left to throw:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Invalid</b> - the hour a spring-forward skips. There is no such instant, so the booking is
/// refused with <see cref="LocalTimeDoesNotExistException"/>, a named 422 the app can explain,
/// instead of an <see cref="ArgumentException"/> surfacing as a 500.
/// </item>
/// <item>
/// <b>Ambiguous</b> - the hour an autumn fall-back repeats. There are two such instants and the
/// diner meant one of them. This resolves to the <b>earlier</b> one, the first time the clock
/// reads 01:30, because that is the sitting a diner who booked before the change was thinking of.
/// Silence here would be worse than a wrong guess: .NET's default is the later instant, so a
/// venue would seat one party an hour after another party was promised the same table.
/// </item>
/// </list>
/// <para>
/// Armenia does not currently observe daylight saving, so neither case arises in Yerevan today.
/// The zone is a per-branch setting and the second customer may be somewhere that does, which is
/// why none of this leans on that fact.
/// </para>
/// </remarks>
public sealed class BranchZone
{
    private BranchZone(TimeZoneInfo zone, string timeZoneId)
    {
        Zone = zone;
        TimeZoneId = timeZoneId;
    }

    public TimeZoneInfo Zone { get; }

    /// <summary>The IANA identifier as configured on the branch, e.g. <c>Asia/Yerevan</c>.</summary>
    public string TimeZoneId { get; }

    /// <summary>
    /// Resolves a branch's configured zone against the host's time zone database.
    /// </summary>
    /// <remarks>
    /// Deliberately not in the domain: resolving an identifier is platform-dependent - it needs
    /// ICU on Windows to accept an IANA name at all - and the domain stays free of environment
    /// concerns. <c>Branch</c> validates the shape of the string; this resolves it.
    /// </remarks>
    /// <exception cref="BranchTimeZoneMisconfiguredException">
    /// The branch names a zone this host cannot resolve. That is our data being wrong, not the
    /// caller's request, so it is not a rejection the diner can act on.
    /// </exception>
    public static BranchZone For(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        try
        {
            return new BranchZone(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new BranchTimeZoneMisconfiguredException(timeZoneId, ex);
        }
    }

    /// <summary>The UTC instant a diner means by this local date and time at this branch.</summary>
    /// <exception cref="LocalTimeDoesNotExistException">The local time is skipped by a clock change.</exception>
    public DateTime ToUtc(DateOnly localDate, TimeOnly localTime)
    {
        var local = localDate.ToDateTime(localTime);

        if (Zone.IsInvalidTime(local))
        {
            throw new LocalTimeDoesNotExistException(localDate, localTime, TimeZoneId);
        }

        if (Zone.IsAmbiguousTime(local))
        {
            // The largest offset is the daylight one, and the largest offset gives the earliest
            // instant: local = utc + offset. That is the first time the clock reads this.
            var offset = Zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
    }

    /// <summary>Whether this local date and time exists at this branch at all.</summary>
    /// <remarks>
    /// The non-throwing form, for the availability read model: a table that cannot be offered
    /// because the clocks go forward is labelled, not an exception.
    /// </remarks>
    public bool TryToUtc(DateOnly localDate, TimeOnly localTime, out DateTime utc)
    {
        var local = localDate.ToDateTime(localTime);

        if (Zone.IsInvalidTime(local))
        {
            utc = default;
            return false;
        }

        utc = ToUtc(localDate, localTime);
        return true;
    }

    /// <summary>The branch's wall clock at a UTC instant.</summary>
    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>The branch's local calendar date at a UTC instant.</summary>
    public DateOnly LocalDateAt(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

    /// <summary>The branch's local wall-clock time at a UTC instant.</summary>
    public TimeOnly LocalTimeAt(DateTime utc) => TimeOnly.FromDateTime(ToLocal(utc));
}

/// <summary>
/// The branch names a time zone this host cannot resolve.
/// </summary>
/// <remarks>
/// Our configuration is wrong, not the request, so this is a 500 and a log entry - not a rejection
/// a diner could do anything about. It carries the identifier because the first question is always
/// which branch and which string.
/// </remarks>
public sealed class BranchTimeZoneMisconfiguredException(string timeZoneId, Exception innerException)
    : InvalidOperationException(
        $"The branch time zone '{timeZoneId}' could not be resolved on this host.", innerException)
{
    public string TimeZoneId { get; } = timeZoneId;
}
