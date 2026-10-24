using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Services;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;
using static Yalla.UnitTests.Integration.PreOrderTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The console's message history (<c>listBranchMessageHistory</c>) and the year's retention: the threads of a year, newest
/// last message first, filtered, searched and paged by a cursor that survives equal instants; the branch's own days; who
/// may read it; nothing marked read; and the daily sweep taking only what is a year quiet and done. See
/// <c>docs/messages.md</c> §history.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class MessageHistoryTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Three threads made where they happened - a booking 200 days ago (four messages, two staff, then cancelled), a
    /// booking two days ago (the guest unanswered), a takeaway order three days ago - read back in every way the page
    /// reads them. Nothing the console reads moves a read mark, and the old thread opens in the right pane.
    /// </summary>
    [SkippableFact]
    public async Task The_history_lists_a_years_threads_filtered_searched_and_paged()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var realNow = factory.Clock.UtcNow;
        AuthBranch branch;
        TestMenu menu;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

            // Effective from before the oldest booking below.
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, realNow.AddDays(-300), acceptsOnlineBookings: true);
        }

        // Signed in on the real clock, so the tokens are valid whatever the test clock says later.
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var armen = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var lusine = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var takeawayGuest = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/takeaway-settings", new
        {
            enabled = true,
            autoAccept = true,
            prepMinutes = 20,
            lastOrderMinutes = 30,
            maxInProgress = 5,
        }));

        // 200 days ago: Armen writes, the waiter and the manager answer, the waiter again; then Armen cancels.
        var oldAt = realNow.AddDays(-200);
        factory.Clock.UtcNow = oldAt;
        var old = (await CreatedAsync(armen, Booking(factory, branch, "Armen Old", tableIndex: 0))).GetProperty("id").GetGuid();
        await GuestWritesAsync(armen, "reservations", old, "Can we bring a cake?");
        factory.Clock.UtcNow = oldAt.AddMinutes(1);
        await StaffWritesAsync(waiter, "reservations", old, "Of course.");
        factory.Clock.UtcNow = oldAt.AddMinutes(2);
        await StaffWritesAsync(manager, "reservations", old, "We will put candles on it.");
        factory.Clock.UtcNow = oldAt.AddMinutes(3);
        await StaffWritesAsync(waiter, "reservations", old, "See you tomorrow.");
        factory.Clock.UtcNow = oldAt.AddMinutes(4);
        await OkAsync(await armen.PostAsJsonAsync($"/api/reservations/{old}/cancel", new { }));

        // Three days ago, a takeaway order: the guest asks, the waiter answers.
        var takeawayAt = DateTime.SpecifyKind(realNow.Date.AddDays(-3).AddHours(10), DateTimeKind.Utc);
        factory.Clock.UtcNow = takeawayAt;
        var order = await OkAsync(await takeawayGuest.PostAsJsonAsync("/api/diner/takeaway-orders", new
        {
            branchId = branch.BranchId,
            pickup = new { asap = true },
            lines = new[] { new { menuItemId = menu.Khachapuri, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }));
        var orderId = order.GetProperty("id").GetGuid();
        var code = order.GetProperty("code").GetString()!;
        await GuestWritesAsync(takeawayGuest, "takeaway-orders", orderId, "Extra napkins, please.");
        factory.Clock.UtcNow = takeawayAt.AddMinutes(1);
        await StaffWritesAsync(waiter, "takeaway-orders", orderId, "Done.");

        // Two days ago: Lusine writes, and nobody answers.
        var recentAt = realNow.AddDays(-2);
        factory.Clock.UtcNow = recentAt;
        var recent = (await CreatedAsync(lusine, Booking(factory, branch, "Lusine Recent", tableIndex: 1))).GetProperty("id").GetGuid();
        await GuestWritesAsync(lusine, "reservations", recent, "Is there parking?");

        factory.Clock.UtcNow = realNow;
        var url = $"/api/branches/{branch.BranchId}/conversations/history";
        var today = YerevanDate(realNow);
        var yearAgo = Iso(today.AddDays(-365));

        // The default: the last seven days, newest first - Lusine's, then the order's.
        var week = await ReadAsync(manager, url);
        Assert.Equal(Iso(today.AddDays(-6)), week.GetProperty("from").GetString());
        Assert.Equal(Iso(today), week.GetProperty("to").GetString());
        Assert.False(week.TryGetProperty("nextCursor", out _));
        Assert.Equal([recent, orderId], SubjectIds(week));

        // The year: all three.
        var year = await ReadAsync(manager, $"{url}?from={yearAgo}");
        Assert.Equal([recent, orderId, old], SubjectIds(year));

        var items = year.GetProperty("items").EnumerateArray().ToList();
        var lusineRow = items[0];
        Assert.True(lusineRow.GetProperty("unanswered").GetBoolean());
        Assert.False(lusineRow.GetProperty("open").GetBoolean());
        Assert.Equal(1, lusineRow.GetProperty("messageCount").GetInt32());
        Assert.Empty(lusineRow.GetProperty("staffNames").EnumerateArray());
        Assert.Equal("upcoming", lusineRow.GetProperty("subject").GetProperty("outcome").GetString());
        Assert.Equal("2", lusineRow.GetProperty("subject").GetProperty("label").GetString());

        var orderRow = items[1];
        Assert.Equal("takeaway", orderRow.GetProperty("subject").GetProperty("kind").GetString());
        Assert.Equal(code, orderRow.GetProperty("subject").GetProperty("label").GetString());
        Assert.Equal(2, orderRow.GetProperty("subject").GetProperty("itemCount").GetInt32());
        Assert.Equal(2 * TestMenu.KhachapuriAmd, orderRow.GetProperty("subject").GetProperty("totalAmd").GetInt64());
        Assert.Equal(Iso(YerevanDate(takeawayAt)), orderRow.GetProperty("subject").GetProperty("localDate").GetString());
        Assert.False(orderRow.GetProperty("subject").TryGetProperty("partySize", out _));
        Assert.Equal("in-progress", orderRow.GetProperty("subject").GetProperty("outcome").GetString());
        Assert.True(orderRow.GetProperty("open").GetBoolean());
        Assert.False(orderRow.GetProperty("unanswered").GetBoolean());
        Assert.Equal(["Test Waiter"], StaffNames(orderRow));

        // The old one: four messages and the guest's "not coming" counted, the system's line not; each writer once, in
        // the order they first wrote; the cancel was its last message.
        var oldRow = items[2];
        Assert.Equal(5, oldRow.GetProperty("messageCount").GetInt32());
        Assert.Equal(["Test Waiter", "Test Manager"], StaffNames(oldRow));
        Assert.Equal(oldAt, oldRow.GetProperty("firstAtUtc").GetDateTime());
        Assert.Equal(oldAt.AddMinutes(4), oldRow.GetProperty("lastAtUtc").GetDateTime());
        Assert.Equal(
            YerevanTime(oldAt.AddMinutes(4)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            oldRow.GetProperty("lastLocal").GetString());
        Assert.False(oldRow.GetProperty("unanswered").GetBoolean());
        Assert.False(oldRow.GetProperty("open").GetBoolean());
        var oldSubject = oldRow.GetProperty("subject");
        Assert.Equal("reservation", oldSubject.GetProperty("kind").GetString());
        Assert.Equal("Armen Old", oldSubject.GetProperty("guestName").GetString());
        Assert.Equal("1", oldSubject.GetProperty("label").GetString());
        Assert.Equal("18:00", oldSubject.GetProperty("localTime").GetString());
        Assert.Equal(Iso(YerevanDate(oldAt).AddDays(1)), oldSubject.GetProperty("localDate").GetString());
        Assert.Equal(2, oldSubject.GetProperty("partySize").GetInt32());
        Assert.Equal("cancelled", oldSubject.GetProperty("outcome").GetString());

        // Kinds, the search, unanswered only.
        Assert.Equal([recent, old], SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&kind=reservation")));
        Assert.Equal([orderId], SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&kind=takeaway")));
        Assert.Equal([old], SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&q=ARMEN")));
        Assert.Equal([old], SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&q=n%20ol")));
        Assert.Equal([recent], SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&q=2")));
        Assert.Equal([orderId], SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&q={code.ToLowerInvariant()}")));
        Assert.Equal([orderId], SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&q={code[2..]}")));
        Assert.Empty(SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&q=nobody")));
        Assert.Equal([recent], SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&unansweredOnly=true")));
        Assert.Equal([orderId], SubjectIds(await ReadAsync(manager, $"{url}?from={yearAgo}&kind=takeaway&unansweredOnly=false")));

        // A page at a time: each once, in order, and the last says so.
        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            var page = await ReadAsync(manager, $"{url}?from={yearAgo}&limit=1" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            seen.AddRange(SubjectIds(page));
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null && seen.Count < 10);

        Assert.Equal([recent, orderId, old], seen);

        // Nothing the console reads marks anything read: Lusine's message is unread for the tablet before and after.
        Guid recentThread;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            recentThread = (await db.Conversations.SingleAsync(c => c.ReservationId == recent)).Id;
        }

        var staffView = await ReadAsync(manager, $"/api/reservations/{recent}/conversation");
        Assert.Equal(1, staffView.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, (await ReadAsync(manager, $"/api/reservations/{recent}/conversation")).GetProperty("unreadCount").GetInt32());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Null((await db.Conversations.SingleAsync(c => c.Id == recentThread)).VenueLastReadUtc);
        }

        // The right pane: the 200-day-old thread whole, with staff names and the system line, and its outcome.
        var oldThread = await ReadAsync(manager, $"/api/reservations/{old}/conversation");
        Assert.Equal("cancelled", oldThread.GetProperty("subject").GetProperty("outcome").GetString());
        Assert.False(oldThread.GetProperty("canWrite").GetBoolean());
        var messages = oldThread.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(6, messages.Count);
        Assert.Equal("Test Manager", messages[2].GetProperty("staffName").GetString());
        Assert.Equal("system", messages[5].GetProperty("author").GetString());

        var orderThread = await ReadAsync(manager, $"/api/takeaway-orders/{orderId}/conversation");
        Assert.Equal("in-progress", orderThread.GetProperty("subject").GetProperty("outcome").GetString());

        // The guest's own view carries no outcome.
        var guestView = await ReadAsync(lusine, $"/api/diner/reservations/{recent}/conversation");
        Assert.False(guestView.GetProperty("subject").TryGetProperty("outcome", out _));
    }

    /// <summary>
    /// A day is the branch's: a message at 00:30 in Yerevan belongs to that day, though it is 20:30 the day before in UTC,
    /// and one at 23:59 to its own.
    /// </summary>
    [SkippableFact]
    public async Task A_day_is_the_branchs_own_day_not_utcs()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, manager, diners) = await ArrangeAsync(factory, diners: 2);
        var afterMidnight = await ThreadAsync(factory, branch, diners[0], tableIndex: 0);
        var beforeMidnight = await ThreadAsync(factory, branch, diners[1], tableIndex: 1);

        var day = YerevanDate(factory.Clock.UtcNow).AddDays(-10);

        // 00:30 on the day, Yerevan (UTC+4) - 20:30 the day before in UTC; 23:59 the day before, Yerevan.
        var halfPastMidnightUtc = DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(0, 30)), DateTimeKind.Utc).AddHours(-4);
        await SetLastMessageAsync(factory, afterMidnight, halfPastMidnightUtc);
        await SetLastMessageAsync(factory, beforeMidnight, halfPastMidnightUtc.AddMinutes(-31));

        var url = $"/api/branches/{branch.BranchId}/conversations/history";

        var onTheDay = await ReadAsync(manager, $"{url}?from={Iso(day)}&to={Iso(day)}");
        Assert.Equal([afterMidnight], ConversationIds(onTheDay));
        Assert.Equal(
            $"{Iso(day)} 00:30",
            Assert.Single(onTheDay.GetProperty("items").EnumerateArray()).GetProperty("lastLocal").GetString());

        var dayBefore = await ReadAsync(manager, $"{url}?from={Iso(day.AddDays(-1))}&to={Iso(day.AddDays(-1))}");
        Assert.Equal([beforeMidnight], ConversationIds(dayBefore));
        Assert.Equal(
            $"{Iso(day.AddDays(-1))} 23:59",
            Assert.Single(dayBefore.GetProperty("items").EnumerateArray()).GetProperty("lastLocal").GetString());

        Assert.Equal(
            [afterMidnight, beforeMidnight],
            ConversationIds(await ReadAsync(manager, $"{url}?from={Iso(day.AddDays(-1))}&to={Iso(day)}")));
    }

    /// <summary>
    /// Three threads whose last messages share one instant: a page of one, then the next, then the last - each once - and
    /// pages of two the same way. The cursor's tie-break is the thread, in the order the list reads.
    /// </summary>
    [SkippableFact]
    public async Task Threads_of_one_instant_page_without_repeating_or_skipping()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, manager, diners) = await ArrangeAsync(factory, diners: 3);
        var threads = new List<Guid>();

        for (var i = 0; i < 3; i++)
        {
            threads.Add(await ThreadAsync(factory, branch, diners[i], tableIndex: i));
        }

        var instant = factory.Clock.UtcNow.AddDays(-20).AddTicks(1234);

        foreach (var thread in threads)
        {
            await SetLastMessageAsync(factory, thread, instant);
        }

        var date = Iso(YerevanDate(instant));
        var url = $"/api/branches/{branch.BranchId}/conversations/history?from={date}&to={date}";

        foreach (var limit in new[] { 1, 2 })
        {
            var seen = new List<Guid>();
            string? cursor = null;
            var pages = 0;

            do
            {
                var page = await ReadAsync(manager, $"{url}&limit={limit}" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
                seen.AddRange(ConversationIds(page));
                cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
                pages++;
            }
            while (cursor is not null && pages < 10);

            Assert.Equal(3, seen.Count);
            Assert.Equal(threads.OrderBy(t => t).ToHashSet(), seen.ToHashSet());
            Assert.Equal(limit == 1 ? 3 : 2, pages);

            // And the same order as one page of all three.
            Assert.Equal(ConversationIds(await ReadAsync(manager, url)), seen);
        }
    }

    /// <summary>
    /// Manager or above at the branch: the branch's manager, the general manager, the owner and a platform admin read; a
    /// waiter, the kitchen, a manager of the venue's other branch and another venue's are refused. A bad query is one
    /// 422 naming every bad field.
    /// </summary>
    [SkippableFact]
    public async Task Only_managers_and_above_at_the_branch_read_the_history()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, manager, diners) = await ArrangeAsync(factory, diners: 1);
        var thread = await ThreadAsync(factory, branch, diners[0], tableIndex: 0);

        PanelAccount owner;
        PanelAccount generalManager;
        PanelAccount siblingManager;
        AuthBranch otherVenue;
        PlatformAdminAccount admin;
        Guid cookId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
            generalManager = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId);
            var siblingBranchId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
            siblingManager = await AuthTestData.SeedManagerAsync(db, branch.VenueId, siblingBranchId);
            otherVenue = await AuthTestData.CreateBranchAsync(db, tableCount: 1);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
            cookId = await AuthTestData.SeedKitchenAsync(db, branch);
        }

        using var ownerClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));
        using var gmClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, generalManager));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var kitchen = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, branch, cookId));
        using var sibling = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, siblingManager));
        using var stranger = factory.CreateClientWithToken(await SignInManagerAsync(factory, otherVenue));

        var url = $"/api/branches/{branch.BranchId}/conversations/history";

        foreach (var reader in new[] { manager, ownerClient, gmClient, platform })
        {
            Assert.Equal([thread], ConversationIds(await ReadAsync(reader, url)));
        }

        foreach (var outsider in new[] { waiter, kitchen, sibling, stranger })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(url)).StatusCode);
        }

        // The right pane for the owner and the general manager too; not for the other branch's manager.
        var reservationId = await SubjectOfAsync(factory, thread);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/api/reservations/{reservationId}/conversation")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gmClient.GetAsync($"/api/reservations/{reservationId}/conversation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sibling.GetAsync($"/api/reservations/{reservationId}/conversation")).StatusCode);

        // No such branch: a platform admin is told so.
        await ProblemAsync(await platform.GetAsync($"/api/branches/{Guid.NewGuid()}/conversations/history"), HttpStatusCode.NotFound, "not-found");

        // Every bad field in one refusal.
        var refused = await ProblemAsync(
            await manager.GetAsync($"{url}?from=yesterday&kind=walk-in&q={new string('x', 61)}&unansweredOnly=maybe&cursor=abc&limit=0"),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");
        Assert.Equal(
            ["from", "kind", "q", "unansweredOnly", "cursor", "limit"],
            refused.GetProperty("context").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()!).ToArray());

        var tooOld = await ProblemAsync(
            await manager.GetAsync($"{url}?from={Iso(YerevanDate(factory.Clock.UtcNow).AddDays(-366))}"),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");
        Assert.Equal("from", tooOld.GetProperty("context").GetProperty("field").GetString());
    }

    /// <summary>
    /// The sweep deletes a thread a year and a day quiet whose booking is over, with its messages - and one whose booking
    /// was left "confirmed" a year after its evening - and keeps one a day short of the year, one whose booking is still
    /// to come and one whose order is still moving, however old their last messages.
    /// </summary>
    [SkippableFact]
    public async Task The_sweep_deletes_only_threads_a_year_quiet_and_done()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var (branch, manager, diners) = await ArrangeAsync(factory, diners: 3);
        TestMenu menu;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
        }

        // 400 days ago, a booking nobody closed: still "confirmed", its evening long gone.
        var realNow = factory.Clock.UtcNow;
        factory.Clock.UtcNow = realNow.AddDays(-400);
        var staleConfirmed = await ThreadAsync(factory, branch, diners[1], tableIndex: 0);
        factory.Clock.UtcNow = realNow;

        var cancelledOld = await ThreadAsync(factory, branch, diners[0], tableIndex: 0, cancel: true);
        var cancelledRecent = await ThreadAsync(factory, branch, diners[1], tableIndex: 1, cancel: true);
        var liveOld = await ThreadAsync(factory, branch, diners[2], tableIndex: 2);

        await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{branch.BranchId}/takeaway-settings", new
        {
            enabled = true,
            autoAccept = true,
            prepMinutes = 20,
            lastOrderMinutes = 30,
            maxInProgress = 5,
        }));
        var order = await OkAsync(await diners[0].PostAsJsonAsync("/api/diner/takeaway-orders", new
        {
            branchId = branch.BranchId,
            pickup = new { asap = true },
            lines = new[] { new { menuItemId = menu.Khachapuri, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));
        var orderId = order.GetProperty("id").GetGuid();
        await GuestWritesAsync(diners[0], "takeaway-orders", orderId, "Ten minutes late.");

        Guid movingOld;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            movingOld = (await db.Conversations.SingleAsync(c => c.TakeawayOrderId == orderId)).Id;
        }

        var cancelledOldBooking = await SubjectOfAsync(factory, cancelledOld);
        var staleConfirmedBooking = await SubjectOfAsync(factory, staleConfirmed);
        var now = factory.Clock.UtcNow;
        await SetLastMessageAsync(factory, cancelledOld, now.AddDays(-366));
        await SetLastMessageAsync(factory, cancelledRecent, now.AddDays(-364));
        await SetLastMessageAsync(factory, liveOld, now.AddDays(-366));
        await SetLastMessageAsync(factory, movingOld, now.AddDays(-366));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<ConversationRetention>().PurgeAsync() >= 1);
        }

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.Conversations.AnyAsync(c => c.Id == cancelledOld));
            Assert.False(await db.ConversationMessages.AnyAsync(m => m.ConversationId == cancelledOld));
            Assert.False(await db.Conversations.AnyAsync(c => c.Id == staleConfirmed));
            Assert.Contains(
                (await db.Reservations.SingleAsync(r => r.Id == staleConfirmedBooking)).Status,
                new[] { ReservationStatus.Confirmed, ReservationStatus.PendingApproval });

            foreach (var kept in new[] { cancelledRecent, liveOld, movingOld })
            {
                Assert.True(await db.Conversations.AnyAsync(c => c.Id == kept), $"Thread {kept} should have been kept.");
                Assert.True(await db.ConversationMessages.AnyAsync(m => m.ConversationId == kept));
            }

            // The booking itself stays - only the thread went.
            Assert.Equal(
                ReservationStatus.CancelledByDiner,
                (await db.Reservations.SingleAsync(r => r.Id == cancelledOldBooking)).Status);
        }

        // The one a day short of the year still opens in the right pane.
        var reservationId = await SubjectOfAsync(factory, cancelledRecent);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/reservations/{reservationId}/conversation")).StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private static readonly TimeZoneInfo Yerevan = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");

    /// <summary>
    /// A host whose day starts at 15:00 in Yerevan, today.
    /// </summary>
    /// <remarks>
    /// The sweep test places an ASAP takeaway order "now". Started from the real current instant, after 22:10 - the test
    /// branch closes at 23:00, its last pickup is half an hour before, and the order takes twenty minutes - or before it
    /// opens at 10:00, the order was refused with 409 <c>takeaway-closed</c> and the test failed for the hour it ran at.
    /// Mid-afternoon the kitchen is always taking orders.
    /// </remarks>
    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString).StartingAtYerevan(new TimeOnly(15, 0));

    private async Task<(AuthBranch Branch, HttpClient Manager, HttpClient[] Diners)> ArrangeAsync(YallaApiFactory factory, int diners)
    {
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, acceptsOnlineBookings: true);
        }

        var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));
        var clients = new HttpClient[diners];

        for (var i = 0; i < diners; i++)
        {
            clients[i] = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        }

        return (branch, manager, clients);
    }

    /// <summary>A booking two days ahead with one guest message - optionally cancelled after - and its thread's id.</summary>
    private async Task<Guid> ThreadAsync(YallaApiFactory factory, AuthBranch branch, HttpClient diner, int tableIndex, bool cancel = false)
    {
        var id = (await CreatedAsync(diner, Booking(factory, branch, "Guest", tableIndex, daysAhead: 2))).GetProperty("id").GetGuid();
        await GuestWritesAsync(diner, "reservations", id, "Hello");

        if (cancel)
        {
            await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/cancel", new { }));
        }

        await using var db = fixture.CreateContext(factory.Clock);

        return (await db.Conversations.SingleAsync(c => c.ReservationId == id)).Id;
    }

    private async Task<Guid> SubjectOfAsync(YallaApiFactory factory, Guid conversationId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        return (await db.Conversations.SingleAsync(c => c.Id == conversationId)).ReservationId!.Value;
    }

    private async Task SetLastMessageAsync(YallaApiFactory factory, Guid conversationId, DateTime atUtc)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.LastMessageUtc, atUtc));
    }

    private static object Booking(YallaApiFactory factory, AuthBranch branch, string guestName, int tableIndex, int daysAhead = 1) =>
        new
        {
            branchId = branch.BranchId,
            tableId = branch.TableIds[tableIndex],
            date = LocalDate(factory, daysAhead),
            time = "18:00",
            partySize = 2,
            guestName,
            guestPhone = "+37411223344",
            clientCommandId = Guid.CreateVersion7(),
            channel = ReservationChannel.App,
        };

    private static async Task GuestWritesAsync(HttpClient diner, string subject, Guid id, string text) =>
        await OkAsync(subject == "reservations"
            ? await diner.PostAsJsonAsync(
                $"/api/diner/reservations/{id}/conversation/messages", new { text, clientCommandId = Guid.CreateVersion7() })
            : await diner.PostAsJsonAsync(
                $"/api/diner/takeaway-orders/{id}/conversation/messages", new { text, clientCommandId = Guid.CreateVersion7() }));

    private static async Task StaffWritesAsync(HttpClient staff, string subject, Guid id, string text) =>
        await OkAsync(subject == "reservations"
            ? await staff.PostAsJsonAsync(
                $"/api/reservations/{id}/conversation/messages", new { text, clientCommandId = Guid.CreateVersion7() })
            : await staff.PostAsJsonAsync(
                $"/api/takeaway-orders/{id}/conversation/messages", new { text, clientCommandId = Guid.CreateVersion7() }));

    private static List<Guid> SubjectIds(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("subject").GetProperty("id").GetGuid())];

    private static List<Guid> ConversationIds(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("conversationId").GetGuid())];

    private static string[] StaffNames(JsonElement row) =>
        [.. row.GetProperty("staffNames").EnumerateArray().Select(n => n.GetString()!)];

    private static DateTime YerevanTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Yerevan);

    private static DateOnly YerevanDate(DateTime utc) => DateOnly.FromDateTime(YerevanTime(utc));

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
