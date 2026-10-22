using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The guest record a venue sees beside a booking (the user's decision of 2026-10-20): how many of the guest's
/// bookings in the last twelve months, at every venue, they came to, missed, or cancelled late - counted only
/// from what the guest's own verified account did. And the venue's setting that confirms a guest with a good
/// record at once.
/// </summary>
[Collection(SqlServerCollection.Name)]
public partial class GuestRecordTests(SqlServerFixture fixture)
{
    // ---------------------------------------------------------------- what counts

    /// <summary>
    /// Every kind of booking in the guest's past, at another venue: came (completed twice, seated once), one
    /// no-show, one late cancellation and one confirmed booking nobody marked count; a cancellation in good time,
    /// a venue's cancellation, a decline, a booking still waiting, one thirteen months ago and one still to come
    /// do not. Nor does anything a stranger could have made: another account's booking typed with this guest's
    /// number, a web booking made with it and no account, or a booking of this account made without a proved
    /// number. Staff read it on the console list, the floor's awaiting cards, the overview-style answers to
    /// approve; the guest reads their own on <c>/mine</c>; no diner read of a booking carries it.
    /// </summary>
    [SkippableFact]
    public async Task Staff_see_the_guests_record_across_venues_counting_only_what_the_guest_did()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory, autoConfirm: false);
        var elsewhere = await ArrangeAsync(factory, autoConfirm: true);

        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var (_, strangerPhone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        var stranger = await AccountOfAsync(factory, strangerPhone);
        var now = factory.Clock.UtcNow;

        var table = elsewhere.FirstTableId;

        // Counted.
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-10), ReservationStatus.Completed);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-20), ReservationStatus.Completed);
        await SeedAsync(factory, elsewhere, elsewhere.TableIds[1], me, phone, now.AddMinutes(-30), ReservationStatus.Seated);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-40), ReservationStatus.NoShow);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-50), ReservationStatus.CancelledByDiner, lateCancel: true);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-5), ReservationStatus.Confirmed);

        // Not counted: the guest's good-time cancel, the venue's own cancels and declines, a booking nobody
        // answered yet, the window's edge, the future.
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-60), ReservationStatus.CancelledByDiner);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-70), ReservationStatus.CancelledByVenue);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-80), ReservationStatus.CancelledByVenue, declined: true);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-6), ReservationStatus.PendingApproval);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-400), ReservationStatus.Completed);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-380), ReservationStatus.NoShow);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(6), ReservationStatus.Confirmed);

        // Not counted: what a stranger could have made.
        await SeedAsync(factory, elsewhere, table, stranger, strangerPhone, now.AddDays(-15), ReservationStatus.NoShow, guestPhone: phone);
        await SeedAsync(factory, elsewhere, table, null, phone, now.AddDays(-25), ReservationStatus.NoShow, phoneUnverified: true, guestPhone: phone);
        await SeedAsync(factory, elsewhere, table, me, phone, now.AddDays(-35), ReservationStatus.NoShow, phoneUnverified: true);

        using var diner = factory.CreateClientWithToken(token);
        var created = await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2));
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal(ReservationStatus.PendingApproval, (ReservationStatus)created.GetProperty("status").GetInt32());

        // Never on a diner's own read of a booking.
        Assert.False(created.TryGetProperty("guestRecord", out _));

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, here));

        // The confirmed booking five days ago that nobody marked is "unmarked"; booked is the four together.
        var expected = new Expected(Booked: 6, Came: 3, NoShows: 1, LateCancellations: 1, Unmarked: 1, IsNewGuest: false);

        var list = await ReadAsync(manager, $"/api/branches/{here.BranchId}/reservations?status=1");
        AssertRecord(expected, list.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id).GetProperty("guestRecord"));

        var awaiting = await ReadAsync(manager, $"/api/branches/{here.BranchId}/reservations/awaiting");
        AssertRecord(
            expected,
            awaiting.EnumerateArray()
                .Single(card => card.GetProperty("reservation").GetProperty("id").GetGuid() == id)
                .GetProperty("reservation")
                .GetProperty("guestRecord"));

        var approved = await manager.PostAsJsonAsync($"/api/reservations/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        AssertRecord(expected, (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("guestRecord"));

        // The guest's own numbers, once, beside their bookings - and not on each booking.
        var mine = await ReadAsync(diner, "/api/reservations/mine");
        AssertRecord(expected, mine.GetProperty("guestRecord"));
        Assert.All(mine.GetProperty("upcoming").EnumerateArray(), b => Assert.False(b.TryGetProperty("guestRecord", out _)));
        Assert.All(mine.GetProperty("past").EnumerateArray(), b => Assert.False(b.TryGetProperty("guestRecord", out _)));
    }

    /// <summary>A guest with nothing behind them is a new guest, with every count at zero - to the venue and to themselves.</summary>
    [SkippableFact]
    public async Task A_guest_with_no_history_is_a_new_guest()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory, autoConfirm: false);
        var (token, _) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        var created = await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2));
        var id = created.GetProperty("id").GetGuid();

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, here));
        var newGuest = new Expected(0, 0, 0, 0, 0, IsNewGuest: true);

        var list = await ReadAsync(manager, $"/api/branches/{here.BranchId}/reservations?status=1");
        AssertRecord(newGuest, list.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id).GetProperty("guestRecord"));

        // A declined booking is finished: its answer carries no record (review S3).
        var rejected = await manager.PostAsJsonAsync($"/api/reservations/{id}/reject", new { });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.False((await rejected.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("guestRecord", out _));

        // A decline is the venue's, never the guest's: still new.
        AssertRecord(newGuest, (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("guestRecord"));
    }

    // ---------------------------------------------------------------- one query per list

    /// <summary>
    /// The console list reads every guest's record in one grouped statement: twenty bookings by twenty guests,
    /// each with history, cost the same number of statements as one.
    /// </summary>
    [SkippableFact]
    public async Task The_console_list_reads_every_guests_record_without_a_query_per_booking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var one = await ListStatementsFor(1);
        var twenty = await ListStatementsFor(20);

        Assert.Equal(one, twenty);

        async Task<int> ListStatementsFor(int guests)
        {
            var clock = new TestClock(Now);
            TestBranch branch;

            await using (var db = fixture.CreateContext(clock))
            {
                branch = await TestBranchBuilder.CreateAsync(db, tableCount: guests);
                var history = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

                for (var i = 0; i < guests; i++)
                {
                    var actor = TestActor.Diner();
                    var service = fixture.CreateReservationService(db, clock, actor);

                    await service.CreateAsync(Booking(branch, branch.TableIds[i], new TimeOnly(19, 0)));

                    await SeedAsync(
                        clock, history.BranchId, history.TableIds[0], actor.DinerUserId, null, Now.AddDays(-10 - i), ReservationStatus.Completed);
                }
            }

            var counter = new CommandCountingInterceptor();
            await using var readDb = fixture.CreateContext(clock, counter);

            var listed = await fixture
                .CreateReservationService(readDb, clock, TestActor.Manager(branch.ManagerId))
                .ListForBranchAsync(branch.BranchId, ReservationStatus.Confirmed);

            Assert.Equal(guests, listed.Count);
            Assert.All(listed, r => Assert.Equal(1, r.GuestRecord!.Came));

            return counter.Count;
        }
    }

    /// <summary>The console's overview lists today's bookings with each guest's record too.</summary>
    [SkippableFact]
    public async Task The_overview_lists_todays_bookings_with_the_guests_record()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var history = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

        var actor = TestActor.Diner();
        var booked = await fixture.CreateReservationService(db, clock, actor)
            .CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(19, 0), date: Today));

        await SeedAsync(clock, history.BranchId, history.TableIds[0], actor.DinerUserId, null, Now.AddDays(-3), ReservationStatus.NoShow);
        await SeedAsync(clock, history.BranchId, history.TableIds[0], actor.DinerUserId, null, Now.AddDays(-4), ReservationStatus.Completed);

        var overview = await fixture
            .CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId))
            .GetAsync(branch.BranchId);

        var record = overview.TodayBookings.Single(b => b.Id == booked.Id).GuestRecord!;

        Assert.Equal(
            new GuestRecord(Booked: 2, Came: 1, NoShows: 1, LateCancellations: 0, Unmarked: 0, IsNewGuest: false, TakeawayNoShows: 0), record);
    }

    // ---------------------------------------------------------------- auto-confirm a good record

    /// <summary>
    /// A branch that approves every booking, with "auto-confirm guests with a good record" off and then on.
    /// On, a guest who came three times with no no-show and no late cancel is confirmed at once; a new guest, one
    /// with a no-show and one with a late cancel still wait for the venue. The setting skips only the
    /// approve-everything reason: a good guest's large party still waits as a large party, and their booking for
    /// tonight still waits for the floor as short notice.
    /// </summary>
    [SkippableFact]
    public async Task Auto_confirm_for_a_good_record_skips_only_the_approve_everything_reason()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory, autoConfirm: false);
        var elsewhere = await ArrangeAsync(factory, autoConfirm: true);
        var now = factory.Clock.UtcNow;

        async Task<HttpClient> GuestAsync(params ReservationStatus[] past)
        {
            var (token, phone) = await SignInVerifiedDinerAsync(factory);
            var account = await AccountOfAsync(factory, phone);

            for (var i = 0; i < past.Length; i++)
            {
                await SeedAsync(
                    factory, elsewhere, elsewhere.FirstTableId, account, phone, now.AddDays(-7 * (i + 1)), past[i],
                    lateCancel: past[i] == ReservationStatus.CancelledByDiner);
            }

            return factory.CreateClientWithToken(token);
        }

        const ReservationStatus Came = ReservationStatus.Completed;

        using var good = await GuestAsync(Came, Came, Came);
        using var newcomer = await GuestAsync();
        using var missed = await GuestAsync(Came, Came, Came, ReservationStatus.NoShow);
        using var lateCanceller = await GuestAsync(Came, Came, Came, ReservationStatus.CancelledByDiner);
        using var twice = await GuestAsync(Came, Came);

        // Off (the default): the good guest waits like everybody.
        var before = await CreatedAsync(good, AppBooking(factory, here, daysAhead: 2));
        AssertWaiting(before, ApprovalTrigger.BranchApprovesEveryBooking);
        await good.PostAsJsonAsync($"/api/reservations/{before.GetProperty("id").GetGuid()}/cancel", new { });

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, here));
        // Parties above two are large here, so a table of four can show it.
        await SetGoodRecordSettingAsync(manager, here, on: true, approvalAbove: 2);

        Assert.Equal(
            ReservationStatus.Confirmed,
            (ReservationStatus)(await CreatedAsync(good, AppBooking(factory, here, daysAhead: 3))).GetProperty("status").GetInt32());

        AssertWaiting(await CreatedAsync(newcomer, AppBooking(factory, here, daysAhead: 3, tableIndex: 1)), ApprovalTrigger.BranchApprovesEveryBooking);
        AssertWaiting(await CreatedAsync(missed, AppBooking(factory, here, daysAhead: 3, tableIndex: 2)), ApprovalTrigger.BranchApprovesEveryBooking);
        AssertWaiting(await CreatedAsync(lateCanceller, AppBooking(factory, here, daysAhead: 4)), ApprovalTrigger.BranchApprovesEveryBooking);
        AssertWaiting(await CreatedAsync(twice, AppBooking(factory, here, daysAhead: 4, tableIndex: 1)), ApprovalTrigger.BranchApprovesEveryBooking);

        // The good guest's large party is still a large party; tonight is still the floor's.
        AssertWaiting(
            await CreatedAsync(good, AppBooking(factory, here, daysAhead: 5, partySize: 3, tableIndex: 2)),
            ApprovalTrigger.LargeParty);

        // Noon two days on (every token already issued): an evening booking that day is at short notice.
        MoveToNoonInTwoDays(factory);
        AssertWaiting(
            await CreatedAsync(good, AppBooking(factory, here, daysAhead: 0, tableIndex: 1)),
            ApprovalTrigger.ShortNotice);

        // Switched off again, the stored value says so; an older console leaving it out keeps it.
        await SetGoodRecordSettingAsync(manager, here, on: false);
        Assert.False((await ReadAsync(manager, $"/api/branches/{here.BranchId}/reservation-policy"))
            .GetProperty("autoConfirmGuestsWithGoodRecord").GetBoolean());
    }

    // ---------------------------------------------------------------- the no-show block

    /// <summary>
    /// Three no-shows on bookings that started in the last 72 hours, at three venues, marked by three floors: the
    /// third cancels every booking of the guest's not yet started - at any venue, with the block as the cause -
    /// tells the guest once (push and feed), shows the block on the guest's record, and refuses every new booking
    /// with the block's end, until it ends.
    /// </summary>
    [SkippableFact]
    public async Task Three_no_shows_in_three_days_cancel_every_booking_still_to_come_and_block_booking_for_a_week()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var y = await ArrangeAsync(factory, autoConfirm: true);
        var z = await ArrangeAsync(factory, autoConfirm: false);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        // Two bookings still to come, one confirmed and one waiting for its venue.
        var confirmed = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 3))).GetProperty("id").GetGuid();
        var waiting = (await CreatedAsync(diner, AppBooking(factory, z, daysAhead: 4))).GetProperty("id").GetGuid();

        var now = factory.Clock.UtcNow;
        var missed = new[]
        {
            await SeedIdAsync(factory, x, x.TableIds[1], me, phone, now.AddHours(-60), ReservationStatus.Confirmed),
            await SeedIdAsync(factory, y, y.FirstTableId, me, phone, now.AddHours(-30), ReservationStatus.Confirmed),
            await SeedIdAsync(factory, z, z.TableIds[1], me, phone, now.AddHours(-1), ReservationStatus.Confirmed),
        };

        await MarkNoShowAsync(factory, x, missed[0]);
        await MarkNoShowAsync(factory, y, missed[1]);

        // Two: nothing yet.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(0, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me));
        }

        await MarkNoShowAsync(factory, z, missed[2]);

        var blockedUntil = (await ReadAsync(diner, "/api/reservations/mine"))
            .GetProperty("guestRecord").GetProperty("blockedUntilUtc").GetDateTime();
        Assert.Equal(now.AddDays(NoShowBlockRules.BlockDays), blockedUntil, TimeSpan.FromSeconds(1));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            foreach (var id in new[] { confirmed, waiting })
            {
                var row = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id);
                Assert.Equal(ReservationStatus.CancelledByVenue, row.Status);
                Assert.Equal(CancellationCause.NoShowBlock, row.CancelledBecause);
            }

            Assert.Equal(1, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me && b.PhoneE164 == phone));
            Assert.Equal(1, await db.DinerNotifications.CountAsync(
                n => n.DinerUserId == me && n.Kind == Domain.Identity.DinerNotificationKinds.BookingsBlockedNoShows));
            Assert.Equal(1, await db.OutboxMessages.CountAsync(
                m => m.Type == Application.Messaging.OutboxMessageTypes.DinerBookingsBlocked && m.PayloadJson.Contains(me.ToString())));
        }

        // The venue reads why.
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));
        var cancelledList = await ReadAsync(manager, $"/api/branches/{x.BranchId}/reservations?status=7");
        Assert.Equal(
            (int)CancellationCause.NoShowBlock,
            cancelledList.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == confirmed).GetProperty("cancelledBecause").GetInt32());

        // The guest reads their own block, and every way to book is refused until it ends.
        var mine = await ReadAsync(diner, "/api/reservations/mine");
        Assert.Equal(blockedUntil, mine.GetProperty("guestRecord").GetProperty("blockedUntilUtc").GetDateTime());

        var refused = await diner.PostAsJsonAsync("/api/reservations", AppBooking(factory, y, daysAhead: 2));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("booking-blocked-no-shows", problem.GetProperty("code").GetString());
        Assert.Equal(blockedUntil, problem.GetProperty("context").GetProperty("blockedUntilUtc").GetDateTime());

        // A week on (the API's clock moved after the token was issued), the guest books again.
        factory.Clock.Advance(TimeSpan.FromDays(NoShowBlockRules.BlockDays).Add(TimeSpan.FromMinutes(1)));

        await CreatedAsync(diner, AppBooking(factory, y, daysAhead: 2));
    }

    /// <summary>
    /// Two no-shows in three days block nobody; nor do three spread over five days of start times - the window is
    /// over when the guest failed to come, not when a floor got round to marking it.
    /// </summary>
    [SkippableFact]
    public async Task Two_no_shows_or_three_spread_over_five_days_block_nobody()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        var upcoming = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 3))).GetProperty("id").GetGuid();
        var now = factory.Clock.UtcNow;

        // Spread: 100 h, 50 h and 1 h ago at three venues - every pair of neighbours inside 72 h, all three not.
        var y = await ArrangeAsync(factory, autoConfirm: true);
        var z = await ArrangeAsync(factory, autoConfirm: true);

        foreach (var (venue, hoursAgo) in new[] { (x, 100), (y, 50), (z, 1) })
        {
            var id = await SeedIdAsync(factory, venue, venue.TableIds[1], me, phone, now.AddHours(-hoursAgo), ReservationStatus.Confirmed);
            await MarkNoShowAsync(factory, venue, id);
        }

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me));
        Assert.Equal(
            ReservationStatus.Confirmed,
            (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == upcoming)).Status);

        await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 4));
    }

    /// <summary>
    /// Nothing a stranger can make counts towards anybody's block: another account's bookings typed with this
    /// guest's number, and bookings made without proof with it. With one no-show of their own, the guest is not
    /// blocked; the stranger who missed three of their own is.
    /// </summary>
    [SkippableFact]
    public async Task A_strangers_no_shows_with_my_number_never_block_me()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var y = await ArrangeAsync(factory, autoConfirm: true);
        var z = await ArrangeAsync(factory, autoConfirm: true);
        var (_, phone) = await SignInVerifiedDinerAsync(factory);
        var (_, strangerPhone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        var stranger = await AccountOfAsync(factory, strangerPhone);
        var now = factory.Clock.UtcNow;

        // The stranger's own account, typing my number as the guest's, three times at three venues.
        foreach (var (venue, hoursAgo) in new[] { (x, 5), (y, 4), (z, 3) })
        {
            var id = await SeedIdAsync(
                factory, venue, venue.TableIds[1], stranger, strangerPhone, now.AddHours(-hoursAgo), ReservationStatus.Confirmed, guestPhone: phone);
            await MarkNoShowAsync(factory, venue, id);
        }

        // Two made with my number and no proof (Yalla#54's web door), and one of mine.
        await SeedAsync(factory, x, x.TableIds[2], null, phone, now.AddHours(-10), ReservationStatus.NoShow, phoneUnverified: true, guestPhone: phone);
        await SeedAsync(factory, x, x.TableIds[2], null, phone, now.AddHours(-11), ReservationStatus.NoShow, phoneUnverified: true, guestPhone: phone);
        var mine = await SeedIdAsync(factory, x, x.FirstTableId, me, phone, now.AddHours(-2), ReservationStatus.Confirmed);
        await MarkNoShowAsync(factory, x, mine);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me || b.PhoneE164 == phone));
        Assert.Equal(1, await db.BookingBlocks.CountAsync(b => b.DinerUserId == stranger && b.PhoneE164 == strangerPhone));
    }

    /// <summary>
    /// The second and third no-shows marked at the same moment by two floors: the account's lock judges them one
    /// after the other, so exactly one block is written and the booking still to come is cancelled once.
    /// </summary>
    [SkippableFact]
    public async Task Two_no_shows_marked_at_the_same_moment_write_exactly_one_block()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoConfirm: true);
        var y = await ArrangeAsync(factory, autoConfirm: true);
        var w = await ArrangeAsync(factory, autoConfirm: true);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        var me = await AccountOfAsync(factory, phone);
        using var diner = factory.CreateClientWithToken(token);

        var upcoming = (await CreatedAsync(diner, AppBooking(factory, x, daysAhead: 3))).GetProperty("id").GetGuid();
        var now = factory.Clock.UtcNow;

        await SeedAsync(factory, w, w.TableIds[2], me, phone, now.AddHours(-20), ReservationStatus.NoShow);
        var atX = await SeedIdAsync(factory, x, x.TableIds[1], me, phone, now.AddHours(-2), ReservationStatus.Confirmed);
        var atY = await SeedIdAsync(factory, y, y.FirstTableId, me, phone, now.AddHours(-1), ReservationStatus.Confirmed);

        using var floorX = factory.CreateClientWithToken(await SignInManagerAsync(factory, x));
        using var floorY = factory.CreateClientWithToken(await SignInManagerAsync(factory, y));

        var results = await Task.WhenAll(
            floorX.PostAsJsonAsync($"/api/reservations/{atX}/no-show", new { clientCommandId = Guid.CreateVersion7() }),
            floorY.PostAsJsonAsync($"/api/reservations/{atY}/no-show", new { clientCommandId = Guid.CreateVersion7() }));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await db.BookingBlocks.CountAsync(b => b.DinerUserId == me));
        Assert.Equal(
            CancellationCause.NoShowBlock,
            (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == upcoming)).CancelledBecause);
        Assert.Equal(1, await db.DinerNotifications.CountAsync(
            n => n.DinerUserId == me && n.Kind == Domain.Identity.DinerNotificationKinds.BookingsBlockedNoShows));
    }

    private async Task<JsonElement> MarkNoShowAsync(YallaApiFactory factory, AuthBranch branch, Guid reservationId)
    {
        using var floor = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));

        var response = await floor.PostAsJsonAsync(
            $"/api/reservations/{reservationId}/no-show", new { clientCommandId = Guid.CreateVersion7() });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<Guid> SeedIdAsync(
        YallaApiFactory factory,
        AuthBranch branch,
        Guid tableId,
        Guid? dinerUserId,
        string? bookerPhone,
        DateTime startUtc,
        ReservationStatus status,
        string? guestPhone = null,
        DateOnly? localDate = null,
        TimeOnly? localTime = null)
    {
        await SeedAsync(
            factory, branch, tableId, dinerUserId, bookerPhone, startUtc, status, guestPhone: guestPhone, localDate: localDate,
            localTime: localTime);

        await using var db = fixture.CreateContext(factory.Clock);

        return await db.Reservations.AsNoTracking()
            .Where(r => r.BranchId == branch.BranchId && r.StartUtc == startUtc && r.DinerUserId == dinerUserId)
            .Select(r => r.Id)
            .SingleAsync();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Thursday 10 September 2026, noon in Yerevan.</summary>
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 9, 10);

    private static readonly DateOnly Sunday = new(2026, 9, 13);

    private sealed record Expected(int Booked, int Came, int NoShows, int LateCancellations, int Unmarked, bool IsNewGuest);

    private static void AssertRecord(Expected expected, JsonElement record) =>
        Assert.Equal(
            expected,
            new Expected(
                record.GetProperty("booked").GetInt32(),
                record.GetProperty("came").GetInt32(),
                record.GetProperty("noShows").GetInt32(),
                record.GetProperty("lateCancellations").GetInt32(),
                record.GetProperty("unmarked").GetInt32(),
                record.GetProperty("isNewGuest").GetBoolean()));

    private static void AssertWaiting(JsonElement booking, ApprovalTrigger because)
    {
        Assert.Equal(ReservationStatus.PendingApproval, (ReservationStatus)booking.GetProperty("status").GetInt32());
        Assert.Equal(because, (ApprovalTrigger)booking.GetProperty("awaitingApprovalBecause").GetInt32());
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<AuthBranch> ArrangeAsync(YallaApiFactory factory, bool autoConfirm)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
        await ReviewTestData.SavePolicyAsync(
            db, branch.BranchId, factory.Clock.UtcNow, autoConfirm: autoConfirm, acceptsOnlineBookings: true);

        return branch;
    }

    private async Task<Guid> AccountOfAsync(YallaApiFactory factory, string phone)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return await db.DinerUsers.AsNoTracking().Where(d => d.PhoneE164 == phone).Select(d => d.Id).SingleAsync();
    }

    private Task SeedAsync(
        YallaApiFactory factory,
        AuthBranch branch,
        Guid tableId,
        Guid? dinerUserId,
        string? bookerPhone,
        DateTime startUtc,
        ReservationStatus status,
        bool lateCancel = false,
        bool declined = false,
        bool phoneUnverified = false,
        string? guestPhone = null,
        DateOnly? localDate = null,
        Seating seating = Seating.ByStaff,
        TimeOnly? localTime = null) =>
        SeedAsync(
            factory.Clock, branch.BranchId, tableId, dinerUserId, bookerPhone, startUtc, status, lateCancel, declined,
            phoneUnverified, guestPhone, localDate, seating, localTime);

    /// <summary>How a seated or completed booking in the past was seated - what "came" is judged by (review S1).</summary>
    private enum Seating
    {
        /// <summary>A waiter seated the party.</summary>
        ByStaff,

        /// <summary>Opened from the booking code alone; nothing ordered, nothing paid.</summary>
        ByCodeOnly,
    }

    /// <summary>
    /// A booking in the guest's past (or future) as the venue's floor left it - written straight to the row, as
    /// history is, not through today's rules.
    /// </summary>
    private async Task SeedAsync(
        IClock clock,
        Guid branchId,
        Guid tableId,
        Guid? dinerUserId,
        string? bookerPhone,
        DateTime startUtc,
        ReservationStatus status,
        bool lateCancel = false,
        bool declined = false,
        bool phoneUnverified = false,
        string? guestPhone = null,
        DateOnly? localDate = null,
        Seating seating = Seating.ByStaff,
        TimeOnly? localTime = null)
    {
        await using var db = fixture.CreateContext(clock);

        var policy = await db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.ReservationPolicy).FirstAsync();

        var reservation = Reservation.Create(
            branchId: branchId,
            diningTableId: tableId,
            startUtc: startUtc,
            localDate: localDate ?? DateOnly.FromDateTime(startUtc),
            localStartTime: localTime ?? TimeOnly.FromDateTime(startUtc),
            partySize: 2,
            guestName: "History",
            guestPhone: guestPhone ?? "+37411223344",
            code: Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            policy: policy,
            dinerUserId: dinerUserId,
            phoneUnverified: phoneUnverified,
            bookerPhoneE164: bookerPhone);

        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        var cancelled = status is ReservationStatus.CancelledByDiner or ReservationStatus.CancelledByVenue or ReservationStatus.NoShow
            ? startUtc.AddHours(lateCancel ? -1 : -48)
            : (DateTime?)null;
        int? decline = declined ? (int)DeclineReason.StaffDeclined : null;

        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE dbo.Reservations
             SET Status = {(int)status}, CancelledAfterDeadline = {lateCancel}, LateCancellationCounts = {lateCancel},
                 CancelledAtUtc = {cancelled}, DeclineReason = {decline},
                 NoShowCountsTowardBlock = {status == ReservationStatus.NoShow}
             WHERE Id = {reservation.Id}
             """);

        if (status is ReservationStatus.Seated or ReservationStatus.Completed)
        {
            // The sitting behind it: a waiter's (staff id on it), or the booking code's (none).
            var waiter = seating == Seating.ByStaff
                ? await db.StaffMembers.Where(m => m.BranchId == branchId).Select(m => (Guid?)m.Id).FirstAsync()
                : null;

            var sitting = TableSession.SeatReservation(branchId, tableId, reservation.Id, 2, startUtc, waiter);

            if (status == ReservationStatus.Completed)
            {
                sitting.Close(startUtc.AddHours(1));
            }

            db.TableSessions.Add(sitting);
            await db.SaveChangesAsync();
        }
    }

    private static async Task SetGoodRecordSettingAsync(HttpClient manager, AuthBranch branch, bool on, int? approvalAbove = null)
    {
        var url = $"/api/branches/{branch.BranchId}/reservation-policy";
        var current = await ReadAsync(manager, url);

        var saved = await manager.PutAsJsonAsync(url, new
        {
            turnTimeMinutes = current.GetProperty("turnTimeMinutes").GetInt32(),
            bufferMinutes = current.GetProperty("bufferMinutes").GetInt32(),
            graceMinutes = current.GetProperty("graceMinutes").GetInt32(),
            lateNudgeAfterMinutes = current.GetProperty("lateNudgeAfterMinutes").GetInt32(),
            graceExtensionMinutes = current.GetProperty("graceExtensionMinutes").GetInt32(),
            minLeadMinutes = current.GetProperty("minLeadMinutes").GetInt32(),
            bookingWindowDays = current.GetProperty("bookingWindowDays").GetInt32(),
            cancellationDeadlineMinutes = current.GetProperty("cancellationDeadlineMinutes").GetInt32(),
            autoConfirm = current.GetProperty("autoConfirm").GetBoolean(),
            serviceChargePercent = current.GetProperty("serviceChargePercent").GetDecimal(),
            pricesIncludeVat = current.GetProperty("pricesIncludeVat").GetBoolean(),
            maxSeatOverhang = current.TryGetProperty("maxSeatOverhang", out var o) ? o.GetInt32() : (int?)null,
            approvalRequiredAbovePartySize = approvalAbove
                ?? (current.TryGetProperty("approvalRequiredAbovePartySize", out var a) ? a.GetInt32() : (int?)null),
            walkInHoldbackMinutes = current.GetProperty("walkInHoldbackMinutes").GetInt32(),
            autoConfirmGuestsWithGoodRecord = on,
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(on, (await ReadAsync(manager, url)).GetProperty("autoConfirmGuestsWithGoodRecord").GetBoolean());
    }

    /// <summary>Moves the API's clock to 12:00 at the branch two days from now.</summary>
    private static void MoveToNoonInTwoDays(YallaApiFactory factory)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);
        var date = DateOnly.FromDateTime(localNow).AddDays(2);

        factory.Clock.UtcNow = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(12, 0)), zone), DateTimeKind.Utc);
    }

    private static CreateReservationCommand Booking(TestBranch branch, Guid tableId, TimeOnly localTime, DateOnly? date = null) =>
        new(
            branch.BranchId,
            tableId,
            date ?? Sunday,
            localTime,
            2,
            GuestName: "Bella Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7());
}
