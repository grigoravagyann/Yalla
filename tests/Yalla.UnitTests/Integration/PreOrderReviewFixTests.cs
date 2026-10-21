using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Services;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The pre-order review's findings, each pinned: a dish on a pre-order cannot be deleted from under it, two saves at
/// once are never a 500, "on my way" is about one start and only from two hours before it, the question is a confirmed
/// booking's and goes once it is moot, a held ticket dies with its booking and is never billed once voided, and the
/// floor's actions survive the races around them.
/// </summary>
public partial class PreOrderTests
{
    // ---------------------------------------------------------------- B1

    /// <summary>
    /// Deleting a dish a pre-order names marks it unavailable instead, as for a dish on an order; deleting its category
    /// is refused the same way - never a 500 from the database. The pre-order keeps the name and price it was ordered at.
    /// </summary>
    [SkippableFact]
    public async Task A_dish_or_category_on_a_pre_order_is_marked_unavailable_not_deleted()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var id = (await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2))).GetProperty("id").GetGuid();
        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var menu = fixture.CreateMenuService(db);

            var deleted = await menu.DeleteItemAsync(x.Branch.BranchId, x.Menu.Coffee);
            Assert.False(deleted.Deleted);
            Assert.True(deleted.Deactivated);
        }

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await Assert.ThrowsAsync<DomainStateException>(
                () => fixture.CreateMenuService(db).DeleteCategoryAsync(x.Branch.BranchId, x.Menu.CategoryId));
        }

        var line = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id)
            .GetProperty("preOrder").GetProperty("lines")[0];
        Assert.Equal(x.Menu.Coffee, line.GetProperty("menuItemId").GetGuid());
        Assert.Equal(TestMenu.CoffeeAmd, line.GetProperty("unitPriceAmd").GetInt64());
        Assert.False(line.GetProperty("availableNow").GetBoolean());
    }

    // ---------------------------------------------------------------- S1

    /// <summary>
    /// Four saves of one booking at once - a retry racing itself, or four different edits - and then four clears: every
    /// answer is a 200, the retry's three copies say they were replays, and the question is written once.
    /// </summary>
    [SkippableFact]
    public async Task Concurrent_saves_and_clears_of_one_booking_never_answer_500()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var other = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        for (var round = 0; round < 2; round++)
        {
            var same = (await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2 + round, tableIndex: 0))).GetProperty("id").GetGuid();
            var different = (await CreatedAsync(other, AppBooking(factory, x.Branch, daysAhead: 2 + round, tableIndex: 1))).GetProperty("id").GetGuid();

            var command = Guid.CreateVersion7();
            var retried = await AllOkAsync(Enumerable.Range(0, 4).Select(_ => diner.PutAsJsonAsync(
                $"/api/reservations/{same}/pre-order",
                new { lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } }, clientCommandId = command })));
            Assert.Single(retried, b => !b.GetProperty("wasReplay").GetBoolean());

            await AllOkAsync(Enumerable.Range(0, 4).Select(i => other.PutAsJsonAsync(
                $"/api/reservations/{different}/pre-order",
                new { lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 + i } }, clientCommandId = Guid.CreateVersion7() })));

            Assert.Equal(1, await QuestionsQueuedAsync(factory, same));
            Assert.Equal(1, await QuestionsQueuedAsync(factory, different));

            var clear = Guid.CreateVersion7();
            var cleared = await AllOkAsync(Enumerable.Range(0, 4).Select(_ => diner.PostAsJsonAsync(
                $"/api/reservations/{same}/pre-order/clear", new { clientCommandId = clear })));
            Assert.Single(cleared, b => !b.GetProperty("wasReplay").GetBoolean());

            await AllOkAsync(Enumerable.Range(0, 4).Select(_ => other.PostAsJsonAsync(
                $"/api/reservations/{different}/pre-order/clear", new { clientCommandId = Guid.CreateVersion7() })));

            Assert.Equal(0, await QuestionsQueuedAsync(factory, same));
            Assert.Equal(0, await QuestionsQueuedAsync(factory, different));
        }

        static async Task<JsonElement[]> AllOkAsync(IEnumerable<Task<HttpResponseMessage>> requests) =>
            await Task.WhenAll((await Task.WhenAll(requests)).Select(OkAsync));
    }

    // ---------------------------------------------------------------- S2

    /// <summary>
    /// "On my way" is taken from two hours before the start - earlier it is 409 <c>on-my-way-not-open</c> saying when it
    /// opens - and a move to another start forgets it, so the card no longer says "Guest confirmed".
    /// </summary>
    [SkippableFact]
    public async Task On_my_way_opens_two_hours_before_and_a_move_forgets_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();
        var token = booking.GetProperty("manageToken").GetString()!;
        using var web = factory.CreateClient();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        factory.Clock.UtcNow = startUtc.AddHours(-2).AddMinutes(-1);
        var early = await ProblemAsync(
            await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }), HttpStatusCode.Conflict, "on-my-way-not-open");
        Assert.Equal(startUtc.AddHours(-2), early.GetProperty("context").GetProperty("opensAtUtc").GetDateTime());
        await ProblemAsync(
            await web.PostAsJsonAsync($"/api/public/bookings/{token}/on-my-way", new { }), HttpStatusCode.Conflict, "on-my-way-not-open");

        factory.Clock.UtcNow = startUtc.AddHours(-2);
        await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }));

        var moved = await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/reschedule", new
        {
            date = LocalDate(factory, 2),
            time = "19:00",
            partySize = 2,
            diningTableId = x.Branch.TableIds[0],
            clientCommandId = Guid.CreateVersion7(),
        }));
        Assert.False(moved.TryGetProperty("guestConfirmedOnWayAtUtc", out _));
        Assert.False((await ReadAsync(web, $"/api/public/bookings/{token}")).TryGetProperty("guestConfirmedOnWayAtUtc", out _));
    }

    // ---------------------------------------------------------------- S3

    /// <summary>
    /// A booking still waiting for the venue's yes when the question's time comes is not asked - no push, and no feed
    /// entry while it waits. Approved, the feed shows the question.
    /// </summary>
    [SkippableFact]
    public async Task The_on_your_way_question_is_for_a_confirmed_booking_only()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Thursday);
        await fixture.ClearOutboxAsync(clock);

        TestBranch branch;
        TestMenu menu;
        TestActor diner;

        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            await AcceptPreOrdersAsync(db, branch.BranchId);
            diner = await DinerWithDeviceAsync(db, clock);
        }

        var id = await BookWithPreOrderAsync(branch, branch.TableIds[0], menu, clock, diner);

        await using (var db = fixture.CreateContext(clock))
        {
            // Waiting for the venue (a large party), with no short-notice window.
            await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.Reservations SET Status = {(int)ReservationStatus.PendingApproval}, ApprovalExpiresAtUtc = NULL WHERE Id = {id}");
        }

        var channel = new RecordingChannel();
        clock.UtcNow = SundayDinnerUtc.AddMinutes(-45);

        await using (var db = fixture.CreateContext(clock))
        {
            await Dispatcher(db, clock, channel).RunOnceAsync();
            Assert.Empty(channel.OfKind("reservation-on-your-way"));
            Assert.DoesNotContain(
                (await new DinerNotificationFeed(db, clock, diner).ListAsync(before: null, limit: 50)).Items,
                n => n.Kind == DinerNotificationKinds.BookingOnYourWay);
        }

        await using (var db = fixture.CreateContext(clock))
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.Reservations SET Status = {(int)ReservationStatus.Confirmed} WHERE Id = {id}");
            Assert.Contains(
                (await new DinerNotificationFeed(db, clock, diner).ListAsync(before: null, limit: 50)).Items,
                n => n.Kind == DinerNotificationKinds.BookingOnYourWay && n.ReservationId == id);
        }
    }

    // ---------------------------------------------------------------- S4

    /// <summary>
    /// A held ticket - sent, the party not seated - is voided when its booking is cancelled by the guest or released by
    /// the venue: off the kitchen screen, on the change stream. A no-show after a send is an ordinary no-show: the
    /// ticket is left as it is.
    /// </summary>
    [SkippableFact]
    public async Task A_held_ticket_is_voided_when_its_booking_is_cancelled_but_not_on_a_no_show()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        var bookings = new List<(Guid Id, Guid PreOrderId, DateTime StartUtc)>();
        var diners = new List<HttpClient>();

        for (var table = 0; table < 3; table++)
        {
            var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
            diners.Add(diner);
            var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2, tableIndex: table));
            var id = booking.GetProperty("id").GetGuid();
            var preOrderId = (await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
            {
                lines = new[] { new { menuItemId = x.Menu.Khachapuri, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            }))).GetProperty("preOrder").GetProperty("id").GetGuid();
            bookings.Add((id, preOrderId, booking.GetProperty("startUtc").GetDateTime()));
        }

        var (cancelled, released, noShow) = (bookings[0], bookings[1], bookings[2]);
        var startUtc = cancelled.StartUtc;

        factory.Clock.UtcNow = startUtc.AddMinutes(-60);
        foreach (var b in bookings)
        {
            await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{b.Id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));
        }

        var after = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/tables/changes?afterSequence=0&limit=500"))
            .GetProperty("maxSequence").GetInt64();

        factory.Clock.UtcNow = startUtc.AddMinutes(-55);
        Assert.Equal(HttpStatusCode.OK, (await diners[0].PostAsJsonAsync($"/api/reservations/{cancelled.Id}/cancel", new { reason = "can't come" })).StatusCode);

        factory.Clock.UtcNow = startUtc.AddMinutes(10);
        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync($"/api/reservations/{released.Id}/release", new { outcome = 2, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        factory.Clock.UtcNow = startUtc.AddMinutes(30);
        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync($"/api/reservations/{noShow.Id}/no-show", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var statuses = await db.PreOrders.AsNoTracking()
                .Where(p => bookings.Select(b => b.Id).Contains(p.ReservationId))
                .ToDictionaryAsync(p => p.ReservationId, p => p.KitchenStatus);

            Assert.Equal(TabOrderStatus.Voided, statuses[cancelled.Id]);
            Assert.Equal(TabOrderStatus.Voided, statuses[released.Id]);
            Assert.Equal(TabOrderStatus.InKitchen, statuses[noShow.Id]);
        }

        var kitchen = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/orders")).EnumerateArray()
            .Select(o => o.GetProperty("orderId").GetGuid()).ToList();
        Assert.DoesNotContain(cancelled.PreOrderId, kitchen);
        Assert.DoesNotContain(released.PreOrderId, kitchen);
        Assert.Contains(noShow.PreOrderId, kitchen);

        var signalled = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/tables/changes?afterSequence={after}&limit=500"))
            .GetProperty("changes").EnumerateArray()
            .Where(c => c.TryGetProperty("signal", out var s) && s.GetInt32() == 6)
            .Select(c => c.GetProperty("reservationId").GetGuid())
            .ToList();
        Assert.Contains(cancelled.Id, signalled);
        Assert.Contains(released.Id, signalled);

        diners.ForEach(d => d.Dispose());
    }

    // ---------------------------------------------------------------- S5

    /// <summary>
    /// The kitchen voided the held ticket before the party sat: seated, they get no order for it and owe nothing, and it
    /// leaves the rail.
    /// </summary>
    [SkippableFact]
    public async Task A_held_ticket_voided_before_the_party_sits_is_never_put_on_their_tab()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var tableId = booking.GetProperty("tableId").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();

        var preOrderId = (await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Khachapuri, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }))).GetProperty("preOrder").GetProperty("id").GetGuid();

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = startUtc.AddMinutes(-25);
        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));
        await OkAsync(await waiter.PostAsJsonAsync($"/api/orders/{preOrderId}/status", new { status = 5 }));

        var seated = await waiter.PostAsJsonAsync(
            $"/api/branches/{x.Branch.BranchId}/tables/{tableId}/seat-reservation",
            new { reservationId = id, clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.TabOrders.AnyAsync(o => o.Id == preOrderId));
            Assert.Equal(0L, await db.Tabs.Where(t => db.TableSessions.Any(s => s.ReservationId == id && s.Id == t.TableSessionId)).SumAsync(t => t.SubtotalAmd));
            Assert.Null(await db.PreOrders.Where(p => p.Id == preOrderId).Select(p => p.TabOrderId).SingleAsync());
        }

        Assert.DoesNotContain(
            (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/pre-orders/due")).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("reservationId").GetGuid() == id);
    }

    // ---------------------------------------------------------------- S6

    /// <summary>
    /// The guest said they are coming, the waiter sent the pre-order, or the waiter marked them not coming - each before
    /// the question's time: its push and its feed entry are withdrawn, so nobody is asked a question already settled.
    /// </summary>
    [SkippableFact]
    public async Task The_question_is_withdrawn_once_answered_sent_or_marked_not_coming()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        var ids = new List<Guid>();
        var startUtc = default(DateTime);
        using var answering = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        for (var table = 0; table < 3; table++)
        {
            using var diner = table == 0 ? null : factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
            var guest = diner ?? answering;
            var booking = await CreatedAsync(guest, AppBooking(factory, x.Branch, daysAhead: 2, tableIndex: table));
            ids.Add(booking.GetProperty("id").GetGuid());
            startUtc = booking.GetProperty("startUtc").GetDateTime();
            await OkAsync(await guest.PutAsJsonAsync($"/api/reservations/{ids[^1]}/pre-order", new
            {
                lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            }));
            Assert.Equal(1, await QuestionsQueuedAsync(factory, ids[^1]));
        }

        factory.Clock.UtcNow = startUtc.AddMinutes(-60);
        await OkAsync(await answering.PostAsJsonAsync($"/api/reservations/{ids[0]}/on-my-way", new { }));
        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{ids[1]}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));
        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{ids[2]}/pre-order/not-coming", new { clientCommandId = Guid.CreateVersion7() }));

        await using var db = fixture.CreateContext(factory.Clock);

        foreach (var id in ids)
        {
            Assert.Equal(0, await QuestionsQueuedAsync(factory, id));
            Assert.Equal(0, await db.DinerNotifications.CountAsync(n => n.ReservationId == id && n.Kind == DinerNotificationKinds.BookingOnYourWay));
        }
    }

    // ---------------------------------------------------------------- nits: the floor's actions

    /// <summary>Sending a booking that has no pre-order is 404, as the wire says - not a 500.</summary>
    [SkippableFact]
    public async Task Sending_a_booking_without_a_pre_order_is_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = booking.GetProperty("startUtc").GetDateTime().AddMinutes(-20);

        var sent = await waiter.PostAsJsonAsync(
            $"/api/reservations/{booking.GetProperty("id").GetGuid()}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.NotFound, sent.StatusCode);
    }

    /// <summary>
    /// The party is seated with no tab; the waiter sends the pre-order just as a "Open tab" (or a phone's scan) gives
    /// the sitting its tab, without the table's lock: the send puts the pre-order on the tab that won - never a 500.
    /// </summary>
    [SkippableFact]
    public async Task A_send_racing_an_open_tab_puts_the_pre_order_on_the_tab_that_won()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        var (id, preOrderId, tableId, startUtc) = await BookedWithPreOrderAsync(factory, x);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = startUtc.AddMinutes(-10);
        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync(
                $"/api/branches/{x.Branch.BranchId}/tables/{tableId}/seat-reservation",
                new { reservationId = id, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        Guid? won = null;
        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("INSERT INTO [Tabs]", StringComparison.Ordinal),
            async () =>
            {
                await using var other = fixture.CreateContext(factory.Clock);
                var session = await other.TableSessions.SingleAsync(s => s.ReservationId == id && s.ClosedAtUtc == null);
                var tab = new Tab(
                    session.BranchId, session.DiningTableId, session.Id, factory.Clock.UtcNow, 0m,
                    TabAdmissionReason.OpenedByStaff, admittedByStaffId: x.Branch.WaiterId);
                session.AttachTab(tab.Id);
                other.Tabs.Add(tab);
                await other.SaveChangesAsync();
                won = tab.Id;
            });

        await using (var db = fixture.CreateContext(factory.Clock, race))
        {
            var card = await fixture.CreatePreOrderService(db, factory.Clock, TestActor.Waiter(x.Branch.WaiterId))
                .SendAsync(new PreOrderStaffCommand(id, Guid.CreateVersion7()));
            Assert.Equal(PreOrderState.Sent, card.PreOrder.State);
        }

        Assert.True(race.Fired);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(won, await db.TabOrders.Where(o => o.Id == preOrderId).Select(o => (Guid?)o.TabId).SingleAsync());
        }
    }

    /// <summary>
    /// The guest moves the booking to another table just before the send takes its table's lock, and the waiter seats
    /// them at the new table while the send is under way: the send follows the booking to its new table's lock, so the
    /// seating waits for it and the pre-order ends on their tab - never a held ticket stranded beside a seated party.
    /// </summary>
    [SkippableFact]
    public async Task A_send_follows_a_booking_moved_to_another_table_under_that_tables_lock()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        var (id, preOrderId, _, startUtc) = await BookedWithPreOrderAsync(factory, x);
        var newTable = x.Branch.TableIds[1];
        factory.Clock.UtcNow = startUtc.AddMinutes(-10);

        var moveFirst = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDLOCK", StringComparison.Ordinal) && sql.Contains("DiningTables", StringComparison.Ordinal),
            async () =>
            {
                await using var other = fixture.CreateContext(factory.Clock);
                await other.Database.ExecuteSqlAsync($"UPDATE dbo.Reservations SET DiningTableId = {newTable} WHERE Id = {id}");
            });

        Task? seating = null;
        var seatDuring = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDATE [PreOrders]", StringComparison.Ordinal),
            async () =>
            {
                seating = Task.Run(async () =>
                {
                    await using var other = fixture.CreateContext(factory.Clock);
                    await fixture.CreateService(other, factory.Clock, TestActor.Waiter(x.Branch.WaiterId))
                        .SeatReservationAsync(new SeatReservationCommand(x.Branch.BranchId, newTable, id, Guid.CreateVersion7()));
                });

                // Long enough for an unblocked seating to finish; one that waits for this send's lock waits it out.
                await Task.WhenAny(seating, Task.Delay(TimeSpan.FromSeconds(2.5)));
            });

        await using (var db = fixture.CreateContext(factory.Clock, moveFirst, seatDuring))
        {
            await fixture.CreatePreOrderService(db, factory.Clock, TestActor.Waiter(x.Branch.WaiterId))
                .SendAsync(new PreOrderStaffCommand(id, Guid.CreateVersion7()));
        }

        Assert.True(moveFirst.Fired);
        Assert.True(seatDuring.Fired);
        await seating!;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(preOrderId, await db.PreOrders.Where(p => p.Id == preOrderId).Select(p => p.TabOrderId).SingleAsync());
            Assert.Equal(
                await db.TableSessions.Where(s => s.ReservationId == id).Select(s => s.TabId).SingleAsync(),
                await db.TabOrders.Where(o => o.Id == preOrderId).Select(o => (Guid?)o.TabId).SingleAsync());
        }
    }

    /// <summary>
    /// "Not coming" loses to a send from another tablet: it is 409 <c>pre-order-already-sent</c> - the tap achieved
    /// nothing, and must not answer as if it had.
    /// </summary>
    [SkippableFact]
    public async Task Not_coming_that_lost_to_a_send_says_it_was_already_sent()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        var (id, _, _, startUtc) = await BookedWithPreOrderAsync(factory, x);
        factory.Clock.UtcNow = startUtc.AddMinutes(-20);

        var sendFirst = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDATE [PreOrders]", StringComparison.Ordinal),
            async () =>
            {
                await using var other = fixture.CreateContext(factory.Clock);
                await fixture.CreatePreOrderService(other, factory.Clock, TestActor.Waiter(x.Branch.WaiterId))
                    .SendAsync(new PreOrderStaffCommand(id, Guid.CreateVersion7()));
            });

        await using (var db = fixture.CreateContext(factory.Clock, sendFirst))
        {
            await Assert.ThrowsAsync<PreOrderAlreadySentException>(
                () => fixture.CreatePreOrderService(db, factory.Clock, TestActor.Waiter(x.Branch.WaiterId))
                    .MarkNotComingAsync(new PreOrderStaffCommand(id, Guid.CreateVersion7())));
        }

        Assert.True(sendFirst.Fired);
    }

    /// <summary>
    /// The kitchen marks the held ticket ready at the very moment the party is seated with it: the seating is refused
    /// as the concurrent update it is - not "the table changed", which it did not - and seating again puts the ticket
    /// on their tab where the kitchen left it.
    /// </summary>
    [SkippableFact]
    public async Task A_seating_that_races_the_kitchen_moving_its_held_ticket_is_a_concurrent_update_not_a_table_conflict()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        var (id, preOrderId, tableId, startUtc) = await BookedWithPreOrderAsync(factory, x);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = startUtc.AddMinutes(-10);
        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));

        var kitchenFirst = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDATE [PreOrders]", StringComparison.Ordinal),
            async () =>
            {
                await using var other = fixture.CreateContext(factory.Clock);
                await fixture.CreateOrderService(other, factory.Clock, TestActor.Waiter(x.Branch.WaiterId))
                    .MoveOrderStatusAsync(preOrderId, TabOrderStatus.Ready);
            });

        await using (var db = fixture.CreateContext(factory.Clock, kitchenFirst))
        {
            var refused = await Record.ExceptionAsync(() => fixture.CreateService(db, factory.Clock, TestActor.Waiter(x.Branch.WaiterId))
                .SeatReservationAsync(new SeatReservationCommand(x.Branch.BranchId, tableId, id, Guid.CreateVersion7())));
            Assert.IsType<DbUpdateConcurrencyException>(refused);
        }

        Assert.True(kitchenFirst.Fired);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await fixture.CreateService(db, factory.Clock, TestActor.Waiter(x.Branch.WaiterId))
                .SeatReservationAsync(new SeatReservationCommand(x.Branch.BranchId, tableId, id, Guid.CreateVersion7()));
        }

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(TabOrderStatus.Ready, await db.TabOrders.Where(o => o.Id == preOrderId).Select(o => o.Status).SingleAsync());
        }
    }

    // ---------------------------------------------------------------- nits: settings and the change stream

    /// <summary>
    /// The settings PUT needs all three fields: one left out is 422 naming it, and nothing is reset - never a silent
    /// "off" or every dish back on.
    /// </summary>
    [SkippableFact]
    public async Task A_settings_save_missing_a_field_is_refused_naming_it_and_changes_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, excluded: true);
        using var manager = factory.CreateClientWithToken(x.ManagerToken);
        var url = $"/api/branches/{x.Branch.BranchId}/pre-order-settings";

        var bodies = new (string Field, object Body)[]
        {
            ("acceptsPreOrders", new { minutesBefore = 30, excludedMenuItemIds = Array.Empty<Guid>() }),
            ("minutesBefore", new { acceptsPreOrders = true, excludedMenuItemIds = Array.Empty<Guid>() }),
            ("excludedMenuItemIds", new { acceptsPreOrders = true, minutesBefore = 30 }),
        };

        foreach (var (field, body) in bodies)
        {
            var problem = await ProblemAsync(await manager.PutAsJsonAsync(url, body), HttpStatusCode.UnprocessableEntity, "validation-failed");
            Assert.Contains(
                problem.GetProperty("context").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()),
                f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));
        }

        var settings = await ReadAsync(manager, url);
        Assert.True(settings.GetProperty("acceptsPreOrders").GetBoolean());
        Assert.Equal(x.Menu.Wine, Assert.Single(settings.GetProperty("excludedMenuItemIds").EnumerateArray()).GetGuid());
    }

    /// <summary>
    /// A settings save reaches the tablets: a booking whose card is (or could be) on the rail gets a signal 6, so the
    /// rail is re-read with the new time and flags.
    /// </summary>
    [SkippableFact]
    public async Task A_settings_save_signals_the_rail()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        var (id, _, _, startUtc) = await BookedWithPreOrderAsync(factory, x);
        using var manager = factory.CreateClientWithToken(x.ManagerToken);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = startUtc.AddMinutes(-90);

        var after = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/tables/changes?afterSequence=0&limit=500"))
            .GetProperty("maxSequence").GetInt64();

        await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{x.Branch.BranchId}/pre-order-settings", new
        {
            acceptsPreOrders = true,
            minutesBefore = 100,
            excludedMenuItemIds = Array.Empty<Guid>(),
        }));

        Assert.Contains(
            (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/tables/changes?afterSequence={after}&limit=500"))
                .GetProperty("changes").EnumerateArray(),
            c => c.TryGetProperty("signal", out var s) && s.GetInt32() == 6 && c.GetProperty("reservationId").GetGuid() == id);
    }

    /// <summary>
    /// The seating that puts a held ticket on the tab occupies the table in the same save: its signal-6 row says
    /// Occupied, as the table now is - a tablet that folds an unknown signal's status must not read Free.
    /// </summary>
    [SkippableFact]
    public async Task A_signal_6_row_carries_the_tables_status_as_the_save_leaves_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        var (id, _, tableId, startUtc) = await BookedWithPreOrderAsync(factory, x);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = startUtc.AddMinutes(-10);

        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));
        var after = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/tables/changes?afterSequence=0&limit=500"))
            .GetProperty("maxSequence").GetInt64();

        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync(
                $"/api/branches/{x.Branch.BranchId}/tables/{tableId}/seat-reservation",
                new { reservationId = id, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var row = Assert.Single(
            (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/tables/changes?afterSequence={after}&limit=500"))
                .GetProperty("changes").EnumerateArray(),
            c => c.TryGetProperty("signal", out var s) && s.GetInt32() == 6);
        Assert.Equal((int)TableStatus.Occupied, row.GetProperty("toStatus").GetInt32());
        Assert.Equal((int)TableStatus.Occupied, row.GetProperty("fromStatus").GetInt32());
    }

    /// <summary>The rail's reads have their index: a branch's pre-orders with their bookings.</summary>
    [SkippableFact]
    public async Task The_rail_reads_a_branchs_pre_orders_through_an_index()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Thursday));

        Assert.Equal(
            1,
            await db.Database.SqlQuery<int>(
                    $"""
                     SELECT COUNT(*) AS [Value] FROM sys.indexes i
                     WHERE i.name = 'IX_PreOrders_BranchId_ReservationId' AND i.object_id = OBJECT_ID('dbo.PreOrders')
                     """)
                .SingleAsync());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A booking two days ahead at the first table, with a pre-order of one coffee.</summary>
    private async Task<(Guid Id, Guid PreOrderId, Guid TableId, DateTime StartUtc)> BookedWithPreOrderAsync(
        YallaApiFactory factory, Arrangement x)
    {
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();

        var preOrderId = (await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }))).GetProperty("preOrder").GetProperty("id").GetGuid();

        return (id, preOrderId, booking.GetProperty("tableId").GetGuid(), booking.GetProperty("startUtc").GetDateTime());
    }
}
