using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The review round on the guest record (GR-BE-REVIEW, decisions relayed 2026-10-20): the no-show block made hard to
/// weaponise (B1), what "came" and a late cancellation mean (S1, S2), where the record is shown (S3), lifts (S4),
/// <c>unmarked</c> (S5), the other venues (S8) and the gaps the review listed (S9).
/// </summary>
public partial class GuestRecordTests
{
    // ---------------------------------------------------------------- B1: the block, hard to weaponise

    /// <summary>
    /// One venue, one evening: three of the guest's bookings there, back to back, all marked no-show after their
    /// grace. At most one no-show per venue per service day counts, so nobody is blocked and the guest's booking at
    /// another venue next week stands.
    /// </summary>
    [SkippableFact]
    public async Task One_venue_in_one_evening_never_blocks_a_guest()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var y = await ArrangeAsync(factory, autoConfirm: true);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        var elsewhere = (await CreatedAsync(diner, AppBooking(factory, y, daysAhead: 5))).GetProperty("id").GetGuid();

        var now = factory.Clock.UtcNow;
        var evening = LocalDateOf(now);

        for (var i = 0; i < 3; i++)
        {
            var id = await SeedIdAsync(
                factory, x, x.TableIds[i], me, phone, now.AddMinutes(-40 - (i * 90)), ReservationStatus.Confirmed, localDate: evening);
            await MarkNoShowAsync(factory, x, id);
        }

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me));
        Assert.Equal(ReservationStatus.Confirmed, (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == elsewhere)).Status);
    }

    /// <summary>
    /// A no-show marked inside the guest's grace - five minutes after the start - is recorded but never counts
    /// toward the block: three venues, three no-shows, one of them too early, no block.
    /// </summary>
    [SkippableFact]
    public async Task A_no_show_marked_inside_the_grace_never_counts_toward_the_block()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var (_, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        var now = factory.Clock.UtcNow;

        var early = await SeedIdAsync(factory, z, z.FirstTableId, me, phone, now.AddMinutes(-5), ReservationStatus.Confirmed);

        await MarkNoShowAsync(factory, x, await SeedIdAsync(factory, x, x.FirstTableId, me, phone, now.AddHours(-50), ReservationStatus.Confirmed));
        await MarkNoShowAsync(factory, y, await SeedIdAsync(factory, y, y.FirstTableId, me, phone, now.AddHours(-20), ReservationStatus.Confirmed));
        await MarkNoShowAsync(factory, z, early);

        await using var db = fixture.CreateContext(factory.Clock);
        var row = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == early);
        Assert.Equal(ReservationStatus.NoShow, row.Status);
        Assert.False(row.NoShowCountsTowardBlock);
        Assert.Equal(0, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me));
    }

    /// <summary>
    /// Every no-show is told to the guest - a push and a feed entry naming the venue - and records who marked it.
    /// </summary>
    [SkippableFact]
    public async Task Each_no_show_is_told_to_the_guest_and_records_who_marked_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var (_, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);

        var id = await SeedIdAsync(factory, x, x.FirstTableId, me, phone, factory.Clock.UtcNow.AddHours(-1), ReservationStatus.Confirmed);
        await MarkNoShowAsync(factory, x, id);

        await using var db = fixture.CreateContext(factory.Clock);
        var row = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(x.ManagerId, row.NoShowMarkedByStaffId);
        Assert.True(row.NoShowCountsTowardBlock);

        Assert.Equal(1, await db.DinerNotifications.CountAsync(
            n => n.DinerUserId == me && n.ReservationId == id && n.Kind == DinerNotificationKinds.BookingNoShow));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(
            m => m.Type == OutboxMessageTypes.ReservationNoShow && m.IdempotencyKey.StartsWith("reservation:" + id)));
    }

    /// <summary>
    /// Staff undo the third no-show the same evening: the booking is confirmed again, the block is lifted because
    /// two no-shows remain, the bookings the block cancelled stay cancelled, the guest is told, and books again. An
    /// undo of an undone no-show answers as it stands; of a booking that is no no-show, 409.
    /// </summary>
    [SkippableFact]
    public async Task Undoing_the_third_no_show_lifts_the_block_restores_nothing_and_tells_the_guest()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        var lost = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 3))).GetProperty("id").GetGuid();
        var third = await BlockAsync(factory, x, y, z, me, phone);

        using var floorZ = factory.CreateClientWithToken(await SignInManagerAsync(factory, z));
        var undone = await floorZ.PostAsJsonAsync(
            $"/api/reservations/{third}/undo-no-show", new { clientCommandId = Guid.CreateVersion7(), reason = "they were at table 4" });

        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.Equal(ReservationStatus.Confirmed, (ReservationStatus)(await undone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var block = await db.BookingBlocks.AsNoTracking().SingleAsync(b => b.DinerUserId == me);
            Assert.NotNull(block.LiftedAtUtc);
            Assert.Equal(BookingBlockLiftCause.NoShowUndone, block.LiftedBecause);
            Assert.Equal(z.ManagerId, block.LiftedByStaffId);

            var row = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == third);
            Assert.Equal(z.ManagerId, row.NoShowUndoneByStaffId);
            Assert.Equal("they were at table 4", row.NoShowUndoReason);

            Assert.Equal(ReservationStatus.CancelledByVenue, (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == lost)).Status);
            Assert.Equal(1, await db.DinerNotifications.CountAsync(
                n => n.DinerUserId == me && n.Kind == DinerNotificationKinds.BookingsUnblocked));
        }

        // Asked again: as it stands. A booking that is no no-show: refused.
        Assert.Equal(
            HttpStatusCode.OK,
            (await floorZ.PostAsJsonAsync($"/api/reservations/{third}/undo-no-show", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var neverMissed = await SeedIdAsync(factory, z, z.TableIds[2], me, phone, factory.Clock.UtcNow.AddDays(4), ReservationStatus.Confirmed);
        var refused = await floorZ.PostAsJsonAsync(
            $"/api/reservations/{neverMissed}/undo-no-show", new { clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await CreatedAsync(diner, AppBooking(factory, y, daysAhead: 6));
    }

    /// <summary>A no-show from three days ago can no longer be undone: the window closed at 05:00 the next morning.</summary>
    [SkippableFact]
    public async Task A_no_show_cannot_be_undone_after_its_service_day()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var (_, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);

        var start = factory.Clock.UtcNow.AddDays(-3);
        var id = await SeedIdAsync(factory, x, x.FirstTableId, me, phone, start, ReservationStatus.Confirmed, localDate: LocalDateOf(start));
        await MarkNoShowAsync(factory, x, id);

        using var floor = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));
        var late = await floor.PostAsJsonAsync($"/api/reservations/{id}/undo-no-show", new { clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        var problem = await late.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no-show-undo-window-closed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("context").GetProperty("undoableUntilUtc").GetDateTime() < factory.Clock.UtcNow);
    }

    /// <summary>
    /// A platform admin lists the blocks in force and lifts one with a reason - audited, the guest told, booking
    /// open again. A manager cannot; a reason is required; a second lift answers as it stands.
    /// </summary>
    [SkippableFact]
    public async Task A_platform_admin_lifts_a_block_with_a_reason_and_it_is_audited()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        await BlockAsync(factory, x, y, z, me, phone);

        PlatformAdminAccount admin;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));

        var listed = await ReadAsync(platform, "/api/platform/booking-blocks");
        var blockId = listed.EnumerateArray().Single(b => b.GetProperty("dinerUserId").GetGuid() == me).GetProperty("blockId").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/platform/booking-blocks")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.PostAsJsonAsync($"/api/platform/booking-blocks/{blockId}/lift", new { reason = "mistake" })).StatusCode);

        var noReason = await platform.PostAsJsonAsync($"/api/platform/booking-blocks/{blockId}/lift", new { reason = "" });
        Assert.True((int)noReason.StatusCode is 400 or 422, $"Expected a validation answer, got {(int)noReason.StatusCode}.");

        var lifted = await platform.PostAsJsonAsync(
            $"/api/platform/booking-blocks/{blockId}/lift", new { reason = "Venue confirmed the guest came" });
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        var body = await lifted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)BookingBlockLiftCause.PlatformAdmin, body.GetProperty("liftedBecause").GetInt32());

        Assert.Equal(
            HttpStatusCode.OK,
            (await platform.PostAsJsonAsync($"/api/platform/booking-blocks/{blockId}/lift", new { reason = "again" })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(1, await db.PlatformAuditLogs.CountAsync(a => a.Action == "booking-block.lift" && a.TargetId == blockId));
            Assert.Equal(1, await db.DinerNotifications.CountAsync(
                n => n.DinerUserId == me && n.Kind == DinerNotificationKinds.BookingsUnblocked));
        }

        Assert.DoesNotContain(
            (await ReadAsync(platform, "/api/platform/booking-blocks")).EnumerateArray(),
            b => b.GetProperty("blockId").GetGuid() == blockId);

        await CreatedAsync(diner, AppBooking(factory, y, daysAhead: 6));
    }

    /// <summary>
    /// After a lift, the no-shows before it never block again: a fourth no-show marked later, on a booking that
    /// started before the lift, inside the same 72 hours, writes no new block.
    /// </summary>
    [SkippableFact]
    public async Task After_a_lift_the_no_shows_before_it_never_block_again()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var w = await ArrangeAsync(factory, true);
        var (_, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);

        var fourth = await SeedIdAsync(factory, w, w.FirstTableId, me, phone, factory.Clock.UtcNow.AddHours(-3), ReservationStatus.Confirmed);
        await BlockAsync(factory, x, y, z, me, phone);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.BookingBlocks SET LiftedAtUtc = {factory.Clock.UtcNow.AddMinutes(-1)}, LiftedBecause = 1 WHERE DinerUserId = {me}");
        }

        await MarkNoShowAsync(factory, w, fourth);

        await using var check = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await check.BookingBlocks.CountAsync(b => b.DinerUserId == me));
    }

    // ---------------------------------------------------------------- S1, S2, S3, S5: what the numbers mean

    /// <summary>
    /// "Came" is only what the venue vouched for: a sitting a waiter seated counts; a booking opened from its code
    /// alone, with nothing ordered or paid, is "unmarked", as is a confirmed booking nobody marked - and booked is the
    /// four added up.
    /// </summary>
    [SkippableFact]
    public async Task Came_counts_only_what_the_venue_vouched_for_and_the_rest_is_unmarked()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        var now = factory.Clock.UtcNow;

        await SeedAsync(factory, x, x.FirstTableId, me, phone, now.AddDays(-10), ReservationStatus.Completed);
        await SeedAsync(factory, x, x.FirstTableId, me, phone, now.AddDays(-6), ReservationStatus.Completed, seating: Seating.ByCodeOnly);
        await SeedAsync(factory, x, x.FirstTableId, me, phone, now.AddDays(-5), ReservationStatus.Seated, seating: Seating.ByCodeOnly);
        await SeedAsync(factory, x, x.FirstTableId, me, phone, now.AddDays(-4), ReservationStatus.Confirmed);

        // Started an hour ago and not over: neither booked nor unmarked yet.
        await SeedAsync(factory, x, x.TableIds[1], me, phone, now.AddMinutes(-30), ReservationStatus.Confirmed);

        using var diner = factory.CreateClientWithToken(token);
        AssertRecord(
            new Expected(Booked: 4, Came: 1, NoShows: 0, LateCancellations: 0, Unmarked: 3, IsNewGuest: false),
            (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("guestRecord"));
    }

    /// <summary>
    /// A late cancellation counts only for a booking that was confirmed and made before its cancellation deadline:
    /// a short-notice booking cancelled while it waited does not, nor one confirmed inside its deadline; a booking
    /// made days ahead and cancelled an hour before does.
    /// </summary>
    [SkippableFact]
    public async Task A_late_cancellation_counts_only_for_a_confirmed_booking_made_before_its_deadline()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var (token, _) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));

        var planned = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 3))).GetProperty("id").GetGuid();

        // Noon two days on: tonight's bookings are at short notice.
        MoveToNoonInTwoDays(factory);

        var waiting = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 0, time: "13:30", tableIndex: 1))).GetProperty("id").GetGuid();
        await diner.PostAsJsonAsync($"/api/reservations/{waiting}/cancel", new { });

        var approvedLate = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 0, time: "13:30", tableIndex: 2))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/reservations/{approvedLate}/approve", new { })).StatusCode);
        await diner.PostAsJsonAsync($"/api/reservations/{approvedLate}/cancel", new { });

        // The day after at 17:00: an hour before the planned booking's 18:00.
        factory.Clock.Advance(TimeSpan.FromHours(29));
        await diner.PostAsJsonAsync($"/api/reservations/{planned}/cancel", new { });

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var rows = await db.Reservations.AsNoTracking()
                .Where(r => r.Id == planned || r.Id == waiting || r.Id == approvedLate)
                .ToDictionaryAsync(r => r.Id);

            Assert.All(rows.Values, r => Assert.True(r.CancelledAfterDeadline));
            Assert.True(rows[planned].LateCancellationCounts);
            Assert.False(rows[waiting].LateCancellationCounts);
            Assert.False(rows[approvedLate].LateCancellationCounts);
        }

        factory.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(
            1,
            (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("guestRecord").GetProperty("lateCancellations").GetInt32());
    }

    /// <summary>
    /// The record goes only on bookings still to come or today's, pending or confirmed: never on a finished one, a
    /// cancelled one or a confirmed one in the past - so an old booking is no window on the guest's evenings since.
    /// </summary>
    [SkippableFact]
    public async Task The_record_goes_only_on_bookings_still_to_come_or_today()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);
        var now = factory.Clock.UtcNow;

        var upcoming = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 2))).GetProperty("id").GetGuid();
        var pastConfirmed = await SeedIdAsync(factory, x, x.TableIds[1], me, phone, now.AddDays(-2), ReservationStatus.Confirmed);
        var finished = await SeedIdAsync(factory, x, x.TableIds[2], me, phone, now.AddDays(-3), ReservationStatus.Completed);

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));

        var confirmed = await ReadAsync(manager, $"/api/branches/{x.BranchId}/reservations?status=2");
        Assert.True(confirmed.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == upcoming).TryGetProperty("guestRecord", out _));
        Assert.False(confirmed.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == pastConfirmed).TryGetProperty("guestRecord", out _));

        var completed = await ReadAsync(manager, $"/api/branches/{x.BranchId}/reservations?status=5");
        Assert.False(completed.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == finished).TryGetProperty("guestRecord", out _));
    }

    // ---------------------------------------------------------------- S8, S9: the rest of the block

    /// <summary>
    /// Each booking a block cancels at another venue is put on that branch's change stream (signal 4, the booking
    /// named), and a table held for it is given back.
    /// </summary>
    [SkippableFact]
    public async Task A_block_tells_the_other_venue_and_gives_back_the_table_held_for_its_booking()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var other = await ArrangeAsync(factory, true);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        var lost = (await CreatedAsync(diner, AppBooking(factory, other, daysAhead: 3))).GetProperty("id").GetGuid();

        // The floor at the other venue holds the table for it.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await fixture.CreateService(db, factory.Clock, TestActor.Waiter(other.WaiterId))
                .HoldForLatePartyAsync(new Application.Tables.TableStateCommand(
                    other.BranchId, other.FirstTableId, Guid.CreateVersion7(), "held"));
        }

        await BlockAsync(factory, x, y, z, me, phone);

        await using var check = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await check.TableStateChanges.CountAsync(
            c => c.BranchId == other.BranchId && c.ReservationId == lost && c.Signal == TableChangeSignal.BookingCancelledByBlock));
        Assert.Equal(
            TableStatus.Free,
            (await check.DiningTables.AsNoTracking().SingleAsync(t => t.Id == other.FirstTableId)).Status);
    }

    /// <summary>
    /// While blocked, every move is refused - the app's and the manage link's - and a new account that proves the
    /// same number is refused too, and sees the block on its own record.
    /// </summary>
    [SkippableFact]
    public async Task A_block_refuses_every_move_and_a_new_account_on_the_same_number()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        await BlockAsync(factory, x, y, z, me, phone);

        // A booking still there (made around the block), with a manage link.
        var kept = await SeedIdAsync(factory, x, x.FirstTableId, me, phone, factory.Clock.UtcNow.AddDays(5), ReservationStatus.Confirmed);
        var manageToken = "manage-" + Guid.NewGuid().ToString("N");

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.Reservations SET ManageTokenHash = {Infrastructure.Identity.Secrets.Hash(manageToken)} WHERE Id = {kept}");
        }

        var move = new { date = LocalDate(factory, 6), time = "18:00", partySize = 2, diningTableId = x.TableIds[1], clientCommandId = Guid.CreateVersion7() };

        await AssertBlockedAsync(await diner.PostAsJsonAsync($"/api/reservations/{kept}/reschedule", move));
        await AssertBlockedAsync(await factory.CreateClient().PostAsJsonAsync($"/api/public/bookings/{manageToken}/reschedule", move));

        // Gone, and proved again on a new account: still blocked, and told so on its own record.
        var deleted = await diner.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, "/api/diner/me") { Content = JsonContent.Create(new { }) });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var (again, _) = await SignInVerifiedDinerAsync(factory, phone);
        using var reborn = factory.CreateClientWithToken(again);

        await AssertBlockedAsync(await reborn.PostAsJsonAsync("/api/reservations", AppBooking(factory, y, daysAhead: 2)));
        Assert.True((await ReadAsync(reborn, "/api/reservations/mine")).GetProperty("guestRecord").TryGetProperty("blockedUntilUtc", out _));
    }

    /// <summary>
    /// A booking sent at the very moment the third no-show is marked never outlives the block: it is either refused
    /// or cancelled with the rest.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_racing_the_third_no_show_never_survives_the_block()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (x, y, z) = (await ArrangeAsync(factory, true), await ArrangeAsync(factory, true), await ArrangeAsync(factory, true));
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);
        var now = factory.Clock.UtcNow;

        await MarkNoShowAsync(factory, x, await SeedIdAsync(factory, x, x.FirstTableId, me, phone, now.AddHours(-50), ReservationStatus.Confirmed));
        await MarkNoShowAsync(factory, y, await SeedIdAsync(factory, y, y.FirstTableId, me, phone, now.AddHours(-20), ReservationStatus.Confirmed));
        var third = await SeedIdAsync(factory, z, z.FirstTableId, me, phone, now.AddHours(-1), ReservationStatus.Confirmed);

        using var floorZ = factory.CreateClientWithToken(await SignInManagerAsync(factory, z));

        await Task.WhenAll(
            floorZ.PostAsJsonAsync($"/api/reservations/{third}/no-show", new { clientCommandId = Guid.CreateVersion7() }),
            diner.PostAsJsonAsync("/api/reservations", AppBooking(factory, x, daysAhead: 4)));

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me));
        Assert.Equal(0, await db.Reservations.CountAsync(
            r => r.DinerUserId == me
                 && r.StartUtc > now
                 && (r.Status == ReservationStatus.PendingApproval || r.Status == ReservationStatus.Confirmed)));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Three no-shows at three venues inside 72 hours, each after its grace: blocked. Answers the third's id.</summary>
    private async Task<Guid> BlockAsync(YallaApiFactory factory, AuthBranch x, AuthBranch y, AuthBranch z, Guid me, string phone)
    {
        var now = factory.Clock.UtcNow;

        await MarkNoShowAsync(factory, x, await SeedIdAsync(factory, x, x.TableIds[2], me, phone, now.AddHours(-50), ReservationStatus.Confirmed));
        await MarkNoShowAsync(factory, y, await SeedIdAsync(factory, y, y.TableIds[2], me, phone, now.AddHours(-20), ReservationStatus.Confirmed));

        var third = await SeedIdAsync(factory, z, z.TableIds[2], me, phone, now.AddHours(-1), ReservationStatus.Confirmed);
        await MarkNoShowAsync(factory, z, third);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me && b.LiftedAtUtc == null));

        return third;
    }

    private static async Task AssertBlockedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Expected 403, got {(int)response.StatusCode}: {body}");
        Assert.Contains("booking-blocked-no-shows", body, StringComparison.Ordinal);
    }

    /// <summary>The branch calendar date (Yerevan) of an instant.</summary>
    private static DateOnly LocalDateOf(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan")));
}
