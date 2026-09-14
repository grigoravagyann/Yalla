using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Application.Diners;
using Yalla.Domain;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// One account on two phones: a heart or a report sent while the other phone deletes the account, and
/// two hearts racing the favourites limit.
/// </summary>
/// <remarks>
/// A request from the second phone can be past the token check - cached for five seconds, per process -
/// when the first phone deletes the account, and two taps can both count before either inserts. Neither
/// happens on a sequential test, so an interceptor makes the competing write happen at the worst moment,
/// every run. Where the fix makes that write wait, it is started and given up on after
/// <see cref="LockWait"/>, and awaited once the first write is done.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DinerAccountLockRaceTests(SqlServerFixture fixture)
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);

    [SkippableFact]
    public async Task A_heart_sent_while_the_account_is_deleted_is_refused_and_leaves_nothing_on_the_tombstone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        AuthBranch branch;
        Guid dinerUserId;
        await using (var db = fixture.CreateContext(clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            dinerUserId = await ReviewTestData.SeedDinerAsync(db, "Ani Twophones", clock.UtcNow);
        }

        // Phone B's heart has been authenticated; phone A's deletion commits before B touches the database.
        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("FROM [Branches]", StringComparison.OrdinalIgnoreCase),
            () => EraseAsync(clock, dinerUserId));

        await using var phoneB = fixture.CreateContext(clock, race);

        var refused = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => Favorites(phoneB, clock, dinerUserId).AddAsync(branch.BranchId));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        Assert.Equal("session-revoked", refused.ReasonCode);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.DinerFavorites.AnyAsync(f => f.DinerUserId == dinerUserId));
    }

    [SkippableFact]
    public async Task A_report_filed_while_the_account_is_deleted_is_refused_and_leaves_nothing_on_the_tombstone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        Guid reporterId;
        Guid reviewId;
        await using (var db = fixture.CreateContext(clock))
        {
            var branch = await AuthTestData.CreateBranchAsync(db);
            var authorId = await ReviewTestData.SeedDinerAsync(db, "Narek Petrosyan", clock.UtcNow);
            reporterId = await ReviewTestData.SeedDinerAsync(db, "Ani Twophones", clock.UtcNow);
            reviewId = await ReviewTestData.SeedReviewAsync(db, branch.BranchId, authorId, 1, "Cold soup.", clock.UtcNow);
        }

        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("FROM [BranchReviews]", StringComparison.OrdinalIgnoreCase),
            () => EraseAsync(clock, reporterId));

        await using var phoneB = fixture.CreateContext(clock, race);
        var reviews = new BranchReviewService(
            phoneB, clock, TestActor.Diner(reporterId), NullLogger<BranchReviewService>.Instance);

        var refused = await Assert.ThrowsAsync<AuthenticationFailedException>(
            () => reviews.ReportAsync(reviewId, new ReportReviewCommand("not-a-visit", "My neighbour, never went.")));

        Assert.True(race.Fired, "The deletion never ran mid-request, so this proves nothing.");
        Assert.Equal("session-revoked", refused.ReasonCode);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.BranchReviewReports.AnyAsync(r => r.DinerUserId == reporterId));
    }

    [SkippableFact]
    public async Task Two_hearts_racing_at_499_leave_500_and_the_later_one_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        AuthBranch home;
        Guid sibling;
        Guid dinerUserId;
        await using (var db = fixture.CreateContext(clock))
        {
            home = await AuthTestData.CreateBranchAsync(db);
            sibling = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan", tableCount: 1);
            dinerUserId = await ReviewTestData.SeedDinerAsync(db, "Ani Twophones", clock.UtcNow);

            db.DinerFavorites.AddRange(
                Enumerable.Range(0, DinerFavorite.MaxPerDiner - 1)
                    .Select(_ => new DinerFavorite(dinerUserId, Guid.CreateVersion7(), clock.UtcNow.AddDays(-1))));

            await db.SaveChangesAsync();
        }

        Task? otherPhone = null;

        // Phone A has counted 499 and is about to insert. Phone B hearts a different place now.
        var race = new BeforeFirstInsertInto("DinerFavorites", async () =>
        {
            otherPhone = Task.Run(async () =>
            {
                await using var otherDb = fixture.CreateContext(clock);
                await Favorites(otherDb, clock, dinerUserId).AddAsync(sibling);
            });

            await Task.WhenAny(otherPhone, Task.Delay(LockWait));
        });

        await using var phoneA = fixture.CreateContext(clock, race);
        var first = await Record.ExceptionAsync(() => Favorites(phoneA, clock, dinerUserId).AddAsync(home.BranchId));

        Assert.True(race.Fired, "The other heart never raced this one, so this proves nothing.");
        var second = await Record.ExceptionAsync(() => otherPhone!);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(DinerFavorite.MaxPerDiner, await verify.DinerFavorites.CountAsync(f => f.DinerUserId == dinerUserId));

        Assert.Null(first);
        Assert.IsType<DomainStateException>(second);
    }

    /// <summary>Phone A's deletion, committed in full on its own connection.</summary>
    private async Task EraseAsync(TestClock clock, Guid dinerUserId)
    {
        await using var db = fixture.CreateContext(clock);
        var diner = await db.DinerUsers.SingleAsync(d => d.Id == dinerUserId);

        // The account owns no pictures, so the photo service is never reached.
        await DinerAccountDeletion.EraseAsync(
            db, photos: null!, new RefreshTokenStore(db, clock), diner, clock.UtcNow, CancellationToken.None);
    }

    /// <summary>Adding a heart never reads the public card, which is all the listing query is for.</summary>
    private static DinerFavoriteService Favorites(YallaDbContext db, IClock clock, Guid dinerUserId) =>
        new(db, clock, TestActor.Diner(dinerUserId), listings: null!, NullLogger<DinerFavoriteService>.Instance);
}
