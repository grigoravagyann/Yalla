using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Application.Reviews;

/// <summary>The moderation list's filter values.</summary>
public static class ReviewModerationFilters
{
    /// <summary>Every review, published or hidden.</summary>
    public const string All = "all";

    /// <summary>Reviews with at least one report nobody has dismissed, most recently reported first.</summary>
    public const string Reported = "reported";

    /// <summary>Reviews that are taken down.</summary>
    public const string Hidden = "hidden";

    /// <summary>The accepted values.</summary>
    public static readonly IReadOnlyList<string> Values = [All, Reported, Hidden];
}

/// <summary>
/// One review as a moderator sees it: the published fields, who wrote it, whether it is down and
/// why, and how often diners have reported it.
/// </summary>
/// <param name="ReviewId">The review.</param>
/// <param name="BranchId">The branch it is about.</param>
/// <param name="Rating">1-5.</param>
/// <param name="Text">Absent when stars only.</param>
/// <param name="AuthorName">The public name, never the account's full name, number or email.</param>
/// <param name="DinerUserId">The account that wrote it.</param>
/// <param name="CreatedAtUtc">First written.</param>
/// <param name="UpdatedAtUtc">Last changed.</param>
/// <param name="Hidden">Taken down.</param>
/// <param name="HiddenReason">The moderator's reason, when hidden.</param>
/// <param name="HiddenAtUtc">When it was taken down.</param>
/// <param name="HiddenByPlatform">
/// Taken down by the platform rather than the venue. A venue cannot put these back; the console
/// shows them as the platform's decision.
/// </param>
/// <param name="ReportCount">
/// How many diners reported it and nobody has dismissed. A platform admin dismissing the reports
/// (<c>POST /api/platform/reviews/{id}/dismiss-reports</c>) brings this to zero for both tiers; a report
/// filed after that counts again.
/// </param>
/// <param name="LastReportedAtUtc">The newest of those reports, or absent when there is none.</param>
/// <param name="Reply">The venue's public reply, or null. Shown here on hidden reviews too.</param>
/// <param name="HiddenByName">
/// The full name of the venue staff member who took it down, read live. Null when it is shown, and when
/// the platform took it down - the console says "the Yalla team" and never names a platform admin.
/// </param>
public sealed record ModeratedReviewView(
    Guid ReviewId,
    Guid BranchId,
    int Rating,
    string? Text,
    string AuthorName,
    Guid DinerUserId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool Hidden,
    string? HiddenReason,
    DateTime? HiddenAtUtc,
    bool HiddenByPlatform,
    int ReportCount,
    DateTime? LastReportedAtUtc,
    ReviewReplyView? Reply,
    string? HiddenByName);

/// <summary>
/// The venue's public reply under a review, as every read shows it: the console, the public reviews,
/// the place's details and the author's own review.
/// </summary>
/// <param name="Text">The reply, 1-500 characters.</param>
/// <param name="AuthorName">The full name of the staff member who last wrote it, as it was then.</param>
/// <param name="AuthorRole">Their role then: 1 Owner, 2 Manager (a branch manager), 6 GeneralManager.</param>
/// <param name="CreatedAtUtc">First published. A reply removed and written again starts over.</param>
/// <param name="UpdatedAtUtc">When the text last changed; equal to <paramref name="CreatedAtUtc"/> until an edit.</param>
/// <param name="Edited">Changed after it was first published.</param>
public sealed record ReviewReplyView(
    string Text,
    string AuthorName,
    StaffRole AuthorRole,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool Edited)
{
    /// <summary>The reply on a review, or null when it has none.</summary>
    public static ReviewReplyView? From(BranchReview review)
    {
        ArgumentNullException.ThrowIfNull(review);

        return From(
            review.ReplyText,
            review.ReplyAuthorName,
            review.ReplyAuthorRole,
            review.RepliedAtUtc,
            review.ReplyUpdatedAtUtc);
    }

    /// <summary>The reply from its columns as a projection reads them, or null when there is none.</summary>
    public static ReviewReplyView? From(
        string? text,
        string? authorName,
        StaffRole? authorRole,
        DateTime? createdAtUtc,
        DateTime? updatedAtUtc) =>
        text is null || authorName is null || authorRole is not { } role
        || createdAtUtc is not { } created || updatedAtUtc is not { } updated
            ? null
            : new ReviewReplyView(text, authorName, role, Utc(created), Utc(updated), updated != created);

    // A nullable column read through a projection can come back unmarked; it goes out with its Z like
    // every other instant.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>One row of <see cref="ModeratedReviewSummary.Distribution"/>.</summary>
/// <param name="Stars">5 down to 1.</param>
/// <param name="Count">Shown reviews with that many stars.</param>
public sealed record ReviewStarCount(int Stars, int Count);

/// <summary>
/// The whole branch's reviews in numbers, whatever the list's filter and page: the filter chips' counts,
/// the rating card and its change.
/// </summary>
/// <param name="All">Every review, hidden ones included.</param>
/// <param name="Reported">Shown reviews with an open report - the <c>reported</c> filter's total.</param>
/// <param name="Hidden">Taken down - the <c>hidden</c> filter's total.</param>
/// <param name="Visible">Shown, and so counted in the rating.</param>
/// <param name="Rating">The shown reviews' average to one decimal, as the public card shows it; null with none.</param>
/// <param name="RatingChange30Days">
/// <paramref name="Rating"/> minus the same average over the shown reviews written more than 30 days ago,
/// to one decimal. Null when either side has no review.
/// </param>
/// <param name="Distribution">Shown reviews per star, always five rows, 5 stars first.</param>
public sealed record ModeratedReviewSummary(
    int All,
    int Reported,
    int Hidden,
    int Visible,
    double? Rating,
    double? RatingChange30Days,
    IReadOnlyList<ReviewStarCount> Distribution);

/// <summary>A page of a branch's reviews for moderation.</summary>
/// <param name="Items">The page.</param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Total">Rows across every page for this filter.</param>
/// <param name="Summary">The branch's numbers, independent of the filter and the page.</param>
public sealed record ModeratedReviewPage(
    IReadOnlyList<ModeratedReviewView> Items,
    int Page,
    int PageSize,
    int Total,
    ModeratedReviewSummary Summary);

/// <summary>Paging and the filter for the moderation list.</summary>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">1 to 100.</param>
/// <param name="Filter"><c>all</c> (default), <c>reported</c> or <c>hidden</c>.</param>
public sealed record ReviewModerationQuery(int Page = 1, int PageSize = 20, string? Filter = null);

/// <summary>Hide or put back one review.</summary>
/// <param name="Hidden">True to take it down, false to put it back.</param>
/// <param name="Reason">Required when hiding, at most 500 characters. Ignored when putting it back.</param>
public sealed record SetReviewVisibilityCommand(bool Hidden, string? Reason);

/// <summary>Write the venue's reply to a review, or replace its text.</summary>
/// <param name="Text">Required, at most 500 characters after trimming.</param>
public sealed record ReplyToReviewCommand(string? Text);

/// <summary>
/// Review moderation by the platform and by the venue (K8 and its extension).
/// </summary>
/// <remarks>
/// <para>
/// Two tiers, one rule set. The <b>venue</b> tier is an owner or manager who covers the branch (the
/// K4 guard, from the stored staff row); the <b>platform</b> tier is an active platform admin. A
/// platform admin reaching a venue route acts as the platform.
/// </para>
/// <para>
/// A venue cannot put back a review the platform hid - that is 403 - and re-hiding one leaves it the
/// platform's. The platform can put back anything. Every change writes a <c>PlatformAuditLogs</c> row,
/// <c>review.hide</c> or <c>review.unhide</c>, carrying the tier.
/// </para>
/// </remarks>
public interface IReviewModerationService
{
    /// <summary>A branch's reviews, for its owner or manager.</summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">The caller does not cover the branch.</exception>
    /// <exception cref="ArgumentException">A page, page size or filter out of range.</exception>
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    Task<ModeratedReviewPage> ListForVenueAsync(
        Guid branchId,
        ReviewModerationQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Hides or puts back a review at this branch, as its venue.</summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">
    /// The caller does not cover the branch, or is putting back a review the platform hid.
    /// </exception>
    /// <exception cref="Yalla.Domain.FieldValidationException">Hiding without a reason, or with one too long.</exception>
    /// <exception cref="KeyNotFoundException">No such review at this branch.</exception>
    Task<ModeratedReviewView> SetVisibilityForVenueAsync(
        Guid branchId,
        Guid reviewId,
        SetReviewVisibilityCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the venue's public reply to a review at this branch, or replaces its text. The writer signs
    /// it. The first reply on a review tells its author in the feed; an edit does not. Audited as
    /// <c>review.reply</c>; the same text again writes nothing.
    /// </summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">
    /// The caller does not cover the branch, or is a platform admin - the platform does not reply as a venue.
    /// </exception>
    /// <exception cref="Yalla.Domain.FieldValidationException">The text is blank or over 500 characters.</exception>
    /// <exception cref="KeyNotFoundException">No such review at this branch.</exception>
    /// <exception cref="Yalla.Domain.DomainStateException">The review is taken down.</exception>
    Task<ModeratedReviewView> ReplyForVenueAsync(
        Guid branchId,
        Guid reviewId,
        ReplyToReviewCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the venue's reply, shown or hidden review alike, and takes back the author's unread notice
    /// of it. Idempotent; audited as <c>review.remove-reply</c> when there was one.
    /// </summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">As for <see cref="ReplyForVenueAsync"/>.</exception>
    /// <exception cref="KeyNotFoundException">No such review at this branch.</exception>
    Task<ModeratedReviewView> RemoveReplyForVenueAsync(
        Guid branchId,
        Guid reviewId,
        CancellationToken cancellationToken = default);

    /// <summary>A branch's reviews, for a platform admin.</summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">Not an active platform admin.</exception>
    /// <exception cref="ArgumentException">A page, page size or filter out of range.</exception>
    /// <exception cref="KeyNotFoundException">No such branch.</exception>
    Task<ModeratedReviewPage> ListForPlatformAsync(
        Guid branchId,
        ReviewModerationQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Hides or puts back any review, as the platform.</summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">Not an active platform admin.</exception>
    /// <exception cref="Yalla.Domain.FieldValidationException">Hiding without a reason, or with one too long.</exception>
    /// <exception cref="KeyNotFoundException">No such review.</exception>
    Task<ModeratedReviewView> SetVisibilityForPlatformAsync(
        Guid reviewId,
        SetReviewVisibilityCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets aside every report on a review without hiding it, as the platform: the review stays as it
    /// is and stops counting as reported. Idempotent - a review with no open report is answered as it
    /// is and nothing is written. Audited as <c>review.dismiss-reports</c>.
    /// </summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">Not an active platform admin.</exception>
    /// <exception cref="KeyNotFoundException">No such review.</exception>
    Task<ModeratedReviewView> DismissReportsForPlatformAsync(
        Guid reviewId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes the most recent dismissal of a review's reports, as the platform: the reports that one
    /// dismissal set aside - every one stamped with its instant - count again; an earlier dismissal's
    /// stay dismissed. Once: a review already restored after its latest dismissal, or never dismissed,
    /// is answered as it is and nothing is written. Audited as <c>review.restore-reports</c>.
    /// </summary>
    /// <exception cref="Yalla.Domain.Staff.StaffPermissionException">Not an active platform admin.</exception>
    /// <exception cref="KeyNotFoundException">No such review.</exception>
    Task<ModeratedReviewView> RestoreReportsForPlatformAsync(
        Guid reviewId,
        CancellationToken cancellationToken = default);
}
