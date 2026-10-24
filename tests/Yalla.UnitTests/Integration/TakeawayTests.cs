using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Identity;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;
using static Yalla.UnitTests.Integration.PreOrderTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Takeaway end to end: the diner orders, the tablet accepts, the kitchen cooks, the counter hands over - and every rule
/// on the way. See <c>docs/takeaway.md</c>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class TakeawayTests(SqlServerFixture fixture)
{
    /// <summary>Tomorrow at 14:00 in Yerevan (UTC+4): inside the test branch's 10:00-23:00 every day.</summary>
    private static readonly DateTime Afternoon = DateTime.UtcNow.Date.AddDays(1).AddHours(10);

    /// <summary>
    /// The whole way: the place page says takeaway is on, the checkout offers ASAP and slots, the order is sent (and a
    /// retry answers the same one), the tablet accepts it for a time, the kitchen sees the TAKEAWAY ticket and marks it
    /// ready, the counter takes cash for the total, and the guest's status screen, Orders tab and feed say each step.
    /// </summary>
    [SkippableFact]
    public async Task A_takeaway_order_goes_from_sent_to_collected_with_the_guest_told_each_step()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        Guid cookId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            cookId = await AuthTestData.SeedKitchenAsync(db, x.Branch);
        }

        using var cook = factory.CreateClientWithToken(await AuthTestData.SignInKitchenAsync(factory, x.Branch, cookId));

        // Signed in on the real clock - a token is checked against it - and then to tomorrow afternoon.
        factory.Clock.UtcNow = Afternoon;

        var place = await ReadAsync(diner, $"/api/public/branches/{x.Branch.BranchId}");
        Assert.True(place.GetProperty("takeaway").GetProperty("enabled").GetBoolean());
        Assert.Equal(20, place.GetProperty("takeaway").GetProperty("prepMinutes").GetInt32());

        var availability = await ReadAsync(diner, $"/api/diner/branches/{x.Branch.BranchId}/takeaway");
        Assert.True(availability.GetProperty("open").GetBoolean());
        Assert.False(availability.GetProperty("busy").GetBoolean());
        Assert.Equal("14:20", availability.GetProperty("asapReadyAt").GetString());
        Assert.Equal(Afternoon.AddMinutes(20), availability.GetProperty("asapReadyAtUtc").GetDateTime());
        Assert.Equal("22:30", availability.GetProperty("lastPickupAt").GetString());
        var slots = availability.GetProperty("slots").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal("14:30", slots[0]);
        Assert.Equal("22:30", slots[^1]);

        var command = Guid.CreateVersion7();
        var body = new
        {
            branchId = x.Branch.BranchId,
            pickup = new { asap = true },
            note = "No onion, separate bag",
            lines = new[]
            {
                new { menuItemId = x.Menu.Khachapuri, quantity = 2 },
                new { menuItemId = x.Menu.Coffee, quantity = 1 },
            },
            clientCommandId = command,
        };

        var sent = await OkAsync(await diner.PostAsJsonAsync("/api/diner/takeaway-orders", body));
        var orderId = sent.GetProperty("id").GetGuid();
        var total = (2 * TestMenu.KhachapuriAmd) + TestMenu.CoffeeAmd;

        Assert.Equal((int)TakeawayStatus.Sent, sent.GetProperty("status").GetInt32());
        Assert.Matches("^T-[0-9]{3}$", sent.GetProperty("code").GetString());
        Assert.Equal(total, sent.GetProperty("totalAmd").GetInt64());
        Assert.Equal("14:20", sent.GetProperty("requestedPickupAt").GetString());
        Assert.Equal(Afternoon.AddMinutes(5), sent.GetProperty("acceptDeadlineUtc").GetDateTime());
        Assert.True(sent.GetProperty("canCancel").GetBoolean());
        Assert.False(sent.GetProperty("wasReplay").GetBoolean());

        // The retry of the same send answers the order the first one made.
        var retried = await OkAsync(await diner.PostAsJsonAsync("/api/diner/takeaway-orders", body));
        Assert.Equal(orderId, retried.GetProperty("id").GetGuid());
        Assert.True(retried.GetProperty("wasReplay").GetBoolean());

        var counter = await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/takeaway-orders");
        Assert.Equal(1, counter.GetProperty("sentCount").GetInt32());
        var card = Assert.Single(counter.GetProperty("active").EnumerateArray());
        Assert.Equal(orderId, card.GetProperty("id").GetGuid());
        Assert.Equal(phone, card.GetProperty("guestPhone").GetString());
        Assert.Equal(Afternoon.AddMinutes(5), card.GetProperty("acceptDeadlineUtc").GetDateTime());

        factory.Clock.UtcNow = Afternoon.AddMinutes(2);
        var accepted = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/takeaway-orders/{orderId}/accept", new { readyAt = Afternoon.AddMinutes(25) }));
        Assert.Equal((int)TakeawayStatus.Accepted, accepted.GetProperty("status").GetInt32());
        Assert.Equal("14:25", accepted.GetProperty("readyAt").GetString());
        Assert.Equal("Test Waiter", accepted.GetProperty("acceptedByName").GetString());

        // Two tablets, one answer.
        var again = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/takeaway-orders/{orderId}/accept", new { readyAt = Afternoon.AddMinutes(30) }));
        Assert.True(again.GetProperty("wasReplay").GetBoolean());
        Assert.Equal(Afternoon.AddMinutes(25), again.GetProperty("readyAtUtc").GetDateTime());

        // The kitchen's ticket: the code for a table, the band with the ready time, the name and the note.
        var queue = await ReadAsync(cook, $"/api/branches/{x.Branch.BranchId}/orders");
        var ticket = Assert.Single(queue.EnumerateArray(), t => t.GetProperty("orderId").GetGuid() == orderId);
        Assert.Equal(sent.GetProperty("code").GetString(), ticket.GetProperty("tableLabel").GetString());
        Assert.Equal(2, ticket.GetProperty("status").GetInt32());
        Assert.False(ticket.TryGetProperty("tabId", out _));
        var band = ticket.GetProperty("takeaway");
        Assert.Equal("14:25:00", band.GetProperty("readyAtLocalTime").GetString());
        Assert.Equal("No onion, separate bag", band.GetProperty("note").GetString());
        Assert.Equal(2, ticket.GetProperty("lines").GetArrayLength());

        // The kitchen may only cook: not accept, not hand over - refused at the route.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await cook.PostAsJsonAsync($"/api/takeaway-orders/{orderId}/collect", new { method = 1 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await cook.GetAsync($"/api/branches/{x.Branch.BranchId}/takeaway-orders")).StatusCode);

        factory.Clock.UtcNow = Afternoon.AddMinutes(24);
        var ready = await OkAsync(await cook.PostAsync($"/api/takeaway-orders/{orderId}/ready", null));
        Assert.Equal((int)TakeawayStatus.Ready, ready.GetProperty("status").GetInt32());
        Assert.Equal(Afternoon.AddMinutes(55), ready.GetProperty("noShowFromUtc").GetDateTime());

        var mid = await ReadAsync(diner, $"/api/diner/takeaway-orders/{orderId}");
        Assert.Equal((int)TakeawayStatus.Ready, mid.GetProperty("status").GetInt32());
        Assert.False(mid.GetProperty("canCancel").GetBoolean());

        factory.Clock.UtcNow = Afternoon.AddMinutes(40);
        var collected = await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{orderId}/collect", new { method = 1 }));
        Assert.Equal((int)TakeawayStatus.Collected, collected.GetProperty("status").GetInt32());
        Assert.Equal(1, collected.GetProperty("paymentMethod").GetInt32());
        Assert.Equal(total, collected.GetProperty("paidAmountAmd").GetInt64());

        var mine = await ReadAsync(diner, $"/api/diner/takeaway-orders/{orderId}");
        Assert.Equal(Afternoon, mine.GetProperty("sentAtUtc").GetDateTime());
        Assert.Equal(Afternoon.AddMinutes(2), mine.GetProperty("acceptedAtUtc").GetDateTime());
        Assert.Equal(Afternoon.AddMinutes(24), mine.GetProperty("readyMarkedAtUtc").GetDateTime());
        Assert.Equal(Afternoon.AddMinutes(40), mine.GetProperty("collectedAtUtc").GetDateTime());
        Assert.Equal("1 Test Street, Yerevan", mine.GetProperty("branchAddress").GetString());

        var orders = await ReadAsync(diner, "/api/diner/orders");
        var entry = Assert.Single(orders.EnumerateArray());
        Assert.Equal("takeaway", entry.GetProperty("kind").GetString());
        Assert.Equal("completed", entry.GetProperty("status").GetString());
        Assert.Equal(sent.GetProperty("code").GetString(), entry.GetProperty("takeaway").GetProperty("code").GetString());
        Assert.Equal(3, entry.GetProperty("takeaway").GetProperty("itemCount").GetInt32());
        Assert.False(entry.TryGetProperty("tabId", out _));
        Assert.Empty((await ReadAsync(diner, "/api/diner/orders?status=active")).EnumerateArray());

        counter = await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/takeaway-orders");
        Assert.Empty(counter.GetProperty("active").EnumerateArray());
        Assert.Equal(orderId, Assert.Single(counter.GetProperty("finishedToday").EnumerateArray()).GetProperty("id").GetGuid());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var types = await db.OutboxMessages
                .Where(m => m.IdempotencyKey.StartsWith($"takeaway:{orderId}:"))
                .Select(m => m.Type)
                .ToListAsync();
            Assert.Equal(
                [OutboxMessageTypes.TakeawayAccepted, OutboxMessageTypes.TakeawayReady],
                types.Order().ToList());

            var feed = await db.DinerNotifications.Where(n => n.OrderId == orderId).Select(n => new { n.Kind, n.ParamsJson }).ToListAsync();
            Assert.Contains(feed, n => n.Kind == DinerNotificationKinds.TakeawayAccepted && n.ParamsJson.Contains("\"readyTime\":\"14:25\""));
            Assert.Contains(feed, n => n.Kind == DinerNotificationKinds.TakeawayReady);
        }
    }

    /// <summary>
    /// Every refusal before an order is saved: takeaway off, closed (with when it opens), a dish served in the hall only
    /// or sold out (each named), a slot not on offer, a shape out of bounds, and an unproved phone.
    /// </summary>
    [SkippableFact]
    public async Task An_order_is_refused_for_each_rule_before_anything_is_saved()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, enabled: false);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var manager = factory.CreateClientWithToken(x.ManagerToken);
        using var unproved = factory.CreateClientWithToken(await RegisterUnprovedAsync(factory));

        // Signed in on the real clock - a token is checked against it - and then to tomorrow afternoon.
        factory.Clock.UtcNow = Afternoon;

        object Order(object pickup, params (Guid Item, int Quantity)[] lines) => new
        {
            branchId = x.Branch.BranchId,
            pickup,
            lines = lines.Select(l => new { menuItemId = l.Item, quantity = l.Quantity }).ToArray(),
            clientCommandId = Guid.CreateVersion7(),
        };

        var asap = new { asap = true };

        Assert.False((await ReadAsync(diner, $"/api/diner/branches/{x.Branch.BranchId}/takeaway")).GetProperty("enabled").GetBoolean());
        await ProblemAsync(
            await diner.PostAsJsonAsync("/api/diner/takeaway-orders", Order(asap, (x.Menu.Coffee, 1))),
            HttpStatusCode.Conflict,
            "takeaway-not-accepted");

        await SettingsAsync(manager, x, enabled: true);

        // Hall only, and sold out: both named, nothing saved.
        await OkAsync(await manager.PatchAsJsonAsync(
            $"/api/branches/{x.Branch.BranchId}/menu/items/{x.Menu.Wine}", new { availableForTakeaway = false }));
        var menu = await ReadAsync(diner, $"/api/public/branches/{x.Branch.BranchId}/menu");
        Assert.Contains(
            menu.GetProperty("categories").EnumerateArray().SelectMany(c => c.GetProperty("items").EnumerateArray()),
            i => i.GetProperty("id").GetGuid() == x.Menu.Wine && !i.GetProperty("availableForTakeaway").GetBoolean());

        var refused = await ProblemAsync(
            await diner.PostAsJsonAsync(
                "/api/diner/takeaway-orders", Order(asap, (x.Menu.Wine, 1), (x.Menu.SoldOut, 1), (x.Menu.Coffee, 1))),
            HttpStatusCode.UnprocessableEntity,
            "takeaway-items-unavailable");
        var items = refused.GetProperty("context").GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("menuItemId").GetGuid(), i.GetProperty("reason").GetInt32()))
            .ToList();
        Assert.Equal([(x.Menu.Wine, 3), (x.Menu.SoldOut, 2)], items);

        var slot = await ProblemAsync(
            await diner.PostAsJsonAsync("/api/diner/takeaway-orders", Order(new { at = "14:15" }, (x.Menu.Coffee, 1))),
            HttpStatusCode.Conflict,
            "takeaway-slot-unavailable");
        Assert.Equal("14:30", slot.GetProperty("context").GetProperty("slots")[0].GetString());

        var shape = await ProblemAsync(
            await diner.PostAsJsonAsync("/api/diner/takeaway-orders", Order(new { asap = true, at = "15:00" }, (x.Menu.Coffee, 25))),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");
        var fields = shape.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("pickup", fields);
        Assert.Contains("lines[0].quantity", fields);

        // An account whose number was never proved cannot order, as it cannot book.
        await ProblemAsync(
            await unproved.PostAsJsonAsync("/api/diner/takeaway-orders", Order(asap, (x.Menu.Coffee, 1))),
            HttpStatusCode.Forbidden,
            "phone-not-verified");

        // Past the last order: closed, and when it opens again - tomorrow at 10:00.
        factory.Clock.UtcNow = Afternoon.AddHours(8).AddMinutes(11);
        var availability = await ReadAsync(diner, $"/api/diner/branches/{x.Branch.BranchId}/takeaway");
        Assert.False(availability.GetProperty("open").GetBoolean());
        Assert.Empty(availability.GetProperty("slots").EnumerateArray());
        Assert.Equal("10:00", availability.GetProperty("opensAt").GetString());

        var closed = await ProblemAsync(
            await diner.PostAsJsonAsync("/api/diner/takeaway-orders", Order(asap, (x.Menu.Coffee, 1))),
            HttpStatusCode.Conflict,
            "takeaway-closed");
        Assert.Equal(Afternoon.AddHours(20), closed.GetProperty("context").GetProperty("opensAtUtc").GetDateTime());

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.False(await db.TakeawayOrders.AnyAsync(o => o.BranchId == x.Branch.BranchId));
    }

    /// <summary>
    /// With room for one order at a time, a second ASAP is refused as busy, the slots its cooking would overlap are no
    /// longer offered, and a later one still is.
    /// </summary>
    [SkippableFact]
    public async Task A_full_kitchen_refuses_asap_and_stops_offering_the_slots_it_would_overlap()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, maxInProgress: 1);
        using var first = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var second = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        // Signed in on the real clock - a token is checked against it - and then to tomorrow afternoon.
        factory.Clock.UtcNow = Afternoon;

        await PlaceAsync(first, x, new { asap = true });

        var availability = await ReadAsync(second, $"/api/diner/branches/{x.Branch.BranchId}/takeaway");
        Assert.True(availability.GetProperty("busy").GetBoolean());
        var slots = availability.GetProperty("slots").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.DoesNotContain("14:30", slots);
        Assert.Equal("14:45", slots[0]);

        var busy = await ProblemAsync(
            await second.PostAsJsonAsync("/api/diner/takeaway-orders", OrderBody(x, new { asap = true })),
            HttpStatusCode.Conflict,
            "takeaway-busy");
        Assert.Equal(1, busy.GetProperty("context").GetProperty("maxInProgress").GetInt32());

        var later = await PlaceAsync(second, x, new { at = "14:45" });
        Assert.Equal("14:45", later.GetProperty("requestedPickupAt").GetString());
        Assert.False(later.GetProperty("pickupAsap").GetBoolean());
    }

    /// <summary>
    /// The guest cancels while the venue has not answered, and not after; the venue declines with a reason the guest
    /// reads; somebody else's order is not found; somebody else's command id is taken.
    /// </summary>
    [SkippableFact]
    public async Task Cancel_and_decline_answer_only_a_sent_order_and_orders_stay_their_owners()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var stranger = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        // Signed in on the real clock - a token is checked against it - and then to tomorrow afternoon.
        factory.Clock.UtcNow = Afternoon;

        var cancelled = await PlaceAsync(diner, x, new { asap = true });
        var id = cancelled.GetProperty("id").GetGuid();
        factory.Clock.UtcNow = Afternoon.AddMinutes(1);

        await ProblemAsync(await stranger.GetAsync($"/api/diner/takeaway-orders/{id}"), HttpStatusCode.NotFound, "not-found");
        await ProblemAsync(await stranger.PostAsync($"/api/diner/takeaway-orders/{id}/cancel", null), HttpStatusCode.NotFound, "not-found");

        var gone = await OkAsync(await diner.PostAsync($"/api/diner/takeaway-orders/{id}/cancel", null));
        Assert.Equal((int)TakeawayStatus.Cancelled, gone.GetProperty("status").GetInt32());
        Assert.True((await OkAsync(await diner.PostAsync($"/api/diner/takeaway-orders/{id}/cancel", null))).GetProperty("wasReplay").GetBoolean());

        // The venue cannot accept what the guest cancelled - and says where it is now.
        var late = await ProblemAsync(
            await waiter.PostAsJsonAsync($"/api/takeaway-orders/{id}/accept", new { readyAt = Afternoon.AddMinutes(20) }),
            HttpStatusCode.Conflict,
            "takeaway-status-conflict");
        Assert.Equal((int)TakeawayStatus.Cancelled, late.GetProperty("context").GetProperty("status").GetInt32());

        var accepted = await PlaceAsync(diner, x, new { asap = true });
        var acceptedId = accepted.GetProperty("id").GetGuid();
        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{acceptedId}/accept", new { readyAt = Afternoon.AddMinutes(20) }));
        await ProblemAsync(
            await diner.PostAsync($"/api/diner/takeaway-orders/{acceptedId}/cancel", null),
            HttpStatusCode.Conflict,
            "takeaway-status-conflict");

        factory.Clock.UtcNow = Afternoon.AddMinutes(2);
        var declined = await PlaceAsync(diner, x, new { asap = true });
        var declinedId = declined.GetProperty("id").GetGuid();
        var no = await OkAsync(await waiter.PostAsJsonAsync(
            $"/api/takeaway-orders/{declinedId}/decline", new { reason = "Out of charcoal tonight" }));
        Assert.Equal((int)TakeawayStatus.Declined, no.GetProperty("status").GetInt32());

        var told = await ReadAsync(diner, $"/api/diner/takeaway-orders/{declinedId}");
        Assert.Equal(1, told.GetProperty("declinedBecause").GetInt32());
        Assert.Equal("Out of charcoal tonight", told.GetProperty("declineReason").GetString());

        // A command id is the person's: somebody else's is taken, not answered with their order.
        factory.Clock.UtcNow = Afternoon.AddMinutes(3);
        var command = Guid.CreateVersion7();
        await PlaceAsync(diner, x, new { at = "15:00" }, command);
        await ProblemAsync(
            await stranger.PostAsJsonAsync("/api/diner/takeaway-orders", OrderBody(x, new { asap = true }, command)),
            HttpStatusCode.Conflict,
            "client-command-id-in-use");

        // The Orders tab: the ones still moving first.
        var orders = (await ReadAsync(diner, "/api/diner/orders")).EnumerateArray().ToList();
        Assert.Equal(4, orders.Count);
        Assert.Equal(["inProgress", "preparing", "cancelled", "cancelled"], orders.Select(o => o.GetProperty("status").GetString()));
    }

    /// <summary>
    /// "No-show" only for a ready order, from thirty minutes after it was ready - not a minute before - and the floor
    /// from another branch may not touch it.
    /// </summary>
    [SkippableFact]
    public async Task No_show_is_offered_thirty_minutes_after_ready_and_only_at_the_orders_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory);
        var elsewhere = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        using var stranger = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, elsewhere.Branch));

        // Signed in on the real clock - a token is checked against it - and then to tomorrow afternoon.
        factory.Clock.UtcNow = Afternoon;

        var id = (await PlaceAsync(diner, x, new { asap = true })).GetProperty("id").GetGuid();

        await ProblemAsync(
            await stranger.PostAsJsonAsync($"/api/takeaway-orders/{id}/accept", new { readyAt = Afternoon.AddMinutes(20) }),
            HttpStatusCode.Forbidden,
            "forbidden");
        await ProblemAsync(
            await waiter.PostAsync($"/api/takeaway-orders/{id}/no-show", null), HttpStatusCode.Conflict, "takeaway-status-conflict");

        await ProblemAsync(
            await waiter.PostAsJsonAsync($"/api/takeaway-orders/{id}/accept", new { readyAt = Afternoon.AddMinutes(-5) }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");

        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{id}/accept", new { readyAt = Afternoon.AddMinutes(20) }));
        factory.Clock.UtcNow = Afternoon.AddMinutes(18);
        await OkAsync(await waiter.PostAsync($"/api/takeaway-orders/{id}/ready", null));

        factory.Clock.UtcNow = Afternoon.AddMinutes(49);
        var early = await ProblemAsync(
            await waiter.PostAsync($"/api/takeaway-orders/{id}/no-show", null), HttpStatusCode.Conflict, "takeaway-no-show-too-early");
        Assert.Equal(Afternoon.AddMinutes(50), early.GetProperty("context").GetProperty("noShowFromUtc").GetDateTime());

        factory.Clock.UtcNow = Afternoon.AddMinutes(50);
        var noShow = await OkAsync(await waiter.PostAsync($"/api/takeaway-orders/{id}/no-show", null));
        Assert.Equal((int)TakeawayStatus.NoShow, noShow.GetProperty("status").GetInt32());

        var finished = await ReadAsync(waiter, $"/api/branches/{x.Branch.BranchId}/takeaway-orders");
        Assert.Equal(id, Assert.Single(finished.GetProperty("finishedToday").EnumerateArray()).GetProperty("id").GetGuid());
    }

    /// <summary>A branch that accepts automatically answers "accepted" at once, ready when asked for, with no push.</summary>
    [SkippableFact]
    public async Task Auto_accept_answers_accepted_at_once_ready_when_asked_for()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, autoAccept: true);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        // Signed in on the real clock - a token is checked against it - and then to tomorrow afternoon.
        factory.Clock.UtcNow = Afternoon;

        var order = await PlaceAsync(diner, x, new { at = "15:30" });

        Assert.Equal((int)TakeawayStatus.Accepted, order.GetProperty("status").GetInt32());
        Assert.Equal("15:30", order.GetProperty("readyAt").GetString());
        Assert.False(order.TryGetProperty("acceptDeadlineUtc", out _));
        Assert.False(order.GetProperty("canCancel").GetBoolean());

        await using var db = fixture.CreateContext(factory.Clock);
        var id = order.GetProperty("id").GetGuid();
        Assert.False(await db.OutboxMessages.AnyAsync(m => m.IdempotencyKey.StartsWith($"takeaway:{id}:")));
        Assert.True(await db.DinerNotifications.AnyAsync(n => n.OrderId == id && n.Kind == DinerNotificationKinds.TakeawayAccepted));
    }

    /// <summary>
    /// The settings: defaults off, saved whole, every field required and bounded, a manager's - not a waiter's.
    /// </summary>
    [SkippableFact]
    public async Task Settings_are_saved_whole_bounded_and_the_managers()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var x = await ArrangeAsync(factory, enabled: false, save: false);
        using var manager = factory.CreateClientWithToken(x.ManagerToken);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));
        var url = $"/api/branches/{x.Branch.BranchId}/takeaway-settings";

        var defaults = await ReadAsync(manager, url);
        Assert.False(defaults.GetProperty("enabled").GetBoolean());
        Assert.False(defaults.GetProperty("autoAccept").GetBoolean());
        Assert.Equal(20, defaults.GetProperty("prepMinutes").GetInt32());
        Assert.Equal(30, defaults.GetProperty("lastOrderMinutes").GetInt32());
        Assert.Equal(5, defaults.GetProperty("maxInProgress").GetInt32());
        Assert.Equal(5, defaults.GetProperty("acceptWindowMinutes").GetInt32());
        Assert.Equal(90, defaults.GetProperty("bounds").GetProperty("prepMinutesMax").GetInt32());

        var bad = await ProblemAsync(
            await manager.PutAsJsonAsync(url, new { enabled = true, autoAccept = false, prepMinutes = 22, lastOrderMinutes = 130, maxInProgress = 0 }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");
        var fields = bad.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["lastOrderMinutes", "maxInProgress", "prepMinutes"], fields.Order().ToList());

        await ProblemAsync(
            await manager.PutAsJsonAsync(url, new { enabled = true, prepMinutes = 20, lastOrderMinutes = 30, maxInProgress = 5 }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");

        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.PutAsJsonAsync(
            url, new { enabled = true, autoAccept = false, prepMinutes = 20, lastOrderMinutes = 30, maxInProgress = 5 })).StatusCode);

        var saved = await OkAsync(await manager.PutAsJsonAsync(
            url, new { enabled = true, autoAccept = true, prepMinutes = 25, lastOrderMinutes = 45, maxInProgress = 8 }));
        Assert.True(saved.GetProperty("enabled").GetBoolean());
        Assert.True(saved.GetProperty("autoAccept").GetBoolean());
        Assert.Equal(25, saved.GetProperty("prepMinutes").GetInt32());
    }

    /// <summary>
    /// Nobody answers within five minutes: the sweep declines the order - not a minute early - the guest is told it was
    /// nobody's answer, and the push says so in their language. An order accepted in time is left alone.
    /// </summary>
    [SkippableFact]
    public async Task The_sweep_declines_an_order_nobody_accepted_within_five_minutes_and_tells_the_guest()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        await fixture.ClearOutboxAsync(factory.Clock);
        var x = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, x.Branch));

        // Signed in on the real clock - a token is checked against it - and then to tomorrow afternoon.
        factory.Clock.UtcNow = Afternoon;

        var lapsed = (await PlaceAsync(diner, x, new { asap = true })).GetProperty("id").GetGuid();
        var answered = (await PlaceAsync(diner, x, new { at = "15:00" })).GetProperty("id").GetGuid();
        await OkAsync(await waiter.PostAsJsonAsync($"/api/takeaway-orders/{answered}/accept", new { readyAt = Afternoon.AddMinutes(60) }));

        var clock = new TestClock(Afternoon.AddMinutes(4));

        await using (var db = fixture.CreateContext(clock))
        {
            Assert.Equal(0, await Sweep(db, clock).RunOnceAsync());
        }

        clock.UtcNow = Afternoon.AddMinutes(5);

        await using (var db = fixture.CreateContext(clock))
        {
            Assert.True(await Sweep(db, clock).RunOnceAsync() >= 1);
        }

        await using (var db = fixture.CreateContext(clock))
        {
            Assert.Equal(0, await Sweep(db, clock).RunOnceAsync());
        }

        var told = await ReadAsync(diner, $"/api/diner/takeaway-orders/{lapsed}");
        Assert.Equal((int)TakeawayStatus.Declined, told.GetProperty("status").GetInt32());
        Assert.Equal(2, told.GetProperty("declinedBecause").GetInt32());
        Assert.Equal(
            (int)TakeawayStatus.Accepted,
            (await ReadAsync(diner, $"/api/diner/takeaway-orders/{answered}")).GetProperty("status").GetInt32());

        var channel = new RecordingChannel();

        await using (var db = fixture.CreateContext(clock))
        {
            Assert.Contains(
                await db.DinerNotifications.Where(n => n.OrderId == lapsed).Select(n => n.Kind + n.ParamsJson).ToListAsync(),
                n => n.StartsWith(DinerNotificationKinds.TakeawayDeclined) && n.Contains("\"noAnswer\":\"true\""));

            await Dispatcher(db, clock, channel).RunOnceAsync();
            await Dispatcher(db, clock, channel).RunOnceAsync();
        }

        // The apology, in the guest's language - Armenian for an account with no phone to say otherwise.
        var push = Assert.Single(channel.OfKind("takeaway-declined"), m => m.Data["orderId"] == lapsed.ToString());
        Assert.Equal(NotificationText.TakeawayDeclined("hy", "-", "-", null, noAnswer: true).Title, push.Title);
        Assert.Single(channel.OfKind("takeaway-accepted"), m => m.Data["orderId"] == answered.ToString());
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Arrangement(AuthBranch Branch, TestMenu Menu, string ManagerToken);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>A branch with the test menu, a manager signed in, and takeaway saved as asked.</summary>
    private async Task<Arrangement> ArrangeAsync(
        YallaApiFactory factory,
        bool enabled = true,
        bool autoAccept = false,
        int maxInProgress = 5,
        bool save = true)
    {
        AuthBranch branch;
        TestMenu menu;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2);
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, acceptsOnlineBookings: true);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
        }

        var x = new Arrangement(branch, menu, await SignInManagerAsync(factory, branch));

        if (save)
        {
            using var manager = factory.CreateClientWithToken(x.ManagerToken);
            await SettingsAsync(manager, x, enabled, autoAccept, maxInProgress);
        }

        return x;
    }

    private static async Task SettingsAsync(
        HttpClient manager, Arrangement x, bool enabled, bool autoAccept = false, int maxInProgress = 5) =>
        await OkAsync(await manager.PutAsJsonAsync($"/api/branches/{x.Branch.BranchId}/takeaway-settings", new
        {
            enabled,
            autoAccept,
            prepMinutes = 20,
            lastOrderMinutes = 30,
            maxInProgress,
        }));

    private static object OrderBody(Arrangement x, object pickup, Guid? command = null) => new
    {
        branchId = x.Branch.BranchId,
        pickup,
        lines = new[] { new { menuItemId = x.Menu.Khachapuri, quantity = 1 } },
        clientCommandId = command ?? Guid.CreateVersion7(),
    };

    private static async Task<JsonElement> PlaceAsync(HttpClient diner, Arrangement x, object pickup, Guid? command = null) =>
        await OkAsync(await diner.PostAsJsonAsync("/api/diner/takeaway-orders", OrderBody(x, pickup, command)));

    private static async Task<string> RegisterUnprovedAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var suffix = Random.Shared.Next(1_000_000, 9_999_999);

        var registered = await client.PostAsJsonAsync("/api/auth/diner/register", new
        {
            username = $"ani{suffix}",
            email = $"ani{suffix}@example.test",
            password = "a long enough password",
            phoneE164 = $"+3749{suffix}",
            displayName = "Ani",
        });

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private TakeawayExpirySweep Sweep(YallaDbContext db, TestClock clock) =>
        new(
            db,
            clock,
            fixture.CreateOutbox(db, clock),
            SqlServerFixture.CreateActivityLog(db, clock, new TestActor(Domain.Enums.ActorType.System, null, null)),
            NullLogger<TakeawayExpirySweep>.Instance);

    private static OutboxDispatcher Dispatcher(YallaDbContext db, TestClock clock, RecordingChannel channel) =>
        new(
            db,
            clock,
            new OutboxOptions(),
            [
                new TakeawayAcceptedHandler(db, channel),
                new TakeawayReadyHandler(db, channel),
                new TakeawayDeclinedHandler(db, channel),
            ],
            NullLogger<OutboxDispatcher>.Instance);
}
