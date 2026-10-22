using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Takeaway in the guest record and the no-show block (2026-10-23): an order made ready and never collected is its own
/// count beside the bookings, counts toward the block as a missed booking does, and a blocked guest orders nothing.
/// </summary>
public partial class GuestRecordTests
{
    /// <summary>
    /// Takeaway orders not collected in the last twelve months are counted apart - on the guest's own record and on the
    /// venue's - and none of the booking counts includes one. A collected order and one over a year old do not count.
    /// Three visits and nothing missed would be a good record; an order left at the counter is not, so a venue that
    /// confirms good records at once still looks at the booking.
    /// </summary>
    [SkippableFact]
    public async Task A_takeaway_order_not_collected_is_its_own_count_and_spoils_a_good_record()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory, autoConfirm: false);
        var elsewhere = await ArrangeAsync(factory, autoConfirm: true);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        var now = factory.Clock.UtcNow;

        foreach (var days in new[] { 10, 20, 30 })
        {
            await SeedAsync(factory, elsewhere, elsewhere.FirstTableId, me, phone, now.AddDays(-days), ReservationStatus.Completed);
        }

        await SeedTakeawayAsync(factory, elsewhere, me, phone, now.AddDays(-12), TakeawayStatus.NoShow);
        await SeedTakeawayAsync(factory, elsewhere, me, phone, now.AddDays(-14), TakeawayStatus.Collected);
        await SeedTakeawayAsync(factory, elsewhere, me, phone, now.AddDays(-400), TakeawayStatus.NoShow);

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, here));
        await SetGoodRecordSettingAsync(manager, here, on: true);

        using var diner = factory.CreateClientWithToken(token);
        var mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("guestRecord");
        AssertRecord(new Expected(3, 3, 0, 0, 0, IsNewGuest: false), mine);
        Assert.Equal(1, mine.GetProperty("takeawayNoShows").GetInt32());

        // Waiting for the venue, as any booking there does: the good record is not good with an order left behind.
        var created = await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2));
        AssertWaiting(created, ApprovalTrigger.BranchApprovesEveryBooking);

        var list = await ReadAsync(manager, $"/api/branches/{here.BranchId}/reservations?status=1");
        var record = list.EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == created.GetProperty("id").GetGuid())
            .GetProperty("guestRecord");
        AssertRecord(new Expected(3, 3, 0, 0, 0, IsNewGuest: false), record);
        Assert.Equal(1, record.GetProperty("takeawayNoShows").GetInt32());

        // A guest who never booked and left one order behind: still a new guest by bookings, with the order said.
        var (otherToken, otherPhone) = await SignInVerifiedDinerAsync(factory);
        await SeedTakeawayAsync(factory, elsewhere, await AccountOfAsync(factory, otherPhone), otherPhone, now.AddDays(-3), TakeawayStatus.NoShow);
        using var other = factory.CreateClientWithToken(otherToken);
        var theirs = (await ReadAsync(other, "/api/reservations/mine")).GetProperty("guestRecord");
        AssertRecord(new Expected(0, 0, 0, 0, 0, IsNewGuest: true), theirs);
        Assert.Equal(1, theirs.GetProperty("takeawayNoShows").GetInt32());
    }

    /// <summary>
    /// Two missed bookings and then a takeaway order nobody collected, at three venues inside 72 hours: the takeaway
    /// no-show is the third and blocks the guest, as a booking's would - the bookings still to come are cancelled. While
    /// blocked, a takeaway order is refused with the booking's own code and end; once a platform admin lifts the block,
    /// the order is judged on its merits again.
    /// </summary>
    [SkippableFact]
    public async Task A_takeaway_order_not_collected_counts_toward_the_block_and_a_blocked_guest_orders_nothing()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        var upcoming = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 3))).GetProperty("id").GetGuid();
        var now = factory.Clock.UtcNow;

        await MarkNoShowAsync(factory, x, await SeedIdAsync(factory, x, x.TableIds[2], me, phone, now.AddHours(-50), ReservationStatus.Confirmed));
        await MarkNoShowAsync(factory, y, await SeedIdAsync(factory, y, y.TableIds[2], me, phone, now.AddHours(-20), ReservationStatus.Confirmed));

        var order = await SeedTakeawayAsync(factory, z, me, phone, now.AddHours(-1), TakeawayStatus.Ready);

        using var counter = factory.CreateClientWithToken(await SignInManagerAsync(factory, z));
        var marked = await counter.PostAsync($"/api/takeaway-orders/{order}/no-show", null);
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        // A tablet sending it twice: one answer, one block.
        Assert.True((await (await counter.PostAsync($"/api/takeaway-orders/{order}/no-show", null))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("wasReplay").GetBoolean());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(1, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me && b.LiftedAtUtc == null));
            var cancelled = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == upcoming);
            Assert.Equal(ReservationStatus.CancelledByVenue, cancelled.Status);
            Assert.Equal(CancellationCause.NoShowBlock, cancelled.CancelledBecause);
        }

        var record = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("guestRecord");
        var blockedUntil = record.GetProperty("blockedUntilUtc").GetDateTime();
        Assert.Equal(2, record.GetProperty("noShows").GetInt32());
        Assert.Equal(1, record.GetProperty("takeawayNoShows").GetInt32());

        // Blocked: no takeaway order either, refused before anything about the branch is asked.
        var refused = await diner.PostAsJsonAsync("/api/diner/takeaway-orders", TakeawayOrderBody(z));
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("booking-blocked-no-shows", problem.GetProperty("code").GetString());
        Assert.Equal(blockedUntil, problem.GetProperty("context").GetProperty("blockedUntilUtc").GetDateTime());

        // Lifted by the platform: the order goes on to the branch's own rules (z does not take takeaway).
        PlatformAdminAccount admin;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        var blockId = (await ReadAsync(platform, "/api/platform/booking-blocks")).EnumerateArray()
            .Single(b => b.GetProperty("dinerUserId").GetGuid() == me)
            .GetProperty("blockId").GetGuid();
        Assert.Equal(
            HttpStatusCode.OK,
            (await platform.PostAsJsonAsync($"/api/platform/booking-blocks/{blockId}/lift", new { reason = "Picked it up late" })).StatusCode);

        var afterLift = await diner.PostAsJsonAsync("/api/diner/takeaway-orders", TakeawayOrderBody(z));
        Assert.Equal(HttpStatusCode.Conflict, afterLift.StatusCode);
        Assert.Equal(
            "takeaway-not-accepted",
            (await afterLift.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>
    /// A missed booking and an order left at the counter at one venue on one service day are one venue-day, as two
    /// missed bookings there would be: with one more elsewhere, two - nobody is blocked.
    /// </summary>
    [SkippableFact]
    public async Task A_missed_booking_and_an_uncollected_order_at_one_venue_on_one_day_count_once()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var (_, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        var now = factory.Clock.UtcNow;
        var day = LocalDateOf(now.AddHours(-6));

        await MarkNoShowAsync(factory, y, await SeedIdAsync(factory, y, y.TableIds[2], me, phone, now.AddHours(-40), ReservationStatus.Confirmed));
        await MarkNoShowAsync(
            factory, x, await SeedIdAsync(factory, x, x.TableIds[2], me, phone, now.AddHours(-6), ReservationStatus.Confirmed, localDate: day));

        var order = await SeedTakeawayAsync(factory, x, me, phone, now.AddHours(-5), TakeawayStatus.Ready, localDate: day);
        using var counter = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));
        Assert.Equal(HttpStatusCode.OK, (await counter.PostAsync($"/api/takeaway-orders/{order}/no-show", null)).StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me));
        Assert.Equal(2, await Yalla.Infrastructure.Services.GuestRecords.BlockingNoShowDaysAsync(db, me, now, CancellationToken.None));
    }

    /// <summary>
    /// A takeaway order of this account in the guest's past, promised for <paramref name="readyAtUtc"/> and walked
    /// through the order's own moves to <paramref name="status"/> - Ready, NoShow or Collected - straight to the rows,
    /// as history is.
    /// </summary>
    private async Task<Guid> SeedTakeawayAsync(
        YallaApiFactory factory,
        AuthBranch branch,
        Guid dinerUserId,
        string phone,
        DateTime readyAtUtc,
        TakeawayStatus status,
        DateOnly? localDate = null)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var dish = (await TestMenuBuilder.CreateAsync(db, branch.BranchId)).Khachapuri;

        var order = new TakeawayOrder(
            branch.BranchId,
            dinerUserId,
            $"T-{Random.Shared.Next(100, 1000)}",
            localDate ?? LocalDateOf(readyAtUtc),
            "History",
            phone,
            pickupAsap: false,
            readyAtUtc,
            TimeOnly.FromDateTime(readyAtUtc.AddHours(4)),
            note: null,
            [new TakeawayLineInput(dish, "Adjarian khachapuri", TestMenu.KhachapuriAmd, 1)],
            Guid.CreateVersion7(),
            readyAtUtc.AddMinutes(-30));

        order.Accept(readyAtUtc, branch.WaiterId, readyAtUtc.AddMinutes(-29));
        order.MarkReady(branch.WaiterId, readyAtUtc);

        if (status == TakeawayStatus.NoShow)
        {
            order.MarkNoShow(branch.WaiterId, readyAtUtc.AddMinutes(31));
        }
        else if (status == TakeawayStatus.Collected)
        {
            order.Collect(TakeawayPaymentMethod.Card, branch.WaiterId, readyAtUtc.AddMinutes(5));
        }

        db.TakeawayOrders.Add(order);
        await db.SaveChangesAsync();

        return order.Id;
    }

    private static object TakeawayOrderBody(AuthBranch branch) => new
    {
        branchId = branch.BranchId,
        pickup = new { asap = true },
        lines = new[] { new { menuItemId = Guid.CreateVersion7(), quantity = 1 } },
        clientCommandId = Guid.CreateVersion7(),
    };
}
