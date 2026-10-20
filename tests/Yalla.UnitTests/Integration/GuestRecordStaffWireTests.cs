using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The staff wire gaps the frontend found after 4e1d45e: the guest record on the floor's tables, the no-show answer
/// saying it blocked the guest, and <c>cancelledBecause</c> with its own refusal for deciding a booking a block took.
/// </summary>
public partial class GuestRecordTests
{
    /// <summary>
    /// The floor carries the record of the next booking on a table and of the party sitting at one, as the console
    /// list does - and nothing for a table with neither.
    /// </summary>
    [SkippableFact]
    public async Task The_floor_shows_the_guest_record_of_the_next_booking_and_of_the_party_sitting_there()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var elsewhere = await ArrangeAsync(factory, autoConfirm: true);
        var now = factory.Clock.UtcNow;

        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var coming = await AccountOfAsync(factory, phone);
        await SeedAsync(factory, elsewhere, elsewhere.FirstTableId, coming, phone, now.AddDays(-10), ReservationStatus.Completed);

        using (var diner = factory.CreateClientWithToken(token))
        {
            await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 2));
        }

        var (_, sittingPhone) = await SignInVerifiedDinerAsync(factory);
        var sitting = await AccountOfAsync(factory, sittingPhone);
        await SeedAsync(factory, elsewhere, elsewhere.FirstTableId, sitting, sittingPhone, now.AddDays(-20), ReservationStatus.NoShow);
        await SeedAsync(factory, x, x.TableIds[1], sitting, sittingPhone, now.AddMinutes(-20), ReservationStatus.Seated, localDate: LocalDateOf(now));

        using var floor = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));
        var tables = (await ReadAsync(floor, $"/api/branches/{x.BranchId}/tables/floor")).GetProperty("tables").EnumerateArray().ToList();

        var next = tables.Single(t => t.GetProperty("tableId").GetGuid() == x.FirstTableId);
        Assert.Equal(1, next.GetProperty("nextReservationGuestRecord").GetProperty("came").GetInt32());

        var seated = tables.Single(t => t.GetProperty("tableId").GetGuid() == x.TableIds[1]);
        Assert.Equal(1, seated.GetProperty("sittingGuestRecord").GetProperty("noShows").GetInt32());

        var empty = tables.Single(t => t.GetProperty("tableId").GetGuid() == x.TableIds[2]);
        Assert.False(empty.TryGetProperty("nextReservationGuestRecord", out _));
        Assert.False(empty.TryGetProperty("sittingGuestRecord", out _));
    }

    /// <summary>
    /// The answer to the no-show that blocked the guest says until when - and to one that did not, nothing; a replay
    /// of the blocking one says it again.
    /// </summary>
    [SkippableFact]
    public async Task The_no_show_answer_says_when_it_blocked_the_guest()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var (_, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        var now = factory.Clock.UtcNow;

        var first = await MarkNoShowAsync(factory, x, await SeedIdAsync(factory, x, x.FirstTableId, me, phone, now.AddHours(-50), ReservationStatus.Confirmed));
        await MarkNoShowAsync(factory, y, await SeedIdAsync(factory, y, y.FirstTableId, me, phone, now.AddHours(-20), ReservationStatus.Confirmed));
        var thirdId = await SeedIdAsync(factory, z, z.FirstTableId, me, phone, now.AddHours(-1), ReservationStatus.Confirmed);
        var third = await MarkNoShowAsync(factory, z, thirdId);

        Assert.False(first.TryGetProperty("guestBlockedUntilUtc", out _));

        var until = third.GetProperty("guestBlockedUntilUtc").GetDateTime();
        Assert.Equal(now.AddDays(NoShowBlockRules.BlockDays), until, TimeSpan.FromSeconds(5));

        var replay = await MarkNoShowAsync(factory, z, thirdId);
        Assert.Equal(until, replay.GetProperty("guestBlockedUntilUtc").GetDateTime());
    }

    /// <summary>
    /// A booking the block cancelled says so on the guest's manage link, and deciding it answers 409
    /// <c>reservation-cancelled-by-block</c> - not the generic "already answered".
    /// </summary>
    [SkippableFact]
    public async Task Deciding_a_booking_the_block_cancelled_says_so()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var strict = await ArrangeAsync(factory, autoConfirm: false);
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        var waiting = await CreatedAsync(diner, AppBooking(factory, strict, daysAhead: 3));
        var id = waiting.GetProperty("id").GetGuid();
        var manageToken = waiting.GetProperty("manageToken").GetString()!;

        await BlockAsync(factory, x, y, z, me, phone);

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, strict));

        foreach (var decision in new[] { "approve", "reject" })
        {
            var refused = await manager.PostAsJsonAsync($"/api/reservations/{id}/{decision}", new { });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(
                "reservation-cancelled-by-block",
                (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        var link = await ReadAsync(factory.CreateClient(), $"/api/public/bookings/{manageToken}");
        Assert.Equal((int)CancellationCause.NoShowBlock, link.GetProperty("cancelledBecause").GetInt32());

        var cancelled = await ReadAsync(manager, $"/api/branches/{strict.BranchId}/reservations?status=7");
        Assert.Equal(
            (int)CancellationCause.NoShowBlock,
            cancelled.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id).GetProperty("cancelledBecause").GetInt32());

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(CancellationCause.NoShowBlock, (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id)).CancelledBecause);
    }
}
