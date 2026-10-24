using System.Globalization;

namespace Yalla.UnitTests;

/// <summary>
/// "Now" for the test suite: the wall clock, unless <see cref="EnvironmentVariable"/> moves it.
/// </summary>
/// <remarks>
/// <para>
/// A test that starts from the real current instant passes or fails with the time of day it happens to run at - a
/// no-show seeded "an hour ago" lands on yesterday's service day at 05:30 in Yerevan, an ASAP takeaway order is
/// refused once the test branch's kitchen has stopped taking orders at 22:30. Those tests should pin their own
/// instant; this is how the suite proves it does, without waiting for 05:30 or touching the machine's clock.
/// </para>
/// <para>
/// Set <c>YALLA_TEST_NOW</c> and every reader of <see cref="UtcNow"/> - the hosted API's clock in
/// <c>YallaApiFactory</c>, the contexts and clocks the tests build - starts from that instant instead, and keeps
/// ticking from it in real time. Two forms:
/// </para>
/// <list type="bullet">
/// <item><c>HH:mm</c> - today at that time on the Yerevan wall clock, e.g. <c>YALLA_TEST_NOW=05:30</c>.</item>
/// <item>An ISO 8601 instant with its offset, e.g. <c>2026-12-31T23:59:00+04:00</c>.</item>
/// </list>
/// <para>
/// Unset - CI and every ordinary run - it is the wall clock exactly. Nothing in the shipped application reads it.
/// </para>
/// </remarks>
public static class TestNow
{
    /// <summary>The environment variable that moves the suite's "now".</summary>
    public const string EnvironmentVariable = "YALLA_TEST_NOW";

    private static readonly TimeZoneInfo Yerevan = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");

    /// <summary>How far the suite's "now" is from the wall clock: zero unless the variable is set.</summary>
    public static TimeSpan Offset { get; } = ReadOffset(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Whether the variable moved "now" away from the wall clock.</summary>
    public static bool IsShifted => Offset != TimeSpan.Zero;

    /// <summary>The suite's current instant, in UTC.</summary>
    public static DateTime UtcNow => DateTime.UtcNow + Offset;

    /// <summary>The suite's current instant, as an offset (for a <c>FakeTimeProvider</c>).</summary>
    public static DateTimeOffset UtcNowOffset => DateTimeOffset.UtcNow + Offset;

    /// <summary>
    /// <paramref name="time"/> on the Yerevan wall clock, on the Yerevan calendar date of <see cref="UtcNow"/>, as UTC.
    /// </summary>
    /// <remarks>
    /// For a test that relies on a time of day - inside the test branches' 10:00-23:00 hours, on today's service day -
    /// and wants a date near the real one. Every test branch keeps the same hours every day of the week, so the date
    /// moving from run to run changes nothing it asserts.
    /// </remarks>
    public static DateTime YerevanTodayAt(TimeOnly time)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, Yerevan));

        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(time), Yerevan), DateTimeKind.Utc);
    }

    private static TimeSpan ReadOffset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.Zero;
        }

        var wall = DateTime.UtcNow;

        if (TimeOnly.TryParseExact(raw.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(wall, Yerevan));
            var at = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(time), Yerevan);

            return at - wall;
        }

        if (DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
        {
            return instant.UtcDateTime - wall;
        }

        throw new InvalidOperationException(
            $"{EnvironmentVariable}='{raw}' is neither HH:mm (Yerevan, today) nor an ISO 8601 instant with an offset.");
    }
}
