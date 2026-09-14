using Yalla.Application.Public;

namespace Yalla.Application.Diners;

/// <summary>One of the diner's favourite places (K11).</summary>
/// <param name="BranchId">The place.</param>
/// <param name="CreatedAtUtc">When it was hearted.</param>
/// <param name="Listing">
/// The card, exactly as the public list serves it - with <c>distanceKm</c> when <c>lat</c>/<c>lng</c> were sent.
/// </param>
public sealed record DinerFavoriteView(Guid BranchId, DateTime CreatedAtUtc, PublicBranchListing Listing);

/// <summary>The diner's favourites, newest first. A place that is not published is left out.</summary>
/// <param name="Items">The favourites.</param>
public sealed record DinerFavoriteList(IReadOnlyList<DinerFavoriteView> Items);

/// <summary>Body of <c>PUT /api/diner/favorites</c>: hearts made while signed out, merged into the account.</summary>
/// <param name="BranchIds">
/// At most 500. Each is added when missing; nothing is ever removed. An unknown or unpublished place is
/// skipped rather than refused - a heart made weeks ago may be for a place that has since closed.
/// </param>
public sealed record MergeFavoritesCommand(IReadOnlyList<Guid>? BranchIds);

/// <summary>A diner's favourite places, kept on the account (K11).</summary>
/// <remarks>
/// The account is the one the token names; there is no diner id in any route. Any diner account may keep
/// favourites - a proved number is not needed.
/// </remarks>
public interface IDinerFavoriteService
{
    /// <summary>The caller's favourites, newest first, unpublished places left out.</summary>
    /// <exception cref="ArgumentException">A position half sent, or out of range.</exception>
    Task<DinerFavoriteList> ListAsync(double? latitude, double? longitude, CancellationToken cancellationToken = default);

    /// <summary>Hearts a place. Idempotent: hearting it again writes nothing.</summary>
    /// <exception cref="KeyNotFoundException">No such published branch.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The account already keeps the most it may.</exception>
    Task AddAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Takes the heart off a place. Idempotent, and works for a place that has since closed.</summary>
    Task RemoveAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Adds every published place not already kept; removes nothing. Returns the list.</summary>
    /// <exception cref="Yalla.Domain.FieldValidationException"><c>branchIds</c> missing or over 500.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The merge would take the account past the limit; nothing is written.</exception>
    /// <exception cref="ArgumentException">A position half sent, or out of range.</exception>
    Task<DinerFavoriteList> MergeAsync(
        MergeFavoritesCommand command,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default);
}
