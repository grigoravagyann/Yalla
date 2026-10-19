using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>Calendars for the pure rule tests, which are about the week and have no special days to read.</summary>
internal static class TestCalendars
{
    /// <summary>
    /// The weekly hours alone, for every date. A test convenience on purpose: in the product every calendar is read
    /// with its special days, for the dates it is asked about.
    /// </summary>
    public static OpeningCalendar Weekly(IEnumerable<OpeningHours> hours) =>
        new(
            hours.Select(h => (h.Day, new OpeningBlock(h.OpensAt, h.ClosesAt, h.ClosesNextDay))),
            [],
            DateOnly.MinValue,
            DateOnly.MaxValue);
}
