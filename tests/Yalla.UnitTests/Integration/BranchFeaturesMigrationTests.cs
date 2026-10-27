using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The <c>ReplaceSubscriptionTierWithBranchFeatures</c> migration over real rows: Free becomes Basic and Paid becomes
/// Full on the way up, and on the way down a branch with table service goes back to Paid and any other to Free.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class BranchFeaturesMigrationTests(SqlServerFixture fixture)
{
    private const string FeaturesMigration = "20261027000801_ReplaceSubscriptionTierWithBranchFeatures";

    private const string MigrationBefore = "20261026180542_StructuredDeviceLabels";

    [SkippableFact]
    public async Task Free_becomes_Basic_and_Paid_becomes_Full_and_down_maps_table_service_back_to_Paid()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_FeaturesMigration_{Guid.NewGuid():N}",
        }.ConnectionString;

        var now = TestNow.UtcNow;

        try
        {
            Guid free;
            Guid paid;
            Guid blocked;

            // The tier's schema, with a branch on each tier written as the old code wrote them: the builder's own insert
            // goes through today's entity, so the bits column it writes is added for the duration and dropped again.
            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
                await LaterColumns.AddBranchFeatureColumnsAsync(db);

                free = (await TestBranchBuilder.CreateAsync(db, tableCount: 1)).BranchId;
                paid = (await TestBranchBuilder.CreateAsync(db, tableCount: 1)).BranchId;
                blocked = (await TestBranchBuilder.CreateAsync(db, tableCount: 1)).BranchId;

                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE dbo.Branches DROP CONSTRAINT DF_Test_AllowedFeatures; "
                    + "ALTER TABLE dbo.Branches DROP COLUMN AllowedFeatures;");
                await db.Database.ExecuteSqlRawAsync(
                    $"UPDATE dbo.Branches SET SubscriptionTier = 1 WHERE Id IN ('{free}', '{blocked}'); "
                    + $"UPDATE dbo.Branches SET SubscriptionTier = 2 WHERE Id = '{paid}';");

                // The builder's rows are still tracked with what it wrote; read what the migration wrote instead.
                db.ChangeTracker.Clear();

                await db.GetService<IMigrator>().MigrateAsync(FeaturesMigration);

                Assert.Equal(BranchFeatures.BasicBits, await BitsAsync(db, free));
                Assert.Equal(BranchFeatures.FullBits, await BitsAsync(db, paid));
                Assert.Equal(1007, BranchFeatures.BasicBits);
                Assert.Equal(1023, BranchFeatures.FullBits);

                var row = await db.Branches.FirstAsync(b => b.Id == free);
                Assert.Equal(BranchFeatures.Basic, row.AllowedFeatures);
                Assert.False(row.Allows(Domain.Enums.BranchFeature.TableService));

                // A set the tier could never say: table service on, reports off.
                var odd = await db.Branches.FirstAsync(b => b.Id == blocked);
                odd.SetAllowedFeatures(
                    [.. BranchFeatures.Full.Where(f => f != Domain.Enums.BranchFeature.Reports)], incompleteMenuItemCount: 0);
                await db.SaveChangesAsync();

                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            }

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                Assert.Equal(1, await ScalarAsync(connection, $"SELECT SubscriptionTier FROM dbo.Branches WHERE Id = '{free}'"));
                Assert.Equal(2, await ScalarAsync(connection, $"SELECT SubscriptionTier FROM dbo.Branches WHERE Id = '{paid}'"));
                Assert.Equal(2, await ScalarAsync(connection, $"SELECT SubscriptionTier FROM dbo.Branches WHERE Id = '{blocked}'"));
                Assert.Equal(0, await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Branches' AND COLUMN_NAME = 'AllowedFeatures'"));
            }

            // And up again: what went down as Paid comes back Full.
            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(FeaturesMigration);

                Assert.Equal(BranchFeatures.BasicBits, await BitsAsync(db, free));
                Assert.Equal(BranchFeatures.FullBits, await BitsAsync(db, paid));
                Assert.Equal(BranchFeatures.FullBits, await BitsAsync(db, blocked));
            }
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    private static Task<int> BitsAsync(YallaDbContext db, Guid branchId) =>
        db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.AllowedFeatureBits).FirstAsync();

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static YallaDbContext CreateContext(string connectionString, DateTime now) =>
        new(
            new DbContextOptionsBuilder<YallaDbContext>().UseSqlServer(connectionString).Options,
            new TestClock(now));

    private static async Task DropAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var name = builder.InitialCatalog;

        // A lingering pooled connection makes DROP DATABASE hang.
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
