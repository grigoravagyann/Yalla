using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The waiter's half of a pre-order: the card on the rail from the branch's minutes before, sending it to the kitchen
/// before the party sits and finding it on their tab when they do, and "not coming / no answer".
/// </summary>
public partial class PreOrderTests
{
    /// <summary>
    /// The card appears exactly at the branch's minutes before - not a minute earlier, when the list says when the next
    /// one will - with everything the waiter needs to call or not call.
    /// </summary>
    [SkippableFact]
    public async Task The_card_appears_at_the_branchs_minutes_before_with_what_the_waiter_needs()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, minutesBefore: 40);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2, partySize: 3));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Khachapuri, quantity = 2, note = "well done" } },
            note = "Birthday",
            clientCommandId = Guid.CreateVersion7(),
        }));

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        factory.Clock.UtcNow = startUtc.AddMinutes(-41);
        var early = await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/pre-orders/due");
        Assert.DoesNotContain(early.GetProperty("items").EnumerateArray(), i => i.GetProperty("reservationId").GetGuid() == id);
        Assert.Equal(startUtc.AddMinutes(-40), early.GetProperty("nextDueAtUtc").GetDateTime());

        factory.Clock.UtcNow = startUtc.AddMinutes(-40);
        var card = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/pre-orders/due")).GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("reservationId").GetGuid() == id);

        Assert.Equal(booking.GetProperty("tableLabel").GetString(), card.GetProperty("tableLabel").GetString());
        Assert.Equal(startUtc, card.GetProperty("startUtc").GetDateTime());
        Assert.Equal(3, card.GetProperty("partySize").GetInt32());
        Assert.Equal("Ani App", card.GetProperty("guestName").GetString());
        Assert.Equal("+37411223344", card.GetProperty("guestPhone").GetString());
        Assert.Equal(startUtc.AddMinutes(-40), card.GetProperty("dueAtUtc").GetDateTime());
        Assert.False(card.TryGetProperty("guestConfirmedOnWayAtUtc", out _));
        Assert.True(card.GetProperty("guestRecord").GetProperty("isNewGuest").GetBoolean());
        Assert.Equal("Birthday", card.GetProperty("preOrder").GetProperty("note").GetString());
        Assert.Equal(2, card.GetProperty("preOrder").GetProperty("state").GetInt32());
        Assert.Equal("well done", card.GetProperty("preOrder").GetProperty("lines")[0].GetProperty("note").GetString());

        // The guest says they are coming: the card says so.
        await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }));
        card = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/pre-orders/due")).GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("reservationId").GetGuid() == id);
        Assert.Equal(startUtc.AddMinutes(-40), card.GetProperty("guestConfirmedOnWayAtUtc").GetDateTime());
        _ = phone;
    }

    /// <summary>
    /// Sent before the party sits, the pre-order is a kitchen ticket at once - marked, with its table, on no tab. When
    /// the waiter seats the booking it is the first order on their tab: the same id, the locked prices, its kitchen
    /// progress kept.
    /// </summary>
    [SkippableFact]
    public async Task Sent_before_seating_it_is_a_kitchen_ticket_and_on_their_tab_when_they_sit()
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
            lines = new object[]
            {
                new { menuItemId = x.Menu.Khachapuri, quantity = 2 },
                new { menuItemId = x.Menu.Wine, quantity = 1 },
            },
            clientCommandId = Guid.CreateVersion7(),
        }))).GetProperty("preOrder").GetProperty("id").GetGuid();

        await SetPriceAsync(factory, x.Menu.Khachapuri, 9_999L);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = startUtc.AddMinutes(-25);

        var sent = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal(3, sent.GetProperty("preOrder").GetProperty("state").GetInt32());
        Assert.Equal(2, sent.GetProperty("preOrder").GetProperty("kitchenStatus").GetInt32());
        Assert.False(sent.GetProperty("preOrder").GetProperty("onTab").GetBoolean());

        var ticket = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/orders")).EnumerateArray()
            .Single(o => o.GetProperty("orderId").GetGuid() == preOrderId);
        Assert.False(ticket.TryGetProperty("tabId", out _));
        Assert.Equal(booking.GetProperty("tableLabel").GetString(), ticket.GetProperty("tableLabel").GetString());
        Assert.Equal(2, ticket.GetProperty("status").GetInt32());
        Assert.Equal(id, ticket.GetProperty("preOrder").GetProperty("reservationId").GetGuid());
        Assert.Equal(startUtc, ticket.GetProperty("preOrder").GetProperty("startUtc").GetDateTime());
        Assert.Equal(2, ticket.GetProperty("lines").GetArrayLength());

        // The kitchen marks it ready before anybody arrives.
        await OkAsync(await waiter.PostAsJsonAsync($"/api/orders/{preOrderId}/status", new { status = 3 }));

        var seated = await waiter.PostAsJsonAsync(
            $"/api/branches/{x.Branch.BranchId}/tables/{tableId}/seat-reservation",
            new { reservationId = id, clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);

        ticket = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/orders")).EnumerateArray()
            .Single(o => o.GetProperty("orderId").GetGuid() == preOrderId);
        var tabId = ticket.GetProperty("tabId").GetGuid();
        Assert.Equal(3, ticket.GetProperty("status").GetInt32());
        Assert.Equal(id, ticket.GetProperty("preOrder").GetProperty("reservationId").GetGuid());

        await using var db = fixture.CreateContext(factory.Clock);
        var order = await db.TabOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == preOrderId);
        Assert.Equal(tabId, order.TabId);
        Assert.Equal(x.Branch.WaiterId, order.PlacedByStaffId);
        Assert.Equal(TabOrderStatus.Ready, order.Status);
        Assert.Equal(TestMenu.KhachapuriAmd, order.Lines.Single(l => l.MenuItemId == x.Menu.Khachapuri).UnitPriceAmdSnapshot);

        var tab = await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId);
        Assert.Equal((2 * TestMenu.KhachapuriAmd) + TestMenu.WineAmd, tab.SubtotalAmd);
        Assert.Equal(TabAdmission.Admitted, tab.Admission);
        Assert.Equal(
            tabId,
            await db.TableSessions.Where(s => s.ReservationId == id).Select(s => s.TabId).SingleAsync());

        // Done: on their tab, off the rail.
        Assert.DoesNotContain(
            (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/pre-orders/due")).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("reservationId").GetGuid() == id);
    }

    /// <summary>
    /// The held ticket that became the party's first order leaves the kitchen's queue when their tab is written off, as
    /// every other order on it does - and keeps the place on the rail it had reached, with nothing taken off the bill.
    /// </summary>
    [SkippableFact]
    public async Task A_held_ticket_on_a_tab_that_is_written_off_leaves_the_kitchens_queue()
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
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, x.Branch));
        factory.Clock.UtcNow = startUtc.AddMinutes(-25);

        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal(
            HttpStatusCode.OK,
            (await waiter.PostAsJsonAsync(
                $"/api/branches/{x.Branch.BranchId}/tables/{tableId}/seat-reservation",
                new { reservationId = id, clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var ticket = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/orders")).EnumerateArray()
            .Single(o => o.GetProperty("orderId").GetGuid() == preOrderId);
        var tabId = ticket.GetProperty("tabId").GetGuid();

        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.PostAsJsonAsync($"/api/tabs/{tabId}/abandon", new { reason = "they walked out" })).StatusCode);

        Assert.DoesNotContain(
            (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/orders")).EnumerateArray(),
            o => o.GetProperty("orderId").GetGuid() == preOrderId);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(TabOrderStatus.InKitchen, await db.TabOrders.Where(o => o.Id == preOrderId).Select(o => o.Status).SingleAsync());
        Assert.Equal(
            2 * TestMenu.KhachapuriAmd,
            await db.TabOrderLines.Where(l => l.TabOrderId == preOrderId && l.VoidedAtUtc == null).SumAsync(l => l.UnitPriceAmdSnapshot * l.Quantity));
    }

    /// <summary>
    /// The guest opens the tab themselves with the booking code, after the waiter sent the pre-order: it is on the tab
    /// they open, and they are its host.
    /// </summary>
    [SkippableFact]
    public async Task Opened_with_the_booking_code_the_tab_carries_the_sent_pre_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();

        var preOrderId = (await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Wine, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }))).GetProperty("preOrder").GetProperty("id").GetGuid();

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = startUtc.AddMinutes(-25);
        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));

        factory.Clock.UtcNow = startUtc.AddMinutes(-10);
        var opened = await diner.PostAsJsonAsync(
            "/api/tabs/open-by-booking",
            new
            {
                bookingCode = booking.GetProperty("code").GetString(),
                deviceId = "phone-" + Guid.NewGuid().ToString("N")[..10],
                clientCommandId = Guid.CreateVersion7(),
                displayName = "Ani",
            });

        var token = (await OkAsync(opened)).GetProperty("token");
        var tabId = token.GetProperty("tabId").GetGuid();

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(tabId, await db.TabOrders.Where(o => o.Id == preOrderId).Select(o => o.TabId).SingleAsync());

        var host = await db.Tabs.Where(t => t.Id == tabId).Select(t => t.HostParticipantId).SingleAsync();
        Assert.Equal(token.GetProperty("participantId").GetGuid(), host);
        Assert.Equal(TestMenu.WineAmd, (await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == tabId)).SubtotalAmd);

        // The bill says when it was ordered: when the waiter sent it to the kitchen, 25 minutes before the start - not
        // when the party opened the tab fifteen minutes later. Read through the staff view, which projects the same
        // lines as the diner's tab (the participant token here is minted two days ahead of the real clock), and off the
        // stored stream, which is what every phone reads.
        var sentAtUtc = startUtc.AddMinutes(-25);
        var line = (await ReadAsync(waiter, $"/api/tabs/{tabId}/participants")).GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("orderId").GetGuid() == preOrderId);
        Assert.Equal(sentAtUtc, line.GetProperty("orderedAtUtc").GetDateTime());

        var placed = await db.TabEvents.AsNoTracking()
            .Where(e => e.TabId == tabId && e.Type == TabEventType.OrderPlaced)
            .Select(e => e.PayloadJson)
            .SingleAsync();
        using var payload = JsonDocument.Parse(placed);
        Assert.Equal(sentAtUtc, payload.RootElement.GetProperty("lines")[0].GetProperty("orderedAtUtc").GetDateTime());
    }

    /// <summary>Not sent before they sit: sending afterwards puts it straight on their tab.</summary>
    [SkippableFact]
    public async Task Sent_after_seating_it_goes_straight_onto_their_tab()
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
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 2 } },
            clientCommandId = Guid.CreateVersion7(),
        }))).GetProperty("preOrder").GetProperty("id").GetGuid();

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = startUtc.AddMinutes(-5);

        await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/branches/{x.Branch.BranchId}/tables/{tableId}/seat-reservation",
            new { reservationId = id, clientCommandId = Guid.CreateVersion7() }));

        // Seated and not sent: still on the rail, to be sent.
        var card = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/pre-orders/due")).GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("reservationId").GetGuid() == id);
        Assert.Equal(4, card.GetProperty("status").GetInt32());

        var sent = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.True(sent.GetProperty("preOrder").GetProperty("onTab").GetBoolean());

        await using var db = fixture.CreateContext(factory.Clock);
        var order = await db.TabOrders.AsNoTracking().SingleAsync(o => o.Id == preOrderId);
        Assert.Equal(TabOrderStatus.InKitchen, order.Status);
        Assert.Equal(sent.GetProperty("tabId").GetGuid(), order.TabId);
        Assert.Equal(2 * TestMenu.CoffeeAmd, (await db.Tabs.AsNoTracking().SingleAsync(t => t.Id == order.TabId)).SubtotalAmd);
    }

    /// <summary>Two tablets send the same pre-order: one ticket, and the second is told it was already sent.</summary>
    [SkippableFact]
    public async Task Two_sends_make_one_ticket()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        using var one = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        using var two = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = booking.GetProperty("startUtc").GetDateTime().AddMinutes(-20);

        var answers = await Task.WhenAll(
            one.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }),
            two.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));

        var bodies = await Task.WhenAll(answers.Select(OkAsync));
        Assert.Single(bodies, b => b.GetProperty("wasReplay").GetBoolean());

        var tickets = (await ReadAsync(one, $"/api/branches/{x.Branch.BranchId}/orders")).EnumerateArray()
            .Where(o => o.TryGetProperty("preOrder", out var p) && p.GetProperty("reservationId").GetGuid() == id);
        Assert.Single(tickets);
    }

    /// <summary>
    /// A send racing the seating: whichever commits first, the pre-order ends on the party's tab, never stranded as a
    /// held ticket nobody attaches.
    /// </summary>
    [SkippableFact]
    public async Task A_send_racing_the_seating_still_ends_on_the_tab()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        for (var round = 0; round < 3; round++)
        {
            var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2 + round, tableIndex: round));
            var id = booking.GetProperty("id").GetGuid();
            var tableId = booking.GetProperty("tableId").GetGuid();

            var preOrderId = (await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
            {
                lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            }))).GetProperty("preOrder").GetProperty("id").GetGuid();

            factory.Clock.UtcNow = booking.GetProperty("startUtc").GetDateTime().AddMinutes(-10);

            var answers = await Task.WhenAll(
                waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }),
                waiter.PostAsJsonAsync(
                    $"/api/branches/{x.Branch.BranchId}/tables/{tableId}/seat-reservation",
                    new { reservationId = id, clientCommandId = Guid.CreateVersion7() }));

            Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));

            await using var db = fixture.CreateContext(factory.Clock);
            var sessionTab = await db.TableSessions.Where(s => s.ReservationId == id).Select(s => s.TabId).SingleAsync();
            Assert.Equal(sessionTab, await db.TabOrders.Where(o => o.Id == preOrderId).Select(o => (Guid?)o.TabId).SingleAsync());

            factory.Clock.UtcNow = TestNow.UtcNow;
        }
    }

    /// <summary>
    /// "Not coming / no answer" sends nothing and keeps the card; the waiter can still send it when they arrive. Not
    /// before the lock, and not once sent.
    /// </summary>
    [SkippableFact]
    public async Task Not_coming_sends_nothing_keeps_the_card_and_can_still_be_sent()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        factory.Clock.UtcNow = startUtc.AddHours(-3);
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/not-coming", new { clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "pre-order-not-sendable");
        await ProblemAsync(
            await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "pre-order-not-sendable");

        factory.Clock.UtcNow = startUtc.AddMinutes(-20);
        var marked = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/reservations/{id}/pre-order/not-coming", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal(4, marked.GetProperty("preOrder").GetProperty("state").GetInt32());
        Assert.Equal("Test Waiter", marked.GetProperty("notComingByName").GetString());

        var again = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/reservations/{id}/pre-order/not-coming", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.True(again.GetProperty("wasReplay").GetBoolean());

        Assert.DoesNotContain(
            (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/orders")).EnumerateArray(),
            o => o.TryGetProperty("preOrder", out var p) && p.GetProperty("reservationId").GetGuid() == id);

        var card = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/pre-orders/due")).GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("reservationId").GetGuid() == id);
        Assert.Equal(4, card.GetProperty("preOrder").GetProperty("state").GetInt32());

        factory.Clock.UtcNow = startUtc.AddMinutes(10);
        var sent = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal(3, sent.GetProperty("preOrder").GetProperty("state").GetInt32());

        await ProblemAsync(
            await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/not-coming", new { clientCommandId = Guid.CreateVersion7() }),
            HttpStatusCode.Conflict,
            "pre-order-already-sent");
    }

    /// <summary>
    /// Sent and the party never comes: the no-show is marked as today and the pre-order stays "sent". An unsent one
    /// on a no-show reads cancelled - and comes back if the no-show is withdrawn.
    /// </summary>
    [SkippableFact]
    public async Task A_no_show_after_a_send_is_a_normal_no_show_and_an_unsent_one_comes_back_on_undo()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var sentBooking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2, tableIndex: 0));
        var unsentBooking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2, time: "20:00", tableIndex: 1));
        var sent = sentBooking.GetProperty("id").GetGuid();
        var unsent = unsentBooking.GetProperty("id").GetGuid();

        foreach (var id in new[] { sent, unsent })
        {
            await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
            {
                lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
                clientCommandId = Guid.CreateVersion7(),
            }));
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = sentBooking.GetProperty("startUtc").GetDateTime().AddMinutes(-15);
        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{sent}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));

        factory.Clock.UtcNow = unsentBooking.GetProperty("startUtc").GetDateTime().AddMinutes(30);

        foreach (var id in new[] { sent, unsent })
        {
            var noShow = await waiter.PostAsJsonAsync($"/api/reservations/{id}/no-show", new { clientCommandId = Guid.CreateVersion7() });
            Assert.Equal(HttpStatusCode.OK, noShow.StatusCode);
        }

        var past = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("past").EnumerateArray().ToList();
        Assert.Equal(3, past.Single(r => r.GetProperty("id").GetGuid() == sent).GetProperty("preOrder").GetProperty("state").GetInt32());
        Assert.Equal(5, past.Single(r => r.GetProperty("id").GetGuid() == unsent).GetProperty("preOrder").GetProperty("state").GetInt32());

        var undone = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/reservations/{unsent}/undo-no-show", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal(2, undone.GetProperty("preOrder").GetProperty("state").GetInt32());
    }

    /// <summary>A booking whose pre-order the kitchen already has cannot be moved; it can still be cancelled.</summary>
    [SkippableFact]
    public async Task A_booking_whose_pre_order_was_sent_cannot_be_moved()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        factory.Clock.UtcNow = booking.GetProperty("startUtc").GetDateTime().AddMinutes(-30);
        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));

        var moved = await diner.PostAsJsonAsync($"/api/reservations/{id}/reschedule", new
        {
            date = LocalDate(factory, 3),
            time = "19:00",
            partySize = 2,
            diningTableId = x.Branch.TableIds[0],
            clientCommandId = Guid.CreateVersion7(),
        });

        await ProblemAsync(moved, HttpStatusCode.Conflict, "pre-order-already-sent");
    }

    /// <summary>
    /// The rail and its actions are the branch's floor staff's: another branch's waiter, a cook and a diner are
    /// refused, and a manager of the venue reads it.
    /// </summary>
    [SkippableFact]
    public async Task The_rail_and_its_actions_belong_to_the_branchs_floor_staff()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        var elsewhere = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));
        Guid cookId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            cookId = await AuthTestData.SeedKitchenAsync(db, x.Branch);
        }

        // Signed in before the clock moves two days on, as on a real evening's shift.
        using var cook = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, x.Branch, cookId));
        using var stranger = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, elsewhere.Branch));

        using var manager = factory.CreateClientWithToken(x.ManagerToken);
        factory.Clock.UtcNow = booking.GetProperty("startUtc").GetDateTime().AddMinutes(-20);

        Assert.Equal(HttpStatusCode.Forbidden, (await cook.GetAsync($"/api/branches/{x.Branch.BranchId}/pre-orders/due")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/branches/{x.Branch.BranchId}/pre-orders/due")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await diner.GetAsync($"/api/branches/{x.Branch.BranchId}/pre-orders/due")).StatusCode);

        foreach (var client in new[] { cook, stranger, diner })
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync($"/api/reservations/{id}/pre-order/not-coming", new { clientCommandId = Guid.CreateVersion7() })).StatusCode);
        }

        Assert.Contains(
            (await ReadAsync(manager, $"/api/branches/{x.Branch.BranchId}/pre-orders/due")).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("reservationId").GetGuid() == id);
    }

    /// <summary>
    /// Every pre-order change reaches the tablets on the change stream as signal 6 naming the booking: saved, sent,
    /// and the guest's "on my way".
    /// </summary>
    [SkippableFact]
    public async Task Pre_order_changes_ride_the_change_stream_as_signal_6()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        var booking = await CreatedAsync(diner, AppBooking(factory, x.Branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        var after = (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/tables/changes?afterSequence=0&limit=500"))
            .GetProperty("maxSequence").GetInt64();

        await OkAsync(await diner.PutAsJsonAsync($"/api/reservations/{id}/pre-order", new
        {
            lines = new[] { new { menuItemId = x.Menu.Coffee, quantity = 1 } },
            clientCommandId = Guid.CreateVersion7(),
        }));
        Assert.Equal(1, await SignalsAsync());

        factory.Clock.UtcNow = booking.GetProperty("startUtc").GetDateTime().AddMinutes(-20);
        await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }));
        Assert.Equal(2, await SignalsAsync());

        await OkAsync(await waiter.PostAsJsonAsync($"/api/reservations/{id}/pre-order/send", new { clientCommandId = Guid.CreateVersion7() }));
        Assert.Equal(3, await SignalsAsync());

        async Task<int> SignalsAsync() =>
            (await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/tables/changes?afterSequence={after}&limit=500"))
                .GetProperty("changes").EnumerateArray()
                .Count(c => c.TryGetProperty("signal", out var s) && s.GetInt32() == 6
                            && c.GetProperty("reservationId").GetGuid() == id);
    }

    /// <summary>The console's settings: off by default, round-trip, bounded, and the exclusions are the menu's.</summary>
    [SkippableFact]
    public async Task The_branch_settings_default_off_round_trip_and_are_bounded()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, accepts: false);
        var other = await ArrangeAsync(factory, accepts: false);
        using var manager = factory.CreateClientWithToken(x.ManagerToken);
        var url = $"/api/branches/{x.Branch.BranchId}/pre-order-settings";

        var defaults = await ReadAsync(manager, url);
        Assert.False(defaults.GetProperty("acceptsPreOrders").GetBoolean());
        Assert.Equal(30, defaults.GetProperty("minutesBefore").GetInt32());
        Assert.Empty(defaults.GetProperty("excludedMenuItemIds").EnumerateArray());
        Assert.Equal(5, defaults.GetProperty("bounds").GetProperty("minutesBeforeMin").GetInt32());
        Assert.Equal(120, defaults.GetProperty("bounds").GetProperty("minutesBeforeMax").GetInt32());
        Assert.Equal(2, defaults.GetProperty("editableUntilHoursBefore").GetInt32());
        Assert.Equal(45, defaults.GetProperty("onYourWayMinutesBefore").GetInt32());

        var saved = await OkAsync(await manager.PutAsJsonAsync(url, new
        {
            acceptsPreOrders = true,
            minutesBefore = 45,
            excludedMenuItemIds = new[] { x.Menu.Wine, x.Menu.Coffee },
        }));
        Assert.True(saved.GetProperty("acceptsPreOrders").GetBoolean());
        Assert.Equal(45, saved.GetProperty("minutesBefore").GetInt32());
        Assert.Equal(2, saved.GetProperty("excludedMenuItemIds").GetArrayLength());

        // The whole list each time: coffee is pre-orderable again.
        await OkAsync(await manager.PutAsJsonAsync(url, new
        {
            acceptsPreOrders = true,
            minutesBefore = 45,
            excludedMenuItemIds = new[] { x.Menu.Wine },
        }));

        var menu = (await ReadAsync(factory.CreateClient(), $"/api/branches/{x.Branch.BranchId}/menu"))
            .GetProperty("categories").EnumerateArray().SelectMany(c => c.GetProperty("items").EnumerateArray()).ToList();
        Assert.False(menu.Single(i => i.GetProperty("id").GetGuid() == x.Menu.Wine).GetProperty("availableForPreOrder").GetBoolean());
        Assert.True(menu.Single(i => i.GetProperty("id").GetGuid() == x.Menu.Coffee).GetProperty("availableForPreOrder").GetBoolean());

        foreach (var minutes in new[] { 4, 121 })
        {
            var fields = (await ProblemAsync(
                    await manager.PutAsJsonAsync(url, new { acceptsPreOrders = true, minutesBefore = minutes, excludedMenuItemIds = Array.Empty<Guid>() }),
                    HttpStatusCode.UnprocessableEntity,
                    "validation-failed"))
                .GetProperty("context").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString());
            Assert.Contains("minutesBefore", fields);
        }

        var foreign = (await ProblemAsync(
                await manager.PutAsJsonAsync(url, new { acceptsPreOrders = true, minutesBefore = 30, excludedMenuItemIds = new[] { other.Menu.Coffee } }),
                HttpStatusCode.UnprocessableEntity,
                "validation-failed"))
            .GetProperty("context").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString());
        Assert.Contains("excludedMenuItemIds", foreign);

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await waiter.PutAsJsonAsync(url, new { acceptsPreOrders = false, minutesBefore = 30, excludedMenuItemIds = Array.Empty<Guid>() })).StatusCode);
    }
}
