using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Application.Public;
using Yalla.Domain;
using Yalla.Domain.Identity;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A diner's favourite places, kept on the account (K11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent both ways.</b> Hearting a hearted place and un-hearting one that is not are successes
/// that write nothing - the app toggles optimistically and may send the same tap twice.
/// </para>
/// <para>
/// <b>The limit is <see cref="DinerFavorite.MaxPerDiner"/>.</b> An add past it is refused, and a merge
/// that would cross it writes nothing at all rather than a first few hundred.
/// </para>
/// <para>
/// <b>The list is the public card.</b> Each entry carries <see cref="PublicBranchListing"/> as the browse
/// list serves it, and a place that is not published - inactive, or its venue suspended or deleted - is
/// left out while its row is kept.
/// </para>
/// </remarks>
internal sealed class DinerFavoriteService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPublicListingQuery listings,
    ILogger<DinerFavoriteService> logger) : IDinerFavoriteService
{
    public async Task<DinerFavoriteList> ListAsync(
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner();

        return await ListForAsync(dinerUserId, latitude, longitude, cancellationToken);
    }

    public async Task AddAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner();

        if (!await Published().AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw new KeyNotFoundException($"Branch {branchId} is not published.");
        }

        // Everything from here under the account's lock (DinerAccountLock). Two phones hearting at 499
        // queue on it, so the count below is still true at the insert and the limit holds; a deletion in
        // progress is waited for and then refused, rather than leaving a heart on the tombstone.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);

        // Already hearted: the success it would have been, before the limit is looked at, so a full
        // account can still re-send a heart it has. A double tap reads the first tap's committed row
        // here, because the lock made it wait for that commit.
        if (await db.DinerFavorites.AnyAsync(f => f.DinerUserId == dinerUserId && f.BranchId == branchId, cancellationToken))
        {
            return;
        }

        if (await db.DinerFavorites.CountAsync(f => f.DinerUserId == dinerUserId, cancellationToken) >= DinerFavorite.MaxPerDiner)
        {
            throw TooMany();
        }

        db.DinerFavorites.Add(new DinerFavorite(dinerUserId, branchId, clock.UtcNow));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner();

        // No published check: taking the heart off a place that has since closed has to work.
        await db.DinerFavorites
            .Where(f => f.DinerUserId == dinerUserId && f.BranchId == branchId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<DinerFavoriteList> MergeAsync(
        MergeFavoritesCommand command,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner();

        // Every refusal before anything is written.
        CheckPosition(latitude, longitude);

        if (command.BranchIds is null)
        {
            throw new FieldValidationException(
            [
                new FieldViolation("branchIds", "Send the places to add, or an empty list.", FieldBounds.Required),
            ]);
        }

        if (command.BranchIds.Count > DinerFavorite.MaxPerDiner)
        {
            throw new FieldValidationException(
            [
                new FieldViolation(
                    "branchIds",
                    $"At most {DinerFavorite.MaxPerDiner} places at once.",
                    FieldBounds.Max,
                    Max: DinerFavorite.MaxPerDiner,
                    Value: command.BranchIds.Count),
            ]);
        }

        var wanted = command.BranchIds.Where(id => id != Guid.Empty).Distinct().ToList();

        if (wanted.Count > 0)
        {
            await MergeUnderLockAsync(dinerUserId, wanted, cancellationToken);
        }

        return await ListForAsync(dinerUserId, latitude, longitude, cancellationToken);
    }

    /// <summary>
    /// Adds what is missing, under the account's lock: no heart from another phone can land between the
    /// read of what is kept and the insert, so neither the limit nor the unique index can be crossed by a
    /// race, and a deletion in progress is refused rather than outlived.
    /// </summary>
    private async Task MergeUnderLockAsync(Guid dinerUserId, List<Guid> wanted, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);

        var published = (await Published()
                .Where(b => wanted.Contains(b.Id))
                .Select(b => b.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var kept = (await db.DinerFavorites
                .AsNoTracking()
                .Where(f => f.DinerUserId == dinerUserId)
                .Select(f => f.BranchId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // Skipped, not refused: a heart made signed out weeks ago may be for a place that closed.
        var adding = wanted.Where(id => published.Contains(id) && !kept.Contains(id)).ToList();

        if (adding.Count == 0)
        {
            return;
        }

        if (kept.Count + adding.Count > DinerFavorite.MaxPerDiner)
        {
            throw TooMany();
        }

        var nowUtc = clock.UtcNow;

        foreach (var branchId in adding)
        {
            db.DinerFavorites.Add(new DinerFavorite(dinerUserId, branchId, nowUtc));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Diner {DinerUserId} merged {Added} favourite(s) into their account.", dinerUserId, adding.Count);
    }

    private async Task<DinerFavoriteList> ListForAsync(
        Guid dinerUserId,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken)
    {
        var rows = await db.DinerFavorites
            .AsNoTracking()
            .Where(f => f.DinerUserId == dinerUserId)
            .OrderByDescending(f => f.CreatedAtUtc)
            .ThenByDescending(f => f.Id)
            .Select(f => new { f.BranchId, f.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        // Only published branches come back, so a closed place drops out here.
        var cards = (await listings.GetListingsAsync(
                [.. rows.Select(r => r.BranchId)], latitude, longitude, cancellationToken))
            .ToDictionary(l => l.BranchId);

        return new DinerFavoriteList(
        [
            .. rows
                .Where(r => cards.ContainsKey(r.BranchId))
                .Select(r => new DinerFavoriteView(r.BranchId, r.CreatedAtUtc, cards[r.BranchId])),
        ]);
    }

    private IQueryable<Branch> Published() =>
        db.Branches
            .AsNoTracking()
            .Where(b => b.IsActive
                        && b.Venue.IsActive
                        && b.Venue.SuspendedAtUtc == null
                        && b.Venue.DeletedAtUtc == null);

    private Guid RequireDiner() =>
        actor.DinerUserId ?? throw new UnauthorizedAccessException("Only a signed-in diner keeps favourites.");

    private static DomainStateException TooMany() =>
        new($"An account keeps at most {DinerFavorite.MaxPerDiner} favourite places. Remove one before adding more.");

    /// <summary>The browse routes' rule for <c>lat</c>/<c>lng</c>, so a bad position is refused before a merge writes.</summary>
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
}
