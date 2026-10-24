using Yalla.Domain.Enums;

namespace Yalla.Application.Staff;

/// <summary>
/// The manager's "Today" on the staff phone: one branch's day so far in one read - takings, guests, bookings, the
/// room, what needs them, and who is on shift.
/// </summary>
/// <remarks>
/// <para>
/// <b>The console's Overview, summed up for a phone.</b> The counts, the room and the hall's takings are the Overview's
/// own (<c>GET /api/branches/{branchId}/overview</c>, same service day, same rules); what this adds is what the phone's
/// board shows and the Overview does not: takeaway takings, guests with and without a booking, the approvals and
/// messages waiting, and the shift.
/// </para>
/// <para>
/// <b>Which branches.</b> A branch manager reads their own branch; an owner or general manager any branch of their venue
/// (<see cref="Branches"/> is the switcher) - on their own phone, and in the console. On a shared tablet a manager's
/// PIN session reads the tablet's branch only, as every PIN session on a tablet is confined to it.
/// </para>
/// </remarks>
/// <param name="BranchId">The branch.</param>
/// <param name="BranchName">Its name.</param>
/// <param name="VenueName">Its venue.</param>
/// <param name="TimeZoneId">The zone every local value is in.</param>
/// <param name="AsOfUtc">The instant it is true of.</param>
/// <param name="ServiceDate">The service day, as the Overview has it: today, or yesterday while last night's block runs.</param>
/// <param name="LocalTime">The branch's clock now.</param>
/// <param name="Branches">The branches the caller may switch to here, by name; this one among them.</param>
/// <param name="Revenue">Takings so far.</param>
/// <param name="Guests">People seated so far.</param>
/// <param name="Bookings">The service day's bookings.</param>
/// <param name="Room">The room now.</param>
/// <param name="NeedsYou">What waits for somebody.</param>
/// <param name="Shifts">Who works today: the service date's shifts at the branch, and last night's still running.</param>
public sealed record StaffToday(
    Guid BranchId,
    string BranchName,
    string VenueName,
    string TimeZoneId,
    DateTime AsOfUtc,
    DateOnly ServiceDate,
    TimeOnly LocalTime,
    IReadOnlyList<TodayBranch> Branches,
    TodayRevenue Revenue,
    TodayGuests Guests,
    TodayBookings Bookings,
    TodayRoom Room,
    TodayNeedsYou NeedsYou,
    IReadOnlyList<TodayShift> Shifts);

/// <summary>A branch the switcher offers.</summary>
public sealed record TodayBranch(Guid BranchId, string Name);

/// <summary>The service day's takings so far, in whole dram.</summary>
/// <param name="TotalAmd">Hall and takeaway together.</param>
/// <param name="HallAmd">Tabs closed since the day's first opening - the Overview's revenue.</param>
/// <param name="TakeawayAmd">Takeaway orders collected since then.</param>
/// <param name="TakeawayOrders">How many were collected.</param>
/// <param name="SinceUtc">Where the day's takings start: the first opening of the service date, or its midnight.</param>
public sealed record TodayRevenue(long TotalAmd, long HallAmd, long TakeawayAmd, int TakeawayOrders, DateTime SinceUtc);

/// <summary>People seated since the day's takings started.</summary>
/// <param name="Total">Every party's size, added up.</param>
/// <param name="WithBooking">Seated against a booking.</param>
/// <param name="WalkIn">Seated with none.</param>
public sealed record TodayGuests(int Total, int WithBooking, int WalkIn);

/// <summary>The service day's bookings, by what became of them - the Overview's counts.</summary>
/// <param name="Total">Every booking still on the books (not the cancelled or declined).</param>
/// <param name="Came">Seated now, or came and left.</param>
/// <param name="Expected">Confirmed and not yet seated, the late ones included.</param>
/// <param name="Late">Of <paramref name="Expected"/>, those past their start.</param>
/// <param name="NoShow">Marked as not having come.</param>
/// <param name="PendingApproval">Requests still waiting for an answer.</param>
public sealed record TodayBookings(int Total, int Came, int Expected, int Late, int NoShow, int PendingApproval);

/// <summary>The room now.</summary>
/// <param name="OccupiedTables">Active tables somebody is sitting at.</param>
/// <param name="Tables">Active tables, out of service included.</param>
/// <param name="People">People sitting down now.</param>
public sealed record TodayRoom(int OccupiedTables, int Tables, int People);

/// <summary>What waits for somebody - the board's "needs you".</summary>
/// <param name="PendingApprovals">Bookings the floor can answer now - the awaiting list's rule, any day.</param>
/// <param name="OldestApprovalRequestedAtUtc">When the longest-waiting of them was asked for; absent with none.</param>
/// <param name="SoonestApprovalDeadlineUtc">The nearest answer deadline among them; absent when none has one.</param>
/// <param name="UnansweredMessages">Open threads whose guest wrote and nobody at the venue answered since.</param>
/// <param name="OldestUnansweredMessageAtUtc">When the longest-waiting of those guests wrote; absent with none.</param>
public sealed record TodayNeedsYou(
    int PendingApprovals,
    DateTime? OldestApprovalRequestedAtUtc,
    DateTime? SoonestApprovalDeadlineUtc,
    int UnansweredMessages,
    DateTime? OldestUnansweredMessageAtUtc);

/// <summary>One person's shift today at the branch.</summary>
/// <param name="StaffMemberId">Who.</param>
/// <param name="FullName">Their name.</param>
/// <param name="Role">Their role.</param>
/// <param name="StartsAt">Branch wall clock.</param>
/// <param name="EndsAt">Branch wall clock, on the next date when <paramref name="EndsNextDay"/>.</param>
/// <param name="EndsNextDay">Ends after midnight.</param>
/// <param name="StartsAtUtc">The start as an instant.</param>
/// <param name="EndsAtUtc">The end as an instant.</param>
/// <param name="OnShiftNow">Started and not over.</param>
/// <param name="SignedIn">Has a live PIN session on a device of this branch now - the board's "signed in".</param>
public sealed record TodayShift(
    Guid StaffMemberId,
    string FullName,
    StaffRole Role,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    bool EndsNextDay,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    bool OnShiftNow,
    bool SignedIn);

/// <summary>Reads <see cref="StaffToday"/>.</summary>
public interface IStaffTodayQuery
{
    /// <summary>The day so far at <paramref name="branchId"/>, or the caller's own branch when it is null.</summary>
    /// <param name="branchId">The branch asked for; null for the session's branch, else the caller's home branch.</param>
    /// <param name="sessionDeviceId">The device a PIN session is open on; null for an admin-panel token.</param>
    /// <param name="tokenBranchId">The branch the token names, when it names one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Domain.Staff.StaffPermissionException">
    /// Not a manager or above who reaches the branch - or, on a shared tablet, a branch other than the tablet's.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    Task<StaffToday> GetAsync(
        Guid? branchId,
        Guid? sessionDeviceId,
        Guid? tokenBranchId,
        CancellationToken cancellationToken = default);
}
