using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>RoomsWithPhotos</c> over a database that already has tables pinned on the cover photo: the old
/// pins are cleared, not carried over, and every table is kept - and Down puts the drawn plan's
/// columns back with values the old code accepts.
/// </summary>
/// <remarks>
/// On a throwaway database of its own, like <see cref="GeneralManagerMigrationTests"/>: going back a
/// migration under the shared schema would pull columns out from under every other test.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class RoomsWithPhotosMigrationTests(SqlServerFixture fixture)
{
    private const string RoomsMigration = "20261016130309_RoomsWithPhotos";

    private const string MigrationBefore = "20261016082952_GeneralManagerRole";

    [SkippableFact]
    public async Task Old_pins_on_the_cover_are_cleared_every_table_is_kept_and_the_cover_stays()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_Rooms_{Guid.NewGuid():N}",
        }.ConnectionString;

        var pinned = Guid.CreateVersion7();
        var unpinned = Guid.CreateVersion7();

        try
        {
            TestBranch branch;
            Guid cover;

            await using (var db = CreateContext(connectionString))
            {
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

                // That schema still has the drawn plan's canvas, NOT NULL and with no default, which
                // today's Branch no longer writes. A default stands in for what the old code sent. And
                // today's Branch reads and writes a column a later migration adds (BranchDistrict), so
                // it is put in place for the entity to find.
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE dbo.Branches ADD CONSTRAINT DF_Test_FloorWidth DEFAULT 1000 FOR FloorWidth; "
                    + "ALTER TABLE dbo.Branches ADD CONSTRAINT DF_Test_FloorHeight DEFAULT 700 FOR FloorHeight; "
                    + "ALTER TABLE dbo.Branches ADD District nvarchar(60) NULL;");

                branch = await TestBranchBuilder.CreateAsync(db, tableCount: 0, roomPhoto: false);
                cover = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);

                var area = await db.FloorAreas.Where(a => a.BranchId == branch.BranchId).Select(a => a.Id).SingleAsync();
                var now = DateTime.UtcNow;

                // Two tables the old way: geometry, and one of them pinned on the cover.
                await db.Database.ExecuteSqlAsync(
                    $"""
                     UPDATE dbo.Branches SET CoverPhotoId = {cover} WHERE Id = {branch.BranchId};

                     INSERT INTO dbo.DiningTables
                         (Id, BranchId, CreatedAtUtc, FloorAreaId, Label, Seats, X, Y, Width, Height, RotationDegrees,
                          Shape, IsBookable, IsActive, QrToken, Status, PhotoX, PhotoY)
                     VALUES
                         ({pinned}, {branch.BranchId}, {now}, {area}, '1', 4, 50, 100, 90, 90, 0, 2, 1, 1, {Token()}, 1, 0.3, 0.6),
                         ({unpinned}, {branch.BranchId}, {now}, {area}, '2', 2, 300, 100, 90, 90, 0, 1, 1, 1, {Token()}, 1, NULL, NULL);
                     """);

                await db.GetService<IMigrator>().MigrateAsync(RoomsMigration);
            }

            await using (var db = CreateContext(connectionString))
            {
                var tables = await db.DiningTables
                    .AsNoTracking()
                    .Where(t => t.BranchId == branch.BranchId)
                    .OrderBy(t => t.Label)
                    .ToListAsync();

                // Both tables, both unpinned: the cover is not a room photo, so the old places mean nothing.
                Assert.Equal([pinned, unpinned], tables.Select(t => t.Id));
                Assert.All(tables, t =>
                {
                    Assert.True(t.IsActive);
                    Assert.Null(t.PhotoId);
                    Assert.Null(t.PhotoX);
                    Assert.Null(t.PhotoY);
                });

                // No room was invented from the cover, and the cover itself is untouched.
                Assert.False(await db.FloorAreaPhotos.AnyAsync());
                Assert.Equal(["Windows"], await db.FloorAreas.Where(a => a.BranchId == branch.BranchId).Select(a => a.Name).ToListAsync());
                Assert.Equal(cover, (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == branch.BranchId)).CoverPhotoId);

                // Down: the drawn plan's columns come back with values the old code accepts.
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

                var canvas = await db.Database
                    .SqlQuery<int>($"SELECT FloorWidth AS Value FROM dbo.Branches WHERE Id = {branch.BranchId}")
                    .SingleAsync();
                Assert.Equal(1000, canvas);

                var widths = await db.Database
                    .SqlQuery<int>($"SELECT Width AS Value FROM dbo.DiningTables WHERE BranchId = {branch.BranchId}")
                    .ToListAsync();
                Assert.Equal([80, 80], widths);
            }
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    private static string Token() => Guid.NewGuid().ToString("N");

    private static YallaDbContext CreateContext(string connectionString) =>
        new(
            new DbContextOptionsBuilder<YallaDbContext>().UseSqlServer(connectionString).Options,
            new TestClock(DateTime.UtcNow));

    private static async Task DropAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var name = builder.InitialCatalog;

        SqlConnection.ClearPool(new SqlConnection(connectionString));

        builder.InitialCatalog = "master";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             IF DB_ID(N'{name}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{name}];
             END
             """;

        await command.ExecuteNonQueryAsync();
    }
}
