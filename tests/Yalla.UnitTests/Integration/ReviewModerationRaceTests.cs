using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Application.Reviews;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A venue putting a review back at the moment the platform takes it down.
/// </summary>
/// <remarks>
/// The venue's request reads the review a moment before the platform's hide commits, so it passes the
/// "the platform hid it" check, and then writes only the columns it changed. Without serialisation the
/// review is public again with <c>HiddenByPlatform</c> still set - a venue undoing a platform takedown,
/// and a flag that makes the venue's next hide a silent no-op. The interceptor commits the platform's
/// hide between the venue's read and its update, every run; with the lock in place the platform waits
/// for the venue instead, and is given up on after a few seconds and awaited at the end.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ReviewModerationRaceTests(SqlServerFixture fixture)
{
    private const string PlatformReason = "Defamatory - platform takedown.";

    [SkippableFact]
    public async Task A_venue_unhide_racing_a_platform_takedown_never_leaves_the_review_public()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        AuthBranch branch;
        PlatformAdminAccount admin;
        Guid reviewId;
        await using (var db = fixture.CreateContext(clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);

            var authorId = await ReviewTestData.SeedDinerAsync(db, "Narek Petrosyan", clock.UtcNow);
            reviewId = await ReviewTestData.SeedReviewAsync(
                db, branch.BranchId, authorId, 1, "Names the manager.", clock.UtcNow.AddMinutes(-30));

            // Taken down by the venue first, so the manager has something to put back.
            var review = await db.BranchReviews.SingleAsync(r => r.Id == reviewId);
            review.Hide(branch.ManagerId, byPlatform: false, "Names a member of staff.", clock.UtcNow.AddMinutes(-10));
            await db.SaveChangesAsync();
        }

        Task? takedown = null;

        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDATE [BranchReviews]", StringComparison.OrdinalIgnoreCase),
            async () =>
            {
                takedown = Task.Run(async () =>
                {
                    await using var adminDb = fixture.CreateContext(clock);

                    await Moderation(adminDb, clock, TestActor.PlatformAdmin(admin.StaffMemberId))
                        .SetVisibilityForPlatformAsync(reviewId, new SetReviewVisibilityCommand(true, PlatformReason));
                });

                await Task.WhenAny(takedown, Task.Delay(TimeSpan.FromSeconds(3)));
            });

        await using var managerDb = fixture.CreateContext(clock, race);

        await Moderation(managerDb, clock, TestActor.Manager(branch.ManagerId))
            .SetVisibilityForVenueAsync(branch.BranchId, reviewId, new SetReviewVisibilityCommand(false, null));

        Assert.True(race.Fired, "The platform's takedown never raced the venue, so this proves nothing.");
        await takedown!;

        await using var verify = fixture.CreateContext(clock);
        var stored = await verify.BranchReviews.AsNoTracking().SingleAsync(r => r.Id == reviewId);

        Assert.True(stored.HiddenAtUtc is not null, "A venue that read the review first undid the platform's takedown.");
        Assert.True(stored.HiddenByPlatform);
        Assert.Equal(PlatformReason, stored.HiddenReason);
    }

    private ReviewModerationService Moderation(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, fixture.CreateBranchGuard(db, actor), NullLogger<ReviewModerationService>.Instance);
}
