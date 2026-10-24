using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Rolling back <c>UnverifiedGuests</c> on a database that already holds a sitting with a turned-away tab and
/// the real party's tab after it (review S5): the old unfiltered one-tab-per-sitting index cannot hold both, so
/// the Down deletes the phantom - and must do it while the column it reads still exists.
/// </summary>
/// <remarks>
/// On a throwaway database of its own rather than the fixture's: a rollback under the shared schema would pull
/// columns out from under every other test.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class UnverifiedGuestsMigrationRollbackTests(SqlServerFixture fixture)
{
    private const string UnverifiedGuestsMigration = "20261020075810_UnverifiedGuests";

    private const string MigrationBefore = "20261019213900_ManageLinkNoticeKinds";

    [SkippableFact]
    public async Task Rolling_back_with_a_turned_away_tab_beside_a_live_one_keeps_the_live_one_and_goes_forward_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_UgRollback_{Guid.NewGuid():N}",
        }.ConnectionString;

        var now = TestNow.UtcNow;
        Guid sessionId;
        Guid phantomId;
        Guid liveId;

        try
        {
            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(UnverifiedGuestsMigration);

                // GuestRecord sorts after this migration, but today's Branch writes its column.
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE dbo.Branches ADD ReservationPolicy_AutoConfirmGuestsWithGoodRecord bit NOT NULL "
                    + "CONSTRAINT DF_Test_GoodRecord DEFAULT 0;");

                await LaterColumns.AddPreOrderColumnsAsync(db);

                var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);
                var tableId = branch.TableIds[0];

                var session = TableSession.SeatWalkIn(branch.BranchId, tableId, 2, now, branch.WaiterId);
                db.TableSessions.Add(session);
                await db.SaveChangesAsync();
                sessionId = session.Id;

                // The phantom: a scan that waited, turned away by the waiter.
                var phantom = new Tab(branch.BranchId, tableId, session.Id, now, 0m, admittedBecause: null);
                var phantomHost = TabParticipant.Host(phantom.Id, "Guest 1", "phone-phantom", now, null);
                phantom.SetHostParticipant(phantomHost.Id);
                db.Tabs.Add(phantom);
                db.TabParticipants.Add(phantomHost);
                session.AttachTab(phantom.Id);
                await db.SaveChangesAsync();

                Assert.True(phantom.RejectAdmission(now, branch.WaiterId));
                session.DetachRejectedTab(phantom.Id);
                await db.SaveChangesAsync();
                phantomId = phantom.Id;

                // The real party's, on the same sitting.
                var live = new Tab(branch.BranchId, tableId, session.Id, now, 0m, TabAdmissionReason.OpenedByStaff);
                db.Tabs.Add(live);
                session.AttachTab(live.Id);
                await db.SaveChangesAsync();
                liveId = live.Id;

                // Failed before the fix: the cleanup read Tabs.Admission after the column was dropped.
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            }

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                var tabs = new List<Guid>();

                await using (var command = new SqlCommand("SELECT Id FROM dbo.Tabs WHERE TableSessionId = @session", connection))
                {
                    command.Parameters.AddWithValue("@session", sessionId);

                    await using var reader = await command.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        tabs.Add(reader.GetGuid(0));
                    }
                }

                Assert.Equal([liveId], tabs);
                Assert.DoesNotContain(phantomId, tabs);

                await using var participants = new SqlCommand("SELECT COUNT(*) FROM dbo.TabParticipants WHERE TabId = @tab", connection);
                participants.Parameters.AddWithValue("@tab", phantomId);
                Assert.Equal(0, (int)(await participants.ExecuteScalarAsync())!);

                await using var column = new SqlCommand(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Tabs' AND COLUMN_NAME = 'Admission'",
                    connection);
                Assert.Equal(0, (int)(await column.ExecuteScalarAsync())!);
            }

            // And forward again over the rolled-back rows.
            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(UnverifiedGuestsMigration);

                var live = await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == liveId);
                Assert.Equal(TabAdmission.Admitted, live.Admission);
            }
        }
        finally
        {
            await DropAsync(connectionString);
        }
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
