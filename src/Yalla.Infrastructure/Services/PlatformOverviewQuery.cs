using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Messaging;
using Yalla.Application.Platform;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The platform console's read models: Home, one venue's bookings, and the activity feed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Aggregates only.</b> Every booking query here groups and counts; none selects a guest's name,
/// phone or code, so there is nothing to leak at any depth of the response. The platform admin runs
/// Yalla; the restaurant is who sees its guests.
/// </para>
/// <para>
/// <b>Each branch counts on its own calendar.</b> A booking's <c>LocalDate</c> is the branch's own
/// date, so "today" is worked out per branch from its time zone and a booking lands on "so many days
/// before its branch's today". Home's labels are dates in the zone most branches share - Asia/Yerevan
/// for every branch there is - which is the only place a second zone would show.
/// </para>
/// <para>
/// A fixed handful of queries whatever the size of the platform; the venue and branch lists are read
/// whole, which is the right size for a platform that onboards venues one at a time by hand.
/// </para>
/// </remarks>
internal sealed class PlatformOverviewQuery(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IBranchReadinessQuery readiness,
    IOutbox outbox) : IPlatformOverviewQuery
{
    /// <summary>How many days the per-day series covers. The 14-day chart is its last fourteen.</summary>
    public const int SeriesDays = 30;

    /// <summary>How many recently added venues Home shows.</summary>
    public const int RecentVenueCount = 5;

    /// <summary>How many not-ready branches Home lists; the total is reported beside them.</summary>
    public const int NotReadyListed = 20;

    /// <summary>The largest activity page.</summary>
    public const int MaxActivityLimit = 100;

    public async Task<PlatformOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Read the platform overview");

        var nowUtc = clock.UtcNow;

        var venues = await db.Venues
            .AsNoTracking()
            .Select(v => new VenueRow
            {
                VenueId = v.Id,
                Name = v.Name,
                Slug = v.Slug,
                Type = v.Type,
                IsActive = v.IsActive,
                IsSuspended = v.SuspendedAtUtc != null,
                IsDeleted = v.DeletedAtUtc != null,
                CreatedAtUtc = v.CreatedAtUtc,
                BranchCount = v.Branches.Count,
            })
            .ToListAsync(cancellationToken);

        var live = venues.Where(v => !v.IsDeleted).ToList();
        var liveById = live.ToDictionary(v => v.VenueId);

        var branches = await db.Branches
            .AsNoTracking()
            .Where(b => b.Venue.DeletedAtUtc == null)
            .Select(b => new BranchRow
            {
                BranchId = b.Id,
                VenueId = b.VenueId,
                Name = b.Name,
                TimeZoneId = b.TimeZoneId,
                IsActive = b.IsActive,
                HasCover = b.CoverPhotoId != null,
            })
            .ToListAsync(cancellationToken);

        var health = await outbox.GetHealthAsync(1, cancellationToken);

        return new PlatformOverview(
            nowUtc,
            VenueCounts(venues, live),
            await BookingsAsync(branches, nowUtc, cancellationToken),
            await PendingApprovalsAsync(liveById, nowUtc, cancellationToken),
            await OpenTabsAsync(liveById, cancellationToken),
            await NotReadyAsync(branches, liveById, cancellationToken),
            [.. live
                .OrderByDescending(v => v.CreatedAtUtc)
                .ThenBy(v => v.Name, StringComparer.CurrentCulture)
                .Take(RecentVenueCount)
                .Select(v => new RecentVenue(
                    v.VenueId,
                    v.Name,
                    v.Slug,
                    v.Type,
                    PlatformRollups.StatusOf(v.IsActive, v.IsSuspended, v.IsDeleted),
                    v.BranchCount,
                    v.CreatedAtUtc))],
            new OverviewOutbox(
                health.Pending,
                health.Due,
                health.Failed,
                health.DeadLettered,
                health.SentLastDay,
                health.DeadLetteredLastDay));
    }

    public async Task<VenueBookingsSummary> GetVenueBookingsSummaryAsync(
        Guid venueId,
        int days = VenueBookingsSummaryWindows.Default,
        CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Read a venue's bookings summary");

        if (!VenueBookingsSummaryWindows.Accepted.Contains(days))
        {
            throw new ArgumentOutOfRangeException(
                "days", days, $"days is one of: {string.Join(", ", VenueBookingsSummaryWindows.Accepted)}.");
        }

        var nowUtc = clock.UtcNow;

        if (!await db.Venues.AsNoTracking().AnyAsync(v => v.Id == venueId, cancellationToken))
        {
            throw new KeyNotFoundException($"Venue {venueId} was not found.");
        }

        var branches = await db.Branches
            .AsNoTracking()
            .Where(b => b.VenueId == venueId)
            .OrderBy(b => b.Name)
            .ThenBy(b => b.Id)
            .Select(b => new { b.Id, b.Name, b.TimeZoneId })
            .ToListAsync(cancellationToken);

        var today = branches.ToDictionary(b => b.Id, b => TodayAt(b.TimeZoneId, nowUtc));
        var branchIds = branches.Select(b => b.Id).ToList();
        var rows = new List<BookingGroup>();

        if (branches.Count > 0)
        {
            var from = today.Values.Min().AddDays(-(days - 1));
            var to = today.Values.Max();

            // Grouped in SQL: a count and a party total per branch, day and status. No row of a booking
            // ever leaves the database.
            rows = await db.Reservations
                .AsNoTracking()
                .Where(r => branchIds.Contains(r.BranchId) && r.LocalDate >= from && r.LocalDate <= to)
                .GroupBy(r => new { r.BranchId, r.LocalDate, r.Status })
                .Select(g => new BookingGroup
                {
                    BranchId = g.Key.BranchId,
                    LocalDate = g.Key.LocalDate,
                    Status = g.Key.Status,
                    Count = g.Count(),
                    PartySum = g.Sum(r => (long)r.PartySize),
                })
                .ToListAsync(cancellationToken);
        }

        // Each branch's own window: its today and the days before it.
        var inWindow = rows
            .Where(r => today[r.BranchId].DayNumber - r.LocalDate.DayNumber is >= 0 and var daysAgo && daysAgo < days)
            .ToList();

        var weekdays = new int[7];

        foreach (var row in inWindow)
        {
            weekdays[(int)row.LocalDate.DayOfWeek] += row.Count;
        }

        var kept = inWindow.Where(r => !IsCancelled(r.Status)).ToList();

        return new VenueBookingsSummary(
            venueId,
            days,
            inWindow.Sum(r => r.Count),
            StatusCounts(inWindow),
            [.. weekdays.Select((count, day) => new WeekdayCount((DayOfWeek)day, count))],
            PlatformRollups.Average(kept.Sum(r => r.PartySum), kept.Sum(r => r.Count)),
            [.. branches.Select(b =>
            {
                var own = StatusCounts([.. inWindow.Where(r => r.BranchId == b.Id)]);

                return new BranchBookingCounts(
                    b.Id,
                    b.Name,
                    own.Completed + own.Upcoming + own.PendingApproval + own.Cancelled + own.NoShow,
                    own.Completed,
                    own.Upcoming,
                    own.PendingApproval,
                    own.Cancelled,
                    own.NoShow,
                    rows.Where(r => r.BranchId == b.Id && r.LocalDate == today[b.Id] && !IsCancelled(r.Status)).Sum(r => r.Count));
            })]);
    }

    public async Task<ActivityFeed> GetActivityAsync(ActivityQuery query, CancellationToken cancellationToken = default)
    {
        RequirePlatformAdmin("Read the activity feed");
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit is < 1 or > MaxActivityLimit)
        {
            throw new ArgumentOutOfRangeException("limit", query.Limit, $"The feed returns 1 to {MaxActivityLimit} entries.");
        }

        // Rows with no staff member behind them - the diner deleting their own account - name nobody
        // and are about nobody the console shows; they stay in the log and out of the feed. So do the
        // wrong current passwords on a console user's own account - the admin's and a venue user's: they
        // are the lockout's counter (the log is where it counts them), a security record, not a change
        // anybody made.
        //
        // A venue answering its own reviews is the venue talking to its guests, not a change the platform
        // follows: review.reply, and a venue's review.remove-reply, stay in the log and out of the feed.
        // The platform taking a reply down is the platform's own act, and is shown.
        var logs = db.PlatformAuditLogs
            .AsNoTracking()
            .Where(l => l.ActorRole != null
                        && l.Action != PlatformAdminAccountService.PasswordChangeFailedAuditAction
                        && l.Action != VenueUserAccountService.PasswordChangeFailedAuditAction
                        && l.Action != ReviewModerationService.ReplyAuditAction
                        && !(l.Action == ReviewModerationService.RemoveReplyAuditAction
                             && l.ActorRole != StaffRole.PlatformAdmin));

        if (query.VenueId is { } venueId)
        {
            logs = logs.Where(l => l.VenueId == venueId);
        }

        var rows = await logs
            .OrderByDescending(l => l.AtUtc)
            .ThenByDescending(l => l.Id)
            .Take(query.Limit)
            .Select(l => new
            {
                l.Id,
                l.AtUtc,
                l.Action,
                l.ActorStaffMemberId,
                l.ActorRole,
                l.ActorDisplay,
                l.TargetType,
                l.TargetId,
                l.TargetDisplay,
                l.VenueId,
                VenueName = db.Venues.Where(v => v.Id == l.VenueId).Select(v => v.Name).FirstOrDefault(),
                l.ChangesJson,
            })
            .ToListAsync(cancellationToken);

        // Two rows written at the same instant (a test clock, or one request that audits twice)
        // need a tiebreak that follows the order they were written. The ids are version-7 GUIDs,
        // time-ordered as .NET compares them but not as SQL Server sorts uniqueidentifier - so
        // the database picks the page and the final order is settled here.
        return new ActivityFeed(
        [
            .. rows
                .OrderByDescending(r => r.AtUtc)
                .ThenByDescending(r => r.Id)
                .Select(r => new ActivityEntry(
                r.Id,
                r.AtUtc,
                r.Action,
                new ActivityActor(r.ActorStaffMemberId, r.ActorRole, r.ActorDisplay),
                new ActivityTarget(r.TargetType, r.TargetId, r.TargetDisplay),
                r.VenueId is { } id && r.VenueName is { } name ? new ActivityVenue(id, name) : null,
                Detail(r.ChangesJson))),
        ]);
    }

    // ---------------------------------------------------------------- Home's pieces

    private static OverviewVenueCounts VenueCounts(List<VenueRow> all, List<VenueRow> live)
    {
        var states = live.Select(v => PlatformRollups.StatusOf(v.IsActive, v.IsSuspended, v.IsDeleted)).ToList();

        return new OverviewVenueCounts(
            Total: live.Count,
            Active: states.Count(s => s == VenueStatus.Active),
            Suspended: states.Count(s => s == VenueStatus.Suspended),
            Inactive: states.Count(s => s == VenueStatus.Inactive),
            Deleted: all.Count - live.Count,
            Branches: live.Sum(v => v.BranchCount));
    }

    private async Task<OverviewBookings> BookingsAsync(
        List<BranchRow> branches,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // The zone most branches share labels the days; every branch still counts on its own.
        var labelZone = branches
            .GroupBy(b => b.TimeZoneId, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault() ?? "UTC";

        var labelToday = TodayAt(labelZone, nowUtc);
        var perDay = new int[SeriesDays];

        if (branches.Count > 0)
        {
            var today = branches.ToDictionary(b => b.BranchId, b => TodayAt(b.TimeZoneId, nowUtc));
            var from = today.Values.Min().AddDays(-(SeriesDays - 1));
            var to = today.Values.Max();

            var rows = await db.Reservations
                .AsNoTracking()
                .Where(r => r.LocalDate >= from
                            && r.LocalDate <= to
                            && r.Status != ReservationStatus.CancelledByDiner
                            && r.Status != ReservationStatus.CancelledByVenue
                            && r.Branch.Venue.DeletedAtUtc == null)
                .GroupBy(r => new { r.BranchId, r.LocalDate })
                .Select(g => new { g.Key.BranchId, g.Key.LocalDate, Count = g.Count() })
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                if (!today.TryGetValue(row.BranchId, out var branchToday))
                {
                    continue;
                }

                var daysAgo = branchToday.DayNumber - row.LocalDate.DayNumber;

                if (daysAgo is >= 0 and < SeriesDays)
                {
                    perDay[SeriesDays - 1 - daysAgo] += row.Count;
                }
            }
        }

        var last7 = perDay[^7..].Sum();
        var previous7 = perDay[^14..^7].Sum();

        return new OverviewBookings(
            last7,
            previous7,
            PlatformRollups.ChangePercent(last7, previous7),
            labelZone,
            [.. perDay.Select((count, i) => new DailyCount(labelToday.AddDays(i - (SeriesDays - 1)), count))]);
    }

    private async Task<OverviewVenueCounter> PendingApprovalsAsync(
        Dictionary<Guid, VenueRow> live,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // A request whose time has passed, or whose answer window has closed, is no longer one anybody
        // can approve - the same predicate the venue's own approval queue lists by.
        var counts = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Status == ReservationStatus.PendingApproval
                        && r.StartUtc > nowUtc
                        && (r.ApprovalExpiresAtUtc == null || r.ApprovalExpiresAtUtc > nowUtc)
                        && r.Branch.Venue.DeletedAtUtc == null)
            .GroupBy(r => r.Branch.VenueId)
            .Select(g => new { VenueId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return Counter(counts.Select(c => (c.VenueId, c.Count)), live);
    }

    private async Task<OverviewVenueCounter> OpenTabsAsync(
        Dictionary<Guid, VenueRow> live,
        CancellationToken cancellationToken)
    {
        var counts = await db.Tabs
            .AsNoTracking()
            .Where(t => (t.Status == TabStatus.Open || t.Status == TabStatus.Closing)
                        && t.Branch.Venue.DeletedAtUtc == null)
            .GroupBy(t => t.Branch.VenueId)
            .Select(g => new { VenueId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return Counter(counts.Select(c => (c.VenueId, c.Count)), live);
    }

    private static OverviewVenueCounter Counter(IEnumerable<(Guid VenueId, int Count)> counts, Dictionary<Guid, VenueRow> live)
    {
        var venues = counts
            .Where(c => c.Count > 0 && live.ContainsKey(c.VenueId))
            .Select(c => new VenueCount(c.VenueId, live[c.VenueId].Name, c.Count))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();

        return new OverviewVenueCounter(venues.Sum(v => v.Count), venues);
    }

    /// <summary>
    /// The branches a diner can reach - venue switched on, not suspended, not deleted; branch switched
    /// on - whose checklist is not done or that have no cover. The newest venues first: those are the
    /// ones somebody is setting up.
    /// </summary>
    private async Task<OverviewNotReady> NotReadyAsync(
        List<BranchRow> branches,
        Dictionary<Guid, VenueRow> live,
        CancellationToken cancellationToken)
    {
        var reachable = branches
            .Where(b => b.IsActive
                        && live.TryGetValue(b.VenueId, out var venue)
                        && venue.IsActive
                        && !venue.IsSuspended)
            .ToList();

        var checklists = await readiness.GetManyAsync([.. reachable.Select(b => b.BranchId)], cancellationToken);

        var notReady = reachable
            .Select(b => new
            {
                Branch = b,
                Venue = live[b.VenueId],
                Issues = PlatformService.IssuesOf(b.BranchId, b.HasCover, checklists),
            })
            .Where(x => x.Issues.Count > 0)
            .OrderByDescending(x => x.Venue.CreatedAtUtc)
            .ThenBy(x => x.Venue.Name, StringComparer.CurrentCulture)
            .ThenBy(x => x.Branch.Name, StringComparer.CurrentCulture)
            .ThenBy(x => x.Branch.BranchId)
            .ToList();

        return new OverviewNotReady(
            notReady.Count,
            [.. notReady
                .Take(NotReadyListed)
                .Select(x => new NotReadyBranch(x.Venue.VenueId, x.Venue.Name, x.Branch.BranchId, x.Branch.Name, x.Issues))]);
    }

    // ---------------------------------------------------------------- a venue's window

    private static BookingStatusCounts StatusCounts(List<BookingGroup> rows)
    {
        int Of(BookingBucket bucket) => rows.Where(r => PlatformRollups.BucketOf(r.Status) == bucket).Sum(r => r.Count);

        return new BookingStatusCounts(
            Of(BookingBucket.Completed),
            Of(BookingBucket.Upcoming),
            Of(BookingBucket.PendingApproval),
            Of(BookingBucket.Cancelled),
            Of(BookingBucket.NoShow));
    }

    private static bool IsCancelled(ReservationStatus status) =>
        PlatformRollups.BucketOf(status) == BookingBucket.Cancelled;

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Today at a branch. A zone this machine does not know falls back to the UTC date rather than
    /// taking Home down with it - the branch's own pages refuse that zone loudly enough.
    /// </summary>
    private static DateOnly TodayAt(string timeZoneId, DateTime nowUtc)
    {
        try
        {
            return BranchZone.For(timeZoneId).LocalDateAt(nowUtc);
        }
        catch (BranchTimeZoneMisconfiguredException)
        {
            return DateOnly.FromDateTime(nowUtc);
        }
    }

    /// <summary>The row's JSON, as a value the response can carry; absent if it does not parse.</summary>
    private static JsonElement? Detail(string changesJson)
    {
        try
        {
            using var document = JsonDocument.Parse(changesJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void RequirePlatformAdmin(string operation)
    {
        if (actor.Type != ActorType.Staff || actor.Role != StaffRole.PlatformAdmin || actor.StaffMemberId is null)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.PlatformAdmin);
        }
    }

    private sealed class VenueRow
    {
        public Guid VenueId { get; init; }

        public string Name { get; init; } = null!;

        public string Slug { get; init; } = null!;

        public VenueType Type { get; init; }

        public bool IsActive { get; init; }

        public bool IsSuspended { get; init; }

        public bool IsDeleted { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        public int BranchCount { get; init; }
    }

    private sealed class BranchRow
    {
        public Guid BranchId { get; init; }

        public Guid VenueId { get; init; }

        public string Name { get; init; } = null!;

        public string TimeZoneId { get; init; } = null!;

        public bool IsActive { get; init; }

        public bool HasCover { get; init; }
    }

    private sealed class BookingGroup
    {
        public Guid BranchId { get; init; }

        public DateOnly LocalDate { get; init; }

        public ReservationStatus Status { get; init; }

        public int Count { get; init; }

        public long PartySum { get; init; }
    }
}
