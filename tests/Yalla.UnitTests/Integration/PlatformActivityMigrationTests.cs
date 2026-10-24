using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>PlatformActivityAndReportDismissal</c> over a log that already has rows: each is given its
/// actor's role and name and its target's name and venue from what the ids name, a review is named by
/// its branch and never its author, and the diner's own account deletion is left with no actor name.
/// </summary>
/// <remarks>
/// On a throwaway database of its own, like <see cref="GeneralManagerMigrationTests"/>: going back a
/// migration under the shared schema would pull the new columns out from under every other test.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class PlatformActivityMigrationTests(SqlServerFixture fixture)
{
    private const string ActivityMigration = "20261018040915_PlatformActivityAndReportDismissal";

    private const string MigrationBefore = "20261017180602_BillRequestTip";

    private const string EmptyJson = "{}";

    [SkippableFact]
    public async Task Rows_already_in_the_log_are_named_from_what_their_ids_name_today()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_Activity_{Guid.NewGuid():N}",
        }.ConnectionString;

        try
        {
            TestBranch branch;
            PlatformAdminAccount admin;
            Guid reviewId;
            Guid dinerId;
            var at = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

            await using (var db = CreateContext(connectionString))
            {
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

                // The tables these rows name have not changed in this migration, so today's entities
                // write them as the old code did - but for the columns later migrations (VenueRestore,
                // StaffSessionGeneration, SpecialDaysVersion, ReviewReplies) give Venues, StaffMembers,
                // Branches and BranchReviews, which today's entities write and are put in place for them
                // to find.
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE dbo.Venues ADD ActiveBeforeDeletion bit NULL; "
                    + "ALTER TABLE dbo.StaffMembers ADD SessionGeneration int NOT NULL CONSTRAINT DF_Test_SessionGeneration DEFAULT 0;"
                    + "ALTER TABLE dbo.Branches ADD SpecialDaysVersion int NOT NULL CONSTRAINT DF_Test_SpecialDaysVersion DEFAULT 0;"
                    + "ALTER TABLE dbo.Branches ADD ReservationPolicy_AutoConfirmGuestsWithGoodRecord bit NOT NULL CONSTRAINT DF_Test_GoodRecord DEFAULT 0; "
                    + "ALTER TABLE dbo.Branches ADD ReservationPolicy_UnverifiedScansNeedAdmission bit NOT NULL CONSTRAINT DF_Test_UnverifiedScans DEFAULT 1;"
                    + "ALTER TABLE dbo.BranchReviews ADD ReplyText nvarchar(500) NULL, ReplyAuthorName nvarchar(200) NULL, "
                    + "ReplyAuthorRole int NULL, ReplyByStaffMemberId uniqueidentifier NULL, RepliedAtUtc datetime2 NULL, "
                    + "ReplyUpdatedAtUtc datetime2 NULL;");

                await LaterColumns.AddPreOrderColumnsAsync(db);

                branch = await TestBranchBuilder.CreateAsync(db);
                admin = await AuthTestData.CreatePlatformAdminAsync(db);
                dinerId = await ReviewTestData.SeedDinerAsync(db, "Anahit Authorsurname", at);
                reviewId = await ReviewTestData.SeedReviewAsync(db, branch.BranchId, dinerId, 1, "Rude.", at);

                // The log itself has, so its rows go in as the old columns.
                async Task Row(Guid actor, string action, string targetType, Guid targetId) =>
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        INSERT INTO dbo.PlatformAuditLogs (Id, CreatedAtUtc, ActorStaffMemberId, Action, TargetType, TargetId, ChangesJson, AtUtc)
                        VALUES ({Guid.CreateVersion7()}, {at}, {actor}, {action}, {targetType}, {targetId}, {EmptyJson}, {at});
                        """);

                await Row(admin.StaffMemberId, "venue.suspend", "Venue", branch.VenueId);
                await Row(admin.StaffMemberId, "branch.update", "Branch", branch.BranchId);
                await Row(branch.ManagerId, "staff.sign-in-issued", "StaffMember", branch.WaiterId);
                await Row(branch.ManagerId, "table.regenerate-qr", "DiningTable", branch.FirstTableId);
                await Row(admin.StaffMemberId, "review.hide", "BranchReview", reviewId);
                await Row(dinerId, "diner.account-deleted", "DinerUser", dinerId);

                await db.GetService<IMigrator>().MigrateAsync(ActivityMigration);
            }

            await using (var db = CreateContext(connectionString))
            {
                var rows = await db.PlatformAuditLogs.AsNoTracking().ToDictionaryAsync(l => l.Action);
                var venueName = await db.Venues.Where(v => v.Id == branch.VenueId).Select(v => v.Name).FirstAsync();
                var branchName = await db.Branches.Where(b => b.Id == branch.BranchId).Select(b => b.Name).FirstAsync();

                var suspend = rows["venue.suspend"];
                Assert.Equal(StaffRole.PlatformAdmin, suspend.ActorRole);
                Assert.Equal("Platform Administrator", suspend.ActorDisplay);
                Assert.Equal(venueName, suspend.TargetDisplay);
                Assert.Equal(branch.VenueId, suspend.VenueId);

                Assert.Equal(branchName, rows["branch.update"].TargetDisplay);
                Assert.Equal(branch.VenueId, rows["branch.update"].VenueId);

                var issued = rows["staff.sign-in-issued"];
                Assert.Equal(StaffRole.Manager, issued.ActorRole);
                Assert.Equal("Test Manager", issued.ActorDisplay);
                Assert.Equal("Test Waiter", issued.TargetDisplay);
                Assert.Equal(branch.VenueId, issued.VenueId);

                Assert.Equal("1", rows["table.regenerate-qr"].TargetDisplay);
                Assert.Equal(branch.VenueId, rows["table.regenerate-qr"].VenueId);

                // A review is named by its branch; its author is a diner and is not copied in.
                var hide = rows["review.hide"];
                Assert.Equal(branchName, hide.TargetDisplay);
                Assert.Equal(branch.VenueId, hide.VenueId);

                var deletion = rows["diner.account-deleted"];
                Assert.Null(deletion.ActorRole);
                Assert.Null(deletion.ActorDisplay);
                Assert.Null(deletion.TargetDisplay);
                Assert.Null(deletion.VenueId);

                Assert.DoesNotContain(rows.Values, r => (r.TargetDisplay ?? string.Empty).Contains("Authorsurname", StringComparison.Ordinal));

                // No report is dismissed by the migration.
                Assert.False(await db.BranchReviewReports.AnyAsync(r => r.DismissedAtUtc != null));

                // Down takes the columns away again.
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

                var columns = await db.Database
                    .SqlQuery<string>($"SELECT name AS Value FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.PlatformAuditLogs')")
                    .ToListAsync();

                Assert.DoesNotContain("ActorDisplay", columns);
                Assert.DoesNotContain("VenueId", columns);
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
            new TestClock(TestNow.UtcNow));

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
