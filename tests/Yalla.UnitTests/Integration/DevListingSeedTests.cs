using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Infrastructure;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Media;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The Development seed that makes the demo branch look like a real listing in the diner app.
/// </summary>
/// <remarks>
/// It runs on every Development startup, so the things worth proving are that a fresh database comes
/// up with everything the app shows - pictures that actually load included - that a second run adds
/// nothing, that a manager's own edits survive it, and that no other environment gets it at all.
/// Nothing here inserts a photo for the seed: every picture the branch shows is one the seed stored.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DevListingSeedTests(SqlServerFixture fixture) : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-dev-seed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A dropped database and an empty photo folder, started in Development with the seed on and the
    /// actor stub off - the mode the apps are tested in - is a demo branch the diner app can show.
    /// </summary>
    /// <remarks>
    /// Its own database rather than the shared one, so "fresh" is true: another test in the run may
    /// already have seeded the demo branch there, with its pictures in some other folder.
    /// </remarks>
    [SkippableFact]
    public async Task A_fresh_database_starts_with_the_demo_branch_pictured_pinned_and_bookable()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var database = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_DevSeed_{Guid.NewGuid():N}",
        };

        try
        {
            await using var factory = new YallaApiFactory()
                .WithDatabase(database.ConnectionString)
                .With("DevSeed:Enabled", "true")
                .With("PhotoStorage:RootPath", root);

            // Published by the seeder this host ran at startup.
            Assert.True(factory.Services.GetRequiredService<DevSeedRegistry>().IsSeeded);

            using (var scope = factory.Services.CreateScope())
            {
                Assert.Null(scope.ServiceProvider.GetService<DevCurrentActor>());
            }

            using var anonymous = factory.CreateClient();

            // The browse list: the demo branch, with a cover.
            var list = await anonymous.GetFromJsonAsync<JsonElement>("/api/public/branches");
            var demo = list.EnumerateArray().Single(b => b.GetProperty("venueSlug").GetString() == "yalla-demo");
            var branchId = demo.GetProperty("branchId").GetGuid();
            var cover = demo.GetProperty("coverPhoto");

            foreach (var variant in new[] { "thumbnailUrl", "cardUrl", "fullUrl" })
            {
                await AssertImageAsync(anonymous, cover.GetProperty(variant).GetString()!);
            }

            // Pins on that cover.
            var markers = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branchId}/table-markers");
            Assert.Equal(cover.GetProperty("photoId").GetGuid(), markers.GetProperty("photo").GetProperty("photoId").GetGuid());
            Assert.NotEmpty(markers.GetProperty("tables").EnumerateArray());

            // A gallery whose pictures load, and bookings open from the app.
            var detail = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branchId}");
            var gallery = detail.GetProperty("gallery").EnumerateArray().ToList();

            Assert.Equal(DevListingSeeder.GalleryResources.Length, gallery.Count);

            foreach (var picture in gallery)
            {
                await AssertImageAsync(anonymous, picture.GetProperty("cardUrl").GetString()!);
            }

            Assert.True(detail.GetProperty("acceptsWebBookings").GetBoolean());
            Assert.True(detail.GetProperty("acceptsAppBookings").GetBoolean());

            // Three rows, each with its three variants on disk under the configured root. Read through
            // the host's own context: this database is not the fixture's.
            using var readScope = factory.Services.CreateScope();
            var db = readScope.ServiceProvider.GetRequiredService<YallaDbContext>();

            var photos = await db.Photos.AsNoTracking().Where(p => p.BranchId == branchId).ToListAsync();

            Assert.Equal(1 + DevListingSeeder.GalleryResources.Length, photos.Count);
            Assert.All(photos, p => Assert.False(p.IsExternallyHosted));
            Assert.All(
                photos.SelectMany(p => new[] { p.ThumbnailPath, p.CardPath, p.FullPath }),
                key => Assert.True(File.Exists(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar))), key));
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    [SkippableFact]
    public async Task Seeding_twice_fills_the_listing_once_and_adds_nothing_the_second_time()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);

        await RunSeederAsync(clock);
        var first = await SnapshotAsync(clock);

        await RunSeederAsync(clock);
        var second = await SnapshotAsync(clock);

        Assert.Equal(first, second);
        Assert.Equal("Armenian & Mediterranean", first.Cuisine);
        Assert.InRange(first.Reviews, 3, 6);
        Assert.Equal(DevListingSeeder.ReviewerPhones.Length, first.Reviewers);

        // The pictures, stored by the seed itself, and the pins it put on the cover.
        Assert.NotNull(first.CoverPhotoId);
        Assert.Equal(DevListingSeeder.GalleryResources.Length, first.GalleryPictures);
        Assert.Equal(1 + DevListingSeeder.GalleryResources.Length, first.BranchPhotos);
        Assert.Equal(0, first.UnplacedTables);

        // Open to bookings from the app: the switch on and the policy marked reviewed (K9).
        Assert.True(first.AcceptsWebBookings);
        Assert.True(first.PolicyReviewed);
    }

    /// <summary>
    /// What a manager did to a branch between two restarts is still there after the second.
    /// </summary>
    /// <remarks>
    /// On a branch of its own rather than the demo branch, which the tests above own. The seeder is
    /// handed the branch; which one does not change what it does with it.
    /// </remarks>
    [SkippableFact]
    public async Task A_managers_cover_amenities_and_a_table_they_took_off_the_photo_survive_the_next_seed()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        Guid branchId;
        Guid coverId;

        await using (var db = fixture.CreateContext(clock))
        {
            branchId = (await AuthTestData.CreateBranchAsync(db, tableCount: 3)).BranchId;
            coverId = await TestMenuBuilder.AddPhotoAsync(db, branchId);

            var branch = await db.Branches.SingleAsync(b => b.Id == branchId);
            branch.SetCoverPhoto(coverId);

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

            // The manager's cover stays, and the seed adds no pictures of its own beside it.
            Assert.Equal(coverId, branch.CoverPhotoId);
            Assert.False(await db.BranchGalleryPhotos.AnyAsync(g => g.BranchId == branchId));
            Assert.Equal(1, await db.Photos.CountAsync(p => p.BranchId == branchId));

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
    /// A manager who has saved the reservation policy and switched bookings off keeps them off.
    /// </summary>
    [SkippableFact]
    public async Task A_policy_somebody_saved_is_left_alone_bookings_off_included()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        var reviewedAt = clock.UtcNow.AddDays(-3);
        Guid branchId;

        await using (var db = fixture.CreateContext(clock))
        {
            branchId = (await AuthTestData.CreateBranchAsync(db)).BranchId;

            var branch = await db.Branches.SingleAsync(b => b.Id == branchId);
            branch.UpdateReservationPolicy(branch.ReservationPolicy, reviewedAt);
            branch.SetAcceptsWebBookings(false);
            await db.SaveChangesAsync();
        }

        await SeedBranchAsync(branchId, clock);

        await using (var db = fixture.CreateContext(clock))
        {
            var branch = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == branchId);

            Assert.False(branch.AcceptsWebBookings);
            Assert.Equal(reviewedAt, branch.ReservationPolicyReviewedAtUtc!.Value, TimeSpan.FromMilliseconds(10));
        }
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

    // ------------------------------------------------------------ helpers

    private LocalDiskPhotoStorage Storage() =>
        new(new PhotoStorageOptions { RootPath = root }, NullLogger<LocalDiskPhotoStorage>.Instance);

    private async Task RunSeederAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var seeder = new DevDataSeeder(
            db,
            new DevSeedRegistry(),
            new DevListingSeeder(db, clock, Storage(), NullLogger<DevListingSeeder>.Instance),
            NullLogger<DevDataSeeder>.Instance);

        await seeder.SeedAsync();
    }

    /// <summary>The listing seed on one branch, as a restart would run it, in a fresh context.</summary>
    private async Task SeedBranchAsync(Guid branchId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var branch = await db.Branches.SingleAsync(b => b.Id == branchId);

        await new DevListingSeeder(db, clock, Storage(), NullLogger<DevListingSeeder>.Instance).SeedAsync(branch);
    }

    private sealed record Snapshot(
        string? Cuisine,
        int Reviews,
        int Reviewers,
        int UnplacedTables,
        Guid? CoverPhotoId,
        int GalleryPictures,
        int BranchPhotos,
        bool AcceptsWebBookings,
        bool PolicyReviewed);

    private async Task<Snapshot> SnapshotAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var branch = await db.Branches.AsNoTracking()
            .SingleAsync(b => b.Slug == "yerevan-centre" && b.Venue.Slug == "yalla-demo");

        return new Snapshot(
            branch.Cuisine,
            await db.BranchReviews.CountAsync(r => r.BranchId == branch.Id),
            await db.DinerUsers.CountAsync(d => DevListingSeeder.ReviewerPhones.Contains(d.PhoneE164) && d.PhoneVerifiedAtUtc != null),
            await db.DiningTables.CountAsync(t => t.BranchId == branch.Id && t.IsActive && t.PhotoX == null),
            branch.CoverPhotoId,
            await db.BranchGalleryPhotos.CountAsync(g => g.BranchId == branch.Id),
            await db.Photos.CountAsync(p => p.BranchId == branch.Id),
            branch.AcceptsWebBookings,
            branch.ReservationPolicyReviewedAtUtc is not null);
    }

    private static async Task AssertImageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("image/", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task DropDatabaseAsync(SqlConnectionStringBuilder database)
    {
        // A pooled connection to it would make the drop wait.
        SqlConnection.ClearAllPools();

        var master = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master" };

        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             IF DB_ID(N'{database.InitialCatalog}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{database.InitialCatalog}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{database.InitialCatalog}];
             END
             """;

        await command.ExecuteNonQueryAsync();
    }
}
