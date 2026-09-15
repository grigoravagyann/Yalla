using System.ComponentModel.DataAnnotations;
using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.Reviews;

namespace Yalla.Api.Endpoints;

/// <summary>Body of the two review visibility routes, the venue's and the platform's.</summary>
/// <param name="Hidden">True takes the review down; false puts it back. Required.</param>
/// <param name="Reason">Required when hiding, at most 500 characters. Ignored when putting it back.</param>
public sealed record SetReviewVisibilityRequest(
    // Nullable so [Required] can fire: a missing bool binds false, which would put a review back.
    [Required] bool? Hidden,
    string? Reason = null);

/// <summary>
/// A venue moderating its own branch's reviews (the K8 extension).
/// </summary>
/// <remarks>
/// <para>
/// <c>ManagerOrAbove</c> and <c>BranchScoped</c> on the route, and the K4 guard again in the service from
/// the stored staff row, so a manager whose account names branch A cannot read or hide branch B's
/// reviews. The platform's own routes live with the rest of the platform tier in
/// <see cref="PlatformEndpoints"/>; both call the same service.
/// </para>
/// <para>
/// A venue can take a review down and put back what it took down. It cannot put back what the platform
/// took down: that is 403 <c>forbidden</c>.
/// </para>
/// </remarks>
public static class ReviewModerationEndpoints
{
    public static IEndpointRouteBuilder MapReviewModerationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapGet("/reviews", ListAsync)
            .WithName("listBranchReviewsForModeration")
            .WithSummary("This branch's reviews for moderation, with report counts")
            .WithDescription(
                "Every review of the branch - published or hidden - with `hidden`, `hiddenReason`, "
                + "`hiddenAtUtc`, `hiddenByPlatform`, `reportCount` and `lastReportedAtUtc`. `authorName` is "
                + "the public name, never the account's full name, number or email.\n\n"
                + "`filter`: `all` (default, newest written first), `reported` (at least one diner report, "
                + "most recently reported first) or `hidden`. `page` from 1, `pageSize` 1-100 (default 20); "
                + "`total` counts the rows for the filter.\n\n"
                + "Scoped like every branch setting: a manager whose account names a home branch reaches "
                + "that branch only.")
            .Produces<ModeratedReviewPage>()
            .ProducesProblemDetails(
                StatusCodes.Status400BadRequest, "`page`, `pageSize` or `filter` out of range; `context.field` names it.")
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "Another venue's staff, a waiter, or a manager of a different branch of this venue.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/reviews/{reviewId:guid}/visibility", SetVisibilityAsync)
            .WithName("setBranchReviewVisibility")
            .WithSummary("Take a review of this branch down, or put one back")
            .WithDescription(
                "`{ \"hidden\": true, \"reason\": \"…\" }` takes it down: out of the public list, the rating, "
                + "the count and the badges at once. `reason` is required when hiding, at most 500 characters. "
                + "`{ \"hidden\": false }` puts it back.\n\n"
                + "**A review the platform took down stays down**: putting it back is `403 forbidden`, and "
                + "hiding it again changes nothing. `hiddenByPlatform` on the list says which ones those are.\n\n"
                + "**Audited** as `review.hide` or `review.unhide` with `actorType: venue`. A request that "
                + "changes nothing writes nothing. Answers the review as the list shows it.")
            .Produces<ModeratedReviewView>()
            .ProducesProblemDetails(
                StatusCodes.Status403Forbidden,
                "The caller does not cover this branch, or is putting back a review the platform took down.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such review at this branch.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity,
                "`hidden` missing, or `reason` missing (bound `required`) or over 500 characters (bound `max`) when hiding.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid branchId,
        IReviewModerationService moderation,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 20,
        string? filter = null) =>
        Results.Ok(await moderation.ListForVenueAsync(
            branchId, new ReviewModerationQuery(page, pageSize, filter), cancellationToken));

    private static async Task<IResult> SetVisibilityAsync(
        Guid branchId,
        Guid reviewId,
        SetReviewVisibilityRequest request,
        IReviewModerationService moderation,
        CancellationToken cancellationToken) =>
        Results.Ok(await moderation.SetVisibilityForVenueAsync(
            branchId,
            reviewId,

            // The validation filter has refused a missing `hidden` before this runs.
            new SetReviewVisibilityCommand(request.Hidden!.Value, request.Reason),
            cancellationToken));
}
