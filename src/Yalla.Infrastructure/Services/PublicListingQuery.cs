using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Application.Public;
using Yalla.Application.Reviews;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The diner app's browse reads: Explore, search, the map, a place's details, its reviews and the
/// tables drawn on its photo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything shown is stored or derived from what is stored.</b> Rating and review count are the
/// reviews table, badges are <see cref="BranchBadgeRules"/> over sittings and reviews, open-now is the
/// opening hours through <see cref="BranchOpenNow"/>, and the free count is the table status cache.
/// Distance is geometry over the branch's coordinates and the position the caller sent.
/// </para>
/// <para>
/// <b>A hidden review is not there (K8).</b> Moderation leaves the row in place; every read here - the
/// list, the recent three, the rating, the count and so the badges - filters it out.
/// </para>
/// <para>
/// <b>Cached for <see cref="PublicVenueQuery.LiveFor"/> as a whole</b> - the estate and the live
/// numbers together - because the list is what every Explore open and pull-to-refresh loads. The id
/// routes read their one branch live, so a suspended venue is a 404 there at once.
/// </para>
/// </remarks>
internal sealed class PublicListingQuery(
    YallaDbContext db,
    IMemoryCache cache,
    IClock clock) : IPublicListingQuery
{
    public const int ReviewPageSize = 20;

    /// <summary>The last page whose offset still fits an int. Anything later is refused, not overflowed.</summary>
    public const int MaxReviewPage = int.MaxValue / ReviewPageSize;

    public const int RecentReviewCount = 3;

    public const int MaxQueryLength = 100;

    public async Task<IReadOnlyList<PublicBranchListing>> SearchAsync(
        BranchSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        CheckPosition(request.Latitude, request.Longitude);

        var needle = request.Query?.Trim() ?? string.Empty;

        if (needle.Length > MaxQueryLength)
        {
            throw new ArgumentException($"A search is at most {MaxQueryLength} characters.", "q");
        }

        var estate = await EstateAsync(cancellationToken);
        var stats = await StatsAsync(cancellationToken);

        var matches = estate
            .Where(b => request.Category is null || b.VenueType == request.Category)
            .Where(b => needle.Length == 0
                        || Contains(b.VenueName, needle)
                        || Contains(b.BranchName, needle)
                        || Contains(b.Cuisine, needle)
                        || Contains(b.Address, needle)
                        || Contains(b.District, needle))
            .Select(b => ToListing(b, stats, request.Latitude, request.Longitude));

        return request.Latitude is not null
            ? [.. matches.OrderBy(l => l.DistanceKm).ThenBy(l => l.VenueName, StringComparer.OrdinalIgnoreCase)]
            : [.. matches
                .OrderByDescending(l => l.Rating ?? -1d)
                .ThenByDescending(l => l.ReviewCount)
                .ThenBy(l => l.VenueName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.BranchName, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<PublicBranchListing>> GetListingsAsync(
        IReadOnlyCollection<Guid> branchIds,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branchIds);

        CheckPosition(latitude, longitude);

        if (branchIds.Count == 0)
        {
            return [];
        }

        var ids = branchIds.Distinct().ToList();

        // The rows live, not from the cached estate: a place hearted a moment after it was published is
        // on the list at once. The numbers are the cached ones, the same as the browse list shows.
        var rows = await Project(Published().Where(b => ids.Contains(b.Id))).ToListAsync(cancellationToken);
        var stats = await StatsAsync(cancellationToken);

        return [.. rows.Select(row => ToListing(row, stats, latitude, longitude))];
    }

    public async Task<PublicBranchDetail> GetDetailAsync(
        Guid branchId,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default)
    {
        CheckPosition(latitude, longitude);

        // Live, not from the cached estate: this is the published check for the id route.
        var row = await Project(Published().Where(b => b.Id == branchId)).FirstOrDefaultAsync(cancellationToken)
                  ?? throw new KeyNotFoundException($"Branch {branchId} is not published.");

        var extra = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new
            {
                b.About,
                b.WebsiteUrl,
                b.PhoneE164,
                b.AmenityKeys,
                b.TakeawayPrepMinutes,
                b.ReservationPolicy.BookingMode,
            })
            .FirstAsync(cancellationToken);

        var hours = await db.OpeningHours
            .AsNoTracking()
            .Where(h => h.BranchId == branchId)
            .OrderBy(h => h.Day)
            .ThenBy(h => h.OpensAt)
            .Select(h => new OpeningHoursView(h.Day, h.OpensAt, h.ClosesAt, h.ClosesNextDay))
            .ToListAsync(cancellationToken);

        var gallery = await GalleryAsync(branchId, cancellationToken);

        var tableCount = await db.DiningTables
            .AsNoTracking()
            .CountAsync(t => t.BranchId == branchId && t.IsActive, cancellationToken);

        // Reviews blocked by the platform are hidden, not refused (docs/branch-features.md): no recent reviews, no
        // rating, no counts - the rows stay and show again when the platform allows reviews again.
        var reviewsShown = BranchFeatures.Has(row.FeatureBits, BranchFeature.Reviews);

        var recent = reviewsShown
            ? await ReviewsAsync(branchId, skip: 0, take: RecentReviewCount, cancellationToken)
            : [];
        var rooms = await RoomsAsync(branchId, cancellationToken);
        var stats = await StatsAsync(cancellationToken);

        // The aggregate live, not from the cached stats: recentReviews above is live, and the two
        // side by side must agree - this is the screen a diner comes back to right after reviewing.
        var summary = reviewsShown ? await ReviewSummaryAsync(branchId, cancellationToken) : NoReviews;

        // The effective set, read live with the row: allowed by the platform and switched on by the venue. The
        // booleans below are the same answers, kept for the screens that read them (K9 included: the app books only
        // where bookings are switched on AND somebody at the venue has saved the reservation policy).
        var features = row.EffectiveFeatures;

        return new PublicBranchDetail(
            ToListing(row, stats, latitude, longitude, (summary.Count, summary.Sum)),
            extra.About,
            extra.WebsiteUrl,
            extra.PhoneE164,
            string.IsNullOrEmpty(extra.AmenityKeys) ? [] : extra.AmenityKeys.Split(',', StringSplitOptions.RemoveEmptyEntries),
            hours,
            await OpeningCalendars.UpcomingAsync(db, branchId, row.TimeZoneId, clock.UtcNow, cancellationToken),
            gallery,
            tableCount,
            features.Contains(BranchFeature.WebBookings),
            features.Contains(BranchFeature.AppBookings),
            recent,
            summary.RatingCounts,
            rooms,
            new PublicTakeawayInfo(features.Contains(BranchFeature.Takeaway), extra.TakeawayPrepMinutes),
            clock.UtcNow,
            extra.BookingMode);
    }

    public async Task<PublicReviewPage> GetReviewsAsync(Guid branchId, int page, CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Pages start at 1.");
        }

        // Past this, (page - 1) * ReviewPageSize overflows int into a negative OFFSET, which SQL
        // Server refuses with an error the caller would see as a 500.
        if (page > MaxReviewPage)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, $"Pages run to at most {MaxReviewPage}.");
        }

        await RequirePublishedAsync(branchId, cancellationToken);

        // Reviews blocked by the platform: an empty page with no rating, not an error (docs/branch-features.md).
        var reviewsShown = await BranchFeatureGate.AllowsAsync(db, branchId, BranchFeature.Reviews, cancellationToken);

        // Live, not the cached aggregate: this is the screen somebody opens right after reviewing.
        var summary = reviewsShown ? await ReviewSummaryAsync(branchId, cancellationToken) : NoReviews;

        return new PublicReviewPage(
            branchId,
            BranchBadgeRules.AverageRating(summary.Count, summary.Sum),
            summary.Count,
            summary.RatingCounts,
            page,
            ReviewPageSize,
            reviewsShown
                ? await ReviewsAsync(branchId, (page - 1) * ReviewPageSize, ReviewPageSize, cancellationToken)
                : []);
    }

    public async Task<PublicBranchRooms> GetRoomsAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await RequirePublishedAsync(branchId, cancellationToken);

        return new PublicBranchRooms(branchId, clock.UtcNow, await RoomsAsync(branchId, cancellationToken));
    }

    // ------------------------------------------------------------ the pieces

    // Published and taking guests: see DinerVisibility.
    private IQueryable<Branch> Published() => DinerVisibility.Branches(db);

    /// <summary>The reviews the public may see: every one that moderation has not taken down.</summary>
    private IQueryable<BranchReview> PublishedReviews() =>
        db.BranchReviews
            .AsNoTracking()
            .Where(r => r.HiddenAtUtc == null);

    private async Task RequirePublishedAsync(Guid branchId, CancellationToken cancellationToken)
    {
        if (!await Published().AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw new KeyNotFoundException($"Branch {branchId} is not published.");
        }
    }

    /// <summary>The stable columns of a branch, one query, the cover as flat columns.</summary>
    private static IQueryable<EstateRow> Project(IQueryable<Branch> branches) =>
        branches.Select(b => new EstateRow
        {
            BranchId = b.Id,
            VenueId = b.VenueId,
            VenueSlug = b.Venue.Slug,
            BranchSlug = b.Slug,
            VenueName = b.Venue.Name,
            BranchName = b.Name,
            VenueType = b.Venue.Type,
            Cuisine = b.Cuisine,
            PriceLevel = b.PriceLevel,
            Address = b.Address,
            District = b.District,
            Latitude = b.Latitude,
            Longitude = b.Longitude,
            TimeZoneId = b.TimeZoneId,
            CreatedAtUtc = b.CreatedAtUtc,
            CoverPhotoId = b.CoverPhotoId,
            CoverIsExternal = (bool?)b.CoverPhoto!.IsExternallyHosted,
            CoverThumbnailPath = b.CoverPhoto!.ThumbnailPath,
            CoverCardPath = b.CoverPhoto!.CardPath,
            CoverFullPath = b.CoverPhoto!.FullPath,
            CoverWidth = b.CoverPhoto!.Width,
            CoverHeight = b.CoverPhoto!.Height,
            FeatureBits = b.AllowedFeatureBits,
            AcceptsWebBookings = b.AcceptsWebBookings,
            ReservationPolicyReviewed = b.ReservationPolicyReviewedAtUtc != null,
            AcceptsPreOrders = b.AcceptsPreOrders,
            TakeawayEnabled = b.TakeawayEnabled,
            GuestsCanMessage = b.GuestsCanMessage,
        });

    private async Task<List<EstateRow>> EstateAsync(CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            "listing:estate",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = PublicVenueQuery.LiveFor;

                return await Project(Published().OrderBy(b => b.Venue.Name).ThenBy(b => b.Name))
                    .ToListAsync(cancellationToken);
            })
        ?? [];

    private async Task<LiveStats> StatsAsync(CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            "listing:stats",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = PublicVenueQuery.LiveFor;

                var nowUtc = clock.UtcNow;
                var windowStartUtc = nowUtc.AddDays(-BranchBadgeRules.PopularWindowDays);

                var free = await db.DiningTables
                    .AsNoTracking()
                    .Where(t => t.IsActive && t.Status == TableStatus.Free)
                    .GroupBy(t => t.BranchId)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

                var reviews = await PublishedReviews()
                    .GroupBy(r => r.BranchId)
                    .Select(g => new { g.Key, Count = g.Count(), Sum = g.Sum(r => (long)r.Rating) })
                    .ToDictionaryAsync(g => g.Key, g => (g.Count, g.Sum), cancellationToken);

                // Every party seated in the window, walk-in or booked: the room actually filling.
                var sittings = await db.TableSessions
                    .AsNoTracking()
                    .Where(s => s.SeatedAtUtc >= windowStartUtc)
                    .GroupBy(s => s.BranchId)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

                var open = await BranchOpenNow.ComputeAsync(db, null, nowUtc, cancellationToken);

                return new LiveStats(nowUtc, open, free, reviews, sittings);
            })
        ?? throw new InvalidOperationException("The listing statistics could not be read.");

    /// <summary>
    /// One branch's published reviews summed up, read live: the count, the rating sum and how many of
    /// each star - one grouped query, five rows at most.
    /// </summary>
    /// <remarks>
    /// The count and sum are added up from the per-star rows rather than read separately, so the bars a
    /// page draws from <c>ratingCounts</c> always add up to the <c>reviewCount</c> beside them, and the
    /// average is over the same reviews.
    /// </remarks>
    private async Task<ReviewSummary> ReviewSummaryAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var perStar = await PublishedReviews()
            .Where(r => r.BranchId == branchId)
            .GroupBy(r => r.Rating)
            .Select(g => new { Stars = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var counts = new int[BranchReview.MaxRating - BranchReview.MinRating + 1];

        foreach (var star in perStar)
        {
            counts[star.Stars - BranchReview.MinRating] = star.Count;
        }

        return new ReviewSummary(
            perStar.Sum(s => s.Count),
            perStar.Sum(s => (long)s.Stars * s.Count),
            counts);
    }

    /// <summary>A branch's shown reviews: how many, their stars added up, and how many of each star, one first.</summary>
    private sealed record ReviewSummary(int Count, long Sum, IReadOnlyList<int> RatingCounts);

    /// <summary>What a branch that shows no reviews sums to: nothing, with a zero for every star.</summary>
    private static readonly ReviewSummary NoReviews =
        new(0, 0L, new int[BranchReview.MaxRating - BranchReview.MinRating + 1]);

    /// <param name="row">The branch.</param>
    /// <param name="stats">The cached live numbers.</param>
    /// <param name="latitude">The caller's position, for distance.</param>
    /// <param name="longitude">The caller's position, for distance.</param>
    /// <param name="liveReviews">
    /// This branch's review aggregate read live, used instead of the cached one - the details route,
    /// where the card sits beside reviews that are read live. Null on the list routes.
    /// </param>
    private static PublicBranchListing ToListing(
        EstateRow row,
        LiveStats stats,
        double? latitude,
        double? longitude,
        (int Count, long Sum)? liveReviews = null)
    {
        // Reviews blocked by the platform count as none: no rating, no count, and no badge earned by them
        // (docs/branch-features.md). "Popular" by sittings alone still shows.
        var (count, sum) = !BranchFeatures.Has(row.FeatureBits, BranchFeature.Reviews)
            ? (0, 0L)
            : liveReviews ?? stats.Reviews.GetValueOrDefault(row.BranchId);
        var rating = BranchBadgeRules.AverageRating(count, sum);

        return new PublicBranchListing(
            row.BranchId,
            row.VenueId,
            row.VenueSlug,
            row.BranchSlug,
            row.VenueName,
            row.BranchName,
            row.VenueType,
            row.Cuisine,
            row.PriceLevel,
            row.Address,
            row.Latitude,
            row.Longitude,
            latitude is { } lat && longitude is { } lng
                ? BranchListingRules.DistanceKm(lat, lng, row.Latitude, row.Longitude)
                : null,
            row.TimeZoneId,
            stats.Open.Contains(row.BranchId),
            stats.Free.GetValueOrDefault(row.BranchId),
            rating,
            count,
            BranchBadgeRules.For(
                row.CreatedAtUtc,
                stats.AsOfUtc,
                stats.Sittings.GetValueOrDefault(row.BranchId),
                count,
                rating),
            Cover(row),
            row.EffectiveFeatures,
            row.District);
    }

    private static PhotoView? Cover(EstateRow row) =>
        row.CoverPhotoId is { } photoId
            ? PhotoView.From(
                photoId,
                row.CoverIsExternal == true,
                row.CoverThumbnailPath!,
                row.CoverCardPath!,
                row.CoverFullPath!,
                row.CoverWidth,
                row.CoverHeight)
            : null;

    private async Task<IReadOnlyList<PhotoView>> GalleryAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var photos = await db.BranchGalleryPhotos
            .AsNoTracking()
            .Where(g => g.BranchId == branchId)
            .OrderBy(g => g.Position)
            .Select(g => g.Photo)
            .ToListAsync(cancellationToken);

        return [.. photos.Select(PhotoView.From)];
    }

    /// <summary>
    /// A page of published reviews, newest <b>written</b> first (K8).
    /// </summary>
    /// <remarks>
    /// By <c>CreatedAtUtc</c>, not the last revision: ordering by revision let anybody lift an old
    /// review back to the top by re-saving it. A revision shows as <c>edited</c> instead.
    /// </remarks>
    private async Task<IReadOnlyList<PublicReviewView>> ReviewsAsync(
        Guid branchId, int skip, int take, CancellationToken cancellationToken)
    {
        var rows = await PublishedReviews()
            .Where(r => r.BranchId == branchId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip(skip)
            .Take(take)
            .Select(r => new
            {
                r.Id,
                r.DinerUser.DisplayName,
                r.Rating,
                r.Text,
                r.CreatedAtUtc,
                r.UpdatedAtUtc,
                r.ReplyText,
                r.ReplyAuthorName,
                r.ReplyAuthorRole,
                r.RepliedAtUtc,
                r.ReplyUpdatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(r => new PublicReviewView(
                r.Id,
                BranchReview.PublicAuthorName(r.DisplayName),
                r.Rating,
                r.Text,
                r.CreatedAtUtc,
                r.UpdatedAtUtc,
                Edited: r.UpdatedAtUtc != r.CreatedAtUtc,

                // The venue's answer under it. Only shown reviews reach this list, so a hidden one's
                // reply never does either.
                PublicReviewReplyView.From(
                    ReviewReplyView.From(r.ReplyText, r.ReplyAuthorName, r.ReplyAuthorRole, r.RepliedAtUtc, r.ReplyUpdatedAtUtc)))),
        ];
    }

    /// <summary>
    /// The rooms with their photos in order, and on each photo the active tables pinned there, each
    /// with the state the floor derives now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Physical status plus the next live booking, through <see cref="TableStateProjection.Derive"/> -
    /// the same rule the staff floor and availability use, so a pin cannot read free while the floor
    /// reads reserved.
    /// </para>
    /// <para>
    /// A room with no photo is left out: there is nothing to swipe and nowhere to tap. A table with no
    /// pin is left out too - a diner books by tapping a pin, so it is not offered. Three reads, none
    /// per room or per table.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<PublicRoom>> RoomsAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        var rooms = await db.FloorAreas
            .AsNoTracking()
            .Where(a => a.BranchId == branchId)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.Name)
            .Select(a => new { a.Id, a.Name, a.DisplayOrder })
            .ToListAsync(cancellationToken);

        var photos = await db.FloorAreaPhotos
            .AsNoTracking()
            .Where(p => p.FloorArea.BranchId == branchId)
            .OrderBy(p => p.Position)
            .Select(p => new { p.FloorAreaId, p.Photo })
            .ToListAsync(cancellationToken);

        var bufferMinutes = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => b.ReservationPolicy.BufferMinutes)
            .FirstAsync(cancellationToken);

        var tables = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.BranchId == branchId
                        && t.IsActive
                        && t.PhotoId != null
                        && t.PhotoX != null
                        && t.PhotoY != null

                        // On a photo of its own room. A pin left on another room's picture would
                        // draw the table in the wrong room; availability and the booking apply the
                        // same rule, so what is drawn here is what can be booked.
                        && db.FloorAreaPhotos.Any(p => p.FloorAreaId == t.FloorAreaId && p.PhotoId == t.PhotoId))
            .OrderBy(t => t.Label)
            .Select(t => new
            {
                t.Id,
                t.Label,
                t.Seats,
                t.IsBookable,
                t.Status,
                t.PhotoId,
                t.PhotoX,
                t.PhotoY,
                NextStartUtc = db.Reservations
                    .Where(r => r.DiningTableId == t.Id
                                && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.PendingApproval)
                                && r.EndUtc > nowUtc)
                    .OrderBy(r => r.StartUtc)
                    .Select(r => (DateTime?)r.StartUtc)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var pinsByPhoto = tables
            .GroupBy(t => t.PhotoId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PublicTableMarker>)
                [
                    .. g.Select(t => new PublicTableMarker(
                        t.Id,
                        t.Label,
                        t.Seats,
                        t.IsBookable,
                        TableStateProjection.Derive(t.Status, t.NextStartUtc, nowUtc, bufferMinutes),
                        t.PhotoX!.Value,
                        t.PhotoY!.Value)),
                ]);

        var photosByRoom = photos
            .GroupBy(p => p.FloorAreaId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PublicRoomPhoto>)
                [
                    .. g.Select(p => new PublicRoomPhoto(
                        PhotoView.From(p.Photo),
                        pinsByPhoto.GetValueOrDefault(p.Photo.Id, []))),
                ]);

        return
        [
            .. rooms
                .Where(r => photosByRoom.ContainsKey(r.Id))
                .Select(r => new PublicRoom(r.Id, r.Name, r.DisplayOrder, photosByRoom[r.Id])),
        ];
    }

    private static void CheckPosition(double? latitude, double? longitude)
    {
        if (latitude.HasValue != longitude.HasValue)
        {
            throw new ArgumentException("Send lat and lng together, or neither.", latitude.HasValue ? "lng" : "lat");
        }

        if (latitude is { } lat && (double.IsNaN(lat) || lat < -90d || lat > 90d))
        {
            throw new ArgumentOutOfRangeException("lat", lat, "Latitude is -90 to 90.");
        }

        if (longitude is { } lng && (double.IsNaN(lng) || lng < -180d || lng > 180d))
        {
            throw new ArgumentOutOfRangeException("lng", lng, "Longitude is -180 to 180.");
        }
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private sealed record LiveStats(
        DateTime AsOfUtc,
        HashSet<Guid> Open,
        Dictionary<Guid, int> Free,
        Dictionary<Guid, (int Count, long Sum)> Reviews,
        Dictionary<Guid, int> Sittings);

    private sealed class EstateRow
    {
        public Guid BranchId { get; init; }

        public Guid VenueId { get; init; }

        public string VenueSlug { get; init; } = null!;

        public string BranchSlug { get; init; } = null!;

        public string VenueName { get; init; } = null!;

        public string BranchName { get; init; } = null!;

        public VenueType VenueType { get; init; }

        public string? Cuisine { get; init; }

        public int? PriceLevel { get; init; }

        public string Address { get; init; } = null!;

        public string? District { get; init; }

        public double Latitude { get; init; }

        public double Longitude { get; init; }

        public string TimeZoneId { get; init; } = null!;

        public DateTime CreatedAtUtc { get; init; }

        public Guid? CoverPhotoId { get; init; }

        public bool? CoverIsExternal { get; init; }

        public string? CoverThumbnailPath { get; init; }

        public string? CoverCardPath { get; init; }

        public string? CoverFullPath { get; init; }

        public int? CoverWidth { get; init; }

        public int? CoverHeight { get; init; }

        /// <summary>What the platform allows, one bit per feature.</summary>
        public int FeatureBits { get; init; }

        public bool AcceptsWebBookings { get; init; }

        public bool ReservationPolicyReviewed { get; init; }

        public bool AcceptsPreOrders { get; init; }

        public bool TakeawayEnabled { get; init; }

        public bool GuestsCanMessage { get; init; }

        /// <summary>What a diner sees as on: allowed, and switched on by the venue where it has a switch.</summary>
        public IReadOnlyList<BranchFeature> EffectiveFeatures => BranchFeatures.Effective(
            FeatureBits,
            new BranchFeatures.OwnerSwitches(
                AcceptsWebBookings, ReservationPolicyReviewed, AcceptsPreOrders, TakeawayEnabled, GuestsCanMessage));
    }
}
