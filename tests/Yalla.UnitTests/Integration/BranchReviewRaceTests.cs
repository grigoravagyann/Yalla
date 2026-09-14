using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Domain;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Two writes of one diner's review of one branch that race past the service's read together.
/// </summary>
/// <remarks>
/// A double-tap on Submit. The service reads first for a readable answer, but only the unique index
/// <c>UX_BranchReviews_BranchId_DinerUserId</c> actually stops a second row, and the catch that turns
/// its violation into a 409 (POST) or a revision (PUT) is the only thing between that and a 500. A
/// sequential second request never reaches the catch, so the interceptor makes the competing write
/// commit between the read and the insert, every run.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class BranchReviewRaceTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Two_racing_puts_leave_one_review_and_the_late_one_revises_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var (branchId, dinerUserId) = await ArrangeAsync(clock);

        DinerReviewView? overtaker = null;

        var race = new BeforeFirstInsertInto("BranchReviews", async () =>
        {
            await using var otherDb = fixture.CreateContext(clock);

            (overtaker, _) = await Reviews(otherDb, clock, dinerUserId)
                .UpsertAsync(branchId, new SubmitBranchReviewCommand(5, "First tap."));
        });

        await using var racingDb = fixture.CreateContext(clock, race);

        var (late, created) = await Reviews(racingDb, clock, dinerUserId)
            .UpsertAsync(branchId, new SubmitBranchReviewCommand(3, "Second tap."));

        Assert.True(race.Fired, "The race never happened, so this proves nothing.");
        Assert.NotNull(overtaker);
        Assert.False(created, "the late write found the overtaker's row and revised it");
        Assert.Equal(overtaker.ReviewId, late.ReviewId);
        Assert.Equal(3, late.Rating);

        await using var verify = fixture.CreateContext(clock);
        var stored = Assert.Single(await verify.BranchReviews.AsNoTracking()
            .Where(r => r.BranchId == branchId && r.DinerUserId == dinerUserId)
            .ToListAsync());
        Assert.Equal(3, stored.Rating);
        Assert.Equal("Second tap.", stored.Text);
    }

    [SkippableFact]
    public async Task Two_racing_posts_leave_one_review_and_the_late_one_is_a_conflict()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var (branchId, dinerUserId) = await ArrangeAsync(clock);

        var race = new BeforeFirstInsertInto("BranchReviews", async () =>
        {
            await using var otherDb = fixture.CreateContext(clock);

            await Reviews(otherDb, clock, dinerUserId)
                .CreateAsync(branchId, new SubmitBranchReviewCommand(5, "First tap."));
        });

        await using var racingDb = fixture.CreateContext(clock, race);

        // DomainStateException is the API's 409; a DbUpdateException escaping here would be a 500.
        await Assert.ThrowsAsync<DomainStateException>(() => Reviews(racingDb, clock, dinerUserId)
            .CreateAsync(branchId, new SubmitBranchReviewCommand(3, "Second tap.")));

        Assert.True(race.Fired, "The race never happened, so this proves nothing.");

        await using var verify = fixture.CreateContext(clock);
        var stored = Assert.Single(await verify.BranchReviews.AsNoTracking()
            .Where(r => r.BranchId == branchId && r.DinerUserId == dinerUserId)
            .ToListAsync());
        Assert.Equal(5, stored.Rating);
    }

    private async Task<(Guid BranchId, Guid DinerUserId)> ArrangeAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var branch = await AuthTestData.CreateBranchAsync(db);

        var diner = new DinerUser($"+3741{Random.Shared.Next(1_000_000, 9_999_999)}", "en", "Ani Racer");
        diner.MarkPhoneVerified(clock.UtcNow);
        db.DinerUsers.Add(diner);
        await db.SaveChangesAsync();

        // A first review needs a visit (K8): a place at one of the branch's tables.
        await ReviewTestData.SeedTabVisitAsync(db, branch, clock.UtcNow, diner.Id);

        return (branch.BranchId, diner.Id);
    }

    private static BranchReviewService Reviews(YallaDbContext db, IClock clock, Guid dinerUserId) =>
        new(db, clock, TestActor.Diner(dinerUserId), NullLogger<BranchReviewService>.Instance);
}
