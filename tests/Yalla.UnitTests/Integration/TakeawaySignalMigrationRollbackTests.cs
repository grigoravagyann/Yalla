using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The <c>TakeawaySignalRowsHaveNoTable</c> migration goes down and up again over real rows: a table's transition and a
/// takeaway signal row (signal 8, no table).
/// </summary>
/// <remarks>
/// Down deletes the takeaway signal rows - the only ones with no table - before the column is made NOT NULL again, and
/// keeps every table's row. Up again lets a row with no table in.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TakeawaySignalMigrationRollbackTests(SqlServerFixture fixture)
{
    private const string TakeawaySignalMigration = "20261026170121_TakeawaySignalRowsHaveNoTable";

    private const string MigrationBefore = "20261024221338_ActivityHistory";

    [SkippableFact]
    public async Task Rolling_back_drops_only_the_takeaway_signal_rows_and_going_forward_takes_them_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_TaSignalRollback_{Guid.NewGuid():N}",
        }.ConnectionString;

        var now = TestNow.UtcNow;
        Guid branchId;
        Guid tableId;

        try
        {
            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(TakeawaySignalMigration);

                var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);
                branchId = branch.BranchId;
                tableId = branch.TableIds[0];

                db.TableStateChanges.Add(new TableStateChange(
                    branchId, tableId, TableStatus.Free, TableStatus.Held, "staff hold", ActorType.System, now,
                    Guid.CreateVersion7()));
                db.TableStateChanges.Add(TableStateChange.ForTakeaway(branchId, "takeaway placed", now));
                await db.SaveChangesAsync();

                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            }

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                Assert.Equal(1, await ScalarAsync(
                    connection, $"SELECT COUNT(*) FROM dbo.TableStateChanges WHERE BranchId = '{branchId}'"));
                Assert.Equal(1, await ScalarAsync(
                    connection, $"SELECT COUNT(*) FROM dbo.TableStateChanges WHERE DiningTableId = '{tableId}'"));
                Assert.Equal(0, await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                    + "WHERE TABLE_NAME = 'TableStateChanges' AND COLUMN_NAME = 'DiningTableId' AND IS_NULLABLE = 'YES'"));
            }

            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(TakeawaySignalMigration);

                db.TableStateChanges.Add(TableStateChange.ForTakeaway(branchId, "takeaway accepted", now));
                await db.SaveChangesAsync();

                Assert.Equal(
                    new Guid?[] { null, tableId },
                    await db.TableStateChanges.AsNoTracking()
                        .Where(c => c.BranchId == branchId)
                        .OrderBy(c => c.DiningTableId)
                        .Select(c => c.DiningTableId)
                        .ToListAsync());
            }
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

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
