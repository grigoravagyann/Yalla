using Yalla.Domain.Enums;

namespace Yalla.Application.Overview;

/// <summary>
/// The venue console's Overview of one branch, today: what is booked, who is in, what is owed, what came in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Today is the branch's service day</b>, not the calendar's. It is the calendar day at the branch -
/// except while last night's opening block is still running past midnight, when it is still last night's:
/// at 01:00 on Sunday a bar open 20:00 to 02:00 is in Saturday's service. A booking belongs to the service
/// day it starts in: one at 00:30 on Sunday, inside Saturday's block, is Saturday's. Everything here is
/// that day's: the counts, the load chart, the list and the takings.
/// </para>
/// <para>
/// <b>Aggregates where the screen shows aggregates, rows where it shows rows.</b> The KPI cards and the
/// load chart are counts and sums; today's bookings are the one list, because the screen lists them with
/// a menu on each. No guest phone number anywhere: the row menu acts on a booking, it calls nobody.
/// </para>
/// <para>
/// <b>A booking whose change is waiting counts where it is still promised.</b> A confirmed 19:00 moved to
/// 21:00 at short notice waits for the floor's answer, and until then the diner still has their 19:00 - so
/// it counts, charts and lists at 19:00, at its old table, with <see cref="OverviewBooking.ChangeRequested"/>;
/// the 21:00 it asks for is in the approvals list and in the chart's pending bars.
/// </para>
/// </remarks>
/// <param name="BranchId">The branch.</param>
/// <param name="TimeZoneId">The branch's IANA zone, which every local value here is in.</param>
/// <param name="AsOfUtc">The instant the numbers are true of - the screen's "updated at".</param>
/// <param name="LocalDate">The calendar date at the branch now.</param>
/// <param name="LocalTime">The branch's wall clock at <see cref="AsOfUtc"/>, whole seconds.</param>
/// <param name="ServiceDate">
/// The service day everything here is about: <see cref="LocalDate"/>, or the day before while last night's
/// block is still open.
/// </param>
/// <param name="GraceMinutes">How long past its start the branch holds a late party's table (the policy's grace).</param>
/// <param name="Hours">Whether the branch is open now, and until or from when.</param>
/// <param name="Bookings">The service day's bookings, counted by status.</param>
/// <param name="InRoom">The parties sitting down now.</param>
/// <param name="OpenTabs">The bills still open, and what they still owe.</param>
/// <param name="Revenue">The service day's takings so far against the same point a week before.</param>
/// <param name="Load">Booked people per hour of the service day, against the room's seats.</param>
/// <param name="TodayBookings">
/// The service day's bookings one by one, earliest first, leaving out the cancelled and declined ones; at
/// most <c>200</c>.
/// </param>
/// <param name="TodayBookingsTruncated">More bookings than the list carries: the counts and the chart still have them all.</param>
public sealed record BranchOverview(
    Guid BranchId,
    string TimeZoneId,
    DateTime AsOfUtc,
    DateOnly LocalDate,
    TimeOnly LocalTime,
    DateOnly ServiceDate,
    int GraceMinutes,
    OverviewHours Hours,
    OverviewBookingCounts Bookings,
    OverviewRoom InRoom,
    OverviewOpenTabs OpenTabs,
    OverviewRevenue Revenue,
    OverviewLoad Load,
    IReadOnlyList<OverviewBooking> TodayBookings,
    bool TodayBookingsTruncated);

/// <summary>The header's "open until" line, from the weekly hours.</summary>
/// <param name="IsOpen">Inside an opening block now - today's, or last night's that runs past midnight.</param>
/// <param name="ClosesAt">While open: when the current block closes. Absent while shut.</param>
/// <param name="ClosesNextDay">
/// While open: the close falls after midnight tonight, so it reads "(next day)". False for a block closing
/// at 00:00 - "until 00:00" - and for last night's block, which closes today.
/// </param>
/// <param name="OpensAt">While shut: the next opening later today. Absent when open, or when nothing opens later today.</param>
/// <param name="SpecialDay">
/// The service date's special opening day - a holiday, a closure, different hours - when it has one; absent on an
/// ordinary day. Everything above already reads it.
/// </param>
public sealed record OverviewHours(
    bool IsOpen,
    TimeOnly? ClosesAt,
    bool ClosesNextDay,
    TimeOnly? OpensAt,
    BranchSettings.SpecialOpeningDayView? SpecialDay = null);

/// <summary>The service day's bookings by status.</summary>
/// <param name="Total">Every booking still on the books: pending, confirmed, seated, completed and no-shows - not the cancelled or declined.</param>
/// <param name="PendingApproval">Requests waiting for an answer, that have no confirmed slot today to fall back to.</param>
/// <param name="Confirmed">
/// Promised and not yet seated - the late ones included, and a confirmed booking whose change is waiting
/// (<see cref="PendingChanges"/>), counted at the slot it still holds.
/// </param>
/// <param name="Late">Of <see cref="Confirmed"/>, those whose start has passed.</param>
/// <param name="Seated">At their table now.</param>
/// <param name="Completed">Came, ate, left.</param>
/// <param name="NoShow">Marked as not having come.</param>
/// <param name="Cancelled">Called off by the diner or the venue, declined requests included.</param>
/// <param name="People">The party sizes of the pending, confirmed, seated and completed.</param>
/// <param name="PendingChanges">Of <see cref="Confirmed"/>, those with a change waiting for an answer.</param>
public sealed record OverviewBookingCounts(
    int Total,
    int PendingApproval,
    int Confirmed,
    int Late,
    int Seated,
    int Completed,
    int NoShow,
    int Cancelled,
    int People,
    int PendingChanges);

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
/// The service day's takings so far, and the same weekday's a week before up to the same point - so 18:42 on
/// a Saturday is compared with 18:42 the Saturday before, and 01:00 on Sunday with 01:00 the Sunday before.
/// </summary>
/// <remarks>A closed tab's total, counted at the moment it closed.</remarks>
/// <param name="TodayAmd">Closed from <see cref="SinceUtc"/> until now.</param>
/// <param name="LastWeekAmd">Closed from the same opening seven days before, for as long on the clock.</param>
/// <param name="LastWeekDate">The service day a week before.</param>
/// <param name="ChangePercent">The change, to one decimal. Absent when last week took nothing.</param>
/// <param name="SinceUtc">
/// Where the service day's takings start: its first opening (the first block on the service date), or local
/// midnight of the service date when it has no hours.
/// </param>
public sealed record OverviewRevenue(
    long TodayAmd,
    long LastWeekAmd,
    DateOnly LastWeekDate,
    double? ChangePercent,
    DateTime SinceUtc);

/// <summary>The load chart: booked people per hour of the service day, against the room.</summary>
/// <param name="Capacity">Seats at the active tables in service - the chart's "the room is full" line.</param>
/// <param name="Hours">
/// One entry per hour, contiguous and earliest first, numbered from midnight starting the service date:
/// 0 to 23 on that date, then 24 for 00:00-01:00 of the next, 25 for 01:00 and so on - label an hour
/// <c>hour % 24</c>. From the first opening (or the first booking, if earlier) to the last hour the branch
/// is open (or the last booked hour, if later). Empty when the branch is shut all day and nothing is booked.
/// </param>
public sealed record OverviewLoad(int Capacity, IReadOnlyList<OverviewLoadHour> Hours);

/// <summary>One bar of the load chart.</summary>
/// <param name="Hour">The hour from the service date's midnight: 0 to 23, then 24 onwards past midnight.</param>
/// <param name="People">Confirmed, seated and completed parties whose sitting touches this hour.</param>
/// <param name="PendingPeople">Requests not yet answered whose sitting touches this hour.</param>
public sealed record OverviewLoadHour(int Hour, int People, int PendingPeople);

/// <summary>One of the service day's bookings, as the Overview's table lists it.</summary>
/// <param name="Id">The booking.</param>
/// <param name="Code">Its door code.</param>
/// <param name="Status">Where it stands. A booking whose change is waiting is <c>PendingApproval</c> - see <paramref name="ChangeRequested"/>.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="EndUtc">When its sitting ends.</param>
/// <param name="LocalDate">The start's calendar date: the service date, or the next for a sitting after midnight.</param>
/// <param name="LocalStartTime">The start on the branch's clock.</param>
/// <param name="PartySize">How many.</param>
/// <param name="GuestName">Whose.</param>
/// <param name="TableId">The table - what <c>seat-reservation</c> and <c>free</c> are addressed by.</param>
/// <param name="TableLabel">The table's label.</param>
/// <param name="TableStatus">The table's state now.</param>
/// <param name="RoomName">The room the table is in, when it is in one.</param>
/// <param name="Channel">Where the booking came from: the app, the web page, or staff.</param>
/// <param name="ChangeRequested">
/// A confirmed booking whose change is waiting for an answer: the start, table and party here are the slot
/// still promised, and the change is in the approvals list. Its status is <c>PendingApproval</c>, so the
/// floor's actions on it wait for that answer.
/// </param>
/// <param name="HoldExpiresAtUtc">While the booking is confirmed and its table held for a late party: until when.</param>
/// <param name="LateNudgeAtUtc">
/// While the booking is confirmed: when the app asked, or will ask, the guest whether they are still coming -
/// the nudge as it stands in the outbox, its send time once sent. Absent when none is scheduled.
/// </param>
/// <param name="LateNudgeSent">With <paramref name="LateNudgeAtUtc"/>: whether it has gone out.</param>
/// <param name="GuestRecord">
/// The guest's record over the last twelve months at every venue; absent when no verified account is behind the
/// booking. See <see cref="Reservations.GuestRecord"/>.
/// </param>
public sealed record OverviewBooking(
    Guid Id,
    string Code,
    ReservationStatus Status,
    DateTime StartUtc,
    DateTime EndUtc,
    DateOnly LocalDate,
    TimeOnly LocalStartTime,
    int PartySize,
    string GuestName,
    Guid TableId,
    string TableLabel,
    TableStatus TableStatus,
    string? RoomName,
    ReservationChannel Channel,
    bool ChangeRequested,
    DateTime? HoldExpiresAtUtc,
    DateTime? LateNudgeAtUtc,
    bool? LateNudgeSent,
    Reservations.GuestRecord? GuestRecord = null);

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
