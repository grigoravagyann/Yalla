using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Rolling back <c>DinerSessionGenerationAndDeletion</c> on a database that already holds deleted
/// accounts, whose rows keep no number.
/// </summary>
/// <remarks>
/// On a throwaway database of its own rather than the fixture's: a rollback under the shared schema
/// would pull columns out from under every other test. The fixture supplies the server and the login.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DinerDeletionMigrationRollbackTests(SqlServerFixture fixture)
{
    private const string DeletionMigration = "20260914102955_DinerSessionGenerationAndDeletion";

    private const string MigrationBefore = "20260913235419_TabParticipantsByDinerAccount";

    [SkippableFact]
    public async Task Rolling_back_with_deleted_accounts_gives_each_a_unique_placeholder_and_keeps_every_row()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_Rollback_{Guid.NewGuid():N}",
        }.ConnectionString;

        var live = Guid.CreateVersion7();
        var firstDeleted = Guid.CreateVersion7();
        var secondDeleted = Guid.CreateVersion7();
        var leftByEarlierRollback = Guid.CreateVersion7();

        try
        {
            await using (var db = CreateContext(connectionString))
            {
                await db.GetService<IMigrator>().MigrateAsync(DeletionMigration);

                var now = DateTime.UtcNow;

                await db.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO dbo.DinerUsers (Id, CreatedAtUtc, IsActive, LocaleCode, PhoneE164, DeletedAtUtc)
                     VALUES ({live}, {now}, 1, 'en', '+37499000001', NULL),
                            ({firstDeleted}, {now}, 0, 'en', NULL, {now}),
                            ({secondDeleted}, {now}, 0, 'en', NULL, {now}),
                            ({leftByEarlierRollback}, {now}, 0, 'en', 'deleted:7', NULL);
                     """);

                // Before the fix this step failed: NOT NULL over two NULLs, then '' twice under a unique index.
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            }

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                var rows = new Dictionary<Guid, (string Phone, bool IsActive)>();

                await using (var command = new SqlCommand("SELECT Id, PhoneE164, IsActive FROM dbo.DinerUsers", connection))
                await using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        rows[reader.GetGuid(0)] = (reader.GetString(1), reader.GetBoolean(2));
                    }
                }

                Assert.Equal(4, rows.Count);
                Assert.Equal(("+37499000001", true), rows[live]);
                Assert.Equal(("deleted:7", false), rows[leftByEarlierRollback]);

                // Numbered past the placeholder already there, one each, and never switched back on.
                Assert.Equal(
                    ["deleted:8", "deleted:9"],
                    new[] { rows[firstDeleted].Phone, rows[secondDeleted].Phone }.Order(StringComparer.Ordinal).ToArray());
                Assert.False(rows[firstDeleted].IsActive);
                Assert.False(rows[secondDeleted].IsActive);

                await using var nullable = new SqlCommand(
                    "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'DinerUsers' AND COLUMN_NAME = 'PhoneE164'",
                    connection);
                Assert.Equal("NO", (string?)await nullable.ExecuteScalarAsync());
            }

            // And forward again over the rolled-back rows.
            await using (var db = CreateContext(connectionString))
            {
                await db.GetService<IMigrator>().MigrateAsync(DeletionMigration);
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
