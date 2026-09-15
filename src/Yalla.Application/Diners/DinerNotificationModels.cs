namespace Yalla.Application.Diners;

/// <summary>One entry in the diner's notifications feed (K12).</summary>
/// <param name="NotificationId">The entry; what <c>upTo</c> and <c>ids</c> name when marking read.</param>
/// <param name="Kind">
/// <c>booking-reminder</c>, <c>booking-confirmed</c>, <c>booking-declined</c>,
/// <c>booking-cancelled-by-venue</c>, <c>order-ready</c> or <c>review-hidden</c>. The app writes the text.
/// </param>
/// <param name="Params">
/// String values the app's text is built from, as they were when the entry was written: for bookings
/// <c>venueName</c>, <c>branchName</c>, <c>date</c> (yyyy-MM-dd), <c>time</c> (HH:mm), <c>partySize</c>,
/// <c>reservationCode</c>; for an order <c>venueName</c>, <c>branchName</c>, <c>tableLabel</c>; for a
/// review <c>venueName</c>, <c>branchName</c>, <c>reviewId</c>.
/// </param>
/// <param name="BranchId">The place, when there is one.</param>
/// <param name="BranchName">The place's name now, or as written when the branch is gone.</param>
/// <param name="ReservationId">The booking, for the booking kinds.</param>
/// <param name="TabId">The tab, for <c>order-ready</c>.</param>
/// <param name="OrderId">The order, for <c>order-ready</c>.</param>
/// <param name="CreatedAtUtc">When it appeared - for a reminder, when the reminder was due.</param>
/// <param name="Read">Marked read.</param>
public sealed record DinerNotificationView(
    Guid NotificationId,
    string Kind,
    IReadOnlyDictionary<string, string> Params,
    Guid? BranchId,
    string? BranchName,
    Guid? ReservationId,
    Guid? TabId,
    Guid? OrderId,
    DateTime CreatedAtUtc,
    bool Read);

/// <summary>A page of the feed, newest first.</summary>
/// <param name="Items">This page.</param>
/// <param name="NextCursor">Pass as <c>before</c> for the next page; absent on the last page.</param>
/// <param name="UnreadCount">Unread entries across the whole feed, not only this page - the badge.</param>
public sealed record DinerNotificationPage(
    IReadOnlyList<DinerNotificationView> Items,
    string? NextCursor,
    int UnreadCount);

/// <summary>Body of <c>POST /api/diner/notifications/read</c>.</summary>
/// <param name="UpTo">Marks this entry and every older one read.</param>
/// <param name="Ids">Marks these entries read; at most 200.</param>
/// <remarks>
/// Both may be sent. Both absent marks the whole feed read ("mark all read"). An id that is unknown, or
/// is not the caller's, is skipped rather than refused.
/// </remarks>
public sealed record MarkNotificationsReadCommand(Guid? UpTo = null, IReadOnlyList<Guid>? Ids = null);

/// <summary>The limits of the feed routes.</summary>
public static class DinerNotificationFeedLimits
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 50;

    public const int MaxIdsPerRead = 200;
}

/// <summary>The signed-in diner's notifications feed (K12).</summary>
public interface IDinerNotificationFeed
{
    /// <summary>A page of the caller's feed, newest first, with the unread count.</summary>
    /// <param name="before">A <c>nextCursor</c> from the previous page, or null for the first.</param>
    /// <param name="limit">1 to 50.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ArgumentException">A cursor this feed did not issue, or a limit out of range.</exception>
    Task<DinerNotificationPage> ListAsync(string? before, int limit, CancellationToken cancellationToken = default);

    /// <summary>Marks entries read.</summary>
    /// <exception cref="Yalla.Domain.FieldValidationException">More than 200 <c>ids</c>.</exception>
    Task MarkReadAsync(MarkNotificationsReadCommand command, CancellationToken cancellationToken = default);
}
