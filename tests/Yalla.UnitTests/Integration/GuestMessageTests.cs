using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Messages;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Identity;
using Yalla.Domain.Messages;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Services;
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
        Assert.Equal("+37411223344", staffView.GetProperty("guestPhone").GetString());

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

        // On the tablet the answer is unread until somebody opens the thread, but it asks nothing back: nothing waits
        // for a reply, nothing rings.
        row = Assert.Single((await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations")).GetProperty("items").EnumerateArray());
        Assert.False(row.GetProperty("awaitingGuestAnswer").GetBoolean());
        Assert.Equal("answer", row.GetProperty("lastMessage").GetProperty("kind").GetString());
        Assert.Equal(15, row.GetProperty("lastMessage").GetProperty("answer").GetProperty("lateMinutes").GetInt32());
        Assert.Equal("18:15", row.GetProperty("latestStatus").GetProperty("heldUntilLocal").GetString());
        Assert.Equal(1, row.GetProperty("unreadFromGuest").GetInt32());
        Assert.False(row.TryGetProperty("oldestUnansweredGuestMessageUtc", out _));
        floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        Assert.Equal(1, floor.GetProperty("messages").GetProperty("unreadTotal").GetInt32());
        Assert.False(floor.GetProperty("messages").TryGetProperty("oldestUnansweredUtc", out _));

        // The guest's words after it do wait - from when they were written, not from the answer.
        factory.Clock.UtcNow = startUtc.AddMinutes(-57);
        await OkAsync(await diner.PostAsJsonAsync($"{thread}/messages", new { text = "Is parking free?", clientCommandId = Guid.CreateVersion7() }));
        row = Assert.Single((await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations")).GetProperty("items").EnumerateArray());
        Assert.Equal(2, row.GetProperty("unreadFromGuest").GetInt32());
        Assert.Equal(startUtc.AddMinutes(-57), row.GetProperty("oldestUnansweredGuestMessageUtc").GetDateTime());
        floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        Assert.Equal(2, floor.GetProperty("messages").GetProperty("unreadTotal").GetInt32());
        Assert.Equal(startUtc.AddMinutes(-57), floor.GetProperty("messages").GetProperty("oldestUnansweredUtc").GetDateTime());

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

        // Guests cannot type here, so no push offers a Reply the venue would refuse: the text has no actions, the ask
        // only its two answer buttons.
        await OkAsync(await manager.PostAsync($"/api/reservations/{id}/ask-on-my-way", null));
        var channel = new RecordingChannel();
        await DeliverAsync(channel, fromManager.GetProperty("conversationId").GetGuid(), factory.Clock);
        Assert.Equal("venue-message-readonly", Assert.Single(channel.Messages, m => m.Data["ask"] == "false").CategoryId);
        Assert.Equal("venue-ask-buttons", Assert.Single(channel.Messages, m => m.Data["ask"] == "true").CategoryId);

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

        // Told on the service's clock - the header and retryAfterUtc say the same moment, whatever the machine's clock.
        factory.Clock.UtcNow = at.AddSeconds(30);
        var refusal = await diner.PostAsJsonAsync(url, new { text = "One more", clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(TimeSpan.FromSeconds(30), refusal.Headers.RetryAfter?.Delta);
        var tooFast = await ProblemAsync(refusal, HttpStatusCode.TooManyRequests, "messages-too-fast");
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
        var afternoon = TestNow.UtcNow.Date.AddDays(1).AddHours(10);
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

        // Read up to the reply the screen showed - by its own createdAtUtc - on both sides of the order's thread.
        var replyAt = view.GetProperty("messages").EnumerateArray().Last().GetProperty("createdAtUtc").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await diner.PostAsJsonAsync(
            $"/api/diner/takeaway-orders/{orderId}/conversation/read", new { upToUtc = replyAt })).StatusCode);
        Assert.Equal(0, (await ReadAsync(diner, $"/api/diner/takeaway-orders/{orderId}/conversation")).GetProperty("unreadCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await waiter.PostAsJsonAsync(
            $"/api/takeaway-orders/{orderId}/conversation/read", new { upToUtc = replyAt })).StatusCode);
        Assert.Equal(0, (await ReadAsync(waiter, $"/api/takeaway-orders/{orderId}/conversation")).GetProperty("unreadCount").GetInt32());

        // A day on, nobody has written since, but the order is still moving: its thread is still in view.
        factory.Clock.UtcNow = afternoon.AddHours(25);
        Assert.Equal(orderId, Assert.Single((await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations"))
            .GetProperty("items").EnumerateArray()).GetProperty("subject").GetProperty("id").GetGuid());
        factory.Clock.UtcNow = afternoon.AddMinutes(3);

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

    /// <summary>
    /// Who reaches a booking's thread: another diner is told nothing exists; a waiter of another venue, a manager of a
    /// sibling branch and the kitchen are refused; a platform admin reads without the guest's number, cannot write, and
    /// their "read" marks nothing. A clientCommandId is a retry of the same words by the same writer - other words, or
    /// another member of staff, is 409 <c>client-command-id-in-use</c>, never the first message answered in their place.
    /// </summary>
    [SkippableFact]
    public async Task Only_the_guest_and_the_branchs_staff_reach_a_thread_and_a_command_id_is_one_message()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, managerToken) = await ArrangeAsync(factory);
        AuthBranch otherVenue;
        PanelAccount siblingManager;
        PlatformAdminAccount admin;
        Guid cookId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            otherVenue = await AuthTestData.CreateBranchAsync(db, tableCount: 1);
            var siblingBranchId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
            siblingManager = await AuthTestData.SeedManagerAsync(db, branch.VenueId, siblingBranchId);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
            cookId = await AuthTestData.SeedKitchenAsync(db, branch);
        }

        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var stranger = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(managerToken);
        using var otherWaiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, otherVenue));
        using var sibling = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, siblingManager));
        using var kitchen = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, branch, cookId));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        factory.Clock.UtcNow = booking.GetProperty("startUtc").GetDateTime().AddDays(-1);

        var thread = $"/api/diner/reservations/{id}/conversation";
        var staffThread = $"/api/reservations/{id}/conversation";
        var guestCommand = Guid.CreateVersion7();

        await OkAsync(await diner.PostAsJsonAsync($"{thread}/messages", new { text = "Hello", clientCommandId = guestCommand }));

        // Another diner: the booking does not exist for them, to read, write or mark.
        await ProblemAsync(
            await stranger.PostAsJsonAsync($"{thread}/messages", new { text = "Hi", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.NotFound,
            "not-found");
        await ProblemAsync(await stranger.PostAsync($"{thread}/read", null), HttpStatusCode.NotFound, "not-found");
        Assert.Equal(0, (await ReadAsync(stranger, "/api/diner/messages/unread")).GetProperty("total").GetInt32());

        // Staff who are not this branch's floor: refused, reading and writing alike.
        foreach (var outsider in new[] { otherWaiter, sibling, kitchen })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(staffThread)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await outsider.PostAsJsonAsync(
                    $"{staffThread}/messages", new { text = "Hi", clientCommandId = Guid.CreateVersion7() })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await sibling.GetAsync($"/api/branches/{branch.BranchId}/conversations")).StatusCode);

        // A platform admin reads - without the guest's number - writes nothing, and their read is nobody's.
        var looked = await ReadAsync(platform, staffThread);
        Assert.Equal(string.Empty, looked.GetProperty("guestPhone").GetString());
        Assert.False(looked.GetProperty("canWrite").GetBoolean());
        await ProblemAsync(
            await platform.PostAsJsonAsync($"{staffThread}/messages", new { text = "Hi", clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Forbidden,
            "forbidden");
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PostAsync($"{staffThread}/read", null)).StatusCode);
        Assert.Equal(1, (await ReadAsync(waiter, staffThread)).GetProperty("unreadCount").GetInt32());

        // The guest's id: the same words are a retry; other words are not.
        Assert.True((await OkAsync(await diner.PostAsJsonAsync(
            $"{thread}/messages", new { text = " Hello ", clientCommandId = guestCommand }))).GetProperty("wasReplay").GetBoolean());
        await ProblemAsync(
            await diner.PostAsJsonAsync($"{thread}/messages", new { text = "Hello again", clientCommandId = guestCommand }),
            HttpStatusCode.Conflict,
            "client-command-id-in-use");

        // The venue's id: the waiter's retry replays; the manager reusing it is another writer, not a retry.
        var staffCommand = Guid.CreateVersion7();
        var reply = await OkAsync(await waiter.PostAsJsonAsync(
            $"{staffThread}/messages", new { text = "Welcome", clientCommandId = staffCommand }));
        var again = await OkAsync(await waiter.PostAsJsonAsync(
            $"{staffThread}/messages", new { text = "Welcome", clientCommandId = staffCommand }));
        Assert.True(again.GetProperty("wasReplay").GetBoolean());
        Assert.Equal(reply.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        await ProblemAsync(
            await manager.PostAsJsonAsync($"{staffThread}/messages", new { text = "Welcome", clientCommandId = staffCommand }),
            HttpStatusCode.Conflict,
            "client-command-id-in-use");

        // Each side's ids are its own: the guest may use the number the venue used.
        Assert.False((await OkAsync(await diner.PostAsJsonAsync(
            $"{thread}/messages", new { text = "Thanks", clientCommandId = staffCommand }))).GetProperty("wasReplay").GetBoolean());

        var messages = (await ReadAsync(diner, thread)).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["Hello", "Welcome", "Thanks"], messages.Select(m => m.GetProperty("text").GetString()!).ToArray());
    }

    /// <summary>
    /// A guest message nobody answered rings the tablet while the thread is open - and not once it has closed: after
    /// that nobody can answer it, and a remind nobody can silence would ring until the thread left the inbox a day later.
    /// </summary>
    [SkippableFact]
    public async Task A_closed_thread_waits_for_no_answer()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var askedAt = booking.GetProperty("startUtc").GetDateTime().AddDays(-1);
        var floorUrl = $"/api/branches/{branch.BranchId}/tables/floor";
        var inboxUrl = $"/api/branches/{branch.BranchId}/conversations";

        factory.Clock.UtcNow = askedAt;
        await OkAsync(await diner.PostAsJsonAsync(
            $"/api/diner/reservations/{id}/conversation/messages", new { text = "Is there parking?", clientCommandId = Guid.CreateVersion7() }));

        // The guest gives up and cancels; the thread stays open two hours more and the question still waits.
        var cancelledAt = askedAt.AddMinutes(30);
        factory.Clock.UtcNow = cancelledAt;
        await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/cancel", new { }));

        factory.Clock.UtcNow = cancelledAt.AddHours(1);
        Assert.Equal(askedAt, (await ReadAsync(waiter, floorUrl)).GetProperty("messages").GetProperty("oldestUnansweredUtc").GetDateTime());
        var row = Assert.Single((await ReadAsync(waiter, inboxUrl)).GetProperty("items").EnumerateArray());
        Assert.True(row.GetProperty("open").GetBoolean());
        Assert.Equal(askedAt, row.GetProperty("oldestUnansweredGuestMessageUtc").GetDateTime());

        // Closed: still in the inbox for the day, but waiting for nobody - nothing to ring about.
        factory.Clock.UtcNow = cancelledAt.AddHours(2).AddMinutes(1);
        Assert.False((await ReadAsync(waiter, floorUrl)).GetProperty("messages").TryGetProperty("oldestUnansweredUtc", out _));
        row = Assert.Single((await ReadAsync(waiter, inboxUrl)).GetProperty("items").EnumerateArray());
        Assert.False(row.GetProperty("open").GetBoolean());
        Assert.False(row.TryGetProperty("oldestUnansweredGuestMessageUtc", out _));

        // Unread is another matter: the venue has still not read the question, nor the "not coming" the cancel wrote.
        Assert.Equal(2, row.GetProperty("unreadFromGuest").GetInt32());
        Assert.Equal("not-coming", row.GetProperty("lastMessage").GetProperty("answer").GetProperty("kind").GetString());
    }

    /// <summary>
    /// Eight quick replies at the longest the bounds allow are saved, whatever they are written in: JSON escapes every
    /// emoji as twelve characters and a control character as six, so the stored list can be several times the 960
    /// characters the bounds suggest.
    /// </summary>
    [SkippableFact]
    public async Task Quick_replies_at_their_bounds_are_saved_whatever_they_hold()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, managerToken) = await ArrangeAsync(factory);
        using var manager = factory.CreateClientWithToken(managerToken);

        // 60 emoji are 120 characters - the longest a quick reply may be.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F44D", ConversationRules.MaxTemplateLength / 2));
        var templates = Enumerable.Range(0, ConversationRules.MaxTemplates)
            .Select(i => i % 2 == 0 ? emoji : "a\u0001" + new string('\u0002', ConversationRules.MaxTemplateLength - 2))
            .ToArray();

        var saved = await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/message-settings", new
        {
            guestsCanWrite = true,
            waitersCanWrite = true,
            templates,
            remindAfterMinutes = 3,
        }));

        Assert.Equal(templates, saved.GetProperty("templates").EnumerateArray().Select(t => t.GetString()!).ToArray());
    }

    /// <summary>
    /// A read mark moves up to the newest message the screen showed (<c>upToUtc</c>) - so one that arrived between the
    /// poll and the "read" stays unread - never past now, never backwards; no body is now. Both sides, an instant with
    /// an offset included.
    /// </summary>
    [SkippableFact]
    public async Task A_read_marks_what_the_screen_showed_never_past_now_and_never_back()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var at = booking.GetProperty("startUtc").GetDateTime().AddDays(-1);
        var thread = $"/api/diner/reservations/{id}/conversation";
        var staffThread = $"/api/reservations/{id}/conversation";

        factory.Clock.UtcNow = at;
        var first = await OkAsync(await diner.PostAsJsonAsync($"{thread}/messages", new { text = "One", clientCommandId = Guid.CreateVersion7() }));
        factory.Clock.UtcNow = at.AddMinutes(1);
        await OkAsync(await diner.PostAsJsonAsync($"{thread}/messages", new { text = "Two", clientCommandId = Guid.CreateVersion7() }));

        // The tablet showed only the first: the second stays unread.
        factory.Clock.UtcNow = at.AddMinutes(2);
        Assert.Equal(HttpStatusCode.NoContent, (await waiter.PostAsJsonAsync(
            $"{staffThread}/read", new { upToUtc = first.GetProperty("createdAtUtc").GetString() })).StatusCode);
        var staffView = await ReadAsync(waiter, staffThread);
        Assert.Equal(1, staffView.GetProperty("unreadCount").GetInt32());
        Assert.Equal(at, staffView.GetProperty("venueLastReadUtc").GetDateTime());

        // An older mark changes nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await waiter.PostAsJsonAsync($"{staffThread}/read", new { upToUtc = at.AddHours(-1) })).StatusCode);
        Assert.Equal(at, (await ReadAsync(waiter, staffThread)).GetProperty("venueLastReadUtc").GetDateTime());

        // A mark in the future is now: what the guest writes next is unread.
        factory.Clock.UtcNow = at.AddMinutes(3);
        Assert.Equal(HttpStatusCode.NoContent, (await waiter.PostAsJsonAsync($"{staffThread}/read", new { upToUtc = at.AddDays(1) })).StatusCode);
        Assert.Equal(at.AddMinutes(3), (await ReadAsync(waiter, staffThread)).GetProperty("venueLastReadUtc").GetDateTime());
        factory.Clock.UtcNow = at.AddMinutes(4);
        await OkAsync(await diner.PostAsJsonAsync($"{thread}/messages", new { text = "Three", clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal(1, (await ReadAsync(waiter, staffThread)).GetProperty("unreadCount").GetInt32());

        // No body: now. An empty object too.
        factory.Clock.UtcNow = at.AddMinutes(5);
        Assert.Equal(HttpStatusCode.NoContent, (await waiter.PostAsync($"{staffThread}/read", null)).StatusCode);
        Assert.Equal(0, (await ReadAsync(waiter, staffThread)).GetProperty("unreadCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await waiter.PostAsJsonAsync($"{staffThread}/read", new { })).StatusCode);

        // The guest's side: two replies, the screen showed the first; then the second, written with Yerevan's offset.
        factory.Clock.UtcNow = at.AddMinutes(6);
        var reply = await OkAsync(await waiter.PostAsJsonAsync($"{staffThread}/messages", new { text = "Yes", clientCommandId = Guid.CreateVersion7() }));
        factory.Clock.UtcNow = at.AddMinutes(7);
        var second = await OkAsync(await waiter.PostAsJsonAsync($"{staffThread}/messages", new { text = "And yes", clientCommandId = Guid.CreateVersion7() }));

        factory.Clock.UtcNow = at.AddMinutes(8);
        Assert.Equal(HttpStatusCode.NoContent, (await diner.PostAsJsonAsync(
            $"{thread}/read", new { upToUtc = reply.GetProperty("createdAtUtc").GetString() })).StatusCode);
        Assert.Equal(1, (await ReadAsync(diner, thread)).GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, (await ReadAsync(diner, "/api/diner/messages/unread")).GetProperty("total").GetInt32());

        var inYerevan = new DateTimeOffset(second.GetProperty("createdAtUtc").GetDateTime(), TimeSpan.Zero).ToOffset(TimeSpan.FromHours(4));
        Assert.Equal(HttpStatusCode.NoContent, (await diner.PostAsJsonAsync(
            $"{thread}/read", new { upToUtc = inYerevan.ToString("O", System.Globalization.CultureInfo.InvariantCulture) })).StatusCode);
        Assert.Equal(0, (await ReadAsync(diner, thread)).GetProperty("unreadCount").GetInt32());
        Assert.Equal(at.AddMinutes(7), (await ReadAsync(waiter, staffThread)).GetProperty("guestLastReadUtc").GetDateTime());
    }

    /// <summary>
    /// The threads in view are those written to in the last day and those whose subject still takes messages - however
    /// long ago anybody wrote - and not a quiet one whose booking ended: the inbox and the floor alike, each thread once.
    /// </summary>
    [SkippableFact]
    public async Task A_quiet_thread_stays_in_view_while_its_booking_is_live()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var other = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var live = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 3));
        var liveId = live.GetProperty("id").GetGuid();
        var startUtc = live.GetProperty("startUtc").GetDateTime();
        var gone = (await CreatedAsync(other, AppBooking(factory, branch, daysAhead: 3, tableIndex: 1))).GetProperty("id").GetGuid();
        var inboxUrl = $"/api/branches/{branch.BranchId}/conversations";
        var floorUrl = $"/api/branches/{branch.BranchId}/tables/floor";

        factory.Clock.UtcNow = startUtc.AddDays(-2);
        await OkAsync(await diner.PostAsJsonAsync(
            $"/api/diner/reservations/{liveId}/conversation/messages", new { text = "A birthday cake?", clientCommandId = Guid.CreateVersion7() }));
        await OkAsync(await other.PostAsJsonAsync(
            $"/api/diner/reservations/{gone}/conversation/messages", new { text = "Window seat?", clientCommandId = Guid.CreateVersion7() }));
        await OkAsync(await other.PostAsJsonAsync($"/api/reservations/{gone}/cancel", new { }));

        // Both written within the day: both in view, once each.
        var items = (await ReadAsync(waiter, inboxUrl)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items.Select(i => i.GetProperty("conversationId").GetGuid()).Distinct().Count());

        // A day and a half on, nobody has written since: the live booking's thread is still in view and still waits;
        // the cancelled one's has left.
        factory.Clock.UtcNow = startUtc.AddDays(-0.5);
        var row = Assert.Single((await ReadAsync(waiter, inboxUrl)).GetProperty("items").EnumerateArray());
        Assert.Equal(liveId, row.GetProperty("subject").GetProperty("id").GetGuid());
        Assert.True(row.GetProperty("open").GetBoolean());
        Assert.Equal(startUtc.AddDays(-2), row.GetProperty("oldestUnansweredGuestMessageUtc").GetDateTime());

        var messages = (await ReadAsync(waiter, floorUrl)).GetProperty("messages");
        Assert.Equal(1, messages.GetProperty("unreadTotal").GetInt32());
        Assert.Equal(startUtc.AddDays(-2), messages.GetProperty("oldestUnansweredUtc").GetDateTime());

        // Two hours after the booking ends, with nothing written for days, it leaves too.
        factory.Clock.UtcNow = live.GetProperty("endUtc").GetDateTime().AddHours(2);
        Assert.Empty((await ReadAsync(waiter, inboxUrl)).GetProperty("items").EnumerateArray());
        Assert.Equal(0, (await ReadAsync(waiter, floorUrl)).GetProperty("messages").GetProperty("unreadTotal").GetInt32());
    }

    /// <summary>
    /// The account is deleted after the venue's write read the booking but before it took the locks: the thread the
    /// write makes is the venue's record with no account behind it - not linked to the tombstone - and nobody is told.
    /// </summary>
    [SkippableFact]
    public async Task A_venue_message_racing_the_guests_deletion_links_no_account()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, _) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        factory.Clock.UtcNow = booking.GetProperty("startUtc").GetDateTime().AddDays(-1);

        Guid dinerUserId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            dinerUserId = (await db.Reservations.SingleAsync(r => r.Id == id)).DinerUserId!.Value;
        }

        // The waiter's write has read the booking - and its account - and is about to take the account's lock.
        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("FROM DinerUsers WITH (UPDLOCK", StringComparison.OrdinalIgnoreCase),
            async () =>
            {
                await using var eraseDb = fixture.CreateContext(factory.Clock);
                var account = await eraseDb.DinerUsers.SingleAsync(d => d.Id == dinerUserId);
                await DinerAccountDeletion.EraseAsync(
                    eraseDb, null!, new RefreshTokenStore(eraseDb, factory.Clock), account, factory.Clock.UtcNow, CancellationToken.None);
            });

        await using (var writeDb = fixture.CreateContext(factory.Clock, race))
        {
            var actor = TestActor.Waiter(branch.WaiterId);
            var messages = new GuestMessageService(
                writeDb,
                factory.Clock,
                actor,
                new AuthorizationQueries(writeDb),
                fixture.CreateBranchGuard(writeDb, actor),
                new Outbox(writeDb, factory.Clock, NullLogger<Outbox>.Instance),
                NullLogger<GuestMessageService>.Instance);

            await messages.WriteAsStaffAsync(ConversationSubject.Reservation(id), "Your table is ready.", Guid.CreateVersion7());
        }

        Assert.True(race.Fired, "The deletion never ran mid-write, so this proves nothing.");

        await using var verify = fixture.CreateContext(factory.Clock);
        var thread = await verify.Conversations.SingleAsync(c => c.ReservationId == id);
        Assert.Null(thread.DinerUserId);
        Assert.False(await verify.OutboxMessages.AnyAsync(m => m.Type == OutboxMessageTypes.MessageToGuest
                                                              && m.PayloadJson.Contains(thread.Id.ToString())));
        Assert.False(await verify.DinerNotifications.AnyAsync(n => n.DinerUserId == dinerUserId));
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
