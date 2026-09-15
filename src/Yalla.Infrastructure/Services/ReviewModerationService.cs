using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Reviews;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Taking reviews down and putting them back, for the platform and for the venue (K8 and its extension).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two tiers.</b> The venue is an owner or manager who covers the branch, checked from the stored
/// staff row by <see cref="IStaffBranchGuard"/> (K4) - a manager of branch A cannot moderate branch B.
/// The platform is an active platform admin, also read from the stored row. A platform admin reaching
/// a venue route is answered as the platform: the guard admits them, and the tier that may undo a
/// takedown must not be demoted by which door it used.
/// </para>
/// <para>
/// <b>The platform outranks the venue.</b> A venue cannot put back a review the platform hid (403), and
/// hiding one again leaves it the platform's - otherwise a venue could re-hide a platform takedown,
/// own it, and then put it back. The platform can put back anything and takes ownership of what it hides.
/// </para>
/// <para>
/// <b>Audited</b> in the same unit of work as the change: <c>review.hide</c> or <c>review.unhide</c>,
/// with the tier, the branch, the reason and the state before. A request that changes nothing - hiding
/// what is already hidden for the same reason, putting back what is published - writes nothing.
/// </para>
/// </remarks>
internal sealed class ReviewModerationService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IStaffBranchGuard branchGuard,
    ILogger<ReviewModerationService> logger) : IReviewModerationService
{
    /// <summary>The audit action for taking a review down.</summary>
    public const string HideAuditAction = "review.hide";

    /// <summary>The audit action for putting one back.</summary>
    public const string UnhideAuditAction = "review.unhide";

    /// <summary>The largest page the list serves.</summary>
    public const int MaxPageSize = 100;

    public async Task<ModeratedReviewPage> ListForVenueAsync(
        Guid branchId,
        ReviewModerationQuery query,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read a branch's reviews", cancellationToken);

        return await ListAsync(branchId, query, cancellationToken);
    }

    public async Task<ModeratedReviewPage> ListForPlatformAsync(
        Guid branchId,
        ReviewModerationQuery query,
        CancellationToken cancellationToken = default)
    {
        await RequirePlatformAdminAsync("Read a branch's reviews", cancellationToken);

        return await ListAsync(branchId, query, cancellationToken);
    }

    public async Task<ModeratedReviewView> SetVisibilityForVenueAsync(
        Guid branchId,
        Guid reviewId,
        SetReviewVisibilityCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Who first: a manager of another branch is told they may not, not that their reason is short.
        var staffId = await branchGuard.RequireAtBranchAsync(branchId, OperationFor(command), cancellationToken);
        var reason = CheckReason(command);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var review = await LockedReview(reviewId)
            .FirstOrDefaultAsync(r => r.BranchId == branchId, cancellationToken)
            ?? throw ReviewNotFound(reviewId);

        var asPlatform = await IsActivePlatformAdminAsync(staffId, cancellationToken);

        var view = await ApplyAsync(review, command.Hidden, reason, staffId, asPlatform, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return view;
    }

    public async Task<ModeratedReviewView> SetVisibilityForPlatformAsync(
        Guid reviewId,
        SetReviewVisibilityCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var staffId = await RequirePlatformAdminAsync(OperationFor(command), cancellationToken);
        var reason = CheckReason(command);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var review = await LockedReview(reviewId).FirstOrDefaultAsync(cancellationToken)
                     ?? throw ReviewNotFound(reviewId);

        var view = await ApplyAsync(review, command.Hidden, reason, staffId, asPlatform: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return view;
    }

    /// <summary>
    /// The review, read under an update lock held to the end of the caller's transaction.
    /// </summary>
    /// <remarks>
    /// Every decision in <see cref="ApplyAsync"/> is made on what this read says - above all "did the
    /// platform hide it", which is what stops a venue putting back a platform takedown. Without the lock
    /// a venue un-hide could read the review a moment before a platform hide committed, pass that check,
    /// and then write only the columns it changed: the review public again with <c>HiddenByPlatform</c>
    /// still set. With it the second moderator waits, and reads what the first one committed.
    /// </remarks>
    private IQueryable<BranchReview> LockedReview(Guid reviewId) =>
        db.BranchReviews.FromSql(
            $"SELECT * FROM [BranchReviews] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {reviewId}");

    // ------------------------------------------------------------ the change

    private async Task<ModeratedReviewView> ApplyAsync(
        BranchReview review,
        bool hide,
        string? reason,
        Guid staffId,
        bool asPlatform,
        CancellationToken cancellationToken)
    {
        var before = new
        {
            hidden = review.IsHidden,
            hiddenByPlatform = review.HiddenByPlatform,
            reason = review.HiddenReason,
        };

        var changed = false;

        if (hide)
        {
            var alreadyTheSame = review.IsHidden
                                 && review.HiddenByPlatform == asPlatform
                                 && string.Equals(review.HiddenReason, reason, StringComparison.Ordinal);

            // Already down by the platform, and a venue asking to hide it: it stays the platform's.
            var platformsAlready = review.HiddenByPlatform && !asPlatform;

            if (!alreadyTheSame && !platformsAlready)
            {
                // The author's feed hears about a takedown once, when the review goes from shown to
                // hidden (K12) - not again when the platform takes over one a venue already hid. No
                // push has ever been sent for this; the entry is written with the change instead.
                if (!before.hidden)
                {
                    var names = await db.Branches
                        .AsNoTracking()
                        .Where(b => b.Id == review.BranchId)
                        .Select(b => new { b.Name, VenueName = b.Venue.Name })
                        .FirstAsync(cancellationToken);

                    DinerNotices.ReviewHidden(
                        db, review.DinerUserId, review.BranchId, review.Id, names.VenueName, names.Name, clock.UtcNow);
                }

                review.Hide(staffId, asPlatform, reason, clock.UtcNow);
                changed = true;
            }
        }
        else if (review.IsHidden)
        {
            if (review.HiddenByPlatform && !asPlatform)
            {
                logger.LogWarning(
                    "Staff member {StaffMemberId} was refused putting back review {ReviewId}, which the platform hid.",
                    staffId, review.Id);

                throw new StaffPermissionException("Showing a review the platform hid", actor.Role, StaffRole.PlatformAdmin);
            }

            review.Unhide();
            changed = true;
        }

        if (changed)
        {
            // Added to this unit of work, so the change and its record commit together or not at all.
            PlatformAudit.Record(
                db,
                actor,
                clock,
                hide ? HideAuditAction : UnhideAuditAction,
                nameof(BranchReview),
                review.Id,
                new
                {
                    actorType = asPlatform ? "platform" : "venue",
                    branchId = review.BranchId,
                    reason = hide ? reason : null,
                    before,
                });

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Review {ReviewId} at branch {BranchId} was {Action} by {Tier} staff member {StaffMemberId}.",
                review.Id, review.BranchId, hide ? "hidden" : "put back", asPlatform ? "platform" : "venue", staffId);
        }

        return await ViewAsync(review.Id, cancellationToken);
    }

    private static string? CheckReason(SetReviewVisibilityCommand command) =>
        command.Hidden ? BranchReview.CheckHideReason(command.Reason) : null;

    private static string OperationFor(SetReviewVisibilityCommand command) =>
        command.Hidden ? "Hiding a review" : "Showing a hidden review";

    private static KeyNotFoundException ReviewNotFound(Guid reviewId) => new($"Review {reviewId} was not found.");

    // ------------------------------------------------------------ who

    /// <summary>An active platform admin: the token's role, and the stored row behind it.</summary>
    private async Task<Guid> RequirePlatformAdminAsync(string operation, CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff
            || actor.StaffMemberId is not { } staffId
            || actor.Role != StaffRole.PlatformAdmin
            || !await IsActivePlatformAdminAsync(staffId, cancellationToken))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.PlatformAdmin);
        }

        return staffId;
    }

    private Task<bool> IsActivePlatformAdminAsync(Guid staffId, CancellationToken cancellationToken) =>
        db.StaffMembers
            .AsNoTracking()
            .AnyAsync(s => s.Id == staffId && s.IsActive && s.Role == StaffRole.PlatformAdmin, cancellationToken);

    // ------------------------------------------------------------ the list

    private async Task<ModeratedReviewPage> ListAsync(
        Guid branchId,
        ReviewModerationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Page < 1)
        {
            throw new ArgumentOutOfRangeException("page", query.Page, "Pages start at 1.");
        }

        if (query.PageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException("pageSize", query.PageSize, $"A page holds 1 to {MaxPageSize} reviews.");
        }

        // Past this, (page - 1) * pageSize overflows into a negative OFFSET SQL Server refuses.
        if (query.Page > int.MaxValue / query.PageSize)
        {
            throw new ArgumentOutOfRangeException("page", query.Page, "That page is past the end of any list.");
        }

        var filter = string.IsNullOrWhiteSpace(query.Filter)
            ? ReviewModerationFilters.All
            : query.Filter.Trim().ToLowerInvariant();

        if (!ReviewModerationFilters.Values.Contains(filter, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"filter is one of: {string.Join(", ", ReviewModerationFilters.Values)}.", "filter");
        }

        // Any branch, published or not: a suspended venue's reviews still need moderating.
        if (!await db.Branches.AsNoTracking().AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw new KeyNotFoundException($"Branch {branchId} was not found.");
        }

        var reviews = db.BranchReviews.AsNoTracking().Where(r => r.BranchId == branchId);

        reviews = filter switch
        {
            ReviewModerationFilters.Reported => reviews.Where(r => db.BranchReviewReports.Any(p => p.ReviewId == r.Id)),
            ReviewModerationFilters.Hidden => reviews.Where(r => r.HiddenAtUtc != null),
            _ => reviews,
        };

        var total = await reviews.CountAsync(cancellationToken);

        // Reported: what diners flagged most recently comes first. Otherwise newest written first,
        // the same order as the public list.
        var ordered = filter == ReviewModerationFilters.Reported
            ? reviews
                .OrderByDescending(r => db.BranchReviewReports.Where(p => p.ReviewId == r.Id).Max(p => (DateTime?)p.CreatedAtUtc))
                .ThenByDescending(r => r.CreatedAtUtc)
                .ThenByDescending(r => r.Id)
            : reviews
                .OrderByDescending(r => r.CreatedAtUtc)
                .ThenByDescending(r => r.Id);

        var rows = await Project(ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize))
            .ToListAsync(cancellationToken);

        return new ModeratedReviewPage([.. rows.Select(ToView)], query.Page, query.PageSize, total);
    }

    private async Task<ModeratedReviewView> ViewAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        var row = await Project(db.BranchReviews.AsNoTracking().Where(r => r.Id == reviewId))
            .FirstAsync(cancellationToken);

        return ToView(row);
    }

    /// <summary>One query: the review, the author's public name and the report count and latest.</summary>
    private IQueryable<Row> Project(IQueryable<BranchReview> reviews) =>
        reviews.Select(r => new Row
        {
            ReviewId = r.Id,
            BranchId = r.BranchId,
            Rating = r.Rating,
            Text = r.Text,
            DisplayName = r.DinerUser.DisplayName,
            DinerUserId = r.DinerUserId,
            CreatedAtUtc = r.CreatedAtUtc,
            UpdatedAtUtc = r.UpdatedAtUtc,
            HiddenAtUtc = r.HiddenAtUtc,
            HiddenReason = r.HiddenReason,
            HiddenByPlatform = r.HiddenByPlatform,
            ReportCount = db.BranchReviewReports.Count(p => p.ReviewId == r.Id),
            LastReportedAtUtc = db.BranchReviewReports
                .Where(p => p.ReviewId == r.Id)
                .Max(p => (DateTime?)p.CreatedAtUtc),
        });

    private static ModeratedReviewView ToView(Row row) =>
        new(
            row.ReviewId,
            row.BranchId,
            row.Rating,
            row.Text,

            // The public name, not the account's: a venue has no more business with a reviewer's full
            // name or number than the public page does.
            BranchReview.PublicAuthorName(row.DisplayName),
            row.DinerUserId,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.HiddenAtUtc is not null,
            row.HiddenReason,
            row.HiddenAtUtc,
            row.HiddenByPlatform,
            row.ReportCount,

            // An aggregate, which the column's UTC conversion does not reach - stamped here instead so
            // it goes out with its Z like every other instant.
            row.LastReportedAtUtc is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null);

    private sealed class Row
    {
        public Guid ReviewId { get; init; }

        public Guid BranchId { get; init; }

        public int Rating { get; init; }

        public string? Text { get; init; }

        public string? DisplayName { get; init; }

        public Guid DinerUserId { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        public DateTime UpdatedAtUtc { get; init; }

        public DateTime? HiddenAtUtc { get; init; }

        public string? HiddenReason { get; init; }

        public bool HiddenByPlatform { get; init; }

        public int ReportCount { get; init; }

        public DateTime? LastReportedAtUtc { get; init; }
    }
}
