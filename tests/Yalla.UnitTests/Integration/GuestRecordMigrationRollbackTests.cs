using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The <c>GuestRecord</c> migration goes down and up again over real rows: a branch, and a guest blocked for
/// no-shows with the feed entry that told them.
/// </summary>
/// <remarks>
/// Down gives the branch Yalla#54's settings back with #54's defaults (unverified bookings wait for the venue, six
/// at most), drops the blocks, and removes the block notices before the older check on notification kinds comes
/// back - which would otherwise refuse to be created over them. Up again starts "auto-confirm guests with a good
/// record" off for every branch: it is not a rename of #54's flag, which defaulted on.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class GuestRecordMigrationRollbackTests(SqlServerFixture fixture)
{
    private const string GuestRecordMigration = "20261020180944_GuestRecord";

    private const string MigrationBefore = "20261020075810_UnverifiedGuests";

    [SkippableFact]
    public async Task Rolling_back_restores_the_old_settings_and_drops_blocks_and_their_notices_and_goes_forward_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_GrRollback_{Guid.NewGuid():N}",
        }.ConnectionString;

        var now = DateTime.UtcNow;
        var diner = Guid.CreateVersion7();
        Guid branchId;

        try
        {
            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(GuestRecordMigration);

                await LaterColumns.AddPreOrderColumnsAsync(db);

                branchId = (await TestBranchBuilder.CreateAsync(db, tableCount: 1)).BranchId;
                const string paramsJson = "{\"cancelledCount\":\"2\"}";

                await db.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO dbo.DinerUsers (Id, CreatedAtUtc, IsActive, LocaleCode, PhoneE164, PhoneVerifiedAtUtc)
                     VALUES ({diner}, {now}, 1, 'en', '+37499000077', {now});
                     INSERT INTO dbo.BookingBlocks (Id, DinerUserId, PhoneE164, BlockedAtUtc, BlockedUntilUtc, Reason, CreatedAtUtc)
                     VALUES ({Guid.CreateVersion7()}, {diner}, '+37499000077', {now}, {now.AddDays(7)}, 1, {now});
                     INSERT INTO dbo.DinerNotifications (Id, DinerUserId, Kind, ParamsJson, CreatedAtUtc)
                     VALUES ({Guid.CreateVersion7()}, {diner}, 'bookings-blocked-no-shows', {paramsJson}, {now});
                     """);

                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            }

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                Assert.Equal(1, await ScalarAsync(
                    connection, $"SELECT CAST(ReservationPolicy_UnverifiedBookingsNeedApproval AS int) FROM dbo.Branches WHERE Id = '{branchId}'"));
                Assert.Equal(6, await ScalarAsync(
                    connection, $"SELECT ReservationPolicy_UnverifiedMaxPartySize FROM dbo.Branches WHERE Id = '{branchId}'"));
                Assert.Equal(0, await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'BookingBlocks'"));
                Assert.Equal(0, await ScalarAsync(
                    connection, $"SELECT COUNT(*) FROM dbo.DinerNotifications WHERE DinerUserId = '{diner}'"));
            }

            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(GuestRecordMigration);

                var policy = await db.Branches.AsNoTracking()
                    .Where(b => b.Id == branchId)
                    .Select(b => b.ReservationPolicy)
                    .SingleAsync();

                Assert.False(policy.AutoConfirmGuestsWithGoodRecord);
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
