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
/// <para>
/// <b>The venue's reply</b> lives here too, because it is the same console list and the same review under
/// the same lock: one public reply per review, written by the venue's own ranks - owner, general manager,
/// the branch's manager - and never by a platform admin, who reaches these routes as the platform and
/// does not speak for a venue. A taken-down review is not answered (409); the lock is what makes "is it
/// hidden" true when the reply is written, not a moment before. Audited as <c>review.reply</c> and
/// <c>review.remove-reply</c>. The platform may take a venue's reply down - never write one - and that
/// removal is audited as <c>review.remove-reply</c> with <c>actorType: "platform"</c>.
/// </para>
/// <para>
/// <b>Lock order.</b> A change that writes into the author's feed takes the author's account lock
/// (<see cref="DinerAccountLock"/>) before the review's row lock - the order account deletion takes them
/// in, account first and then the reviews it deletes - so the two cannot deadlock, and a notice is never
/// written into an account that is not live.
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

    /// <summary>The audit action for setting a review's reports aside without hiding it.</summary>
    public const string DismissReportsAuditAction = "review.dismiss-reports";

    /// <summary>The audit action for undoing the last dismissal.</summary>
    public const string RestoreReportsAuditAction = "review.restore-reports";

    /// <summary>The audit action for writing or editing the venue's reply.</summary>
    public const string ReplyAuditAction = "review.reply";

    /// <summary>The audit action for removing it.</summary>
    public const string RemoveReplyAuditAction = "review.remove-reply";

    /// <summary>The largest page the list serves.</summary>
    public const int MaxPageSize = 100;

    /// <summary>How far back the summary's rating change looks.</summary>
    public const int RatingChangeDays = 30;

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

        // A takedown tells the author, so their account is locked first; putting back tells nobody.
        var authorLive = command.Hidden && await LockAuthorAsync(reviewId, branchId, cancellationToken);

        var review = await LockedReview(reviewId)
            .FirstOrDefaultAsync(r => r.BranchId == branchId, cancellationToken)
            ?? throw ReviewNotFound(reviewId);

        var asPlatform = await IsActivePlatformAdminAsync(staffId, cancellationToken);

        var view = await ApplyAsync(review, command.Hidden, reason, staffId, asPlatform, authorLive, cancellationToken);
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

        var authorLive = command.Hidden && await LockAuthorAsync(reviewId, branchId: null, cancellationToken);

        var review = await LockedReview(reviewId).FirstOrDefaultAsync(cancellationToken)
                     ?? throw ReviewNotFound(reviewId);

        var view = await ApplyAsync(review, command.Hidden, reason, staffId, asPlatform: true, authorLive, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return view;
    }

    public async Task<ModeratedReviewView> ReplyForVenueAsync(
        Guid branchId,
        Guid reviewId,
        ReplyToReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Who first, then the body: a platform admin is told they may not, not that their text is short.
        var author = await RequireVenueRankAsync(branchId, "Replying to a review", cancellationToken);
        var text = BranchReview.CheckReply(command.Text);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The author's account first (the first reply tells them), then the review.
        var authorLive = await LockAuthorAsync(reviewId, branchId, cancellationToken);

        // The moderation lock: a reply and a takedown of the same review serialise, so a reply is never
        // written to a review that was hidden a moment before.
        var review = await LockedReview(reviewId)
            .FirstOrDefaultAsync(r => r.BranchId == branchId, cancellationToken)
            ?? throw ReviewNotFound(reviewId);

        var before = review.ReplyText;
        var first = !review.HasReply;

        if (review.Reply(author.StaffMemberId, author.FullName, author.Role, text, clock.UtcNow))
        {
            var target = await ReviewTargetAsync(review, cancellationToken);

            // The author hears once, when the review is first answered - not on every edit, and not at all
            // into an account that is deleted or deactivated.
            if (first && authorLive)
            {
                var names = await db.Branches
                    .AsNoTracking()
                    .Where(b => b.Id == review.BranchId)
                    .Select(b => new { b.Name, VenueName = b.Venue.Name })
                    .FirstAsync(cancellationToken);

                DinerNotices.ReviewReplied(
                    db, review.DinerUserId, review.BranchId, review.Id, names.VenueName, names.Name, clock.UtcNow);
            }

            await PlatformAudit.RecordAsync(
                db,
                actor,
                clock,
                ReplyAuditAction,
                target,
                new
                {
                    actorType = "venue",
                    branchId = review.BranchId,
                    edited = !first,
                    before,
                },
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Review {ReviewId} at branch {BranchId} was {Action} by staff member {StaffMemberId}.",
                review.Id, review.BranchId, first ? "answered" : "re-answered", author.StaffMemberId);
        }

        await transaction.CommitAsync(cancellationToken);

        return await ViewAsync(review.Id, cancellationToken);
    }

    public async Task<ModeratedReviewView> RemoveReplyForVenueAsync(
        Guid branchId,
        Guid reviewId,
        CancellationToken cancellationToken = default)
    {
        var author = await RequireVenueRankAsync(branchId, "Removing a reply to a review", cancellationToken);

        return await RemoveReplyAsync(reviewId, branchId, author.StaffMemberId, asPlatform: false, cancellationToken);
    }

    public async Task<ModeratedReviewView> RemoveReplyForPlatformAsync(
        Guid reviewId,
        CancellationToken cancellationToken = default)
    {
        var staffId = await RequirePlatformAdminAsync("Removing a venue's reply to a review", cancellationToken);

        return await RemoveReplyAsync(reviewId, branchId: null, staffId, asPlatform: true, cancellationToken);
    }

    /// <summary>
    /// Takes the reply down under the author's account lock and then the review's, takes back the
    /// author's unread notice of it, and audits it with the tier. Nothing is written when there is none.
    /// </summary>
    private async Task<ModeratedReviewView> RemoveReplyAsync(
        Guid reviewId,
        Guid? branchId,
        Guid staffId,
        bool asPlatform,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The notice is the author's row, so their account is locked before the review is.
        await LockAuthorAsync(reviewId, branchId, cancellationToken);

        var review = await LockedReview(reviewId)
            .FirstOrDefaultAsync(r => branchId == null || r.BranchId == branchId, cancellationToken)
            ?? throw ReviewNotFound(reviewId);

        var before = new { text = review.ReplyText, authorName = review.ReplyAuthorName };

        if (review.RemoveReply())
        {
            // Most often the console's undo, seconds after the reply went out: the notice of a reply
            // that is no longer there goes too, unless the author has already read it.
            await DinerNotices.TakeBackUnreadReplyAsync(db, review.DinerUserId, review.BranchId, cancellationToken);

            await PlatformAudit.RecordAsync(
                db,
                actor,
                clock,
                RemoveReplyAuditAction,
                await ReviewTargetAsync(review, cancellationToken),
                new
                {
                    actorType = asPlatform ? "platform" : "venue",
                    branchId = review.BranchId,
                    before,
                },
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "The reply to review {ReviewId} at branch {BranchId} was removed by {Tier} staff member {StaffMemberId}.",
                review.Id, review.BranchId, asPlatform ? "platform" : "venue", staffId);
        }

        await transaction.CommitAsync(cancellationToken);

        return await ViewAsync(review.Id, cancellationToken);
    }

    /// <summary>
    /// Takes the review author's account lock, inside the open transaction and before the review's own
    /// lock, and answers whether the account is live. The author is read without a lock - a review never
    /// changes hands - and a review that is not there (at this branch) is false, left for the locked read
    /// to answer 404.
    /// </summary>
    private async Task<bool> LockAuthorAsync(Guid reviewId, Guid? branchId, CancellationToken cancellationToken)
    {
        var dinerUserId = await db.BranchReviews
            .AsNoTracking()
            .Where(r => r.Id == reviewId && (branchId == null || r.BranchId == branchId))
            .Select(r => (Guid?)r.DinerUserId)
            .FirstOrDefaultAsync(cancellationToken);

        return dinerUserId is { } id && await DinerAccountLock.IsLiveAsync(db, id, cancellationToken);
    }

    public async Task<ModeratedReviewView> DismissReportsForPlatformAsync(
        Guid reviewId,
        CancellationToken cancellationToken = default)
    {
        var staffId = await RequirePlatformAdminAsync("Dismissing a review's reports", cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The same lock a visibility change takes, so two admins dismissing at once serialise: the
        // second reads the reports the first already set aside, finds none open, and writes nothing.
        var review = await LockedReview(reviewId).FirstOrDefaultAsync(cancellationToken)
                     ?? throw ReviewNotFound(reviewId);

        var open = await db.BranchReviewReports
            .Where(p => p.ReviewId == review.Id && p.DismissedAtUtc == null)
            .ToListAsync(cancellationToken);

        if (open.Count > 0)
        {
            var nowUtc = clock.UtcNow;

            foreach (var report in open)
            {
                report.Dismiss(staffId, nowUtc);
            }

            // Added to this unit of work, so the dismissal and its record commit together or not at all.
            await PlatformAudit.RecordAsync(
                db,
                actor,
                clock,
                DismissReportsAuditAction,
                await ReviewTargetAsync(review, cancellationToken),
                new
                {
                    actorType = "platform",
                    branchId = review.BranchId,
                    dismissed = open.Count,
                    reasons = open
                        .GroupBy(p => p.Reason, StringComparer.Ordinal)
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                },
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Platform admin {StaffMemberId} dismissed {Count} report(s) on review {ReviewId}.",
                staffId, open.Count, review.Id);
        }

        await transaction.CommitAsync(cancellationToken);

        return await ViewAsync(review.Id, cancellationToken);
    }

    public async Task<ModeratedReviewView> RestoreReportsForPlatformAsync(
        Guid reviewId,
        CancellationToken cancellationToken = default)
    {
        var staffId = await RequirePlatformAdminAsync("Restoring a review's reports", cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The lock a dismissal takes, so an undo and a second dismissal cannot interleave.
        var review = await LockedReview(reviewId).FirstOrDefaultAsync(cancellationToken)
                     ?? throw ReviewNotFound(reviewId);

        // One dismissal stamps every report it sets aside with the same instant, so the latest instant
        // names the latest dismissal - and only its reports come back.
        var latest = await db.BranchReviewReports
            .Where(p => p.ReviewId == review.Id && p.DismissedAtUtc != null)
            .MaxAsync(p => p.DismissedAtUtc, cancellationToken);

        // An undo undoes one dismissal, once: when this review was last restored after its latest
        // dismissal, that dismissal is already undone and an earlier one is not this call's to undo.
        var lastRestore = await db.PlatformAuditLogs
            .AsNoTracking()
            .Where(l => l.TargetId == review.Id && l.Action == RestoreReportsAuditAction)
            .MaxAsync(l => (DateTime?)l.AtUtc, cancellationToken);

        if (latest is { } dismissedAtUtc && !(lastRestore is { } restoredAt && restoredAt >= dismissedAtUtc))
        {
            var batch = await db.BranchReviewReports
                .Where(p => p.ReviewId == review.Id && p.DismissedAtUtc == dismissedAtUtc)
                .ToListAsync(cancellationToken);

            foreach (var report in batch)
            {
                report.Undismiss();
            }

            await PlatformAudit.RecordAsync(
                db,
                actor,
                clock,
                RestoreReportsAuditAction,
                await ReviewTargetAsync(review, cancellationToken),
                new
                {
                    actorType = "platform",
                    branchId = review.BranchId,
                    restored = batch.Count,
                    dismissedAtUtc = DateTime.SpecifyKind(dismissedAtUtc, DateTimeKind.Utc),
                },
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Platform admin {StaffMemberId} restored {Count} dismissed report(s) on review {ReviewId}.",
                staffId, batch.Count, review.Id);
        }

        await transaction.CommitAsync(cancellationToken);

        return await ViewAsync(review.Id, cancellationToken);
    }

    /// <summary>
    /// What a review's audit rows are about: the review, named by the branch it is about - the
    /// reviewer is a diner and is not copied into the log - and the branch's venue.
    /// </summary>
    private async Task<AuditTarget> ReviewTargetAsync(BranchReview review, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == review.BranchId)
            .Select(b => new { b.Name, b.VenueId })
            .FirstAsync(cancellationToken);

        return new AuditTarget(nameof(BranchReview), review.Id, branch.Name, branch.VenueId);
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
        bool authorLive,
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
                if (!before.hidden && authorLive)
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
            await PlatformAudit.RecordAsync(
                db,
                actor,
                clock,
                hide ? HideAuditAction : UnhideAuditAction,
                await ReviewTargetAsync(review, cancellationToken),
                new
                {
                    actorType = asPlatform ? "platform" : "venue",
                    branchId = review.BranchId,
                    reason = hide ? reason : null,
                    before,
                },
                cancellationToken);

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

    /// <summary>
    /// The venue's own ranks at this branch - owner, general manager, the branch's manager - with the
    /// name and role a reply is signed with, from the stored row. A platform admin, whom the branch guard
    /// admits, is refused: the platform does not reply on a venue's behalf.
    /// </summary>
    private async Task<ReplyAuthor> RequireVenueRankAsync(Guid branchId, string operation, CancellationToken cancellationToken)
    {
        var staffId = await branchGuard.RequireAtBranchAsync(branchId, operation, cancellationToken);

        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffId)
            .Select(s => new { s.FullName, s.Role })
            .FirstAsync(cancellationToken);

        if (!StaffRoleRules.ManagesAtVenue(staff.Role))
        {
            logger.LogWarning(
                "Staff member {StaffMemberId} ({Role}) was refused writing a venue's reply at branch {BranchId}.",
                staffId, staff.Role, branchId);

            throw new StaffPermissionException(operation, staff.Role, StaffRole.Manager);
        }

        return new ReplyAuthor(staffId, staff.FullName, staff.Role);
    }

    private sealed record ReplyAuthor(Guid StaffMemberId, string FullName, StaffRole Role);

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
        var openReports = OpenReports();

        reviews = filter switch
        {
            // Waiting for an answer: hiding a review answers its reports, so a hidden one is
            // found under Hidden rather than still waiting here.
            ReviewModerationFilters.Reported => reviews.Where(r =>
                r.HiddenAtUtc == null && openReports.Any(p => p.ReviewId == r.Id)),
            ReviewModerationFilters.Hidden => reviews.Where(r => r.HiddenAtUtc != null),
            _ => reviews,
        };

        var total = await reviews.CountAsync(cancellationToken);

        // Reported: what diners flagged most recently comes first. Otherwise newest written first,
        // the same order as the public list.
        var ordered = filter == ReviewModerationFilters.Reported
            ? reviews
                .OrderByDescending(r => openReports.Where(p => p.ReviewId == r.Id).Max(p => (DateTime?)p.CreatedAtUtc))
                .ThenByDescending(r => r.CreatedAtUtc)
                .ThenByDescending(r => r.Id)
            : reviews
                .OrderByDescending(r => r.CreatedAtUtc)
                .ThenByDescending(r => r.Id);

        var rows = await Project(ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize))
            .ToListAsync(cancellationToken);

        return new ModeratedReviewPage(
            [.. rows.Select(ToView)],
            query.Page,
            query.PageSize,
            total,
            await SummaryAsync(branchId, cancellationToken));
    }

    /// <summary>
    /// The branch's numbers for the chips and the rating card: three counts, and the shown reviews by star
    /// split at <see cref="RatingChangeDays"/> days ago. Two small grouped reads, whatever the page.
    /// </summary>
    private async Task<ModeratedReviewSummary> SummaryAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var reviews = db.BranchReviews.AsNoTracking().Where(r => r.BranchId == branchId);
        var openReports = OpenReports();

        var counts = await reviews
            .GroupBy(_ => 1)
            .Select(g => new
            {
                All = g.Count(),
                Hidden = g.Count(r => r.HiddenAtUtc != null),
                Reported = g.Count(r => r.HiddenAtUtc == null && openReports.Any(p => p.ReviewId == r.Id)),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var sinceUtc = clock.UtcNow.AddDays(-RatingChangeDays);

        var stars = await reviews
            .Where(r => r.HiddenAtUtc == null)
            .GroupBy(r => new { r.Rating, Older = r.CreatedAtUtc < sinceUtc })
            .Select(g => new { g.Key.Rating, g.Key.Older, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var visible = stars.Sum(s => s.Count);
        var visibleSum = stars.Sum(s => (long)s.Rating * s.Count);
        var older = stars.Where(s => s.Older).ToList();

        return new ModeratedReviewSummary(
            counts?.All ?? 0,
            counts?.Reported ?? 0,
            counts?.Hidden ?? 0,
            visible,
            BranchBadgeRules.AverageRating(visible, visibleSum),
            BranchBadgeRules.RatingChange(
                visible, visibleSum, older.Sum(s => s.Count), older.Sum(s => (long)s.Rating * s.Count)),
            [
                .. Enumerable.Range(BranchReview.MinRating, BranchReview.MaxRating - BranchReview.MinRating + 1)
                    .Reverse()
                    .Select(n => new ReviewStarCount(n, stars.Where(s => s.Rating == n).Sum(s => s.Count))),
            ]);
    }

    private async Task<ModeratedReviewView> ViewAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        var row = await Project(db.BranchReviews.AsNoTracking().Where(r => r.Id == reviewId))
            .FirstAsync(cancellationToken);

        return ToView(row);
    }

    /// <summary>One query: the review, the author's public name and the report count and latest.</summary>
    private IQueryable<Row> Project(IQueryable<BranchReview> reviews)
    {
        var openReports = OpenReports();

        return reviews.Select(r => new Row
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
            ReportCount = openReports.Count(p => p.ReviewId == r.Id),
            LastReportedAtUtc = openReports
                .Where(p => p.ReviewId == r.Id)
                .Max(p => (DateTime?)p.CreatedAtUtc),
            ReplyText = r.ReplyText,
            ReplyAuthorName = r.ReplyAuthorName,
            ReplyAuthorRole = r.ReplyAuthorRole,
            RepliedAtUtc = r.RepliedAtUtc,
            ReplyUpdatedAtUtc = r.ReplyUpdatedAtUtc,

            // The venue's own moderator by name, read live; the platform's never.
            HiddenByName = r.HiddenAtUtc != null && !r.HiddenByPlatform
                ? db.StaffMembers
                    .Where(s => s.Id == r.HiddenByStaffMemberId)
                    .Select(s => s.FullName)
                    .FirstOrDefault()
                : null,
        });
    }

    /// <summary>
    /// The reports nobody has dismissed - the only ones that count, for both tiers. A dismissed report
    /// is kept as the diner's word, and is set aside.
    /// </summary>
    private IQueryable<BranchReviewReport> OpenReports() =>
        db.BranchReviewReports.Where(p => p.DismissedAtUtc == null);

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
            row.LastReportedAtUtc is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
            ReviewReplyView.From(
                row.ReplyText, row.ReplyAuthorName, row.ReplyAuthorRole, row.RepliedAtUtc, row.ReplyUpdatedAtUtc),
            row.HiddenByName);

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

        public string? ReplyText { get; init; }

        public string? ReplyAuthorName { get; init; }

        public StaffRole? ReplyAuthorRole { get; init; }

        public DateTime? RepliedAtUtc { get; init; }

        public DateTime? ReplyUpdatedAtUtc { get; init; }

        public string? HiddenByName { get; init; }
    }
}
