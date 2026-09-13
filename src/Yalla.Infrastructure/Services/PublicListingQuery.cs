using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Media;
using Yalla.Application.Public;
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
                        || Contains(b.Address, needle))
            .Select(b => ToListing(b, stats, request.Latitude, request.Longitude));

        return request.Latitude is not null
            ? [.. matches.OrderBy(l => l.DistanceKm).ThenBy(l => l.VenueName, StringComparer.OrdinalIgnoreCase)]
            : [.. matches
                .OrderByDescending(l => l.Rating ?? -1d)
                .ThenByDescending(l => l.ReviewCount)
                .ThenBy(l => l.VenueName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.BranchName, StringComparer.OrdinalIgnoreCase)];
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
            .Select(b => new { b.About, b.WebsiteUrl, b.PhoneE164, b.AmenityKeys, b.AcceptsWebBookings })
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

        var recent = await ReviewsAsync(branchId, skip: 0, take: RecentReviewCount, cancellationToken);
        var markers = await MarkersAsync(branchId, cancellationToken);
        var stats = await StatsAsync(cancellationToken);

        return new PublicBranchDetail(
            ToListing(row, stats, latitude, longitude),
            extra.About,
            extra.WebsiteUrl,
            extra.PhoneE164,
            string.IsNullOrEmpty(extra.AmenityKeys) ? [] : extra.AmenityKeys.Split(',', StringSplitOptions.RemoveEmptyEntries),
            hours,
            gallery,
            tableCount,
            extra.AcceptsWebBookings,
            recent,
            markers,
            clock.UtcNow);
    }

    public async Task<PublicReviewPage> GetReviewsAsync(Guid branchId, int page, CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Pages start at 1.");
        }

        await RequirePublishedAsync(branchId, cancellationToken);

        // Live, not the cached aggregate: this is the screen somebody opens right after reviewing.
        var aggregate = await db.BranchReviews
            .AsNoTracking()
            .Where(r => r.BranchId == branchId)
            .GroupBy(r => r.BranchId)
            .Select(g => new { Count = g.Count(), Sum = g.Sum(r => (long)r.Rating) })
            .FirstOrDefaultAsync(cancellationToken);

        var count = aggregate?.Count ?? 0;

        return new PublicReviewPage(
            branchId,
            BranchBadgeRules.AverageRating(count, aggregate?.Sum ?? 0L),
            count,
            page,
            ReviewPageSize,
            await ReviewsAsync(branchId, (page - 1) * ReviewPageSize, ReviewPageSize, cancellationToken));
    }

    public async Task<PublicTableMarkers> GetTableMarkersAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var row = await Project(Published().Where(b => b.Id == branchId)).FirstOrDefaultAsync(cancellationToken)
                  ?? throw new KeyNotFoundException($"Branch {branchId} is not published.");

        return new PublicTableMarkers(
            branchId,
            Cover(row),
            clock.UtcNow,
            await MarkersAsync(branchId, cancellationToken));
    }

    // ------------------------------------------------------------ the pieces

    private IQueryable<Branch> Published() =>
        db.Branches
            .AsNoTracking()
            .Where(b => b.IsActive
                        && b.Venue.IsActive
                        && b.Venue.SuspendedAtUtc == null
                        && b.Venue.DeletedAtUtc == null);

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

                var reviews = await db.BranchReviews
                    .AsNoTracking()
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

    private static PublicBranchListing ToListing(EstateRow row, LiveStats stats, double? latitude, double? longitude)
    {
        var (count, sum) = stats.Reviews.GetValueOrDefault(row.BranchId);
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
            Cover(row));
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

    private async Task<IReadOnlyList<PublicReviewView>> ReviewsAsync(
        Guid branchId, int skip, int take, CancellationToken cancellationToken)
    {
        var rows = await db.BranchReviews
            .AsNoTracking()
            .Where(r => r.BranchId == branchId)
            .OrderByDescending(r => r.UpdatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip(skip)
            .Take(take)
            .Select(r => new { r.Id, r.DinerUser.DisplayName, r.Rating, r.Text, r.CreatedAtUtc, r.UpdatedAtUtc })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(r => new PublicReviewView(
                r.Id, BranchReview.PublicAuthorName(r.DisplayName), r.Rating, r.Text, r.CreatedAtUtc, r.UpdatedAtUtc)),
        ];
    }

    /// <summary>
    /// Tables placed on the cover photo, each with the state the floor plan derives now.
    /// </summary>
    /// <remarks>
    /// Physical status plus the next live booking, through <see cref="TableStateProjection.Derive"/> -
    /// the same rule the staff floor and availability use, so a marker cannot read free while the
    /// floor reads reserved.
    /// </remarks>
    private async Task<IReadOnlyList<PublicTableMarker>> MarkersAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        var bufferMinutes = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => b.ReservationPolicy.BufferMinutes)
            .FirstAsync(cancellationToken);

        var tables = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.IsActive && t.PhotoX != null && t.PhotoY != null)
            .OrderBy(t => t.Label)
            .Select(t => new
            {
                t.Id,
                t.Label,
                t.Seats,
                t.IsBookable,
                t.Status,
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

        return
        [
            .. tables.Select(t => new PublicTableMarker(
                t.Id,
                t.Label,
                t.Seats,
                t.IsBookable,
                TableStateProjection.Derive(t.Status, t.NextStartUtc, nowUtc, bufferMinutes),
                t.PhotoX!.Value,
                t.PhotoY!.Value)),
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
    }
}
