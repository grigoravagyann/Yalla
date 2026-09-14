using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Application.Diners;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K12: the notifications feed - each producer writes its entry beside its push, the reminder waits for
/// its moment, the unread count, marking read, one diner's feed being nobody else's, and the 90-day sweep.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DinerNotificationFeedTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ producers

    [SkippableFact]
    public async Task Approving_and_declining_a_pending_booking_each_write_an_entry_beside_the_push()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (token, _) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        AuthBranch branch;
        Guid bigTableId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);

            // Ten seats, so a party of nine fits and needs the venue's approval.
            var testBranch = new TestBranch(
                branch.VenueId, branch.BranchId, branch.WaiterId, branch.ManagerId, branch.TableIds, "Asia/Yerevan");
            bigTableId = (await TestBranchBuilder.AddTableAsync(db, testBranch, "12", seats: 10)).Id;
        }

        var approvedId = await DinerFeedTestData.BookAsync(diner, factory, branch, bigTableId, daysOut: 2, partySize: 9);
        var declinedId = await DinerFeedTestData.BookAsync(diner, factory, branch, bigTableId, daysOut: 3, partySize: 9);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/reservations/{approvedId}/approve", new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PostAsJsonAsync($"/api/reservations/{declinedId}/reject", new { reason = "A private event that evening." })).StatusCode);

        var feed = await diner.GetFromJsonAsync<JsonElement>("/api/diner/notifications");
        var items = feed.GetProperty("items").EnumerateArray().ToList();

        // Two decisions. Any reminders are days from due and do not show yet.
        Assert.Equal(2, items.Count);
        Assert.Equal(2, feed.GetProperty("unreadCount").GetInt32());

        // Newest first: the decline was written last.
        Assert.Equal(DinerNotificationKinds.BookingDeclined, items[0].GetProperty("kind").GetString());
        Assert.Equal(declinedId, items[0].GetProperty("reservationId").GetGuid());

        var confirmed = items[1];
        Assert.Equal(DinerNotificationKinds.BookingConfirmed, confirmed.GetProperty("kind").GetString());
        Assert.Equal(approvedId, confirmed.GetProperty("reservationId").GetGuid());
        Assert.Equal(branch.BranchId, confirmed.GetProperty("branchId").GetGuid());
        Assert.False(string.IsNullOrEmpty(confirmed.GetProperty("branchName").GetString()));
        Assert.False(confirmed.GetProperty("read").GetBoolean());

        // The app's words come from these, not from the server.
        var parameters = confirmed.GetProperty("params");
        Assert.Equal("18:00", parameters.GetProperty("time").GetString());
        Assert.Equal("9", parameters.GetProperty("partySize").GetString());
        Assert.False(string.IsNullOrEmpty(parameters.GetProperty("venueName").GetString()));
        Assert.False(string.IsNullOrEmpty(parameters.GetProperty("reservationCode").GetString()));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", parameters.GetProperty("date").GetString());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            // Beside each push.
            Assert.True(await db.OutboxMessages.AnyAsync(
                m => m.IdempotencyKey == OutboxMessageTypes.KeyFor("reservation", approvedId, "approved")));
            Assert.True(await db.OutboxMessages.AnyAsync(
                m => m.IdempotencyKey == OutboxMessageTypes.KeyFor("reservation", declinedId, "rejected")));

            // The declined booking's waiting reminder went with it; the approved one's matches its push.
            Assert.False(await db.DinerNotifications.AnyAsync(
                n => n.ReservationId == declinedId && n.Kind == DinerNotificationKinds.BookingReminder));
            Assert.Equal(
                await db.OutboxMessages.AnyAsync(m => m.IdempotencyKey == OutboxMessageTypes.KeyFor("reservation", approvedId, "reminder")),
                await db.DinerNotifications.AnyAsync(n => n.ReservationId == approvedId && n.Kind == DinerNotificationKinds.BookingReminder));
        }
    }

    [SkippableFact]
    public async Task A_reminder_shows_only_once_it_is_due_and_cancelling_the_booking_first_removes_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (token, dinerUserId) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        var keptId = await DinerFeedTestData.BookAsync(diner, factory, branch, branch.TableIds[0], daysOut: 2);
        var cancelledId = await DinerFeedTestData.BookAsync(diner, factory, branch, branch.TableIds[1], daysOut: 2);

        // Written, not yet shown.
        var now = await diner.GetFromJsonAsync<JsonElement>("/api/diner/notifications");
        Assert.Equal(0, now.GetProperty("items").GetArrayLength());
        Assert.Equal(0, now.GetProperty("unreadCount").GetInt32());

        Assert.Equal(
            HttpStatusCode.OK,
            (await diner.PostAsJsonAsync($"/api/reservations/{cancelledId}/cancel", new { reason = "Plans changed." })).StatusCode);

        await using var context = fixture.CreateContext(factory.Clock);

        Assert.False(await context.DinerNotifications.AnyAsync(n => n.ReservationId == cancelledId));

        var reminder = await context.DinerNotifications.AsNoTracking()
            .SingleAsync(n => n.ReservationId == keptId && n.Kind == DinerNotificationKinds.BookingReminder);
        var push = await context.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.IdempotencyKey == OutboxMessageTypes.KeyFor("reservation", keptId, "reminder"));

        Assert.Equal(dinerUserId, reminder.DinerUserId);
        Assert.Equal(push.ScheduledForUtc, reminder.CreatedAtUtc);
        Assert.True(reminder.CreatedAtUtc > factory.Clock.UtcNow);

        // At its moment, the feed shows it. The service over a clock moved on, rather than a token
        // presented days after it was issued.
        var feed = new DinerNotificationFeed(
            context,
            new TestClock(reminder.CreatedAtUtc.AddMinutes(1)),
            new TestActor(ActorType.Diner, staffMemberId: null, role: null, dinerUserId: dinerUserId));

        var page = await feed.ListAsync(before: null, limit: 20);
        var entry = Assert.Single(page.Items);

        Assert.Equal(DinerNotificationKinds.BookingReminder, entry.Kind);
        Assert.Equal(keptId, entry.ReservationId);
        Assert.Equal(1, page.UnreadCount);
        Assert.Null(page.NextCursor);
    }

    [SkippableFact]
    public async Task A_booking_seated_before_its_reminder_is_due_never_shows_it_and_completing_one_drops_it_too()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (token, dinerUserId) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        var seatedId = await DinerFeedTestData.BookAsync(diner, factory, branch, branch.TableIds[0], daysOut: 2);
        var finishedId = await DinerFeedTestData.BookAsync(diner, factory, branch, branch.TableIds[1], daysOut: 2);

        DateTime dueAtUtc;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            dueAtUtc = (await db.DinerNotifications.AsNoTracking()
                .SingleAsync(n => n.ReservationId == seatedId && n.Kind == DinerNotificationKinds.BookingReminder)).CreatedAtUtc;
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // Seated two days early, long before the reminder is due.
        Assert.Equal(HttpStatusCode.OK, (await SeatAsync(waiter, branch, branch.TableIds[0], seatedId)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.DinerNotifications.AnyAsync(n => n.ReservationId == seatedId));

            // The other booking's reminder is its own.
            Assert.True(await db.DinerNotifications.AnyAsync(
                n => n.ReservationId == finishedId && n.Kind == DinerNotificationKinds.BookingReminder));
        }

        // A booking seated before seating dropped reminders still has one waiting: finishing the meal drops it.
        Assert.Equal(HttpStatusCode.OK, (await SeatAsync(waiter, branch, branch.TableIds[1], finishedId)).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            db.DinerNotifications.Add(new DinerNotification(
                dinerUserId, DinerNotificationKinds.BookingReminder, "{\"time\":\"18:00\"}", dueAtUtc, branch.BranchId, finishedId));
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync(
                $"/api/branches/{branch.BranchId}/tables/{branch.TableIds[1]}/free",
                new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        await using var context = fixture.CreateContext(factory.Clock);

        Assert.Equal(
            ReservationStatus.Completed,
            (await context.Reservations.AsNoTracking().SingleAsync(r => r.Id == finishedId)).Status);
        Assert.False(await context.DinerNotifications.AnyAsync(n => n.ReservationId == finishedId));

        // Once both reminders would have been due, the feed has nothing to show.
        var feed = new DinerNotificationFeed(
            context,
            new TestClock(dueAtUtc.AddMinutes(1)),
            new TestActor(ActorType.Diner, staffMemberId: null, role: null, dinerUserId: dinerUserId));

        Assert.Empty((await feed.ListAsync(before: null, limit: 20)).Items);
    }

    [SkippableFact]
    public async Task The_venue_letting_an_accepted_booking_go_writes_an_entry_and_drops_its_reminder()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (token, _) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        var bookingId = await DinerFeedTestData.BookAsync(diner, factory, branch, branch.TableIds[0], daysOut: 2);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync(
                $"/api/reservations/{bookingId}/release",
                new { outcome = 2, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var feed = await diner.GetFromJsonAsync<JsonElement>("/api/diner/notifications");
        var entry = Assert.Single(feed.GetProperty("items").EnumerateArray());

        Assert.Equal(DinerNotificationKinds.BookingCancelledByVenue, entry.GetProperty("kind").GetString());
        Assert.Equal(bookingId, entry.GetProperty("reservationId").GetGuid());
        Assert.Equal(1, feed.GetProperty("unreadCount").GetInt32());

        await using var db2 = fixture.CreateContext(factory.Clock);
        Assert.False(await db2.DinerNotifications.AnyAsync(
            n => n.ReservationId == bookingId && n.Kind == DinerNotificationKinds.BookingReminder));
    }

    [SkippableFact]
    public async Task An_order_moved_to_ready_lands_in_its_diners_feed_at_a_branch_that_sends_that_push()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (token, dinerUserId) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        AuthBranch branch;
        Guid tabId;
        Guid orderId;
        string tableLabel;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            branch = await AuthTestData.CreateBranchAsync(db);
            var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            var tab = await AuthTestData.CreateOpenTabAsync(db, branch, branch.FirstTableId, now);

            var participant = TabParticipant.Guest(tab.TabId, "Ani", $"device-{Guid.NewGuid():N}", now, false, dinerUserId);
            participant.Approve(now);
            db.TabParticipants.Add(participant);

            var order = TabOrder.PlacedByDiner(tab.TabId, participant.Id, now);
            order.AddLine(menu.Coffee, "Flat white", TestMenu.CoffeeAmd, 1);
            db.TabOrders.Add(order);

            await db.SaveChangesAsync();

            await db.Branches
                .Where(b => b.Id == branch.BranchId)
                .ExecuteUpdateAsync(set => set.SetProperty(b => b.NotifyOnOrderReady, true));

            tabId = tab.TabId;
            orderId = order.Id;
            tableLabel = await db.DiningTables.Where(t => t.Id == branch.FirstTableId).Select(t => t.Label).FirstAsync();
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        Assert.Equal(HttpStatusCode.OK, (await waiter.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = 2 })).StatusCode);

        // In the kitchen is not news.
        Assert.Equal(0, (await diner.GetFromJsonAsync<JsonElement>("/api/diner/notifications")).GetProperty("unreadCount").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await waiter.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status = 3 })).StatusCode);

        var feed = await diner.GetFromJsonAsync<JsonElement>("/api/diner/notifications");
        var entry = Assert.Single(feed.GetProperty("items").EnumerateArray());

        Assert.Equal(DinerNotificationKinds.OrderReady, entry.GetProperty("kind").GetString());
        Assert.Equal(orderId, entry.GetProperty("orderId").GetGuid());
        Assert.Equal(tabId, entry.GetProperty("tabId").GetGuid());
        Assert.Equal(branch.BranchId, entry.GetProperty("branchId").GetGuid());
        Assert.Equal(tableLabel, entry.GetProperty("params").GetProperty("tableLabel").GetString());

        await using var db2 = fixture.CreateContext(factory.Clock);
        Assert.True(await db2.OutboxMessages.AnyAsync(m => m.IdempotencyKey == OutboxMessageTypes.KeyFor("order", orderId, "ready")));
    }

    [SkippableFact]
    public async Task A_review_taken_down_tells_its_author_once_per_takedown()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch home;
        Guid authorId;
        Guid reviewId;
        PlatformAdminAccount admin;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            home = await AuthTestData.CreateBranchAsync(db);
            authorId = await ReviewTestData.SeedDinerAsync(db, "Ani Grigoryan", now);
            reviewId = await ReviewTestData.SeedReviewAsync(db, home.BranchId, authorId, 1, "Awful.", now.AddMinutes(-5));
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        var url = $"/api/platform/reviews/{reviewId}/visibility";

        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(url, new { hidden = true, reason = "Names a member of staff." })).StatusCode);

        // A new reason on a review already down is not a second takedown.
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(url, new { hidden = true, reason = "Names staff." })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var entry = await db.DinerNotifications.AsNoTracking()
                .SingleAsync(n => n.DinerUserId == authorId && n.Kind == DinerNotificationKinds.ReviewHidden);

            Assert.Equal(home.BranchId, entry.BranchId);
            Assert.Contains(reviewId.ToString(), entry.ParamsJson, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(url, new { hidden = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(url, new { hidden = true, reason = "Again." })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(
                2,
                await db.DinerNotifications.CountAsync(n => n.DinerUserId == authorId && n.Kind == DinerNotificationKinds.ReviewHidden));
        }
    }

    // ------------------------------------------------------------ reading and marking

    [SkippableFact]
    public async Task The_feed_pages_newest_first_counts_unread_and_marks_read_only_the_callers_own()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (aniToken, aniId) = await DinerFeedTestData.SignInDinerAsync(factory);
        var (narekToken, narekId) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var ani = factory.CreateClientWithToken(aniToken);
        using var narek = factory.CreateClientWithToken(narekToken);

        Guid futureId;
        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            var anHourAgo = factory.Clock.UtcNow.AddHours(-1);

            // Ten written in the same instant, so a page boundary falls between equals; fifteen older.
            db.DinerNotifications.AddRange(Enumerable.Range(0, 10).Select(_ => Entry(aniId, anHourAgo)));
            db.DinerNotifications.AddRange(Enumerable.Range(1, 15).Select(i => Entry(aniId, anHourAgo.AddMinutes(-i))));
            db.DinerNotifications.AddRange(Enumerable.Range(0, 3).Select(_ => Entry(narekId, anHourAgo)));

            var future = Entry(aniId, factory.Clock.UtcNow.AddHours(2));
            db.DinerNotifications.Add(future);

            await db.SaveChangesAsync();
            futureId = future.Id;
        }

        var first = await ani.GetFromJsonAsync<JsonElement>("/api/diner/notifications?limit=20");
        Assert.Equal(20, first.GetProperty("items").GetArrayLength());
        Assert.Equal(25, first.GetProperty("unreadCount").GetInt32());

        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await ani.GetFromJsonAsync<JsonElement>($"/api/diner/notifications?limit=20&before={Uri.EscapeDataString(cursor)}");
        Assert.Equal(5, second.GetProperty("items").GetArrayLength());
        Assert.False(second.TryGetProperty("nextCursor", out var last) && last.ValueKind == JsonValueKind.String);

        var all = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).ToList();
        var ids = all.Select(i => i.GetProperty("notificationId").GetGuid()).ToList();

        // Every entry once, none of Narek's, not the one still to come, newest first.
        Assert.Equal(25, ids.Distinct().Count());
        Assert.DoesNotContain(futureId, ids);

        var times = all.Select(i => i.GetProperty("createdAtUtc").GetDateTime()).ToList();
        Assert.Equal(times.OrderByDescending(t => t).ToList(), times);

        // A cursor the feed did not issue, and limits out of range.
        await ReviewIntegrityTests.AssertProblemAsync(
            await ani.GetAsync("/api/diner/notifications?before=not-a-cursor"), HttpStatusCode.BadRequest, "invalid-request");
        Assert.Equal(HttpStatusCode.BadRequest, (await ani.GetAsync("/api/diner/notifications?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ani.GetAsync("/api/diner/notifications?limit=51")).StatusCode);

        // Two by id.
        Assert.Equal(HttpStatusCode.NoContent, (await ani.PostAsJsonAsync("/api/diner/notifications/read", new { ids = new[] { ids[0], ids[1] } })).StatusCode);
        Assert.Equal(23, await UnreadAsync(ani));

        // Narek naming Ani's entries marks nothing of hers.
        Assert.Equal(HttpStatusCode.NoContent, (await narek.PostAsJsonAsync("/api/diner/notifications/read", new { upTo = ids[3], ids = new[] { ids[2] } })).StatusCode);
        Assert.Equal(23, await UnreadAsync(ani));
        Assert.Equal(3, await UnreadAsync(narek));

        // Up to the eleventh: it and everything older. The newest ten less the two already read remain.
        Assert.Equal(HttpStatusCode.NoContent, (await ani.PostAsJsonAsync("/api/diner/notifications/read", new { upTo = ids[10] })).StatusCode);
        Assert.Equal(8, await UnreadAsync(ani));

        var reread = await ani.GetFromJsonAsync<JsonElement>("/api/diner/notifications?limit=50");
        Assert.All(
            reread.GetProperty("items").EnumerateArray().Skip(10),
            i => Assert.True(i.GetProperty("read").GetBoolean()));

        await ReviewIntegrityTests.AssertProblemAsync(
            await ani.PostAsJsonAsync(
                "/api/diner/notifications/read",
                new { ids = Enumerable.Range(0, DinerNotificationFeedLimits.MaxIdsPerRead + 1).Select(_ => Guid.CreateVersion7()).ToArray() }),
            HttpStatusCode.UnprocessableEntity, "validation-failed", "ids");

        // Neither named: everything that has appeared.
        Assert.Equal(HttpStatusCode.NoContent, (await ani.PostAsJsonAsync("/api/diner/notifications/read", new { })).StatusCode);
        Assert.Equal(0, await UnreadAsync(ani));
        Assert.Equal(3, await UnreadAsync(narek));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            // The one still to come is not marked read before anybody could have seen it.
            Assert.Null((await db.DinerNotifications.AsNoTracking().SingleAsync(n => n.Id == futureId)).ReadAtUtc);
        }

        // Only a diner account.
        using var anyone = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anyone.GetAsync("/api/diner/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anyone.PostAsJsonAsync("/api/diner/notifications/read", new { })).StatusCode);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/diner/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/diner/notifications/read", new { })).StatusCode);
    }

    [SkippableFact]
    public async Task Entries_older_than_ninety_days_are_swept_and_newer_ones_stay()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        DinerNotification old;
        DinerNotification recent;
        DinerNotification fresh;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var now = factory.Clock.UtcNow;
            var dinerUserId = await ReviewTestData.SeedDinerAsync(db, "Ani", now);

            old = Entry(dinerUserId, now.AddDays(-(DinerNotification.RetentionDays + 1)));
            recent = Entry(dinerUserId, now.AddDays(-(DinerNotification.RetentionDays - 1)));
            fresh = Entry(dinerUserId, now);

            db.DinerNotifications.AddRange(old, recent, fresh);
            await db.SaveChangesAsync();
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DinerNotificationRetention>().PurgeAsync();
        }

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.DinerNotifications.AnyAsync(n => n.Id == old.Id));
            Assert.True(await db.DinerNotifications.AnyAsync(n => n.Id == recent.Id));
            Assert.True(await db.DinerNotifications.AnyAsync(n => n.Id == fresh.Id));
        }
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private static Task<HttpResponseMessage> SeatAsync(HttpClient waiter, AuthBranch branch, Guid tableId, Guid reservationId) =>
        waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{tableId}/seat-reservation",
            new { reservationId, clientCommandId = Guid.CreateVersion7() });

    private static DinerNotification Entry(Guid dinerUserId, DateTime atUtc) =>
        new(dinerUserId, DinerNotificationKinds.OrderReady, "{\"tableLabel\":\"3\"}", atUtc);

    private static async Task<int> UnreadAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/diner/notifications?limit=1")).GetProperty("unreadCount").GetInt32();
}
