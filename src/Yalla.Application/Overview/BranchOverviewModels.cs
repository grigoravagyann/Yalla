using Yalla.Domain.Enums;

namespace Yalla.Application.Overview;

/// <summary>
/// The venue console's Overview of one branch, today: what is booked, who is in, what is owed, what came in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Today is the branch's calendar day</b>, in its own time zone, at <see cref="AsOfUtc"/> - a booking's
/// <c>LocalDate</c> is that same calendar, so "today's bookings" is an equality, never a UTC range that
/// moves the late sittings into the wrong day.
/// </para>
/// <para>
/// <b>Aggregates where the screen shows aggregates, rows where it shows rows.</b> The KPI cards and the
/// load chart are counts and sums; today's bookings are the one list, because the screen lists them with
/// a menu on each. No guest phone number anywhere: the row menu acts on a booking, it calls nobody.
/// </para>
/// </remarks>
/// <param name="BranchId">The branch.</param>
/// <param name="TimeZoneId">The branch's IANA zone, which every local value here is in.</param>
/// <param name="AsOfUtc">The instant the numbers are true of - the screen's "updated at".</param>
/// <param name="LocalDate">The branch's today.</param>
/// <param name="LocalTime">The branch's wall clock at <see cref="AsOfUtc"/>.</param>
/// <param name="Hours">Whether the branch is open now, and until or from when.</param>
/// <param name="Bookings">Today's bookings, counted by status.</param>
/// <param name="InRoom">The parties sitting down now.</param>
/// <param name="OpenTabs">The bills still open, and what they still owe.</param>
/// <param name="Revenue">Today's takings against the same weekday a week ago, to the same time of day.</param>
/// <param name="Load">Booked people per hour today, against the room's seats.</param>
/// <param name="TodayBookings">Today's bookings one by one, earliest first.</param>
public sealed record BranchOverview(
    Guid BranchId,
    string TimeZoneId,
    DateTime AsOfUtc,
    DateOnly LocalDate,
    TimeOnly LocalTime,
    OverviewHours Hours,
    OverviewBookingCounts Bookings,
    OverviewRoom InRoom,
    OverviewOpenTabs OpenTabs,
    OverviewRevenue Revenue,
    OverviewLoad Load,
    IReadOnlyList<OverviewBooking> TodayBookings);

/// <summary>The header's "open until" line, from the weekly hours.</summary>
/// <param name="IsOpen">Inside an opening block now - today's, or yesterday's that runs past midnight.</param>
/// <param name="ClosesAt">While open: when the current block closes. Absent while shut.</param>
/// <param name="ClosesNextDay">While open: the close is after midnight - "(next day)".</param>
/// <param name="OpensAt">While shut: the next opening later today. Absent when open, or when nothing opens later today.</param>
public sealed record OverviewHours(bool IsOpen, TimeOnly? ClosesAt, bool ClosesNextDay, TimeOnly? OpensAt);

/// <summary>Today's bookings by status.</summary>
/// <param name="Total">Every booking still on the books for today: pending, confirmed, seated, completed and no-shows - not the cancelled or declined.</param>
/// <param name="PendingApproval">Waiting for the venue's answer (or the guest before's).</param>
/// <param name="Confirmed">Promised and not yet seated - the late ones included.</param>
/// <param name="Late">Of <see cref="Confirmed"/>, those whose start has passed.</param>
/// <param name="Seated">At their table now.</param>
/// <param name="Completed">Came, ate, left.</param>
/// <param name="NoShow">Marked as not having come.</param>
/// <param name="Cancelled">Called off by the diner or the venue, declined requests included.</param>
/// <param name="People">The party sizes of the pending, confirmed, seated and completed.</param>
public sealed record OverviewBookingCounts(
    int Total,
    int PendingApproval,
    int Confirmed,
    int Late,
    int Seated,
    int Completed,
    int NoShow,
    int Cancelled,
    int People);

/// <summary>The room now.</summary>
/// <param name="People">The party sizes of the sittings still open.</param>
/// <param name="OccupiedTables">Active tables somebody is sitting at.</param>
/// <param name="Tables">Active tables, whatever their state - out of service included.</param>
/// <param name="OutOfServiceTables">Active tables withdrawn from service.</param>
public sealed record OverviewRoom(int People, int OccupiedTables, int Tables, int OutOfServiceTables);

/// <summary>The bills not yet closed.</summary>
/// <param name="Count">Tabs open or settling.</param>
/// <param name="UnpaidAmd">What they still owe, in whole dram: the server's own <c>RemainingAmd</c>, summed.</param>
public sealed record OverviewOpenTabs(int Count, long UnpaidAmd);

/// <summary>
/// Today's takings, and the same weekday a week ago up to the same time of day - so 18:42 on a Saturday
/// is compared with 18:42 the Saturday before, not with that whole evening.
/// </summary>
/// <remarks>Counted as the revenue report counts: a closed tab's total, on the day it closed.</remarks>
/// <param name="TodayAmd">Closed today, from midnight at the branch until now.</param>
/// <param name="LastWeekAmd">Closed seven days ago, from midnight to the same wall-clock time.</param>
/// <param name="LastWeekDate">That day.</param>
/// <param name="ChangePercent">The change, to one decimal. Absent when last week took nothing.</param>
public sealed record OverviewRevenue(long TodayAmd, long LastWeekAmd, DateOnly LastWeekDate, double? ChangePercent);

/// <summary>The load chart: booked people per hour today, against the room.</summary>
/// <param name="Capacity">Seats at the active tables - the chart's "the room is full" line.</param>
/// <param name="Hours">
/// One entry per local hour, contiguous and earliest first: from the first opening today (or the first
/// booking, if earlier) to the last hour the branch is open (or the last booked hour, if later). Empty when
/// the branch is shut all day and nothing is booked.
/// </param>
public sealed record OverviewLoad(int Capacity, IReadOnlyList<OverviewLoadHour> Hours);

/// <summary>One bar of the load chart.</summary>
/// <param name="Hour">The local hour, 0 to 23.</param>
/// <param name="People">Confirmed, seated and completed parties whose sitting touches this hour.</param>
/// <param name="PendingPeople">Requests not yet answered whose sitting touches this hour.</param>
public sealed record OverviewLoadHour(int Hour, int People, int PendingPeople);

/// <summary>One of today's bookings, as the Overview's table lists it.</summary>
/// <param name="Id">The booking.</param>
/// <param name="Code">Its door code.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="EndUtc">When its sitting ends.</param>
/// <param name="LocalStartTime">The start on the branch's clock.</param>
/// <param name="PartySize">How many.</param>
/// <param name="GuestName">Whose.</param>
/// <param name="TableId">The table - what <c>seat-reservation</c> and <c>free</c> are addressed by.</param>
/// <param name="TableLabel">The table's label.</param>
/// <param name="TableStatus">The table's state now.</param>
/// <param name="RoomName">The room the table is in, when it is in one.</param>
/// <param name="Channel">Where the booking came from: the app, the web page, or staff.</param>
/// <param name="DeclinedBecause">Set only on a declined request - the screen leaves those out.</param>
/// <param name="HoldExpiresAtUtc">While the table is held for a late party: until when.</param>
/// <param name="LateNudgeAtUtc">
/// When the app asks the guest whether they are still coming - the start plus the branch's late-nudge
/// minutes. Absent for a guest with no app account, whom nothing asks.
/// </param>
public sealed record OverviewBooking(
    Guid Id,
    string Code,
    ReservationStatus Status,
    DateTime StartUtc,
    DateTime EndUtc,
    TimeOnly LocalStartTime,
    int PartySize,
    string GuestName,
    Guid TableId,
    string TableLabel,
    TableStatus TableStatus,
    string? RoomName,
    ReservationChannel Channel,
    DeclineReason? DeclinedBecause,
    DateTime? HoldExpiresAtUtc,
    DateTime? LateNudgeAtUtc);

/// <summary>Reads <see cref="BranchOverview"/>.</summary>
public interface IBranchOverviewQuery
{
    /// <summary>The branch's Overview, now.</summary>
    /// <exception cref="Domain.Staff.StaffPermissionException">
    /// Not an owner, general manager or platform admin covering the branch, nor its own branch manager.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    Task<BranchOverview> GetAsync(Guid branchId, CancellationToken cancellationToken = default);
}
