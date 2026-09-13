using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Domain.Media;
using Yalla.Infrastructure;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The Development seed that makes the demo branch look like a real listing in the diner app.
/// </summary>
/// <remarks>
/// It runs on every Development startup, so the two things worth proving are that a second run
/// adds nothing and that no other environment gets it at all.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class DevListingSeedTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Seeding_twice_fills_the_listing_once_and_adds_nothing_the_second_time()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);

        await RunSeederAsync(clock);

        // A cover photo, so the second run has tables to place - and must place each exactly once.
        await using (var db = fixture.CreateContext(clock))
        {
            var branch = await db.Branches.SingleAsync(b => b.Slug == "yerevan-centre" && b.Venue.Slug == "yalla-demo");

            if (branch.CoverPhotoId is null)
            {
                var photo = new Photo(branch.Id, $"dev-seed-{Guid.NewGuid():N}", "t.jpg", "c.jpg", "f.jpg", 1600, 900, 1, clock.UtcNow);
                db.Add(photo);
                branch.SetCoverPhoto(photo.Id);
                await db.SaveChangesAsync();
            }
        }

        await RunSeederAsync(clock);
        var first = await SnapshotAsync(clock);

        await RunSeederAsync(clock);
        var second = await SnapshotAsync(clock);

        Assert.Equal(first, second);
        Assert.Equal("Armenian & Mediterranean", first.Cuisine);
        Assert.InRange(first.Reviews, 3, 6);
        Assert.Equal(DevListingSeeder.ReviewerPhones.Length, first.Reviewers);
        Assert.Equal(0, first.UnplacedTables);
    }

    [Fact]
    public async Task Nothing_is_seeded_outside_Development_even_with_the_dev_actor_switched_on()
    {
        await using var factory = new YallaApiFactory()
            .WithEnvironment(Environments.Staging)
            .With("DevActor:Enabled", "true");

        Assert.Null(factory.Services.GetService<DevListingSeeder>());
        Assert.False(await factory.Services.InitialiseDevelopmentDataAsync());
    }

    private async Task RunSeederAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var seeder = new DevDataSeeder(
            db,
            new DevSeedRegistry(),
            new DevListingSeeder(db, clock, NullLogger<DevListingSeeder>.Instance),
            NullLogger<DevDataSeeder>.Instance);

        await seeder.SeedAsync();
    }

    private async Task<(string? Cuisine, int Reviews, int Reviewers, int UnplacedTables)> SnapshotAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var branch = await db.Branches.AsNoTracking()
            .SingleAsync(b => b.Slug == "yerevan-centre" && b.Venue.Slug == "yalla-demo");

        return (
            branch.Cuisine,
            await db.BranchReviews.CountAsync(r => r.BranchId == branch.Id),
            await db.DinerUsers.CountAsync(d => DevListingSeeder.ReviewerPhones.Contains(d.PhoneE164) && d.PhoneVerifiedAtUtc != null),
            await db.DiningTables.CountAsync(t => t.BranchId == branch.Id && t.PhotoX == null));
    }
}
