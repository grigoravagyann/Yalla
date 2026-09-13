using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Domain;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A diner rating a branch: one review each, revised rather than repeated, proved phone numbers only.
/// </summary>
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

        var review = await db.BranchReviews
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.BranchId == branchId && r.DinerUserId == dinerUserId, cancellationToken)
            ?? throw new KeyNotFoundException("You have not reviewed this place.");

        return ToView(review);
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

        if (await db.BranchReviews.AnyAsync(r => r.BranchId == branchId && r.DinerUserId == dinerUserId, cancellationToken))
        {
            throw AlreadyReviewed();
        }

        var review = new BranchReview(branchId, dinerUserId, command.Rating, command.Text, clock.UtcNow);
        db.BranchReviews.Add(review);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.BranchReviewPerDiner))
        {
            // Two taps on Submit. The index is the rule; the check above is only the readable answer.
            throw AlreadyReviewed();
        }

        logger.LogInformation("Diner {DinerUserId} reviewed branch {BranchId}: {Rating} stars.", dinerUserId, branchId, review.Rating);

        return ToView(review);
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

        for (var attempt = 1; ; attempt++)
        {
            var review = await db.BranchReviews
                .FirstOrDefaultAsync(r => r.BranchId == branchId && r.DinerUserId == dinerUserId, cancellationToken);

            var created = review is null;

            if (review is null)
            {
                review = new BranchReview(branchId, dinerUserId, command.Rating, command.Text, clock.UtcNow);
                db.BranchReviews.Add(review);
            }
            else
            {
                review.Revise(command.Rating, command.Text, clock.UtcNow);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);

                return (ToView(review), created);
            }
            catch (DbUpdateException ex) when (attempt == 1 && UniqueViolation.IsOn(ex, DatabaseIndexNames.BranchReviewPerDiner))
            {
                // A concurrent first write landed between the read and the insert. Revise that one.
                db.ChangeTracker.Clear();
            }
        }
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

    private static DomainStateException AlreadyReviewed() =>
        new("You have already reviewed this place. Change that review instead of writing a second one.");

    private static DinerReviewView ToView(BranchReview review) =>
        new(review.Id, review.BranchId, review.Rating, review.Text, review.CreatedAtUtc, review.UpdatedAtUtc);
}
