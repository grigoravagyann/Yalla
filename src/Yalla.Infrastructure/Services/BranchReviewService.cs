using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A diner rating a branch - one review each, revised rather than repeated, from proved numbers that
/// have been there - and a diner reporting somebody else's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may write a first review (K8).</b> A phone-verified account, read from the stored row, that
/// visited the branch in the last <see cref="BranchReview.VisitWindowDays"/> days: a booking of theirs
/// there that was Seated or Completed, or an approved place on one of its tabs. Anyone else gets
/// <see cref="ReviewNeedsVisitException"/>. <b>Revising</b> a review the diner already has is never
/// refused for the visit - it was allowed once, and a diner who went a year ago may still correct it.
/// </para>
/// <para>
/// <b>An identical revision writes nothing.</b> The app's form re-sends the whole review on save, and
/// a no-op that moved <c>updatedAtUtc</c> would mark every re-saved review "edited".
/// </para>
/// </remarks>
internal sealed class BranchReviewService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ILogger<BranchReviewService> logger) : IBranchReviewService
{
    private const string Operation = "Reviewing a place";

    public async Task<DinerReviewView> GetMineAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner();

        await RequirePublishedAsync(branchId, cancellationToken);

        var row = await db.BranchReviews
            .AsNoTracking()
            .Where(r => r.BranchId == branchId && r.DinerUserId == dinerUserId)
            .Select(r => new { Review = r, r.DinerUser.DisplayName })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("You have not reviewed this place.");

        return ToView(row.Review, row.DisplayName);
    }

    public async Task<DinerReviewView> CreateAsync(
        Guid branchId,
        SubmitBranchReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner();

        await RequirePublishedAsync(branchId, cancellationToken);
        await DinerPhoneGate.RequireVerifiedPhoneAsync(db, dinerUserId, Operation, cancellationToken);

        BranchReview.Check(command.Rating, command.Text);

        // Under the account's lock, and only for a live account: a review inserted while the account is
        // being deleted would outlive it - public, and counted in the rating, for ever.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);

        if (await db.BranchReviews.AnyAsync(r => r.BranchId == branchId && r.DinerUserId == dinerUserId, cancellationToken))
        {
            throw AlreadyReviewed();
        }

        await RequireVisitAsync(branchId, dinerUserId, cancellationToken);

        var review = new BranchReview(branchId, dinerUserId, command.Rating, command.Text, clock.UtcNow);
        db.BranchReviews.Add(review);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.BranchReviewPerDiner))
        {
            // Two taps on Submit. The index is the rule; the check above is only the readable answer.
            throw AlreadyReviewed();
        }

        logger.LogInformation("Diner {DinerUserId} reviewed branch {BranchId}: {Rating} stars.", dinerUserId, branchId, review.Rating);

        return ToView(review, await DisplayNameAsync(dinerUserId, cancellationToken));
    }

    public async Task<(DinerReviewView Review, bool Created)> UpsertAsync(
        Guid branchId,
        SubmitBranchReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner();

        await RequirePublishedAsync(branchId, cancellationToken);
        await DinerPhoneGate.RequireVerifiedPhoneAsync(db, dinerUserId, Operation, cancellationToken);

        BranchReview.Check(command.Rating, command.Text);

        var displayName = await DisplayNameAsync(dinerUserId, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            // Under the account's lock, as a POST is: a first review or a revision saved behind the
            // deletion's deletes would keep a review on the tombstone, or fail on a row that went.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);

            var review = await db.BranchReviews
                .FirstOrDefaultAsync(r => r.BranchId == branchId && r.DinerUserId == dinerUserId, cancellationToken);

            var created = review is null;

            if (review is null)
            {
                // A first write through PUT is a first review, and needs the visit a POST needs.
                await RequireVisitAsync(branchId, dinerUserId, cancellationToken);

                review = new BranchReview(branchId, dinerUserId, command.Rating, command.Text, clock.UtcNow);
                db.BranchReviews.Add(review);
            }
            else if (!review.Revise(command.Rating, command.Text, clock.UtcNow))
            {
                // The same rating and text: nothing to write, and updatedAtUtc stays where it was.
                return (ToView(review, displayName), false);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return (ToView(review, displayName), created);
            }
            catch (DbUpdateException ex) when (attempt == 1 && UniqueViolation.IsOn(ex, DatabaseIndexNames.BranchReviewPerDiner))
            {
                // A concurrent first write landed between the read and the insert. Revise that one.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task ReportAsync(Guid reviewId, ReportReviewCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner();

        // The body first, like every other refusal of a malformed request.
        var (reason, note) = BranchReviewReport.Check(command.Reason, command.Note);

        // A hidden review is not there to report, and neither is one at a branch that stopped trading:
        // both are the same 404 as a review that never existed.
        var review = await db.BranchReviews
            .AsNoTracking()
            .Where(r => r.Id == reviewId
                        && r.HiddenAtUtc == null
                        && r.Branch.IsActive
                        && r.Branch.Venue.IsActive
                        && r.Branch.Venue.SuspendedAtUtc == null
                        && r.Branch.Venue.DeletedAtUtc == null)
            .Select(r => new { r.DinerUserId, r.BranchId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Review {reviewId} was not found.");

        if (review.DinerUserId == dinerUserId)
        {
            throw new DomainStateException("You cannot report your own review. Change it or ask for it to be removed instead.");
        }

        // Under the account's lock, and only for a live account: a report written while the account is
        // being deleted would outlive it - its note kept, and counted against the venue, for ever.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);

        // One report per diner per review. A repeat is the success it would have been, and writes nothing.
        if (await db.BranchReviewReports.AnyAsync(r => r.ReviewId == reviewId && r.DinerUserId == dinerUserId, cancellationToken))
        {
            return;
        }

        db.BranchReviewReports.Add(new BranchReviewReport(reviewId, dinerUserId, reason, note, clock.UtcNow));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.BranchReviewReportPerDiner))
        {
            // Two taps on Report. The first one counted; so does this answer.
            return;
        }

        logger.LogInformation(
            "Diner {DinerUserId} reported review {ReviewId} at branch {BranchId} as {Reason}.",
            dinerUserId, reviewId, review.BranchId, reason);
    }

    private Guid RequireDiner() =>
        actor.DinerUserId ?? throw new UnauthorizedAccessException("Only a signed-in diner can review a place.");

    private async Task RequirePublishedAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var published = await db.Branches
            .AsNoTracking()
            .AnyAsync(
                b => b.Id == branchId
                     && b.IsActive
                     && b.Venue.IsActive
                     && b.Venue.SuspendedAtUtc == null
                     && b.Venue.DeletedAtUtc == null,
                cancellationToken);

        if (!published)
        {
            throw new KeyNotFoundException($"Branch {branchId} is not published.");
        }
    }

    /// <summary>
    /// Refuses a first review from a diner with no visit to the branch in the window.
    /// </summary>
    /// <remarks>
    /// Two facts count, both stored and neither self-reported: a booking of this account's at the
    /// branch that the venue <b>seated</b> (Seated, or Completed after it), by its start; and a place
    /// on one of the branch's tabs that was <b>approved</b> - the host's own, or a joiner the host let
    /// on - by when it was approved. A place still waiting for approval, or turned away without ever
    /// being approved, is not a visit: joining needs only the invitation link, which can be passed
    /// round a group chat by people who were never there. A place approved and later taken off still
    /// counts - that person was at the table. Both reads are by the account's own indexed column.
    /// </remarks>
    private async Task RequireVisitAsync(Guid branchId, Guid dinerUserId, CancellationToken cancellationToken)
    {
        var sinceUtc = clock.UtcNow.AddDays(-BranchReview.VisitWindowDays);

        var booked = await db.Reservations
            .AsNoTracking()
            .AnyAsync(
                r => r.DinerUserId == dinerUserId
                     && r.BranchId == branchId
                     && (r.Status == ReservationStatus.Seated || r.Status == ReservationStatus.Completed)
                     && r.StartUtc >= sinceUtc,
                cancellationToken);

        if (booked)
        {
            return;
        }

        var atATable = await db.TabParticipants
            .AsNoTracking()
            .AnyAsync(
                p => p.UserId == dinerUserId
                     && p.Tab.BranchId == branchId
                     && p.ApprovedAtUtc != null
                     && p.ApprovedAtUtc >= sinceUtc,
                cancellationToken);

        if (!atATable)
        {
            logger.LogInformation(
                "Diner {DinerUserId} tried to review branch {BranchId} with no visit in the last {Days} days.",
                dinerUserId, branchId, BranchReview.VisitWindowDays);

            throw new ReviewNeedsVisitException(branchId, BranchReview.VisitWindowDays);
        }
    }

    private Task<string?> DisplayNameAsync(Guid dinerUserId, CancellationToken cancellationToken) =>
        db.DinerUsers
            .AsNoTracking()
            .Where(d => d.Id == dinerUserId)
            .Select(d => d.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);

    private static DomainStateException AlreadyReviewed() =>
        new("You have already reviewed this place. Change that review instead of writing a second one.");

    private static DinerReviewView ToView(BranchReview review, string? displayName) =>
        new(
            review.Id,
            review.BranchId,
            review.Rating,
            review.Text,
            review.CreatedAtUtc,
            review.UpdatedAtUtc,
            BranchReview.PublicAuthorName(displayName),
            review.IsHidden);
}
