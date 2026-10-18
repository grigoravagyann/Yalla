using System.Text.Json;
using Yalla.Application.BranchSettings;
using Yalla.Domain.Enums;

namespace Yalla.Application.Platform;

/// <summary>A venue's state, as one value. Derived from the venue's flags; never stored.</summary>
public enum VenueStatus
{
    /// <summary>Switched on, not suspended, not deleted. Diners can find it.</summary>
    Active = 1,

    /// <summary>Suspended - usually for not paying. Hidden from diners; every row kept.</summary>
    Suspended = 2,

    /// <summary>Switched off with <c>isActive</c> false, neither suspended nor deleted.</summary>
    Inactive = 3,

    /// <summary>Soft-deleted. Never cleared.</summary>
    Deleted = 4,
}

/// <summary>How a venue's branches are spread over the tiers. Derived; never stored.</summary>
public enum TierMix
{
    /// <summary>No branch is paid - including a venue that has no branch.</summary>
    Free = 1,

    /// <summary>Some branches are paid and some are not.</summary>
    Mixed = 2,

    /// <summary>Every branch is paid.</summary>
    Paid = 3,
}

/// <summary>
/// The small arithmetic the platform's read models share, kept apart from the queries so it can be
/// tested without a database and so every screen rounds the same way.
/// </summary>
public static class PlatformRollups
{
    /// <summary>Free when no branch is paid, Paid when every branch is, Mixed otherwise.</summary>
    public static TierMix TierMixOf(int branchCount, int paidBranchCount) =>
        paidBranchCount <= 0
            ? TierMix.Free
            : paidBranchCount >= branchCount
                ? TierMix.Paid
                : TierMix.Mixed;

    /// <summary>Deleted outranks suspended, which outranks switched off.</summary>
    public static VenueStatus StatusOf(bool isActive, bool isSuspended, bool isDeleted) =>
        isDeleted
            ? VenueStatus.Deleted
            : isSuspended
                ? VenueStatus.Suspended
                : isActive
                    ? VenueStatus.Active
                    : VenueStatus.Inactive;

    /// <summary>
    /// The change from <paramref name="previous"/> to <paramref name="current"/> in percent, to one
    /// decimal. Absent when there was nothing before: growth from zero is not a percentage.
    /// </summary>
    public static double? ChangePercent(int current, int previous) =>
        previous <= 0
            ? null
            : Math.Round((current - previous) * 100.0 / previous, 1, MidpointRounding.AwayFromZero);

    /// <summary>A mean to one decimal, or absent when there is nothing to average.</summary>
    public static double? Average(long sum, int count) =>
        count <= 0 ? null : Math.Round((double)sum / count, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// What a guest would find missing at a branch, from its readiness checklist and whether it has a
    /// cover: <c>no-tables</c>, <c>no-menu</c>, <c>incomplete-dishes</c> (with its count) and
    /// <c>no-hours</c> in checklist order, then <c>no-photo</c>.
    /// </summary>
    /// <remarks>
    /// The console's "not ready" is what diners would see unfinished. The checklist's other lines - a
    /// reservation policy nobody saved, no staff, no tablet - are the venue's own work, shown on the
    /// branch's readiness page, and do not make a branch look unfinished to a guest. A table without a
    /// label reads as no tables, and a menu with categories and no dishes as no menu.
    /// </remarks>
    public static IReadOnlyList<ReadinessIssue> GuestFacingIssues(IEnumerable<ReadinessIssue> checklist, bool hasCover)
    {
        ArgumentNullException.ThrowIfNull(checklist);

        var issues = new List<ReadinessIssue>();

        foreach (var issue in checklist)
        {
            var shown = issue.Code switch
            {
                ReadinessIssueCodes.NoTables or ReadinessIssueCodes.TablesUnlabelled => new ReadinessIssue(ReadinessIssueCodes.NoTables),
                ReadinessIssueCodes.NoMenu or ReadinessIssueCodes.MenuEmpty => new ReadinessIssue(ReadinessIssueCodes.NoMenu),
                ReadinessIssueCodes.IncompleteDishes => issue,
                ReadinessIssueCodes.NoHours => issue,
                _ => null,
            };

            if (shown is not null && !issues.Exists(i => i.Code == shown.Code))
            {
                issues.Add(shown);
            }
        }

        if (!hasCover)
        {
            issues.Add(new ReadinessIssue(ReadinessIssueCodes.NoPhoto));
        }

        return issues;
    }

    /// <summary>ISO weekday: 1 Monday through 7 Sunday - the week as Armenia writes it.</summary>
    public static int IsoDay(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    /// <summary>Which bucket of the bookings summary a booking in this status falls into.</summary>
    public static BookingBucket BucketOf(ReservationStatus status) => status switch
    {
        ReservationStatus.PendingApproval => BookingBucket.PendingApproval,
        ReservationStatus.Confirmed => BookingBucket.Confirmed,
        ReservationStatus.Seated or ReservationStatus.Completed => BookingBucket.Completed,
        ReservationStatus.CancelledByDiner or ReservationStatus.CancelledByVenue => BookingBucket.Cancelled,
        ReservationStatus.NoShow => BookingBucket.NoShow,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a booking status this summary knows."),
    };
}

/// <summary>The buckets the platform's booking numbers are counted in.</summary>
public enum BookingBucket
{
    /// <summary>Waiting for the venue to approve.</summary>
    PendingApproval = 1,

    /// <summary>Confirmed and not yet seated: still ahead, or late and not yet marked.</summary>
    Confirmed = 2,

    /// <summary>Seated or completed - the booking took place.</summary>
    Completed = 3,

    /// <summary>Cancelled by the diner or by the venue, a rejected request included.</summary>
    Cancelled = 4,

    /// <summary>Nobody came.</summary>
    NoShow = 5,
}

// ------------------------------------------------------------------ Home

/// <summary>
/// Everything the platform console's Home shows, in one read. <b>Aggregates only</b>: no guest's name,
/// phone or booking code is in it, at any depth.
/// </summary>
/// <param name="GeneratedAtUtc">When this was worked out.</param>
/// <param name="Venues">How many venues there are, by tier and by state.</param>
/// <param name="Bookings">Bookings per day for the last 30 days and the week-on-week change.</param>
/// <param name="PendingApprovals">Booking requests still waiting for a venue to approve them, per venue.</param>
/// <param name="OpenTabs">Tabs open or closing right now, per venue.</param>
/// <param name="NotReady">Branches diners can reach that a guest would find unfinished.</param>
/// <param name="RecentVenues">The five newest venues that are not deleted, newest first.</param>
/// <param name="Outbox">The message outbox's counts - the same numbers <c>GET /api/platform/outbox</c> serves.</param>
public sealed record PlatformOverview(
    DateTime GeneratedAtUtc,
    OverviewVenueCounts Venues,
    OverviewBookings Bookings,
    OverviewVenueCounter PendingApprovals,
    OverviewVenueCounter OpenTabs,
    OverviewNotReady NotReady,
    IReadOnlyList<RecentVenue> RecentVenues,
    OverviewOutbox Outbox);

/// <summary>
/// Venue counts. Everything but <paramref name="Deleted"/> counts venues that are not deleted; the tier
/// split is per venue, from its branches' tiers (<see cref="TierMix"/>).
/// </summary>
/// <param name="Total">Venues not deleted: <paramref name="Paid"/> + <paramref name="Mixed"/> + <paramref name="Free"/>.</param>
/// <param name="Paid">Every branch paid.</param>
/// <param name="Mixed">Some branches paid.</param>
/// <param name="Free">No branch paid, or no branch.</param>
/// <param name="Active">Active (see <see cref="VenueStatus"/>).</param>
/// <param name="Suspended">Suspended.</param>
/// <param name="Inactive">Switched off.</param>
/// <param name="Deleted">Soft-deleted - outside every other number here.</param>
/// <param name="Branches">Branches of the venues in <paramref name="Total"/>.</param>
/// <param name="PaidBranches">How many of those are paid.</param>
public sealed record OverviewVenueCounts(
    int Total,
    int Paid,
    int Mixed,
    int Free,
    int Active,
    int Suspended,
    int Inactive,
    int Deleted,
    int Branches,
    int PaidBranches);

/// <summary>
/// Bookings by the day of the visit. A booking counts on the branch's own local date; cancelled ones
/// are left out (a request the venue rejected is a cancellation). Venues that are deleted are left out.
/// </summary>
/// <param name="Last7Days">Today and the six days before it.</param>
/// <param name="Previous7Days">The seven days before those.</param>
/// <param name="ChangePercent">
/// <paramref name="Last7Days"/> against <paramref name="Previous7Days"/>, in percent to one decimal;
/// absent when the previous week had none.
/// </param>
/// <param name="LabelTimeZoneId">
/// The zone the <see cref="DailyCount.Date"/> labels are written in: the one most branches share. Each
/// branch is counted on its own calendar, aligned so the last entry is every branch's today.
/// </param>
/// <param name="PerDay">Thirty entries, oldest first, the last one today. The 14-day chart is the last fourteen.</param>
public sealed record OverviewBookings(
    int Last7Days,
    int Previous7Days,
    double? ChangePercent,
    string LabelTimeZoneId,
    IReadOnlyList<DailyCount> PerDay);

/// <summary>One day's count.</summary>
/// <param name="Date">The day, in <see cref="OverviewBookings.LabelTimeZoneId"/>.</param>
/// <param name="Count">Bookings for that day.</param>
public sealed record DailyCount(DateOnly Date, int Count);

/// <summary>A total and the venues it is made of.</summary>
/// <param name="Total">The sum over every venue.</param>
/// <param name="Venues">Each venue with any, the most first, then by name.</param>
public sealed record OverviewVenueCounter(int Total, IReadOnlyList<VenueCount> Venues);

/// <summary>One venue's count.</summary>
public sealed record VenueCount(Guid VenueId, string Name, int Count);

/// <summary>The branches still not ready for diners.</summary>
/// <param name="Total">How many branches.</param>
/// <param name="Branches">The first twenty, by venue name then branch name.</param>
public sealed record OverviewNotReady(int Total, IReadOnlyList<NotReadyBranch> Branches);

/// <summary>A branch that is not ready, and why.</summary>
/// <param name="VenueId">Its venue.</param>
/// <param name="VenueName">The venue's name.</param>
/// <param name="BranchId">The branch.</param>
/// <param name="BranchName">The branch's name.</param>
/// <param name="Issues">
/// What a guest would find missing (<see cref="PlatformRollups.GuestFacingIssues"/>): <c>no-tables</c>,
/// <c>no-menu</c>, <c>incomplete-dishes</c>, <c>no-hours</c>, <c>no-photo</c>. The card shows the first.
/// </param>
public sealed record NotReadyBranch(
    Guid VenueId,
    string VenueName,
    Guid BranchId,
    string BranchName,
    IReadOnlyList<ReadinessIssue> Issues);

/// <summary>A recently added venue, as Home's table shows it.</summary>
public sealed record RecentVenue(
    Guid VenueId,
    string Name,
    string Slug,
    VenueType Type,
    VenueStatus Status,
    TierMix TierMix,
    int BranchCount,
    int PaidBranchCount,
    DateTime CreatedAtUtc);

/// <summary>The outbox's counts.</summary>
/// <param name="Pending">Written, not yet sent, not dead.</param>
/// <param name="Due">Pending and due now.</param>
/// <param name="Failed">Pending with at least one failed attempt.</param>
/// <param name="DeadLettered">Given up on, ever.</param>
/// <param name="SentLastDay">Sent in the last 24 hours.</param>
/// <param name="DeadLetteredLastDay">Given up on in the last 24 hours - what Home's alert counts.</param>
public sealed record OverviewOutbox(
    int Pending,
    int Due,
    int Failed,
    int DeadLettered,
    int SentLastDay,
    int DeadLetteredLastDay);

// ------------------------------------------------------------------ a venue's bookings

/// <summary>
/// One venue's bookings as counts, for the venue page. <b>Aggregates only</b>: no guest's name, phone
/// or code.
/// </summary>
/// <param name="VenueId">The venue.</param>
/// <param name="GeneratedAtUtc">When this was worked out.</param>
/// <param name="PendingApprovalUpcoming">
/// Requests waiting for the venue to approve them whose visit is still ahead - the number the venue
/// page's card shows. A request whose time has passed is not counted here.
/// </param>
/// <param name="Today">Every branch with today's bookings, cancelled ones left out, on its own calendar.</param>
/// <param name="Last7Days">The last seven days, today included.</param>
/// <param name="Last30Days">The last thirty days, today included.</param>
public sealed record VenueBookingsSummary(
    Guid VenueId,
    DateTime GeneratedAtUtc,
    int PendingApprovalUpcoming,
    IReadOnlyList<BranchBookingsToday> Today,
    BookingWindowSummary Last7Days,
    BookingWindowSummary Last30Days);

/// <summary>One branch's bookings today.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="Name">Its name.</param>
/// <param name="LocalDate">Today at the branch.</param>
/// <param name="Bookings">Bookings for today, cancelled ones left out.</param>
public sealed record BranchBookingsToday(Guid BranchId, string Name, DateOnly LocalDate, int Bookings);

/// <summary>
/// Bookings whose visit falls in the window - each branch's own local dates, today and the days before
/// it - in <b>every</b> status, cancelled ones included, so the split adds up to the total.
/// </summary>
/// <param name="Days">How many days: 7 or 30.</param>
/// <param name="Total">Every booking in the window.</param>
/// <param name="ByStatus">The total, split by outcome.</param>
/// <param name="ByBranch">Each branch of the venue, by name, every one listed even with none.</param>
/// <param name="ByWeekday">Seven entries, Monday (1) to Sunday (7), by the weekday of the visit.</param>
/// <param name="AveragePartySize">
/// The average party of the bookings that were not cancelled, to one decimal; absent with none.
/// </param>
public sealed record BookingWindowSummary(
    int Days,
    int Total,
    BookingStatusCounts ByStatus,
    IReadOnlyList<BranchBookingCounts> ByBranch,
    IReadOnlyList<WeekdayCount> ByWeekday,
    double? AveragePartySize);

/// <summary>Bookings by outcome. See <see cref="BookingBucket"/> for which statuses each counts.</summary>
/// <param name="Completed">Seated or completed: they came.</param>
/// <param name="Confirmed">Confirmed and not seated - still ahead, or late and not yet marked.</param>
/// <param name="PendingApproval">Still waiting for the venue to approve.</param>
/// <param name="Cancelled">Cancelled by either side, rejections included.</param>
/// <param name="NoShow">Nobody came.</param>
public sealed record BookingStatusCounts(int Completed, int Confirmed, int PendingApproval, int Cancelled, int NoShow);

/// <summary>One branch's share of a window.</summary>
public sealed record BranchBookingCounts(Guid BranchId, string Name, int Total, BookingStatusCounts ByStatus);

/// <summary>One weekday's count.</summary>
/// <param name="IsoDay">1 Monday … 7 Sunday.</param>
/// <param name="Count">Bookings whose visit fell on that weekday.</param>
public sealed record WeekdayCount(int IsoDay, int Count);

// ------------------------------------------------------------------ activity

/// <summary>Which audit rows the activity feed returns.</summary>
/// <param name="Limit">1 to 100; 20 by default.</param>
/// <param name="VenueId">Only the rows about this venue - its own, its branches', its staff's and its reviews'.</param>
public sealed record ActivityQuery(int Limit = 20, Guid? VenueId = null);

/// <summary>The newest audit rows, newest first.</summary>
public sealed record ActivityFeed(IReadOnlyList<ActivityEntry> Items);

/// <summary>
/// One audit row as the console's activity feed shows it.
/// </summary>
/// <param name="Id">The row.</param>
/// <param name="AtUtc">When it happened.</param>
/// <param name="Action">
/// The stable slug - <c>venue.create</c>, <c>venue.update</c>, <c>venue.suspend</c>,
/// <c>venue.reactivate</c>, <c>venue.delete</c>, <c>branch.create</c>, <c>branch.update</c>,
/// <c>branch.tier-change</c>, <c>branch.relocate</c>, <c>staff.create</c>, <c>staff.sign-in-issued</c>,
/// <c>venue.restore</c>, <c>review.hide</c>, <c>review.unhide</c>, <c>review.dismiss-reports</c>, <c>table.regenerate-qr</c>,
/// <c>admin.rename</c>, <c>admin.password-change</c>.
/// </param>
/// <param name="Actor">Who did it, as they were called then.</param>
/// <param name="Target">What it was done to, as it was called then.</param>
/// <param name="Venue">The venue it concerns, by its current name; absent for the admin's own account.</param>
/// <param name="Detail">The row's JSON as written - its shape is per action (docs/platform-admin.md).</param>
public sealed record ActivityEntry(
    Guid Id,
    DateTime AtUtc,
    string Action,
    ActivityActor Actor,
    ActivityTarget Target,
    ActivityVenue? Venue,
    JsonElement? Detail);

/// <summary>Who acted.</summary>
/// <param name="StaffMemberId">Their staff row.</param>
/// <param name="Role">Their role when they acted: 5 is the platform admin, the rest a venue's own staff.</param>
/// <param name="DisplayName">Their name when they acted; absent for a row from before names were kept.</param>
public sealed record ActivityActor(Guid StaffMemberId, StaffRole? Role, string? DisplayName);

/// <summary>What was acted on.</summary>
/// <param name="Type"><c>Venue</c>, <c>Branch</c>, <c>StaffMember</c>, <c>BranchReview</c>, <c>DiningTable</c>.</param>
/// <param name="Id">Its id.</param>
/// <param name="DisplayName">
/// Its name then - a table's label, a person's name; for a review, the branch it is about, because the
/// reviewer is a diner and is not named in the log.
/// </param>
public sealed record ActivityTarget(string Type, Guid Id, string? DisplayName);

/// <summary>The venue an entry concerns.</summary>
public sealed record ActivityVenue(Guid VenueId, string Name);

/// <summary>
/// The platform console's read models: Home, a venue's bookings, and the activity feed.
/// <b>Platform admin only</b>, checked inside, and aggregates only - nothing here names a guest.
/// </summary>
public interface IPlatformOverviewQuery
{
    /// <summary>Home in one read.</summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">Not a platform admin.</exception>
    Task<PlatformOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>One venue's bookings for the last 7 and 30 days.</summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">Not a platform admin.</exception>
    /// <exception cref="KeyNotFoundException">No such venue.</exception>
    Task<VenueBookingsSummary> GetVenueBookingsSummaryAsync(Guid venueId, CancellationToken cancellationToken = default);

    /// <summary>The newest audit rows written by staff, newest first.</summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">Not a platform admin.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit outside 1-100.</exception>
    Task<ActivityFeed> GetActivityAsync(ActivityQuery query, CancellationToken cancellationToken = default);
}

// ------------------------------------------------------------------ the admin's own account

/// <summary>The signed-in platform admin's own account.</summary>
/// <param name="StaffMemberId">Their staff row.</param>
/// <param name="FullName">The name the console shows and the audit log records. Theirs to change.</param>
/// <param name="Email">
/// The sign-in address. Fixed: it is <c>PlatformAdmin:Email</c> in configuration, and the seed finds the
/// account by it, so there is no endpoint that changes it.
/// </param>
public sealed record PlatformAdminProfile(Guid StaffMemberId, string FullName, string Email);

/// <summary>Rename the signed-in platform admin.</summary>
/// <param name="FullName">Required; 1-200 characters after trimming.</param>
public sealed record UpdatePlatformAdminProfileCommand(string? FullName);

/// <summary>Change the signed-in platform admin's password.</summary>
/// <param name="CurrentPassword">Required, and must be right.</param>
/// <param name="NewPassword">At least <c>Auth:MinimumPasswordLength</c> characters (12) - the one rule every console password follows.</param>
/// <param name="KeepRefreshChainId">The sign-in this request came from, which stays signed in. Every other is signed out.</param>
public sealed record ChangePlatformAdminPasswordCommand(
    string? CurrentPassword,
    string? NewPassword,
    Guid? KeepRefreshChainId);

/// <summary>
/// The platform admin's own name and password. <b>Platform admin only</b>, and only their own row.
/// </summary>
public interface IPlatformAdminAccountService
{
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">Not an active platform admin.</exception>
    Task<PlatformAdminProfile> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Renames the admin. Audited as <c>admin.rename</c>; a name that does not change writes nothing.</summary>
    /// <exception cref="Yalla.Domain.FieldValidationException">The name is missing or too long.</exception>
    Task<PlatformAdminProfile> UpdateAsync(UpdatePlatformAdminProfileCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the password after checking the current one, and signs every other sign-in out.
    /// Audited as <c>admin.password-change</c>, with nothing about either password.
    /// </summary>
    /// <exception cref="Yalla.Domain.FieldValidationException">
    /// <c>currentPassword</c> missing or wrong, or <c>newPassword</c> missing or too short - every one at once.
    /// </exception>
    Task ChangePasswordAsync(ChangePlatformAdminPasswordCommand command, CancellationToken cancellationToken = default);
}
