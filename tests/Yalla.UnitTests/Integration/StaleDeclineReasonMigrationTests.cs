using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>ClearStaleChangeDeclineReasons</c> over bookings that already exist: a cancelled booking that kept the reason a
/// change of it was declined for loses it wherever the row shows it was not itself declined, and every real decline -
/// and every row that cannot be told from one - keeps its reason.
/// </summary>
/// <remarks>
/// On a throwaway database of its own, like <see cref="GuestMessagesMigrationRollbackTests"/>. The migration changes no
/// schema, so today's entities write the rows before it as they are.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class StaleDeclineReasonMigrationTests(SqlServerFixture fixture)
{
    private const string MigrationBefore = "20261023001536_GuestMessages";

    private const string Migration = "20261024120522_ClearStaleChangeDeclineReasons";

    [SkippableFact]
    public async Task Only_cancellations_that_cannot_be_declines_lose_their_decline_reason()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"Yalla_StaleDecline_{Guid.NewGuid():N}",
        }.ConnectionString;

        var now = TestNow.UtcNow;

        try
        {
            Dictionary<string, Guid> rows;

            await using (var db = CreateContext(connectionString, now))
            {
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

                var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

                // (status, decline reason, approved at, cancellation text, cancelled because)
                var seeds = new Dictionary<string, (ReservationStatus Status, DeclineReason Reason, bool Approved, string? Text, int? Because)>
                {
                    // Cleared: none of these can be a decline.
                    ["released after an approval"] = (ReservationStatus.CancelledByVenue, DeclineReason.StaffDeclined, true, "Kitchen closed", null),
                    ["no-show block"] = (ReservationStatus.CancelledByVenue, DeclineReason.NoAnswer, false, "blocked", 1),
                    ["no answer with a text"] = (ReservationStatus.CancelledByVenue, DeclineReason.NoAnswer, false, "Table broken", null),
                    ["earlier guest with a text"] = (ReservationStatus.CancelledByVenue, DeclineReason.EarlierGuestDeclined, false, "Out of service", null),
                    ["default release text"] = (ReservationStatus.CancelledByVenue, DeclineReason.StaffDeclined, false, "released: the venue let the booking go", null),
                    ["guest cancelled"] = (ReservationStatus.CancelledByDiner, DeclineReason.StaffDeclined, true, null, null),

                    // Kept: real declines, a confirmed booking whose change was declined, and what cannot be told apart.
                    ["staff decline"] = (ReservationStatus.CancelledByVenue, DeclineReason.StaffDeclined, false, "Full tonight", null),
                    ["staff decline without a text"] = (ReservationStatus.CancelledByVenue, DeclineReason.StaffDeclined, false, null, null),
                    ["nobody answered"] = (ReservationStatus.CancelledByVenue, DeclineReason.NoAnswer, false, null, null),
                    ["earlier guest said no"] = (ReservationStatus.CancelledByVenue, DeclineReason.EarlierGuestDeclined, false, null, null),
                    ["earlier guest silent"] = (ReservationStatus.CancelledByVenue, DeclineReason.EarlierGuestNoAnswer, false, null, null),
                    ["confirmed after a declined change"] = (ReservationStatus.Confirmed, DeclineReason.NoAnswer, true, null, null),
                };

                rows = [];
                var day = 0;

                foreach (var (name, seed) in seeds)
                {
                    var startUtc = now.Date.AddDays(10 + day++).AddHours(15);
                    var reservation = Reservation.Create(
                        branchId: branch.BranchId,
                        diningTableId: branch.TableIds[0],
                        startUtc: startUtc,
                        localDate: DateOnly.FromDateTime(startUtc),
                        localStartTime: TimeOnly.FromDateTime(startUtc),
                        partySize: 2,
                        guestName: "Stale",
                        guestPhone: "+37411223344",
                        code: Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                        policy: ReservationPolicy.DefaultFor(VenueType.Restaurant));

                    db.Reservations.Add(reservation);
                    await db.SaveChangesAsync();

                    DateTime? approvedAt = seed.Approved ? now : null;
                    DateTime? cancelledAt = seed.Status == ReservationStatus.Confirmed ? null : now;

                    await db.Database.ExecuteSqlAsync(
                        $"""
                         UPDATE dbo.Reservations
                         SET Status = {(int)seed.Status}, DeclineReason = {(int)seed.Reason}, ConfirmedAtUtc = {approvedAt},
                             CancelledAtUtc = {cancelledAt}, CancellationReason = {seed.Text}, CancelledBecause = {seed.Because}
                         WHERE Id = {reservation.Id}
                         """);

                    rows[name] = reservation.Id;
                }

                await db.GetService<IMigrator>().MigrateAsync(Migration);
            }

            await using (var db = CreateContext(connectionString, now))
            {
                var after = await db.Reservations.AsNoTracking()
                    .ToDictionaryAsync(r => r.Id, r => r.DeclineReason);

                Assert.Null(after[rows["released after an approval"]]);
                Assert.Null(after[rows["no-show block"]]);
                Assert.Null(after[rows["no answer with a text"]]);
                Assert.Null(after[rows["earlier guest with a text"]]);
                Assert.Null(after[rows["default release text"]]);
                Assert.Null(after[rows["guest cancelled"]]);

                Assert.Equal(DeclineReason.StaffDeclined, after[rows["staff decline"]]);
                Assert.Equal(DeclineReason.StaffDeclined, after[rows["staff decline without a text"]]);
                Assert.Equal(DeclineReason.NoAnswer, after[rows["nobody answered"]]);
                Assert.Equal(DeclineReason.EarlierGuestDeclined, after[rows["earlier guest said no"]]);
                Assert.Equal(DeclineReason.EarlierGuestNoAnswer, after[rows["earlier guest silent"]]);
                Assert.Equal(DeclineReason.NoAnswer, after[rows["confirmed after a declined change"]]);

                // Down puts nothing back and refuses nothing.
                await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
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
