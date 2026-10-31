using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The <c>GuestMessages</c> migration goes down and up again over real rows: a branch whose settings were changed, a
/// thread on a takeaway order with a message, and a venue message's feed entry and queued push.
/// </summary>
/// <remarks>
/// Down drops the threads and the branch's message settings, and removes the <c>venue-message</c> feed entries and the
/// queued <c>message.to-guest</c> pushes before the older check on notification kinds comes back - which would otherwise
/// refuse to be created over them. Up again starts every branch with guests and waiters writing, the default replies and
/// a three-minute reminder.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class GuestMessagesMigrationRollbackTests(SqlServerFixture fixture)
{
    private const string GuestMessagesMigration = "20261023001536_GuestMessages";

    private const string MigrationBefore = "20261022210621_TakeawayRecordAndReports";

    [SkippableFact]
    public async Task Rolling_back_drops_threads_and_their_notices_and_going_forward_starts_with_the_defaults()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_MsgRollback_{Guid.NewGuid():N}",
        }.ConnectionString;

        var now = TestNow.UtcNow;
        var diner = Guid.CreateVersion7();
        var order = Guid.CreateVersion7();
        var thread = Guid.CreateVersion7();
        Guid branchId;

        try
        {
            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(GuestMessagesMigration);

                // The builder writes today's Branches row, so the later features column is put in place.
                await LaterColumns.AddBranchFeatureColumnsAsync(db);

                branchId = (await TestBranchBuilder.CreateAsync(db, tableCount: 1, enrolTablet: false)).BranchId;
                const string paramsJson = "{\"venueName\":\"Cafe\"}";
                const string payloadJson = "{}";
                const string templates = "[\"OK\"]";
                var key = $"conversation:{thread}:message:x";

                await db.Database.ExecuteSqlAsync(
                    $"""
                     UPDATE dbo.Branches SET GuestsCanMessage = 0, WaitersCanMessage = 0, MessageRemindAfterMinutes = 9,
                         MessageTemplatesJson = {templates} WHERE Id = {branchId};
                     INSERT INTO dbo.DinerUsers (Id, CreatedAtUtc, IsActive, LocaleCode, PhoneE164, PhoneVerifiedAtUtc)
                     VALUES ({diner}, {now}, 1, 'en', '+37499000079', {now});
                     INSERT INTO dbo.TakeawayOrders (Id, BranchId, DinerUserId, Code, LocalDate, GuestName, GuestPhone, PickupAsap,
                         RequestedPickupUtc, RequestedPickupLocalTime, Status, ClientCommandId, CreatedAtUtc)
                     VALUES ({order}, {branchId}, {diner}, 'T-001', {DateOnly.FromDateTime(now)}, 'Ani', '+37499000079', 1,
                         {now}, '12:00', 1, {Guid.CreateVersion7()}, {now});
                     INSERT INTO dbo.Conversations (Id, BranchId, TakeawayOrderId, DinerUserId, LastMessageUtc, CreatedAtUtc)
                     VALUES ({thread}, {branchId}, {order}, {diner}, {now}, {now});
                     INSERT INTO dbo.ConversationMessages (Id, ConversationId, Author, Kind, Body, CreatedAtUtc)
                     VALUES ({Guid.CreateVersion7()}, {thread}, 1, 1, 'Hello', {now});
                     INSERT INTO dbo.DinerNotifications (Id, DinerUserId, Kind, ParamsJson, CreatedAtUtc)
                     VALUES ({Guid.CreateVersion7()}, {diner}, 'venue-message', {paramsJson}, {now});
                     INSERT INTO dbo.OutboxMessages (Id, Type, PayloadJson, ScheduledForUtc, AttemptCount, IdempotencyKey, CreatedAtUtc)
                     VALUES ({Guid.CreateVersion7()}, 'message.to-guest', {payloadJson}, {now}, 0, {key}, {now});
                     """);

                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            }

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                Assert.Equal(0, await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ('Conversations', 'ConversationMessages')"));
                Assert.Equal(0, await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Branches' AND COLUMN_NAME IN "
                    + "('GuestsCanMessage', 'WaitersCanMessage', 'MessageTemplatesJson', 'MessageRemindAfterMinutes')"));
                Assert.Equal(0, await ScalarAsync(
                    connection, $"SELECT COUNT(*) FROM dbo.DinerNotifications WHERE DinerUserId = '{diner}'"));
                Assert.Equal(0, await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM dbo.OutboxMessages WHERE Type = 'message.to-guest'"));
                Assert.Equal(1, await ScalarAsync(
                    connection, $"SELECT COUNT(*) FROM dbo.TakeawayOrders WHERE Id = '{order}'"));
            }

            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(GuestMessagesMigration);

                var branch = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == branchId);
                Assert.True(branch.GuestsCanMessage);
                Assert.True(branch.WaitersCanMessage);
                Assert.Null(branch.MessageTemplatesJson);
                Assert.Equal(3, branch.MessageRemindAfterMinutes);
                Assert.False(await db.Conversations.AnyAsync());
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
