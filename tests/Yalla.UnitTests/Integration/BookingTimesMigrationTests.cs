using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>BookingTimes</c> over branches that already exist: the two old defaults - thirty minutes of lead
/// and fifteen of clearing - become ten and five, each column on its own, and every value an owner
/// chose stays exactly as it was.
/// </summary>
/// <remarks>
/// On a throwaway database of its own, like <see cref="GeneralManagerMigrationTests"/>: going back a
/// migration under the shared schema would pull the new table out from under every other test.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class BookingTimesMigrationTests(SqlServerFixture fixture)
{
    private const string BookingTimesMigration = "20261018091708_BookingTimes";

    private const string MigrationBefore = "20261018044216_ShortNoticeApproval";

    [SkippableFact]
    public async Task Branches_on_the_old_defaults_move_to_the_new_ones_and_edited_values_stay()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_BookingTimes_{Guid.NewGuid():N}",
        }.ConnectionString;

        try
        {
            TestBranch untouched;
            TestBranch edited;
            TestBranch leadEdited;
            TestBranch bufferEdited;

            await using (var db = CreateContext(connectionString))
            {
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

                // StaffSessionGeneration, SpecialDaysVersion and UnverifiedGuests and GuestRecord sort after this migration, but today's StaffMember
                // Branch and Reservation write their columns, so they are put in place for the fixture to find.
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE dbo.StaffMembers ADD SessionGeneration int NOT NULL CONSTRAINT DF_Test_SessionGeneration DEFAULT 0;"
                    + "ALTER TABLE dbo.Branches ADD SpecialDaysVersion int NOT NULL CONSTRAINT DF_Test_SpecialDaysVersion DEFAULT 0;"
                    + "ALTER TABLE dbo.Branches ADD ReservationPolicy_AutoConfirmGuestsWithGoodRecord bit NOT NULL CONSTRAINT DF_Test_GoodRecord DEFAULT 0; "
                    + "ALTER TABLE dbo.Branches ADD ReservationPolicy_UnverifiedScansNeedAdmission bit NOT NULL CONSTRAINT DF_Test_UnverifiedScans DEFAULT 1;"
                    + "ALTER TABLE dbo.Reservations ADD PhoneUnverified bit NOT NULL CONSTRAINT DF_Test_PhoneUnverified DEFAULT 0, BookerPhoneE164 nvarchar(32) NULL, CancelledBecause int NULL, LateCancellationCounts bit NOT NULL CONSTRAINT DF_Test_LateCounts DEFAULT 0, NoShowCountsTowardBlock bit NOT NULL CONSTRAINT DF_Test_NoShowCounts DEFAULT 0, NoShowMarkedByStaffId uniqueidentifier NULL, NoShowUndoReason nvarchar(500) NULL, NoShowUndoneAtUtc datetime2 NULL, NoShowUndoneByStaffId uniqueidentifier NULL;");

                // Nothing the branch, its tables or its room write changed in this migration, so today's
                // entities write the schema before it as they are.
                untouched = await TestBranchBuilder.CreateAsync(db, tableCount: 1, policy: Policy(30, 15));
                edited = await TestBranchBuilder.CreateAsync(db, tableCount: 1, policy: Policy(45, 20));
                leadEdited = await TestBranchBuilder.CreateAsync(db, tableCount: 1, policy: Policy(60, 15));
                bufferEdited = await TestBranchBuilder.CreateAsync(db, tableCount: 1, policy: Policy(30, 0));

                await db.GetService<IMigrator>().MigrateAsync(BookingTimesMigration);
            }

            await using (var db = CreateContext(connectionString))
            {
                Assert.Equal((10, 5), await LeadAndBufferAsync(db, untouched));
                Assert.Equal((45, 20), await LeadAndBufferAsync(db, edited));
                Assert.Equal((60, 5), await LeadAndBufferAsync(db, leadEdited));
                Assert.Equal((10, 0), await LeadAndBufferAsync(db, bufferEdited));

                // And everything else on the policy is as it was.
                var policy = await db.Branches.AsNoTracking()
                    .Where(b => b.Id == edited.BranchId)
                    .Select(b => b.ReservationPolicy)
                    .SingleAsync();

                Assert.Equal(90, policy.TurnTimeMinutes);
                Assert.Equal(120, policy.CancellationDeadlineMinutes);
                Assert.Equal(30, policy.WalkInHoldbackMinutes);

                // The new table is there and empty.
                Assert.Equal(0, await db.TableShareRequests.CountAsync());

                // Bookings carrying the numbers this migration brought: waiting on the guest before with a
                // deadline and without one, declined by that guest and for their silence.
                foreach (var minutes in new[] { 60, 120, 180, 240 })
                {
                    await TestBranchBuilder.AddConfirmedReservationAsync(
                        db, untouched, untouched.FirstTableId, new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc).AddMinutes(minutes * 3));
                }

                var ids = await db.Reservations.AsNoTracking()
                    .Where(r => r.BranchId == untouched.BranchId)
                    .OrderBy(r => r.StartUtc)
                    .Select(r => r.Id)
                    .ToListAsync();

                await db.Database.ExecuteSqlAsync(
                    $"UPDATE dbo.Reservations SET Status = 1, ApprovalTrigger = 5, ApprovalExpiresAtUtc = '2026-11-02T09:00:00' WHERE Id = {ids[0]}");
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE dbo.Reservations SET Status = 1, ApprovalTrigger = 5 WHERE Id = {ids[1]}");
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE dbo.Reservations SET Status = 7, DeclineReason = 3 WHERE Id = {ids[2]}");
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE dbo.Reservations SET Status = 7, DeclineReason = 4 WHERE Id = {ids[3]}");

                // Down puts the old defaults back where the new ones are, and leaves the rest.
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

                // And maps the new numbers onto ones the code before it knows.
                Assert.Equal(4, await TriggerAsync(db, ids[0]));
                Assert.Equal(1, await TriggerAsync(db, ids[1]));
                Assert.Equal(1, await DeclineAsync(db, ids[2]));
                Assert.Equal(2, await DeclineAsync(db, ids[3]));
            }

            await using (var db = CreateContext(connectionString))
            {
                Assert.Equal((30, 15), await RawLeadAndBufferAsync(db, untouched));
                Assert.Equal((45, 20), await RawLeadAndBufferAsync(db, edited));
                Assert.Equal((60, 15), await RawLeadAndBufferAsync(db, leadEdited));
                Assert.Equal((30, 0), await RawLeadAndBufferAsync(db, bufferEdited));
            }
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    private static ReservationPolicy Policy(int minLeadMinutes, int bufferMinutes) => new(
        turnTimeMinutes: 90,
        bufferMinutes: bufferMinutes,
        graceMinutes: 15,
        lateNudgeAfterMinutes: 10,
        graceExtensionMinutes: 10,
        minLeadMinutes: minLeadMinutes,
        bookingWindowDays: 14,
        cancellationDeadlineMinutes: 120,
        autoConfirm: true,
        serviceChargePercent: 10m,
        pricesIncludeVat: true,
        maxSeatOverhang: 2,
        approvalRequiredAbovePartySize: 8);

    private static async Task<(int Lead, int Buffer)> LeadAndBufferAsync(YallaDbContext db, TestBranch branch)
    {
        var policy = await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => b.ReservationPolicy)
            .SingleAsync();

        return (policy.MinLeadMinutes, policy.BufferMinutes);
    }

    /// <summary>The two columns read with SQL, for the schema before the migration that today's model does not describe.</summary>
    private static async Task<(int Lead, int Buffer)> RawLeadAndBufferAsync(YallaDbContext db, TestBranch branch)
    {
        var lead = await db.Database
            .SqlQuery<int>($"SELECT ReservationPolicy_MinLeadMinutes AS Value FROM dbo.Branches WHERE Id = {branch.BranchId}")
            .SingleAsync();

        var buffer = await db.Database
            .SqlQuery<int>($"SELECT ReservationPolicy_BufferMinutes AS Value FROM dbo.Branches WHERE Id = {branch.BranchId}")
            .SingleAsync();

        return (lead, buffer);
    }

    private static Task<int> TriggerAsync(YallaDbContext db, Guid id) =>
        db.Database.SqlQuery<int>($"SELECT ApprovalTrigger AS Value FROM dbo.Reservations WHERE Id = {id}").SingleAsync();

    private static Task<int> DeclineAsync(YallaDbContext db, Guid id) =>
        db.Database.SqlQuery<int>($"SELECT DeclineReason AS Value FROM dbo.Reservations WHERE Id = {id}").SingleAsync();

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
