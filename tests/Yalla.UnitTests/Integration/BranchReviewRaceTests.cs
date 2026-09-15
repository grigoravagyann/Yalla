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
/// Two writes of one diner's review of one branch, the second sent while the first is inserting.
/// </summary>
/// <remarks>
/// <para>
/// A double-tap on Submit. Both writes take the account's lock (<c>DinerAccountLock</c>) before they
/// read, so the second waits for the first's commit and then finds its row: a PUT revises it and a POST
/// is the 409. The unique index <c>UX_BranchReviews_BranchId_DinerUserId</c> and its catch stay behind
/// that as the backstop.
/// </para>
/// <para>
/// The interceptor starts the second tap just before the first inserts. It cannot be committed in full
/// there - it is waiting on the lock the first holds - so it is given <see cref="LockWait"/>, the first
/// goes on and commits, and the second is awaited after.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class BranchReviewRaceTests(SqlServerFixture fixture)
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);

    [SkippableFact]
    public async Task Two_racing_puts_leave_one_review_and_the_late_one_revises_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var (branchId, dinerUserId) = await ArrangeAsync(clock);

        Task<(DinerReviewView Review, bool Created)>? secondTap = null;

        var race = new BeforeFirstInsertInto("BranchReviews", async () =>
        {
            secondTap = Task.Run(async () =>
            {
                await using var otherDb = fixture.CreateContext(clock);

                return await Reviews(otherDb, clock, dinerUserId)
                    .UpsertAsync(branchId, new SubmitBranchReviewCommand(3, "Second tap."));
            });

            await Task.WhenAny(secondTap, Task.Delay(LockWait));
        });

        await using var racingDb = fixture.CreateContext(clock, race);

        var (first, firstCreated) = await Reviews(racingDb, clock, dinerUserId)
            .UpsertAsync(branchId, new SubmitBranchReviewCommand(5, "First tap."));

        Assert.True(race.Fired, "The race never happened, so this proves nothing.");
        var (late, lateCreated) = await secondTap!;

        Assert.True(firstCreated);
        Assert.False(lateCreated, "the late write found the first one's row and revised it");
        Assert.Equal(first.ReviewId, late.ReviewId);
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

        Task? secondTap = null;

        var race = new BeforeFirstInsertInto("BranchReviews", async () =>
        {
            secondTap = Task.Run(async () =>
            {
                await using var otherDb = fixture.CreateContext(clock);

                await Reviews(otherDb, clock, dinerUserId)
                    .CreateAsync(branchId, new SubmitBranchReviewCommand(3, "Second tap."));
            });

            await Task.WhenAny(secondTap, Task.Delay(LockWait));
        });

        await using var racingDb = fixture.CreateContext(clock, race);

        await Reviews(racingDb, clock, dinerUserId)
            .CreateAsync(branchId, new SubmitBranchReviewCommand(5, "First tap."));

        Assert.True(race.Fired, "The race never happened, so this proves nothing.");

        // DomainStateException is the API's 409; a DbUpdateException escaping here would be a 500.
        await Assert.ThrowsAsync<DomainStateException>(() => secondTap!);

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
