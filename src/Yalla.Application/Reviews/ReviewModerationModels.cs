namespace Yalla.Application.Reviews;

/// <summary>The moderation list's filter values.</summary>
public static class ReviewModerationFilters
{
    /// <summary>Every review, published or hidden.</summary>
    public const string All = "all";

    /// <summary>Reviews with at least one report, most recently reported first.</summary>
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
/// <param name="ReportCount">How many diners reported it.</param>
/// <param name="LastReportedAtUtc">The newest report, or absent when there is none.</param>
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
    DateTime? LastReportedAtUtc);

/// <summary>A page of a branch's reviews for moderation.</summary>
/// <param name="Items">The page.</param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Total">Rows across every page for this filter.</param>
public sealed record ModeratedReviewPage(
    IReadOnlyList<ModeratedReviewView> Items,
    int Page,
    int PageSize,
    int Total);

/// <summary>Paging and the filter for the moderation list.</summary>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">1 to 100.</param>
/// <param name="Filter"><c>all</c> (default), <c>reported</c> or <c>hidden</c>.</param>
public sealed record ReviewModerationQuery(int Page = 1, int PageSize = 20, string? Filter = null);

/// <summary>Hide or put back one review.</summary>
/// <param name="Hidden">True to take it down, false to put it back.</param>
/// <param name="Reason">Required when hiding, at most 500 characters. Ignored when putting it back.</param>
public sealed record SetReviewVisibilityCommand(bool Hidden, string? Reason);

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
}
