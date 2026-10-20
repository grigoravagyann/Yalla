using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>GeneralManagerRole</c> over a database that already holds venue-wide managers: every
/// branchless manager becomes a general manager, every branch manager stays one, and only then
/// does the role-branch constraint go on.
/// </summary>
/// <remarks>
/// On a throwaway database of its own, like <see cref="DinerDeletionMigrationRollbackTests"/>: going
/// back a migration under the shared schema would pull the constraint out from under every other test.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class GeneralManagerMigrationTests(SqlServerFixture fixture)
{
    private const string GeneralManagerMigration = "20261016082952_GeneralManagerRole";

    private const string MigrationBefore = "20261015142529_TelegramVerifications";

    [SkippableFact]
    public async Task A_branchless_manager_becomes_a_general_manager_and_a_branch_manager_stays_one()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_GeneralManager_{Guid.NewGuid():N}",
        }.ConnectionString;

        try
        {
            TestBranch branch;
            Guid venueWideManager;

            await using (var db = CreateContext(connectionString))
            {
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

                // That schema still has the drawn floor plan's canvas, NOT NULL and with no default,
                // and today's Branch no longer writes it (RoomsWithPhotos). A default stands in for the
                // value the old code would have sent; no tables and no room photo, which that schema
                // cannot hold either. And today's Branch and Venue read and write columns later migrations
                // add (BranchDistrict, VenueRestore, StaffSessionGeneration), so they are put in place for the entities to find.
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE dbo.Branches ADD CONSTRAINT DF_Test_FloorWidth DEFAULT 1000 FOR FloorWidth; "
                    + "ALTER TABLE dbo.Branches ADD CONSTRAINT DF_Test_FloorHeight DEFAULT 700 FOR FloorHeight; "
                    + "ALTER TABLE dbo.Branches ADD District nvarchar(60) NULL; "
                    + "ALTER TABLE dbo.Venues ADD ActiveBeforeDeletion bit NULL; "
                    + "ALTER TABLE dbo.StaffMembers ADD SessionGeneration int NOT NULL CONSTRAINT DF_Test_SessionGeneration DEFAULT 0;"
                    + "ALTER TABLE dbo.Branches ADD SpecialDaysVersion int NOT NULL CONSTRAINT DF_Test_SpecialDaysVersion DEFAULT 0;"
                    + "ALTER TABLE dbo.Branches ADD ReservationPolicy_UnverifiedBookingsNeedApproval bit NOT NULL CONSTRAINT DF_Test_UnverifiedApproval DEFAULT 1; "
                    + "ALTER TABLE dbo.Branches ADD ReservationPolicy_UnverifiedMaxPartySize int NOT NULL CONSTRAINT DF_Test_UnverifiedMaxParty DEFAULT 6;");

                // The fixture branch's manager names the branch. A second person is made a branchless
                // manager the only way today's entity allows - as somebody else, then rewritten - which
                // is what the old code wrote for "manages every branch".
                branch = await TestBranchBuilder.CreateAsync(db, tableCount: 0, roomPhoto: false);

                var venueWide = new StaffMember(branch.VenueId, "Venue-Wide Manager", "+37455500100", StaffRole.Owner, "hash");
                db.StaffMembers.Add(venueWide);
                await db.SaveChangesAsync();
                venueWideManager = venueWide.Id;

                await db.Database.ExecuteSqlAsync(
                    $"UPDATE dbo.StaffMembers SET Role = 2 WHERE Id = {venueWideManager}");

                await db.GetService<IMigrator>().MigrateAsync(GeneralManagerMigration);
            }

            await using (var db = CreateContext(connectionString))
            {
                var roles = await db.StaffMembers
                    .AsNoTracking()
                    .Where(s => s.VenueId == branch.VenueId)
                    .ToDictionaryAsync(s => s.Id, s => (s.Role, s.BranchId));

                Assert.Equal((StaffRole.GeneralManager, (Guid?)null), roles[venueWideManager]);
                Assert.Equal((StaffRole.Manager, (Guid?)branch.BranchId), roles[branch.ManagerId]);
                Assert.Equal((StaffRole.Waiter, (Guid?)branch.BranchId), roles[branch.WaiterId]);

                // And the rule is now the table's: neither half of a bad pair can be written.
                await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync(
                    $"UPDATE dbo.StaffMembers SET BranchId = NULL WHERE Id = {branch.ManagerId}"));
                await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync(
                    $"UPDATE dbo.StaffMembers SET BranchId = {branch.BranchId} WHERE Id = {venueWideManager}"));

                // Down puts the venue-wide manager back exactly as the old code understood them.
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

                var reverted = await db.Database
                    .SqlQuery<int>($"SELECT Role AS Value FROM dbo.StaffMembers WHERE Id = {venueWideManager}")
                    .SingleAsync();
                Assert.Equal((int)StaffRole.Manager, reverted);
            }
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

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
