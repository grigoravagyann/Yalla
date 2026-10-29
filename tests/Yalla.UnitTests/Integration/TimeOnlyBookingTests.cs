using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Booking by time only (2026-10-29): a branch where diners pick a time and how many, with no table, and
/// the venue takes at most so many guests at once.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class TimeOnlyBookingTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Guests_at_once_fill_up_over_overlapping_sittings_and_a_waiter_seats_the_party_at_a_table()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 1);
            var row = await db.Branches.FirstAsync(b => b.Id == branch.BranchId);
            row.UpdateReservationPolicy(TimeOnly(guestsAtOnce: 6), factory.Clock.UtcNow);
            await db.SaveChangesAsync();
        }

        var date = DateOnly.FromDateTime(factory.Clock.UtcNow.AddHours(4)).AddDays(2);

        using var first = await DinerAsync(factory);
        var booked = await first.PostAsJsonAsync("/api/reservations", Body(branch, date, "19:00", 4));
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var made = await booked.Content.ReadFromJsonAsync<JsonElement>();
        // No table, so none on the wire (absent, as every null is).
        Assert.False(made.TryGetProperty("tableId", out var tableId) && tableId.ValueKind != JsonValueKind.Null);
        Assert.True(made.GetProperty("bookedWithoutTable").GetBoolean());

        // 19:30 for ninety minutes shares the room with the 19:00 four: two places left, not six.
        using var second = await DinerAsync(factory);
        var full = await ReviewIntegrityTests.AssertProblemAsync(
            await second.PostAsJsonAsync("/api/reservations", Body(branch, date, "19:30", 4)),
            HttpStatusCode.Conflict,
            "time-full");
        Assert.Equal(2, full.GetProperty("context").GetProperty("guestsLeft").GetInt32());

        Assert.Equal(
            HttpStatusCode.Created,
            (await second.PostAsJsonAsync("/api/reservations", Body(branch, date, "19:30", 2))).StatusCode);

        // Naming a table where the branch chooses it is refused, naming the field.
        using var third = await DinerAsync(factory);
        await ReviewIntegrityTests.AssertProblemAsync(
            await third.PostAsJsonAsync("/api/reservations", Body(branch, date, "21:00", 2, branch.TableIds[0])),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");

        // The day's times answer in guests: none left for a sitting from 19:30, all six once both have gone.
        using var anyone = factory.CreateClient();
        var day = await anyone.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{branch.BranchId}/availability/slots?date={date:yyyy-MM-dd}&partySize=1");
        var slots = day.GetProperty("slots").EnumerateArray().ToDictionary(s => s.GetProperty("time").GetString()!);
        Assert.Equal(0, slots["19:30"].GetProperty("guestsLeft").GetInt32());
        Assert.False(slots["19:30"].GetProperty("isAvailable").GetBoolean());
        Assert.Equal(6, slots["21:00"].GetProperty("guestsLeft").GetInt32());
        Assert.True(slots["21:00"].GetProperty("isAvailable").GetBoolean());

        // The party arrives and a waiter seats them at the table they choose: the booking has it from then on.
        var reservationId = made.GetProperty("id").GetGuid();
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        var seated = await waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{branch.TableIds[0]}/seat-reservation",
            new { reservationId, clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == reservationId);
            Assert.Equal(branch.TableIds[0], row.DiningTableId);
            Assert.Equal(ReservationStatus.Seated, row.Status);
            Assert.True(row.BookedWithoutTable);
        }
    }

    [SkippableFact]
    public async Task A_branch_that_books_by_time_only_takes_guests_with_no_table_on_a_photo()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var readiness = SqlServerFixture.CreateReadinessQuery(db);

        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 0);
        Assert.Equal([ReadinessIssueCodesNoTables], (await readiness.GetAsync(branch.BranchId)).MissingForGuests);

        var row = await db.Branches.FirstAsync(b => b.Id == branch.BranchId);
        row.UpdateReservationPolicy(TimeOnly(guestsAtOnce: 30), clock.UtcNow);
        await db.SaveChangesAsync();

        var view = await readiness.GetAsync(branch.BranchId);
        Assert.True(view.BooksByTimeOnly);
        Assert.True(view.TakingGuests);
        Assert.True(await DinerVisibility.IsVisibleAsync(db, branch.BranchId, CancellationToken.None));
    }

    private const string ReadinessIssueCodesNoTables = Application.BranchSettings.ReadinessIssueCodes.NoTables;

    private static ReservationPolicy TimeOnly(int guestsAtOnce)
    {
        var d = ReservationPolicy.DefaultFor(VenueType.Restaurant);

        return new ReservationPolicy(
            d.TurnTimeMinutes,
            d.BufferMinutes,
            d.GraceMinutes,
            d.LateNudgeAfterMinutes,
            d.GraceExtensionMinutes,
            d.MinLeadMinutes,
            d.BookingWindowDays,
            d.CancellationDeadlineMinutes,
            autoConfirm: true,
            d.ServiceChargePercent,
            d.PricesIncludeVat,
            d.MaxSeatOverhang,
            d.ApprovalRequiredAbovePartySize,
            d.WalkInHoldbackMinutes,
            bookingMode: BookingMode.TimeOnly,
            guestsAtOnce: guestsAtOnce);
    }

    private static object Body(AuthBranch branch, DateOnly date, string time, int partySize, Guid? tableId = null) => new
    {
        branchId = branch.BranchId,
        tableId,
        date = date.ToString("yyyy-MM-dd"),
        time,
        partySize,
        guestName = "Ani Test",
        guestPhone = "+37499000999",
        clientCommandId = Guid.CreateVersion7(),
        note = "By the window, please",
    };

    private static async Task<HttpClient> DinerAsync(YallaApiFactory factory) =>
        factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
