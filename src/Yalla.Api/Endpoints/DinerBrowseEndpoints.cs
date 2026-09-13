using Yalla.Api.Authorization;
using Yalla.Application.Diners;

namespace Yalla.Api.Endpoints;

/// <summary>
/// A signed-in diner's side of browsing: reviewing a place, and the Orders tab.
/// </summary>
/// <remarks>
/// No diner id in any route. The account acted on is the one the bearer token names, so there is no
/// parameter through which one diner could reach another's review or orders.
/// </remarks>
public static class DinerBrowseEndpoints
{
    public static IEndpointRouteBuilder MapDinerBrowseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diner")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner);

        group.MapGet("/branches/{branchId:guid}/review", GetMyReviewAsync)
            .WithName("getMyBranchReview")
            .WithSummary("The signed-in diner's own review of this place")
            .WithDescription("For pre-filling the review form. **404** when this diner has not reviewed it.")
            .Produces<DinerReviewView>()
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such published branch, or no review by this diner.");

        group.MapPost("/branches/{branchId:guid}/review", CreateReviewAsync)
            .WithName("createBranchReview")
            .WithSummary("Review a place: 1-5 stars and optional text")
            .WithDescription(
                "**One review per diner per branch.** A second POST is `409 conflicting-state`; change the "
                + "review with PUT instead.\n\n"
                + "**Needs a verified phone number** - `403 phone-not-verified` otherwise - so a throwaway "
                + "registration cannot flood a place with one-star reviews.\n\n"
                + "`rating` 1-5 and `text` at most 1000 characters are `422 validation-failed` with "
                + "`context.fields` naming each. The branch's `rating` and `reviewCount` on the list routes "
                + "follow within fifteen seconds; the reviews route reads them live.")
            .Produces<DinerReviewView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`phone-not-verified`.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such published branch.")
            .ProducesProblemDetails(StatusCodes.Status409Conflict, "This diner has already reviewed it.")
            .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity, "Rating or text out of bounds.");

        group.MapPut("/branches/{branchId:guid}/review", UpsertReviewAsync)
            .WithName("putBranchReview")
            .WithSummary("Write or replace the diner's review of a place")
            .WithDescription(
                "Replaces rating and text together; blank text clears it. Writes the review if there was "
                + "none (**201**), otherwise **200**. Same phone gate and bounds as POST.")
            .Produces<DinerReviewView>()
            .Produces<DinerReviewView>(StatusCodes.Status201Created)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`phone-not-verified`.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such published branch.")
            .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity, "Rating or text out of bounds.");

        group.MapGet("/orders", ListOrdersAsync)
            .WithName("getDinerOrders")
            .WithSummary("The diner's orders, for the Orders tab")
            .WithDescription(
                "Every order this diner is on: placed from their phone, keyed in by a waiter on their "
                + "behalf, or keyed in for the whole table on a tab they were approved onto. Newest first, "
                + "at most 100.\n\n"
                + "`status=active` is New, InKitchen and Ready; `status=history` is Served and Voided; "
                + "absent is both. Anything else is `400`.\n\n"
                + "`status` on each order is the app's word: New `confirmed`, InKitchen `preparing`, "
                + "Ready `ready`, Served `completed`, Voided `cancelled`. `inProgress` is never produced - "
                + "there is no takeaway. `kind` is always `dineIn`. `timeline` starts with `confirmed` at "
                + "`placedAtUtc` and adds each kitchen step as it was recorded. `canCancel` is always "
                + "false: voiding an order is a staff action.")
            .Produces<IReadOnlyList<DinerOrderView>>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`status` is not `active` or `history`.")
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.");

        group.MapGet("/orders/{orderId:guid}", GetOrderAsync)
            .WithName("getDinerOrder")
            .WithSummary("One of the diner's orders")
            .WithDescription("Same shape as a list entry. Somebody else's order is **404**, the same as no order.")
            .Produces<DinerOrderView>()
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such order of this diner's.");

        return app;
    }

    private static async Task<IResult> GetMyReviewAsync(Guid branchId, IBranchReviewService reviews, CancellationToken ct) =>
        Results.Ok(await reviews.GetMineAsync(branchId, ct));

    private static async Task<IResult> CreateReviewAsync(
        Guid branchId, SubmitBranchReviewCommand command, IBranchReviewService reviews, CancellationToken ct)
    {
        var review = await reviews.CreateAsync(branchId, command, ct);

        return Results.Created($"/api/diner/branches/{branchId}/review", review);
    }

    private static async Task<IResult> UpsertReviewAsync(
        Guid branchId, SubmitBranchReviewCommand command, IBranchReviewService reviews, CancellationToken ct)
    {
        var (review, created) = await reviews.UpsertAsync(branchId, command, ct);

        return created
            ? Results.Created($"/api/diner/branches/{branchId}/review", review)
            : Results.Ok(review);
    }

    private static async Task<IResult> ListOrdersAsync(IDinerOrderQuery orders, CancellationToken ct, string? status = null) =>
        Results.Ok(await orders.ListAsync(status, ct));

    private static async Task<IResult> GetOrderAsync(Guid orderId, IDinerOrderQuery orders, CancellationToken ct) =>
        Results.Ok(await orders.GetAsync(orderId, ct));
}
