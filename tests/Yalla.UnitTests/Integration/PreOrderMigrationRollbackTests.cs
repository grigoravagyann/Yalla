using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The <c>PreOrders</c> migration goes down and up again over real rows: a branch with a menu, and an "on your way"
/// question queued with its feed entry.
/// </summary>
/// <remarks>
/// Down drops the pre-order tables and columns, and removes the question's feed entries and queued pushes before the
/// older check on notification kinds comes back - which would otherwise refuse to be created over them. Up again starts
/// every branch with pre-orders off and 30 minutes, and every dish already on a menu pre-orderable.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class PreOrderMigrationRollbackTests(SqlServerFixture fixture)
{
    private const string PreOrdersMigration = "20261021120922_PreOrders";

    private const string MigrationBefore = "20261020193232_GuestRecordReview";

    [SkippableFact]
    public async Task Rolling_back_drops_pre_orders_and_their_questions_and_going_forward_starts_them_off()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_PoRollback_{Guid.NewGuid():N}",
        }.ConnectionString;

        var now = DateTime.UtcNow;
        var diner = Guid.CreateVersion7();
        Guid branchId;

        try
        {
            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreOrdersMigration);

                branchId = (await TestBranchBuilder.CreateAsync(db, tableCount: 1)).BranchId;
                await TestMenuBuilder.CreateAsync(db, branchId);
                const string paramsJson = "{\"venueName\":\"Cafe\"}";
                const string payloadJson = "{}";
                var key = $"reservation:{diner}:on-your-way:x";

                await db.Database.ExecuteSqlAsync(
                    $"""
                     UPDATE dbo.Branches SET AcceptsPreOrders = 1, PreOrderMinutesBefore = 45 WHERE Id = {branchId};
                     INSERT INTO dbo.DinerUsers (Id, CreatedAtUtc, IsActive, LocaleCode, PhoneE164, PhoneVerifiedAtUtc)
                     VALUES ({diner}, {now}, 1, 'en', '+37499000078', {now});
                     INSERT INTO dbo.DinerNotifications (Id, DinerUserId, Kind, ParamsJson, CreatedAtUtc)
                     VALUES ({Guid.CreateVersion7()}, {diner}, 'booking-on-your-way', {paramsJson}, {now});
                     INSERT INTO dbo.OutboxMessages (Id, Type, PayloadJson, ScheduledForUtc, AttemptCount, IdempotencyKey, CreatedAtUtc)
                     VALUES ({Guid.CreateVersion7()}, 'reservation.on-your-way', {payloadJson}, {now}, 0, {key}, {now});
                     """);

                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            }

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                Assert.Equal(0, await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ('PreOrders', 'PreOrderLines')"));
                Assert.Equal(0, await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE COLUMN_NAME IN "
                    + "('AcceptsPreOrders', 'PreOrderMinutesBefore', 'AvailableForPreOrder', 'GuestConfirmedOnWayAtUtc')"));
                Assert.Equal(0, await ScalarAsync(
                    connection, $"SELECT COUNT(*) FROM dbo.DinerNotifications WHERE DinerUserId = '{diner}'"));
                Assert.Equal(0, await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM dbo.OutboxMessages WHERE Type = 'reservation.on-your-way'"));
            }

            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreOrdersMigration);

                var branch = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == branchId);
                Assert.False(branch.AcceptsPreOrders);
                Assert.Equal(30, branch.PreOrderMinutesBefore);

                Assert.All(
                    await db.MenuItems.AsNoTracking().Where(m => m.MenuCategory.BranchId == branchId).ToListAsync(),
                    m => Assert.True(m.AvailableForPreOrder));
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
