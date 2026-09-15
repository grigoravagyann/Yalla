using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.Diners;

namespace Yalla.Api.Endpoints;

/// <summary>
/// A diner's favourite places, kept on the account (K11).
/// </summary>
/// <remarks>
/// No diner id in any route: the account is the one the bearer token names, so one diner cannot read
/// or change another's hearts. Any diner account - a proved number is not needed.
/// </remarks>
public static class DinerFavoriteEndpoints
{
    private const string RateLimitedDescription =
        "`rate-limited`: more than the `diner-write` budget - ten writes a minute per account by default - "
        + "counted across favourites, reviews, reports and photo uploads.";

    public static IEndpointRouteBuilder MapDinerFavoriteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diner/favorites")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner);

        group.MapGet("/", ListAsync)
            .WithName("getDinerFavorites")
            .WithSummary("The diner's favourite places")
            .WithDescription(
                "Newest first: `{ items: [ { branchId, createdAtUtc, listing } ] }`, where `listing` is the "
                + "`PublicBranchListing` the browse list serves. A place that is not published - inactive, or "
                + "its venue suspended or deleted - is left out, and comes back if it reopens.\n\n"
                + "`lat` and `lng` work as on the browse list: together they add `distanceKm`. One without the "
                + "other, or out of range, is `400`.")
            .Produces<DinerFavoriteList>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`lat`/`lng` half sent or out of range.")
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.");

        group.MapPut("/", MergeAsync)
            .WithName("mergeDinerFavorites")
            .WithSummary("Merge hearts made while signed out into the account")
            .WithDescription(
                "Body `{ branchIds: uuid[] }`, at most 500. Every published place not already kept is added; "
                + "**nothing is removed**. An unknown or unpublished place is skipped rather than refused. "
                + "Answers with the list, as the GET does (and takes the same `lat`/`lng`).\n\n"
                + "Meant for once, at sign-in. If the merge would take the account past 500 favourites, "
                + "`409 conflicting-state` and nothing is written.")
            .RequireRateLimiting(RateLimitingExtensions.DinerWritePolicy)
            .Accepts<MergeFavoritesCommand>("application/json")
            .Produces<DinerFavoriteList>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`lat`/`lng` half sent or out of range.")
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The merge would pass 500 favourites; nothing was written.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`branchIds` missing (bound `required`) or over 500 (bound `max`).")
            .ProducesProblemDetails(StatusCodes.Status429TooManyRequests, RateLimitedDescription);

        group.MapPut("/{branchId:guid}", AddAsync)
            .WithName("addDinerFavorite")
            .WithSummary("Heart a place")
            .WithDescription(
                "**204**, and idempotent: hearting a place already kept writes nothing. `404` for a place that "
                + "does not exist or is not published. An account keeps at most 500: a new one past that is "
                + "`409 conflicting-state`.")
            .RequireRateLimiting(RateLimitingExtensions.DinerWritePolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such published branch.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "The account already keeps 500 favourites.")
            .ProducesProblemDetails(StatusCodes.Status429TooManyRequests, RateLimitedDescription);

        group.MapDelete("/{branchId:guid}", RemoveAsync)
            .WithName("removeDinerFavorite")
            .WithSummary("Take the heart off a place")
            .WithDescription(
                "**204**, and idempotent: a place that is not kept, or does not exist, is the same 204. Works "
                + "for a place that has since closed.")
            .RequireRateLimiting(RateLimitingExtensions.DinerWritePolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblemDetails(StatusCodes.Status429TooManyRequests, RateLimitedDescription);

        return app;
    }

    private static async Task<IResult> ListAsync(
        IDinerFavoriteService favorites, CancellationToken ct, double? lat = null, double? lng = null) =>
        Results.Ok(await favorites.ListAsync(lat, lng, ct));

    private static async Task<IResult> MergeAsync(
        MergeFavoritesCommand command,
        IDinerFavoriteService favorites,
        CancellationToken ct,
        double? lat = null,
        double? lng = null) =>
        Results.Ok(await favorites.MergeAsync(command, lat, lng, ct));

    private static async Task<IResult> AddAsync(Guid branchId, IDinerFavoriteService favorites, CancellationToken ct)
    {
        await favorites.AddAsync(branchId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RemoveAsync(Guid branchId, IDinerFavoriteService favorites, CancellationToken ct)
    {
        await favorites.RemoveAsync(branchId, ct);

        return Results.NoContent();
    }
}
