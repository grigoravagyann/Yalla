using Yalla.Api.Authorization;
using Yalla.Api.Errors;
using Yalla.Application.Diners;

namespace Yalla.Api.Endpoints;

/// <summary>
/// The diner's notifications feed (K12): what the pushes said, kept so the app can list it.
/// </summary>
/// <remarks>
/// No diner id in any route: the feed is the one the bearer token names. Any diner account.
/// </remarks>
public static class DinerNotificationFeedEndpoints
{
    public static IEndpointRouteBuilder MapDinerNotificationFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diner/notifications")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner);

        group.MapGet("/", ListAsync)
            .WithName("getDinerNotifications")
            .WithSummary("The diner's notifications, newest first, with the unread count")
            .WithDescription(
                "`{ items, nextCursor, unreadCount }`. Each item is `{ notificationId, kind, params, branchId, "
                + "branchName, reservationId, tabId, orderId, createdAtUtc, read }`.\n\n"
                + "`kind` is `booking-reminder`, `booking-confirmed`, `booking-declined`, "
                + "`booking-cancelled-by-venue`, `order-ready` or `review-hidden`. **The server sends no prose**: "
                + "the app writes the text from `kind` and the string values in `params`.\n\n"
                + "A reminder appears when it is due, not when the booking was made; cancelling the booking "
                + "before then removes it. `unreadCount` covers the whole feed, not the page.\n\n"
                + "Pages: `limit` 1-50 (default 20); pass `nextCursor` back as `before` for the next page - "
                + "absent on the last. A cursor this feed did not issue, or a `limit` out of range, is `400`. "
                + "Entries are kept for 90 days.")
            .Produces<DinerNotificationPage>()
            .ProducesProblemDetails(StatusCodes.Status400BadRequest, "`before` is not a cursor from this feed, or `limit` is out of range.")
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.");

        group.MapPost("/read", MarkReadAsync)
            .WithName("markDinerNotificationsRead")
            .WithSummary("Mark notifications read")
            .WithDescription(
                "Body `{ upTo: uuid | null, ids: uuid[] | null }`, **204**. `upTo` marks that entry and every "
                + "older one; `ids` (at most 200) marks those. Both may be sent. **Both absent marks the whole "
                + "feed read.** An id that is unknown or not the caller's is skipped, not refused.")
            .Accepts<MarkNotificationsReadCommand>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a diner token.")
            .ProducesProblem<ValidationFailedProblem>(
                StatusCodes.Status422UnprocessableEntity, "More than 200 `ids` (bound `max`).");

        return app;
    }

    private static async Task<IResult> ListAsync(
        IDinerNotificationFeed feed,
        CancellationToken ct,
        string? before = null,
        int limit = DinerNotificationFeedLimits.DefaultPageSize) =>
        Results.Ok(await feed.ListAsync(before, limit, ct));

    private static async Task<IResult> MarkReadAsync(
        MarkNotificationsReadCommand command, IDinerNotificationFeed feed, CancellationToken ct)
    {
        await feed.MarkReadAsync(command, ct);

        return Results.NoContent();
    }
}
