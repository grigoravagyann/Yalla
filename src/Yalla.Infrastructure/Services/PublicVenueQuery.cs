using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Application.Menus;
using Yalla.Application.Public;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The anonymous read surface: a link anybody can open, with no app.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two cache lifetimes, because two kinds of thing are being served.</b> A menu and a week of
/// opening hours change a few times a year; a free-table count changes every few minutes and is the
/// number somebody is refreshing the page to see. Caching both for the same period would either
/// make the table count a lie or make the menu query run on every scrape. So the slow things are
/// held for minutes and the live count is held for seconds - long enough to absorb a burst, short
/// enough that nobody is told a full room is empty.
/// </para>
/// <para>
/// <b>Everything here is blind to anything private.</b> No staff rows, no tabs, no participants, no
/// QR tokens, and no table status beyond "is somebody sitting there". The shapes in
/// <c>PublicModels</c> physically cannot carry those fields, which is a stronger guarantee than
/// remembering not to select them.
/// </para>
/// </remarks>
internal sealed class PublicVenueQuery(
    YallaDbContext db,
    IMemoryCache cache,
    IClock clock,
    IMenuQuery menu) : IPublicVenueQuery
{
    /// <summary>How long a menu, a set of hours or a floor plan is held. They barely move.</summary>
    public static readonly TimeSpan StableFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a free-table count is held.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds absorbs the burst a shared link produces and is short enough that nobody
    /// walks to a venue because a stale page said it was empty. A stale menu is fine; a stale table
    /// count is the one thing on this surface that can waste somebody's evening.
    /// </remarks>
    public static readonly TimeSpan LiveFor = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<PublicVenueCard>> GetVenuesAsync(CancellationToken cancellationToken = default)
    {
        // The estate itself is cached; the counts underneath it are not, and are stitched on below.
        var venues = await cache.GetOrCreateAsync(
            "public:venues",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = StableFor;

                return await db.Venues
                    .AsNoTracking()
                    .Where(v => v.IsActive && v.SuspendedAtUtc == null && v.DeletedAtUtc == null)
                    .OrderBy(v => v.Name)
                    .Select(v => new
                    {
                        v.Slug,
                        v.Name,
                        v.Type,
                        Branches = v.Branches
                            .Where(b => b.IsActive)
                            .OrderBy(b => b.Name)
                            .Select(b => new
                            {
                                b.Id,
                                b.Slug,
                                b.Name,
                                b.Address,
                                b.TimeZoneId,
                            })
                            .ToList(),
                    })
                    .ToListAsync(cancellationToken);
            })
            ?? [];

        // Filtered against the live estate, so a venue suspended since the list was cached drops off
        // it within seconds rather than at the end of the five-minute window. See
        // PublishedBranchIdsAsync for why seconds and not immediately.
        var live = await PublishedBranchIdsAsync(cancellationToken);

        venues =
        [
            .. venues.Select(v => new
            {
                v.Slug,
                v.Name,
                v.Type,
                Branches = v.Branches.Where(b => live.Contains(b.Id)).ToList(),
            }),
        ];

        var branchIds = venues.SelectMany(v => v.Branches).Select(b => b.Id).ToList();
        var free = await FreeTableCountsAsync(branchIds, cancellationToken);
        var openNow = await OpenNowAsync(branchIds, cancellationToken);

        return
        [
            .. venues
                .Select(v => new PublicVenueCard(
                    v.Slug,
                    v.Name,
                    v.Type,
                    [
                        .. v.Branches.Select(b => new PublicBranchCard(
                            b.Id,
                            b.Slug,
                            b.Name,
                            b.Address,
                            free.GetValueOrDefault(b.Id),
                            openNow.Contains(b.Id),

                            // Already in the cached estate, and dropped here until the diner app
                            // needed it to put a slot on the branch's wall clock.
                            b.TimeZoneId)),
                    ]))

                // A venue whose every branch is switched off is not a venue anybody can visit.
                .Where(v => v.Branches.Count > 0),
        ];
    }

    public async Task<PublicBranchPage> GetBranchAsync(
        string venueSlug,
        string branchSlug,
        CancellationToken cancellationToken = default)
    {
        var key = $"public:branch:{venueSlug}/{branchSlug}";

        // The room, the hours and the address. Everything on this half is stable.
        var plan = await cache.GetOrCreateAsync(
            key,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = StableFor;

                return await LoadBranchPlanAsync(venueSlug, branchSlug, cancellationToken);
            });

        if (plan is null)
        {
            // Nothing to distinguish here between "no such venue", "no such branch", "wrong pairing"
            // and "suspended". A public page that answered differently for a suspended venue would
            // be publishing a customer's billing status to anybody who guessed the slug.
            throw new KeyNotFoundException($"No branch is published at {venueSlug}/{branchSlug}.");
        }

        // Checked again, live, against the branch the cached plan resolved to. The plan itself was
        // only built for a published branch, but it outlives the moment it was built by minutes -
        // and a suspension inside those minutes has to take effect now.
        var live = await LiveBranchAsync(plan.BranchId, cancellationToken);

        var free = await FreeTableCountsAsync([plan.BranchId], cancellationToken);
        var occupied = await OccupiedTableLabelsAsync(plan.BranchId, cancellationToken);
        var openNow = await OpenNowAsync([plan.BranchId], cancellationToken);

        return plan.Page with
        {
            FreeTableCount = free.GetValueOrDefault(plan.BranchId),
            IsOpenNow = openNow.Contains(plan.BranchId),

            // Stamped from the server clock, beside the reads it describes. This is what lets the
            // page say "4 tables free, as of a minute ago" instead of implying it is live - which
            // matters because the count is cached for seconds and the link is shared for days.
            AsOfUtc = clock.UtcNow,

            // Read live, not from the cached plan. A venue that switches bookings off must not
            // have its page go on offering the button - see LiveBranchAsync.
            PhoneE164 = live.PhoneE164,
            AcceptsWebBookings = live.AcceptsWebBookings,
            FloorPlan = plan.Page.FloorPlan with
            {
                Tables =
                [
                    .. plan.Page.FloorPlan.Tables.Select(t => t with { IsFree = !occupied.Contains(t.Label) }),
                ],
            },
        };
    }

    /// <summary>
    /// The menu, straight through the diner's own read model.
    /// </summary>
    /// <remarks>
    /// Not a second implementation. <c>MenuQuery</c> already applies the venue gate and already
    /// drops incomplete items - an item with no photo or no allergens must never appear on a public
    /// page any more than it may reach the app - and reimplementing either rule here is how the two
    /// would come to disagree.
    /// </remarks>
    public async Task<BranchMenuView> GetMenuAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await RequirePublicBranchAsync(branchId, cancellationToken);

        return await cache.GetOrCreateAsync(
            $"public:menu:{branchId}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = StableFor;

                return await menu.GetBranchMenuAsync(branchId, cancellationToken);
            })
            ?? throw new KeyNotFoundException($"Branch {branchId} has no published menu.");
    }

    public async Task<PublicBranchMeta> GetMetaAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await RequirePublicBranchAsync(branchId, cancellationToken);

        return await cache.GetOrCreateAsync(
            $"public:meta:{branchId}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = StableFor;

                var row = await db.Branches
                    .AsNoTracking()
                    .Where(b => b.Id == branchId)
                    .Select(b => new
                    {
                        b.Name,
                        b.Slug,
                        b.Address,
                        VenueName = b.Venue.Name,
                        VenueSlug = b.Venue.Slug,
                        b.Venue.Type,
                        b.CoverPhotoId,
                        CoverIsExternal = (bool?)b.CoverPhoto!.IsExternallyHosted,
                        CoverCardPath = b.CoverPhoto!.CardPath,
                    })
                    .FirstAsync(cancellationToken);

                return new PublicBranchMeta(
                    Title: $"{row.VenueName} — {row.Name}",

                    // The venue description, not the free-table count. A card is fetched once by
                    // whichever chat app saw the link and cached for hours, so a live number would
                    // be frozen at whatever it was when somebody first pasted it.
                    Description: $"{row.Type} in Yerevan. {row.Address}",
                    ImageUrl: row.CoverPhotoId is { } photoId
                        ? (row.CoverIsExternal == true
                            ? row.CoverCardPath
                            : $"{PhotoView.RouteTemplate}/{photoId}/card")
                        : null,
                    CanonicalPath: $"/{row.VenueSlug}/{row.Slug}",
                    Locale: "hy_AM");
            })
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");
    }

    /// <summary>
    /// Refuses a branch that may not be addressed publicly.
    /// </summary>
    /// <remarks>
    /// <b>Never cached, deliberately.</b> Suspension is what happens when a venue stops paying, and
    /// a gate held for five minutes is a lever that takes five minutes to pull - so a venue
    /// suspended at noon would keep serving its public page, its menu and its table counts until
    /// twelve past. It is one indexed boolean over a row the request is about to read anyway; the
    /// payload behind it is the expensive part and is what the cache is for.
    /// </remarks>
    public async Task RequirePublicBranchAsync(Guid branchId, CancellationToken cancellationToken = default) =>
        await LiveBranchAsync(branchId, cancellationToken);

    /// <summary>
    /// The published check, and the settings that must never be served stale, in one read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>AcceptsWebBookings</c> cannot live in the cached half.</b> It gates the page's whole
    /// booking UI, and the rule it mirrors is enforced live in the reservation service - so a
    /// cached copy means a venue switches bookings off and the page goes on offering a button that
    /// is now refused for minutes afterwards. The phone number rides along because it is on the
    /// same row and a manager who corrects a wrong number expects to see it corrected.
    /// </para>
    /// <para>
    /// It costs nothing: the published check was already a query against this row, and this reads
    /// two more columns from it instead of a <c>bool</c>.
    /// </para>
    /// </remarks>
    private async Task<LiveBranch> LiveBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var live = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId
                        && b.IsActive
                        && b.Venue.IsActive
                        && b.Venue.SuspendedAtUtc == null
                        && b.Venue.DeletedAtUtc == null)
            .Select(b => new LiveBranch(b.PhoneE164, b.AcceptsWebBookings))
            .FirstOrDefaultAsync(cancellationToken);

        return live ?? throw new KeyNotFoundException($"Branch {branchId} is not published.");
    }

    /// <summary>The half of the branch page that is read per request rather than cached.</summary>
    private sealed record LiveBranch(string? PhoneE164, bool AcceptsWebBookings);

    /// <summary>
    /// Every branch that may be addressed publicly right now. Cached for seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what drops a suspended venue off the browse list. It was read per request, and the
    /// browse list is now the diner app's Explore as well as the web chooser - every open and every
    /// pull-to-refresh in the city paid for it. Held for <see cref="LiveFor"/>, the same bound the
    /// free-table count already lives with: a suspension leaves the list within fifteen seconds.
    /// </para>
    /// <para>
    /// The branch routes do not use it. Each checks its one branch live in
    /// <see cref="LiveBranchAsync"/>, because there a stale answer would serve a whole page, menu and
    /// booking button included, for a venue that has stopped trading.
    /// </para>
    /// </remarks>
    private async Task<HashSet<Guid>> PublishedBranchIdsAsync(CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            "public:published",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = LiveFor;

                var ids = await db.Branches
                    .AsNoTracking()
                    .Where(b => b.IsActive
                                && b.Venue.IsActive
                                && b.Venue.SuspendedAtUtc == null
                                && b.Venue.DeletedAtUtc == null)
                    .Select(b => b.Id)
                    .ToListAsync(cancellationToken);

                return ids.ToHashSet();
            })
        ?? [];

    // ------------------------------------------------------------ the pieces

    private sealed record BranchPlan(Guid BranchId, PublicBranchPage Page);

    private async Task<BranchPlan?> LoadBranchPlanAsync(
        string venueSlug,
        string branchSlug,
        CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Slug == branchSlug
                        && b.Venue.Slug == venueSlug
                        && b.IsActive
                        && b.Venue.IsActive
                        && b.Venue.SuspendedAtUtc == null
                        && b.Venue.DeletedAtUtc == null)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Slug,
                b.Address,
                b.Latitude,
                b.Longitude,
                b.TimeZoneId,
                b.FloorWidth,
                b.FloorHeight,
                VenueName = b.Venue.Name,
                VenueSlug = b.Venue.Slug,
                b.Venue.Type,
                b.ReservationPolicy.BookingWindowDays,
                b.ReservationPolicy.TurnTimeMinutes,
                b.ReservationPolicy.MinLeadMinutes,
                b.ReservationPolicy.CancellationDeadlineMinutes,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (branch is null)
        {
            return null;
        }

        var hours = await db.OpeningHours
            .AsNoTracking()
            .Where(h => h.BranchId == branch.Id)
            .OrderBy(h => h.Day)
            .ThenBy(h => h.OpensAt)
            .Select(h => new OpeningHoursView(h.Day, h.OpensAt, h.ClosesAt, h.ClosesNextDay))
            .ToListAsync(cancellationToken);

        var areas = await db.FloorAreas
            .AsNoTracking()
            .Where(a => a.BranchId == branch.Id)
            .OrderBy(a => a.DisplayOrder)
            .Select(a => new FloorAreaView(a.Id, a.Name, a.DisplayOrder))
            .ToListAsync(cancellationToken);

        // Inactive tables are excluded, not flagged. The admin plan includes them so an editor can
        // show a table somebody just tried to delete; a diner has no use for a table that is not in
        // the room, and no business knowing one used to be.
        var tables = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.BranchId == branch.Id && t.IsActive)
            .OrderBy(t => t.Label)
            .Select(t => new PublicFloorTable(
                t.Label,
                t.Seats,
                t.X,
                t.Y,
                t.Width,
                t.Height,
                t.RotationDegrees,
                t.Shape,
                t.FloorArea!.Name,
                t.IsBookable,

                // IsFree, placeholder. Stitched on by the caller from a live read: the cached copy
                // of this page must not carry a table's occupancy, or a shared link would freeze
                // the room for five minutes. Positional because an expression tree cannot carry a
                // named argument.
                true))
            .ToListAsync(cancellationToken);

        var page = new PublicBranchPage(
            branch.VenueSlug,
            branch.Slug,
            branch.Id,
            branch.VenueName,
            branch.Name,
            branch.Type,
            branch.Address,
            branch.Latitude,
            branch.Longitude,
            branch.TimeZoneId,
            hours,
            IsOpenNow: false,
            FreeTableCount: 0,

            // Every table the plan draws, bookable or not: the set the free count counts, so the
            // page's "x of y" is out of the room it shows.
            TableCount: tables.Count,
            new PublicFloorPlan(branch.FloorWidth, branch.FloorHeight, areas, tables),

            // Placeholders. Both are read live by the caller and deliberately absent from the
            // cached plan, so a stale copy of either cannot be served even by accident.
            PhoneE164: null,
            AcceptsWebBookings: false,
            BookingWindowDays: branch.BookingWindowDays,
            Policy: new PublicReservationPolicy(
                branch.TurnTimeMinutes,
                branch.MinLeadMinutes,
                branch.CancellationDeadlineMinutes),

            // Placeholder. Stamped by the caller, for the same reason as the free-table count: a
            // cached "as of" would tell the diner the count is fresh when it is minutes old.
            AsOfUtc: default);

        return new BranchPlan(branch.Id, page);
    }

    /// <summary>
    /// How many active tables have nobody at them, per branch. Cached for seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from <c>DiningTable.Status</c> rather than by counting open sessions: the status is the
    /// denormalisation the whole floor already runs on, and a second way of deciding whether a table
    /// is occupied is a second answer waiting to disagree with the first.
    /// </para>
    /// <para>
    /// <b>Every active table, bookable or not - and the page's <c>TableCount</c> counts the same.</b>
    /// The denominator used to count bookable tables only, so a bar of empty walk-in stools read
    /// "18 of 10 free". Narrowing this count to bookable tables made the two agree, but then a room
    /// with its tables taken and its stools empty read "no tables free" beside a stool the plan
    /// draws free - to a walk-in, who is who this number is for. So the denominator widened
    /// instead, and the count is exactly the tables <see cref="OccupiedTableLabelsAsync"/> leaves
    /// free on the plan.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<Guid, int>> FreeTableCountsAsync(
        IReadOnlyList<Guid> branchIds,
        CancellationToken cancellationToken)
    {
        if (branchIds.Count == 0)
        {
            return [];
        }

        var key = $"public:free:{string.Join(',', branchIds.Order())}";

        return await cache.GetOrCreateAsync(
            key,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = LiveFor;

                return await db.DiningTables
                    .AsNoTracking()
                    .Where(t => branchIds.Contains(t.BranchId) && t.IsActive && t.Status == TableStatus.Free)
                    .GroupBy(t => t.BranchId)
                    .Select(g => new { BranchId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(g => g.BranchId, g => g.Count, cancellationToken);
            })
            ?? [];
    }

    /// <summary>The labels of tables somebody is sitting at right now.</summary>
    private async Task<HashSet<string>> OccupiedTableLabelsAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var labels = await cache.GetOrCreateAsync(
            $"public:occupied:{branchId}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = LiveFor;

                return await db.DiningTables
                    .AsNoTracking()
                    .Where(t => t.BranchId == branchId && t.IsActive && t.Status != TableStatus.Free)
                    .Select(t => t.Label)
                    .ToListAsync(cancellationToken);
            })
            ?? [];

        return [.. labels];
    }

    /// <summary>
    /// Which branches are inside an opening block at this moment, decided in each one's own zone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hours are wall-clock values and are stored as such, so this converts <i>now</i> into the
    /// branch's local time rather than converting the hours into UTC - which would silently shift a
    /// venue's opening by an hour across a daylight-saving change.
    /// </para>
    /// <para>
    /// Cached for <see cref="LiveFor"/>, like the free count beside it. It reads every opening block
    /// of every branch on the list, and the list is what the diner app's Explore loads on every open
    /// and every pull-to-refresh. A branch that opened in the last fifteen seconds may read closed
    /// for that long.
    /// </para>
    /// </remarks>
    private async Task<HashSet<Guid>> OpenNowAsync(
        IReadOnlyList<Guid> branchIds,
        CancellationToken cancellationToken)
    {
        if (branchIds.Count == 0)
        {
            return [];
        }

        return await cache.GetOrCreateAsync(
            $"public:open:{string.Join(',', branchIds.Order())}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = LiveFor;

                return await ComputeOpenNowAsync(branchIds, cancellationToken);
            })
            ?? [];
    }

    private async Task<HashSet<Guid>> ComputeOpenNowAsync(
        IReadOnlyList<Guid> branchIds,
        CancellationToken cancellationToken)
    {
        var rows = await db.OpeningHours
            .AsNoTracking()
            .Where(h => branchIds.Contains(h.BranchId))
            .Select(h => new
            {
                h.BranchId,
                h.Branch.TimeZoneId,
                h.Day,
                h.OpensAt,
                h.ClosesAt,
                h.ClosesNextDay,
            })
            .ToListAsync(cancellationToken);

        var nowUtc = clock.UtcNow;
        var open = new HashSet<Guid>();

        foreach (var group in rows.GroupBy(r => (r.BranchId, r.TimeZoneId)))
        {
            var local = BranchTime.ToLocal(nowUtc, group.Key.TimeZoneId);
            var today = TimeOnly.FromDateTime(local);
            var yesterdayDay = local.DayOfWeek == DayOfWeek.Sunday ? DayOfWeek.Saturday : local.DayOfWeek - 1;

            var isOpen = group.Any(h =>
                (h.Day == local.DayOfWeek && !h.ClosesNextDay && today >= h.OpensAt && today < h.ClosesAt)
                || (h.Day == local.DayOfWeek && h.ClosesNextDay && today >= h.OpensAt)

                // A block that ran past midnight is still the previous day's block until it closes.
                || (h.Day == yesterdayDay && h.ClosesNextDay && today < h.ClosesAt));

            if (isOpen)
            {
                open.Add(group.Key.BranchId);
            }
        }

        return open;
    }
}
