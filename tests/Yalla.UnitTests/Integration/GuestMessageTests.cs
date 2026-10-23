using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Identity;
using Yalla.Domain.Messages;
using Yalla.Infrastructure.Notifications;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;
using static Yalla.UnitTests.Integration.PreOrderTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Guest messages end to end: a booking's thread with the guest's words, the venue's reply and ask, the guest's answer
/// and the system line; the tablet's inbox and floor badge; the venue's settings; the pace limit; the closed thread; a
/// takeaway order's thread. See <c>docs/messages.md</c>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class GuestMessageTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The whole way on a booking: the guest writes (a retry answers the same message), the floor and the inbox badge it,
    /// the waiter reads and replies (the guest is told, without the waiter's name), the guest reads; the venue asks "are
    /// you coming?" (the answer bar shows, the push carries the venue-ask category); the guest says late by 15 - the
    /// answer and "held until 18:15" land in the thread.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_thread_carries_both_sides_the_ask_and_the_answer()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        await fixture.ClearOutboxAsync(factory.Clock);
        var (branch, _) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var stranger = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();
        var thread = $"/api/diner/reservations/{id}/conversation";
        var staffThread = $"/api/reservations/{id}/conversation";

        factory.Clock.UtcNow = startUtc.AddDays(-1);

        // Nobody has written: no thread yet, and the composer still shows.
        var empty = await ReadAsync(diner, thread);
        Assert.False(empty.TryGetProperty("conversationId", out _));
        Assert.Empty(empty.GetProperty("messages").EnumerateArray());
        Assert.True(empty.GetProperty("open").GetBoolean());
        Assert.True(empty.GetProperty("guestsCanWrite").GetBoolean());
        Assert.False(empty.GetProperty("pendingAsk").GetBoolean());
        Assert.Equal("reservation", empty.GetProperty("subject").GetProperty("kind").GetString());
        Assert.Equal("18:00", empty.GetProperty("subject").GetProperty("localTime").GetString());
        Assert.Equal(booking.GetProperty("endUtc").GetDateTime().AddHours(2), empty.GetProperty("closesAtUtc").GetDateTime());

        var command = Guid.CreateVersion7();
        var sent = await OkAsync(await diner.PostAsJsonAsync(
            $"{thread}/messages", new { text = "  We are four, one needs a high chair.  ", clientCommandId = command }));
        var conversationId = sent.GetProperty("conversationId").GetGuid();
        Assert.Equal("We are four, one needs a high chair.", sent.GetProperty("text").GetString());
        Assert.Equal("guest", sent.GetProperty("author").GetString());
        Assert.False(sent.GetProperty("wasReplay").GetBoolean());

        var retried = await OkAsync(await diner.PostAsJsonAsync(
            $"{thread}/messages", new { text = "We are four, one needs a high chair.", clientCommandId = command }));
        Assert.True(retried.GetProperty("wasReplay").GetBoolean());
        Assert.Equal(sent.GetProperty("id").GetGuid(), retried.GetProperty("id").GetGuid());

        // The floor badges it - the tile and the tab - and the inbox puts it first.
        var floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        Assert.Equal(1, floor.GetProperty("messages").GetProperty("unreadTotal").GetInt32());
        Assert.Equal(startUtc.AddDays(-1), floor.GetProperty("messages").GetProperty("oldestUnansweredUtc").GetDateTime());
        var tile = floor.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("tableId").GetGuid() == branch.TableIds[0]);
        Assert.Equal(1, tile.GetProperty("nextReservationUnreadMessages").GetInt32());

        var inbox = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations");
        var row = Assert.Single(inbox.GetProperty("items").EnumerateArray());
        Assert.Equal(conversationId, row.GetProperty("conversationId").GetGuid());
        Assert.Equal(1, row.GetProperty("unreadFromGuest").GetInt32());
        Assert.Equal("Ani App", row.GetProperty("subject").GetProperty("guestName").GetString());
        Assert.Equal(3, inbox.GetProperty("remindAfterMinutes").GetInt32());
        Assert.False(inbox.TryGetProperty("templates", out _));
        Assert.Equal(1, inbox.GetProperty("unreadTotal").GetInt32());

        // The waiter opens it and replies.
        factory.Clock.UtcNow = startUtc.AddDays(-1).AddMinutes(2);
        var staffView = await ReadAsync(waiter, staffThread);
        Assert.Equal(1, staffView.GetProperty("unreadCount").GetInt32());
        Assert.True(staffView.GetProperty("canWrite").GetBoolean());
        Assert.Equal("Ani App", staffView.GetProperty("guestName").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await waiter.PostAsync($"{staffThread}/read", null)).StatusCode);
        floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        Assert.Equal(0, floor.GetProperty("messages").GetProperty("unreadTotal").GetInt32());

        // Read, but not answered: the remind still counts from the guest's message.
        Assert.Equal(startUtc.AddDays(-1), floor.GetProperty("messages").GetProperty("oldestUnansweredUtc").GetDateTime());

        factory.Clock.UtcNow = startUtc.AddDays(-1).AddMinutes(3);
        var reply = await OkAsync(await waiter.PostAsJsonAsync(
            $"{staffThread}/messages", new { text = "The chair will be at table 6.", clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal("Test Waiter", reply.GetProperty("staffName").GetString());
        Assert.Equal(conversationId, reply.GetProperty("conversationId").GetGuid());

        floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        Assert.False(floor.GetProperty("messages").TryGetProperty("oldestUnansweredUtc", out _));

        // The guest sees the venue, not the waiter.
        var guestView = await ReadAsync(diner, thread);
        Assert.Equal(1, guestView.GetProperty("unreadCount").GetInt32());
        var venueMessage = guestView.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("venue", venueMessage.GetProperty("author").GetString());
        Assert.False(venueMessage.TryGetProperty("staffName", out _));
        Assert.Equal(startUtc.AddDays(-1).AddMinutes(3), guestView.GetProperty("venueLastReadUtc").GetDateTime());

        var mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal(1, mine.GetProperty("messages").GetProperty("unread").GetInt32());
        Assert.Equal("venue", mine.GetProperty("messages").GetProperty("lastAuthor").GetString());
        Assert.Equal("The chair will be at table 6.", mine.GetProperty("messages").GetProperty("lastText").GetString());

        var unread = await ReadAsync(diner, "/api/diner/messages/unread");
        Assert.Equal(1, unread.GetProperty("total").GetInt32());
        Assert.Equal(id, Assert.Single(unread.GetProperty("items").EnumerateArray()).GetProperty("subjectId").GetGuid());

        factory.Clock.UtcNow = startUtc.AddDays(-1).AddMinutes(4);
        Assert.Equal(HttpStatusCode.NoContent, (await diner.PostAsync($"{thread}/read", null)).StatusCode);
        Assert.Equal(0, (await ReadAsync(diner, "/api/diner/messages/unread")).GetProperty("total").GetInt32());
        Assert.Equal(
            startUtc.AddDays(-1).AddMinutes(4),
            (await ReadAsync(waiter, staffThread)).GetProperty("guestLastReadUtc").GetDateTime());

        // The venue asks; the answer bar shows.
        factory.Clock.UtcNow = startUtc.AddMinutes(-60);
        Assert.True((await OkAsync(await waiter.PostAsync($"/api/reservations/{id}/ask-on-my-way", null))).GetProperty("notified").GetBoolean());

        guestView = await ReadAsync(diner, thread);
        Assert.True(guestView.GetProperty("pendingAsk").GetBoolean());
        Assert.Equal("ask-coming", guestView.GetProperty("messages").EnumerateArray().Last().GetProperty("kind").GetString());

        staffView = await ReadAsync(waiter, staffThread);
        Assert.Equal("Test Waiter", staffView.GetProperty("messages").EnumerateArray().Last().GetProperty("staffName").GetString());
        Assert.True(Assert.Single((await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations"))
            .GetProperty("items").EnumerateArray()).GetProperty("awaitingGuestAnswer").GetBoolean());

        // Two venue messages, two pushes and two feed entries - the reply's and the ask's.
        var channel = new RecordingChannel();
        await DeliverAsync(channel, conversationId, factory.Clock);

        Assert.Equal(2, channel.OfKind("venue-message").Count);
        var text = Assert.Single(channel.Messages, m => m.Data["ask"] == "false");
        Assert.Equal("venue-message", text.CategoryId);
        Assert.Equal("The chair will be at table 6.", text.Body);
        Assert.Equal(id.ToString(), text.Data["reservationId"]);
        var ask = Assert.Single(channel.Messages, m => m.Data["ask"] == "true");
        Assert.Equal("venue-ask", ask.CategoryId);
        Assert.Equal(NotificationText.VenueMessage("hy", "-", null, ask: true, new TimeOnly(18, 0)).Body, ask.Body);
        Assert.Equal(conversationId.ToString(), ask.Data["conversationId"]);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var entries = await db.DinerNotifications
                .Where(n => n.ReservationId == id && n.Kind == DinerNotificationKinds.VenueMessage)
                .Select(n => n.ParamsJson)
                .ToListAsync();

            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.Contains($"\"conversationId\":\"{conversationId}\"", e));
            Assert.DoesNotContain(entries, e => e.Contains("Test Waiter"));

            // The venue's ask no longer goes out as the automatic question's push on top.
            Assert.False(await db.OutboxMessages.AnyAsync(
                m => m.Type == OutboxMessageTypes.ReservationOnYourWay && m.IdempotencyKey.Contains(":asked:")));
        }

        // The guest answers from the bar: late by 15 - the answer, then "held until 18:15".
        factory.Clock.UtcNow = startUtc.AddMinutes(-58);
        await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/running-late", new { minutes = 15 }));

        guestView = await ReadAsync(diner, thread);
        Assert.False(guestView.GetProperty("pendingAsk").GetBoolean());
        var last = guestView.GetProperty("messages").EnumerateArray().TakeLast(2).ToList();
        Assert.Equal("answer", last[0].GetProperty("kind").GetString());
        Assert.Equal("late", last[0].GetProperty("answer").GetProperty("kind").GetString());
        Assert.Equal(15, last[0].GetProperty("answer").GetProperty("lateMinutes").GetInt32());
        Assert.Equal("system", last[1].GetProperty("author").GetString());
        Assert.Equal("status", last[1].GetProperty("kind").GetString());
        Assert.Equal("18:15", last[1].GetProperty("status").GetProperty("heldUntilLocal").GetString());
        Assert.False(last[1].GetProperty("status").GetProperty("cancelled").GetBoolean());

        // On the tablet the answer is a guest message waiting for a reply.
        row = Assert.Single((await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations")).GetProperty("items").EnumerateArray());
        Assert.False(row.GetProperty("awaitingGuestAnswer").GetBoolean());
        Assert.Equal("answer", row.GetProperty("lastMessage").GetProperty("kind").GetString());
        Assert.Equal(startUtc.AddMinutes(-58), row.GetProperty("oldestUnansweredGuestMessageUtc").GetDateTime());

        // Somebody else's booking is not found - word for word as no booking.
        await ProblemAsync(await stranger.GetAsync(thread), HttpStatusCode.NotFound, "not-found");
    }

    /// <summary>
    /// The console's settings: every bad field named at once; guests switched off are refused a typed message but the
    /// answer buttons still write; waiters switched off are refused, a manager is not; the inbox carries the venue's
    /// quick replies and reminder.
    /// </summary>
    [SkippableFact]
    public async Task The_venues_settings_decide_who_writes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, managerToken) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(managerToken);
        var settings = $"/api/branches/{branch.BranchId}/message-settings";

        var defaults = await ReadAsync(manager, settings);
        Assert.True(defaults.GetProperty("guestsCanWrite").GetBoolean());
        Assert.True(defaults.GetProperty("waitersCanWrite").GetBoolean());
        Assert.Equal(3, defaults.GetProperty("remindAfterMinutes").GetInt32());
        Assert.False(defaults.TryGetProperty("templates", out _));
        Assert.Equal(8, defaults.GetProperty("bounds").GetProperty("maxTemplates").GetInt32());

        var bad = await ProblemAsync(
            await manager.PutAsJsonAsync(settings, new
            {
                guestsCanWrite = true,
                waitersCanWrite = true,
                templates = new[] { "a", " ", "c", "d", "e", "f", "g", "h", new string('x', 121) },
                remindAfterMinutes = 0,
            }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");
        var fields = bad.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("templates", fields);
        Assert.Contains("templates[1]", fields);
        Assert.Contains("templates[8]", fields);
        Assert.Contains("remindAfterMinutes", fields);

        await ProblemAsync(
            await manager.PutAsJsonAsync(settings, new { waitersCanWrite = true, remindAfterMinutes = 3 }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");

        var saved = await OkAsync(await manager.PutAsJsonAsync(settings, new
        {
            guestsCanWrite = false,
            waitersCanWrite = false,
            templates = new[] { "  Լավ, սպասում ենք  ", "Սեղանը պատրաստ է" },
            remindAfterMinutes = 5,
        }));
        Assert.Equal("Լավ, սպասում ենք", saved.GetProperty("templates")[0].GetString());

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();

        factory.Clock.UtcNow = startUtc.AddMinutes(-60);

        var refused = await ProblemAsync(
            await diner.PostAsJsonAsync(
                $"/api/diner/reservations/{id}/conversation/messages", new { text = "Hello", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Forbidden,
            "guests-cannot-write");
        Assert.Equal(branch.BranchId, refused.GetProperty("context").GetProperty("branchId").GetGuid());

        await ProblemAsync(
            await waiter.PostAsJsonAsync(
                $"/api/reservations/{id}/conversation/messages", new { text = "Hello", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Forbidden,
            "waiters-cannot-write");
        Assert.False((await ReadAsync(waiter, $"/api/reservations/{id}/conversation")).GetProperty("canWrite").GetBoolean());

        var fromManager = await OkAsync(await manager.PostAsJsonAsync(
            $"/api/reservations/{id}/conversation/messages", new { text = "See you at six.", clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal("Test Manager", fromManager.GetProperty("staffName").GetString());

        // The answer buttons still write, typed messages switched off or not.
        await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }));
        var guestView = await ReadAsync(diner, $"/api/diner/reservations/{id}/conversation");
        Assert.False(guestView.GetProperty("guestsCanWrite").GetBoolean());
        var answer = guestView.GetProperty("messages").EnumerateArray().TakeLast(2).ToList();
        Assert.Equal("coming", answer[0].GetProperty("answer").GetProperty("kind").GetString());
        Assert.Equal("coming", answer[1].GetProperty("status").GetProperty("answer").GetString());
        Assert.False(answer[1].GetProperty("status").TryGetProperty("heldUntilLocal", out _));

        var inbox = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations");
        Assert.Equal(5, inbox.GetProperty("remindAfterMinutes").GetInt32());
        Assert.False(inbox.GetProperty("waitersCanWrite").GetBoolean());
        Assert.Equal(2, inbox.GetProperty("templates").GetArrayLength());
    }

    /// <summary>Five messages a minute and no more; the sixth is told when to try again, and a minute later it goes.</summary>
    [SkippableFact]
    public async Task A_guest_writing_too_fast_is_slowed_down()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var at = booking.GetProperty("startUtc").GetDateTime().AddDays(-1);
        var url = $"/api/diner/reservations/{id}/conversation/messages";

        for (var i = 0; i < ConversationRules.GuestMessagesPerMinute; i++)
        {
            factory.Clock.UtcNow = at.AddSeconds(i * 5);
            await OkAsync(await diner.PostAsJsonAsync(url, new { text = $"Message {i}", clientCommandId = Guid.CreateVersion7() }));
        }

        factory.Clock.UtcNow = at.AddSeconds(30);
        var tooFast = await ProblemAsync(
            await diner.PostAsJsonAsync(url, new { text = "One more", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.TooManyRequests,
            "messages-too-fast");
        Assert.Equal(at.AddMinutes(1), tooFast.GetProperty("context").GetProperty("retryAfterUtc").GetDateTime());

        factory.Clock.UtcNow = at.AddMinutes(1);
        await OkAsync(await diner.PostAsJsonAsync(url, new { text = "One more", clientCommandId = Guid.CreateVersion7() }));

        await ProblemAsync(
            await diner.PostAsJsonAsync(url, new { text = "   ", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");
        await ProblemAsync(
            await diner.PostAsJsonAsync(url, new { text = new string('a', 501), clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");
    }

    /// <summary>
    /// "Not coming" is the booking's cancel: it lands in a thread that exists, with "booking cancelled" after it, and a
    /// booking cancelled a week ahead with no thread gets none. Two hours later the thread is closed (409), readable for
    /// 30 days, then not found.
    /// </summary>
    [SkippableFact]
    public async Task A_cancelled_bookings_thread_closes_and_is_read_for_thirty_days()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var other = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();
        var quiet = (await CreatedAsync(other, AppBooking(factory, branch, daysAhead: 2, tableIndex: 1))).GetProperty("id").GetGuid();
        var thread = $"/api/diner/reservations/{id}/conversation";

        factory.Clock.UtcNow = startUtc.AddDays(-1);
        await OkAsync(await diner.PostAsJsonAsync($"{thread}/messages", new { text = "Can we come at seven?", clientCommandId = Guid.CreateVersion7() }));
        await OkAsync(await other.PostAsJsonAsync($"/api/reservations/{quiet}/cancel", new { }));
        Assert.False((await ReadAsync(other, $"/api/diner/reservations/{quiet}/conversation")).TryGetProperty("conversationId", out _));

        factory.Clock.UtcNow = startUtc.AddDays(-1).AddMinutes(10);
        await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/cancel", new { }));

        var view = await ReadAsync(diner, thread);
        var last = view.GetProperty("messages").EnumerateArray().TakeLast(2).ToList();
        Assert.Equal("not-coming", last[0].GetProperty("answer").GetProperty("kind").GetString());
        Assert.True(last[1].GetProperty("status").GetProperty("cancelled").GetBoolean());
        Assert.True(view.GetProperty("open").GetBoolean());
        Assert.Equal(startUtc.AddDays(-1).AddMinutes(10).AddHours(2), view.GetProperty("closesAtUtc").GetDateTime());

        factory.Clock.UtcNow = startUtc.AddDays(-1).AddMinutes(10).AddHours(2);
        var closed = await ProblemAsync(
            await diner.PostAsJsonAsync($"{thread}/messages", new { text = "Hello?", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "conversation-closed");
        Assert.Equal(id, closed.GetProperty("context").GetProperty("subjectId").GetGuid());
        await ProblemAsync(
            await waiter.PostAsJsonAsync(
                $"/api/reservations/{id}/conversation/messages", new { text = "Sorry to miss you.", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "conversation-closed");
        Assert.False((await ReadAsync(diner, thread)).GetProperty("open").GetBoolean());

        factory.Clock.UtcNow = startUtc.AddDays(29);
        Assert.False((await ReadAsync(diner, thread)).GetProperty("open").GetBoolean());

        factory.Clock.UtcNow = startUtc.AddDays(31);
        await ProblemAsync(await diner.GetAsync(thread), HttpStatusCode.NotFound, "not-found");
        Assert.Equal(id, (await ReadAsync(waiter, $"/api/reservations/{id}/conversation")).GetProperty("subject").GetProperty("id").GetGuid());
    }

    /// <summary>
    /// A takeaway order's thread: the guest asks, the counter answers and is told, the Orders tab and the unread list
    /// carry it; two hours after it is collected it closes.
    /// </summary>
    [SkippableFact]
    public async Task A_takeaway_order_has_its_own_thread()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        TestMenu menu;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 1);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
        }

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/takeaway-settings", new
        {
            enabled = true,
            autoAccept = true,
            prepMinutes = 20,
            lastOrderMinutes = 30,
            maxInProgress = 5,
        }));

        // Tomorrow at 14:00 in Yerevan: inside the test branch's hours.
        var afternoon = DateTime.UtcNow.Date.AddDays(1).AddHours(10);
        factory.Clock.UtcNow = afternoon;

        var order = await OkAsync(await diner.PostAsJsonAsync("/api/diner/takeaway-orders", new
        {
            branchId = branch.BranchId,
            pickup = new { asap = true },
            lines = new[] { new { menuItemId = menu.Khachapuri, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }));
        var orderId = order.GetProperty("id").GetGuid();
        var code = order.GetProperty("code").GetString();

        factory.Clock.UtcNow = afternoon.AddMinutes(1);
        await OkAsync(await diner.PostAsJsonAsync(
            $"/api/diner/takeaway-orders/{orderId}/conversation/messages",
            new { text = "Can I come ten minutes early?", clientCommandId = Guid.CreateVersion7() }));

        var row = Assert.Single((await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations")).GetProperty("items").EnumerateArray());
        Assert.Equal("takeaway", row.GetProperty("subject").GetProperty("kind").GetString());
        Assert.Equal(code, row.GetProperty("subject").GetProperty("label").GetString());
        Assert.Equal(2, row.GetProperty("subject").GetProperty("itemCount").GetInt32());
        Assert.Equal("14:20", row.GetProperty("subject").GetProperty("localTime").GetString());

        factory.Clock.UtcNow = afternoon.AddMinutes(2);
        await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/takeaway-orders/{orderId}/conversation/messages",
            new { text = "Yes, it will be ready.", clientCommandId = Guid.CreateVersion7() }));

        var view = await ReadAsync(diner, $"/api/diner/takeaway-orders/{orderId}/conversation");
        Assert.Equal(code, view.GetProperty("subject").GetProperty("takeawayCode").GetString());
        Assert.False(view.TryGetProperty("closesAtUtc", out _));
        Assert.Equal(1, view.GetProperty("unreadCount").GetInt32());

        var tab = (await ReadAsync(diner, "/api/diner/orders")).EnumerateArray().Single(o => o.GetProperty("orderId").GetGuid() == orderId);
        Assert.Equal(1, tab.GetProperty("messages").GetProperty("unread").GetInt32());
        Assert.Equal("Yes, it will be ready.", tab.GetProperty("messages").GetProperty("lastText").GetString());

        var unread = Assert.Single((await ReadAsync(diner, "/api/diner/messages/unread")).GetProperty("items").EnumerateArray());
        Assert.Equal("takeaway", unread.GetProperty("subjectKind").GetString());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var push = await db.OutboxMessages.SingleAsync(m => m.Type == OutboxMessageTypes.MessageToGuest
                                                                 && m.PayloadJson.Contains(orderId.ToString()));
            Assert.Contains("\"ask\":false", push.PayloadJson);
            Assert.True(await db.DinerNotifications.AnyAsync(n => n.OrderId == orderId && n.Kind == DinerNotificationKinds.VenueMessage));
        }

        // Ready and collected; two hours on, the thread closes.
        factory.Clock.UtcNow = afternoon.AddMinutes(15);
        await OkAsync(await waiter.PostAsync($"/api/takeaway-orders/{orderId}/ready", null));
        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{orderId}/collect", new { method = 1 }));

        factory.Clock.UtcNow = afternoon.AddMinutes(15).AddHours(2);
        await ProblemAsync(
            await diner.PostAsJsonAsync(
                $"/api/diner/takeaway-orders/{orderId}/conversation/messages",
                new { text = "Thank you!", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "conversation-closed");
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<(AuthBranch Branch, string ManagerToken)> ArrangeAsync(YallaApiFactory factory)
    {
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2);
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, acceptsOnlineBookings: true);
        }

        return (branch, await SignInManagerAsync(factory, branch));
    }

    /// <summary>Hands the thread's unsent pushes to their handler, as the dispatcher would.</summary>
    private async Task DeliverAsync(RecordingChannel channel, Guid conversationId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var handler = new VenueMessageHandler(db, channel, NullLogger<VenueMessageHandler>.Instance);
        var prefix = OutboxMessageTypes.PrefixFor("conversation", conversationId);

        var payloads = await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith(prefix) && m.Type == OutboxMessageTypes.MessageToGuest)
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => m.PayloadJson)
            .ToListAsync();

        foreach (var payload in payloads)
        {
            await handler.HandleAsync(payload, CancellationToken.None);
        }
    }
}
