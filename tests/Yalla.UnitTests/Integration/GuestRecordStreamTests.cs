using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The frontend review of the guest record (GR-FE-REVIEW B1, S1, S4): booking events on the change stream, the floor
/// read's consistency, and the undo window on the service day.
/// </summary>
public partial class GuestRecordTests
{
    /// <summary>
    /// Every booking event writes a row on its table's change stream - made, approved, moved (both tables), cancelled,
    /// declined, marked a no-show, a no-show withdrawn - signal 5 naming the booking, so a tablet that follows the stream
    /// refreshes that table's next booking.
    /// </summary>
    [SkippableFact]
    public async Task Every_booking_event_puts_a_row_on_its_tables_change_stream()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: false);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));

        var from = (await ReadAsync(manager, $"/api/branches/{x.BranchId}/tables/floor")).GetProperty("maxSequence").GetInt64();

        // Made, approved, moved to another table, cancelled.
        var moved = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 3))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/reservations/{moved}/approve", new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await diner.PostAsJsonAsync($"/api/reservations/{moved}/reschedule", new
            {
                date = LocalDate(factory, 3), time = "19:00", partySize = 2, diningTableId = x.TableIds[1],
                clientCommandId = Guid.CreateVersion7(),
            })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await diner.PostAsJsonAsync($"/api/reservations/{moved}/cancel", new { })).StatusCode);

        // Made and declined.
        var declined = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 4, tableIndex: 2))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/reservations/{declined}/reject", new { })).StatusCode);

        // Marked a no-show, and withdrawn.
        var missed = await SeedIdAsync(factory, x, x.TableIds[2], me, phone, factory.Clock.UtcNow.AddHours(-1), ReservationStatus.Confirmed);
        await MarkNoShowAsync(factory, x, missed);
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PostAsJsonAsync($"/api/reservations/{missed}/undo-no-show", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var page = await ReadAsync(manager, $"/api/branches/{x.BranchId}/tables/changes?afterSequence={from}&limit=500");
        var signals = page.GetProperty("changes").EnumerateArray()
            .Where(c => c.TryGetProperty("signal", out var signal) && signal.GetInt32() == (int)TableChangeSignal.BookingChanged)
            .Select(c => (Table: c.GetProperty("diningTableId").GetGuid(), Booking: c.GetProperty("reservationId").GetGuid()))
            .ToList();

        // Made, approved, moved (its old table and its new one), cancelled.
        Assert.True(signals.Count(s => s.Booking == moved) >= 5, $"{signals.Count(s => s.Booking == moved)} rows for the moved booking.");
        Assert.Contains((x.TableIds[0], moved), signals);
        Assert.Contains((x.TableIds[1], moved), signals);

        Assert.True(signals.Count(s => s.Booking == declined) >= 2, $"{signals.Count(s => s.Booking == declined)} rows for the declined booking.");
        Assert.True(signals.Count(s => s.Booking == missed) >= 2, $"{signals.Count(s => s.Booking == missed)} rows for the no-show.");
    }

    /// <summary>
    /// The floor's tables and its <c>maxSequence</c> are read in one statement, which is one consistent snapshot because
    /// the database reads committed snapshots (EF turns it on when it creates the database, as the deploy's migration
    /// bundle does): a change committed during the read is either in both or in neither.
    /// </summary>
    [SkippableFact]
    public async Task The_database_reads_committed_snapshots_so_the_floor_and_its_sequence_agree()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(DateTime.UtcNow));

        var on = await db.Database
            .SqlQuery<bool>($"SELECT is_read_committed_snapshot_on AS [Value] FROM sys.databases WHERE name = DB_NAME()")
            .SingleAsync();

        Assert.True(on, "READ_COMMITTED_SNAPSHOT is off: the floor's tables and maxSequence could disagree.");
    }

    /// <summary>
    /// A 01:00 booking belongs to the evening before: its no-show can be withdrawn until 05:00 that same morning, not
    /// 28 hours later (GR-FE-REVIEW S4).
    /// </summary>
    [SkippableFact]
    public async Task A_no_show_on_a_booking_after_midnight_can_be_withdrawn_only_until_five_that_morning()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var (_, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);

        // A date whose 05:00 has passed and whose next 05:00 has not: today if it is past five now, else yesterday.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);
        var date = DateOnly.FromDateTime(localNow.Hour >= 5 ? localNow : localNow.AddDays(-1));
        var start = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(1, 0)), zone), DateTimeKind.Utc);

        var id = await SeedIdAsync(factory, x, x.FirstTableId, me, phone, start, ReservationStatus.Confirmed, localDate: date, localTime: new TimeOnly(1, 0));
        await MarkNoShowAsync(factory, x, id);

        using var floor = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));
        var late = await floor.PostAsJsonAsync($"/api/reservations/{id}/undo-no-show", new { clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        var problem = await late.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no-show-undo-window-closed", problem.GetProperty("code").GetString());
        Assert.Equal(
            DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(5, 0)), zone), DateTimeKind.Utc),
            problem.GetProperty("context").GetProperty("undoableUntilUtc").GetDateTime());
    }
}
