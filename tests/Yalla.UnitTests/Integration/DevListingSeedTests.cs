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

    /// <summary>
    /// What a manager did to a branch between two restarts is still there after the second.
    /// </summary>
    /// <remarks>
    /// On a branch of its own rather than the demo branch, which the test above owns. The seeder is
    /// handed the branch; which one does not change what it does with it.
    /// </remarks>
    [SkippableFact]
    public async Task A_managers_amenities_and_a_table_they_took_off_the_photo_survive_the_next_seed()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        Guid branchId;

        await using (var db = fixture.CreateContext(clock))
        {
            branchId = (await AuthTestData.CreateBranchAsync(db, tableCount: 3)).BranchId;
            var photoId = await TestMenuBuilder.AddPhotoAsync(db, branchId);

            var branch = await db.Branches.SingleAsync(b => b.Id == branchId);
            branch.SetCoverPhoto(photoId);

            // Amenities and nothing else: every field the old skip check looked at is empty.
            branch.UpdateListing(null, null, null, null, ["wifi", "vegan"]);
            await db.SaveChangesAsync();
        }

        await SeedBranchAsync(branchId, clock);

        Guid takenOff;
        await using (var db = fixture.CreateContext(clock))
        {
            var branch = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == branchId);
            Assert.Equal(["wifi", "vegan"], branch.Amenities);
            Assert.Null(branch.Cuisine);

            // Nobody had laid the photo out, so the seed placed every table.
            var tables = await db.DiningTables
                .Where(t => t.BranchId == branchId && t.IsActive)
                .OrderBy(t => t.Label)
                .ToListAsync();
            Assert.All(tables, t => Assert.NotNull(t.PhotoX));

            // The manager takes one off the picture.
            tables[0].PlaceOnPhoto(null, null);
            takenOff = tables[0].Id;
            await db.SaveChangesAsync();
        }

        await SeedBranchAsync(branchId, clock);

        await using (var db = fixture.CreateContext(clock))
        {
            Assert.Null((await db.DiningTables.AsNoTracking().SingleAsync(t => t.Id == takenOff)).PhotoX);
            Assert.Equal(2, await db.DiningTables.CountAsync(t => t.BranchId == branchId && t.IsActive && t.PhotoX != null));
            Assert.Equal(["wifi", "vegan"], (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == branchId)).Amenities);
        }
    }

    /// <summary>
    /// The mode the apps are really tested in: real sign-in, so no stub - and the demo data anyway.
    /// </summary>
    [SkippableFact]
    public async Task Development_seeds_the_demo_branch_with_the_dev_actor_switched_off()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        // DevActor:Enabled stays false, as the factory sets it.
        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("DevSeed:Enabled", "true");

        // Published by the seeder this host ran at startup, so it cannot be a demo branch left
        // behind in the shared database by another test.
        Assert.True(factory.Services.GetRequiredService<DevSeedRegistry>().IsSeeded);

        using var scope = factory.Services.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<DevCurrentActor>());
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task Nothing_is_seeded_outside_Development_even_with_seeding_switched_on(string devActorEnabled)
    {
        await using var factory = new YallaApiFactory()
            .WithEnvironment(Environments.Staging)
            .With("DevSeed:Enabled", "true")
            .With("DevActor:Enabled", devActorEnabled);

        Assert.Null(factory.Services.GetService<DevDataSeeder>());
        Assert.Null(factory.Services.GetService<DevListingSeeder>());
        Assert.Null(factory.Services.GetService<DevSeedRegistry>());
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

    /// <summary>The listing seed on one branch, as a restart would run it, in a fresh context.</summary>
    private async Task SeedBranchAsync(Guid branchId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var branch = await db.Branches.SingleAsync(b => b.Id == branchId);

        await new DevListingSeeder(db, clock, NullLogger<DevListingSeeder>.Instance).SeedAsync(branch);
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
            await db.DiningTables.CountAsync(t => t.BranchId == branch.Id && t.IsActive && t.PhotoX == null));
    }
}
