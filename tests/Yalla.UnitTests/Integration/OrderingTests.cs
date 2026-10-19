using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yalla.Application.Menus;
using Yalla.Application.Ordering;
using Yalla.Application.Reservations;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Billing;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Ordering from both surfaces, voids, comps, and the totals that come out.
/// </summary>
/// <remarks>
/// Against real SQL Server, because most of what is under test is the database's: the unique index
/// that makes a double-tap idempotent, the row version that makes two simultaneous orders safe, and
/// the identity column that gives the event stream its order.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class OrderingTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 6, 18, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ the menu

    /// <summary>
    /// A pending participant reads the whole menu, prices included, and can do nothing else.
    /// </summary>
    /// <remarks>
    /// The rule was set in Prompt 5 and only starts to bite now that there is a menu to read. It is
    /// the one thing a guest waiting on the host's approval must be able to do: they are sitting at
    /// the table, and a menu they cannot open is the friction the product exists to remove. Prices
    /// stay visible even to somebody the host has hidden the table total from, so they can always
    /// work out what their own order costs.
    /// </remarks>
    [SkippableFact]
    public async Task A_pending_participant_reads_the_menu_with_prices_and_cannot_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        // Somebody who scanned the code and has not been let on yet.
        var pending = await JoinWithoutApprovalAsync(world, "phone-pending", "Vahe");

        var menu = await SqlServerFixture.CreateMenuQuery(db).GetBranchMenuAsync(world.Branch.BranchId);

        var items = menu.Categories.SelectMany(c => c.Items).ToList();

        Assert.Equal(4, items.Count);
        Assert.All(items, i => Assert.True(i.PriceAmd > 0L, $"{i.Name} came back without a price."));

        // Every descriptive field is present, because the whole argument for making them required
        // was that the diner stops needing to ask a waiter.
        Assert.All(items, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Description));
            Assert.False(string.IsNullOrWhiteSpace(i.Ingredients));
            Assert.False(string.IsNullOrWhiteSpace(i.Allergens));
            Assert.False(string.IsNullOrWhiteSpace(i.PortionSize));
            // Asserted rather than dereferenced: a photo is required on a menu
            // item, so a null one is the failure this line is here to catch,
            // and `i.Photo.CardUrl` on its own reports it as an NRE instead.
            Assert.NotNull(i.Photo);
            Assert.False(string.IsNullOrWhiteSpace(i.Photo.CardUrl));
            Assert.NotEqual(Guid.Empty, i.Photo.PhotoId);
            Assert.True(i.PrepMinutes > 0);
        });

        // The sold-out dish is present and flagged, not hidden.
        var soldOut = items.Single(i => i.Id == world.Menu.SoldOut);

        Assert.False(soldOut.IsAvailable);
        Assert.Equal(2_000L, soldOut.PriceAmd);

        // And that is the whole of what they may do: ordering is refused until the host approves.
        await using var orderDb = fixture.CreateContext(world.Clock);
        var orders = fixture.CreateOrderService(orderDb, world.Clock, TestActor.Participant(pending));

        var refused = await Assert.ThrowsAsync<TabPermissionException>(
            () => orders.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7())));

        Assert.Contains("approved", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ 1. spoken orders and attribution

    [SkippableFact]
    public async Task A_spoken_order_on_a_named_guests_behalf_is_attributed_to_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiter = fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var placed = await waiter.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [new OrderItemInput(world.Menu.Coffee, 1)],
            Guid.CreateVersion7(),
            OnBehalfOfParticipantId: world.GuestId));

        Assert.Equal(world.Branch.WaiterId, placed.PlacedByStaffId);
        Assert.Equal(world.GuestId, placed.OnBehalfOfParticipantId);

        var line = Assert.Single(placed.Lines);
        Assert.False(line.IsTableAttributed);
        Assert.False(line.IsShared);
        Assert.Empty(line.SharedWithParticipantIds);

        // It lands on that guest alone.
        var shares = await SharesAsync(world);

        Assert.Equal(TestMenu.CoffeeAmd, Share(shares, world.GuestId).OwnItemsAmd);
        Assert.Equal(0L, Share(shares, world.HostId).OwnItemsAmd);
    }

    [SkippableFact]
    public async Task A_spoken_order_with_nobody_named_is_attributed_to_the_table_and_split_across_everyone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiter = fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        // The waiter took the order across a noisy table and could not say who asked for it.
        var placed = await waiter.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Wine, 1)], Guid.CreateVersion7()));

        var line = Assert.Single(placed.Lines);

        // Explicit in the data, not incidental: this is where paper-ordering habits show up.
        Assert.True(line.IsTableAttributed);
        Assert.Equal(2, line.SharedWithParticipantIds.Count);

        var shares = await SharesAsync(world);

        // 9,500 two ways is 4,750 each, and neither owns it.
        Assert.Equal(4_750L, Share(shares, world.HostId).SharedItemsAmd);
        Assert.Equal(4_750L, Share(shares, world.GuestId).SharedItemsAmd);
        Assert.Equal(0L, Share(shares, world.HostId).OwnItemsAmd);
    }

    // ------------------------------------------------------------ 1b. who, per line

    /// <summary>
    /// The tablet assigns each line of a spoken round on its own, and the round is still one order.
    /// </summary>
    /// <remarks>
    /// The floor tablet's draft says per line "the table, shared" or a named guest, then sends once.
    /// With only the order-level <c>onBehalfOfParticipantId</c> that was one order per person - three
    /// kitchen tickets for one table's round - or every line on one guest. Each line's own
    /// <c>participantId</c> is what lets one send stay one ticket and every dish land on the person
    /// who ate it: on <c>/shares</c>, on their own lines, on the stream and in their "my share" ask.
    /// </remarks>
    [SkippableFact]
    public async Task One_send_with_a_line_per_guest_is_one_order_and_one_ticket_and_each_line_is_charged_to_its_guest()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiter = fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var placed = await waiter.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [
                new OrderItemInput(world.Menu.Coffee, 1, ParticipantId: world.HostId),
                new OrderItemInput(world.Menu.Khachapuri, 1, ParticipantId: world.GuestId),
                new OrderItemInput(world.Menu.Wine, 1, IsShared: true),
            ],
            Guid.CreateVersion7()));

        Assert.Null(placed.OnBehalfOfParticipantId);

        var coffee = placed.Lines.Single(l => l.MenuItemId == world.Menu.Coffee);
        var khachapuri = placed.Lines.Single(l => l.MenuItemId == world.Menu.Khachapuri);
        var wine = placed.Lines.Single(l => l.MenuItemId == world.Menu.Wine);

        Assert.Equal(world.HostId, coffee.OnBehalfOfParticipantId);
        Assert.Equal(world.GuestId, khachapuri.OnBehalfOfParticipantId);
        Assert.Null(wine.OnBehalfOfParticipantId);

        // A named line is somebody's, so it is neither split nor "we do not know whose".
        Assert.All([coffee, khachapuri], l =>
        {
            Assert.False(l.IsTableAttributed);
            Assert.False(l.IsShared);
            Assert.Empty(l.SharedWithParticipantIds);
        });
        Assert.True(wine.IsShared);
        Assert.False(wine.IsTableAttributed);
        Assert.Equal(2, wine.SharedWithParticipantIds.Count);

        // One send, one order, one ticket on the kitchen screen.
        await using var verify = fixture.CreateContext(world.Clock);
        Assert.Equal(1, await verify.TabOrders.CountAsync(o => o.TabId == world.TabId));

        var queue = await fixture.CreateOrderService(verify, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .GetBranchOrdersAsync(world.Branch.BranchId, null);
        var ticket = Assert.Single(queue);
        Assert.Equal(3, ticket.Lines.Count);
        Assert.Equal(world.GuestId, ticket.Lines.Single(l => l.MenuItemId == world.Menu.Khachapuri).OnBehalfOfParticipantId);

        // Each dish on the person who ate it; the bottle two ways.
        var shares = await SharesAsync(world);

        Assert.Equal(TestMenu.CoffeeAmd, Share(shares, world.HostId).OwnItemsAmd);
        Assert.Equal(TestMenu.KhachapuriAmd, Share(shares, world.GuestId).OwnItemsAmd);
        Assert.Equal(4_750L, Share(shares, world.HostId).SharedItemsAmd);
        Assert.Equal(4_750L, Share(shares, world.GuestId).SharedItemsAmd);

        // The guest's "my share" ask is priced off the same lines.
        await using (var askDb = fixture.CreateContext(world.Clock))
        {
            var ask = await fixture.CreateServiceRequests(askDb, world.Clock, TestActor.Participant(world.GuestId))
                .RaiseAsync(world.TabId, world.GuestId, ServiceRequestPreset.TheBill, null, BillScope.MyShare, 0);

            Assert.Equal(Share(shares, world.GuestId).ShareAmd, ask.BillAmountAmd);
            Assert.Equal(TestMenu.KhachapuriAmd + 4_750L, Share(shares, world.GuestId).PersonalAmd);
        }

        // The tab says the same, line by line: the staff view names each line's guest, and each
        // guest's own lines are their dish and the bottle - not the other one's dish.
        await using var readDb = fixture.CreateContext(world.Clock);
        var tabs = fixture.CreateTabQuery(readDb);

        var staffView = await tabs.GetForStaffAsync(world.TabId);
        Assert.NotNull(staffView);
        Assert.Equal(world.HostId, staffView.Lines.Single(l => l.LineId == coffee.LineId).OnBehalfOfParticipantId);
        Assert.Equal(world.GuestId, staffView.Lines.Single(l => l.LineId == khachapuri.LineId).OnBehalfOfParticipantId);

        var hostsView = await tabs.GetForParticipantAsync(world.TabId, world.HostId);
        var guestsView = await tabs.GetForParticipantAsync(world.TabId, world.GuestId);

        Assert.Equal(
            new[] { coffee.LineId, wine.LineId }.Order(),
            hostsView!.MyLines.Select(l => l.LineId).Order());
        Assert.Equal(
            new[] { khachapuri.LineId, wine.LineId }.Order(),
            guestsView!.MyLines.Select(l => l.LineId).Order());

        // And the stream carries each line's guest, so a phone catching up can attribute it.
        var payload = await verify.TabEvents
            .AsNoTracking()
            .Where(e => e.TabId == world.TabId && e.Type == TabEventType.OrderPlaced)
            .Select(e => e.PayloadJson)
            .SingleAsync();

        using var document = System.Text.Json.JsonDocument.Parse(payload);
        var lines = document.RootElement.GetProperty("lines").EnumerateArray().ToList();

        Assert.Equal(
            world.GuestId,
            lines.Single(l => l.GetProperty("lineId").GetGuid() == khachapuri.LineId)
                .GetProperty("onBehalfOfParticipantId").GetGuid());
        Assert.False(
            lines.Single(l => l.GetProperty("lineId").GetGuid() == wine.LineId)
                .TryGetProperty("onBehalfOfParticipantId", out var wineOwner)
            && wineOwner.ValueKind != System.Text.Json.JsonValueKind.Null,
            "The table's bottle went on the stream in somebody's name.");
    }

    /// <summary>
    /// A line's own guest wins over the order's; the lines that name nobody keep the order's.
    /// </summary>
    [SkippableFact]
    public async Task A_lines_own_guest_overrides_the_orders_and_the_other_lines_keep_the_orders()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiter = fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var placed = await waiter.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [
                new OrderItemInput(world.Menu.Coffee, 1, ParticipantId: world.HostId),
                new OrderItemInput(world.Menu.Khachapuri, 1),
            ],
            Guid.CreateVersion7(),
            OnBehalfOfParticipantId: world.GuestId));

        Assert.Equal(world.GuestId, placed.OnBehalfOfParticipantId);
        Assert.Equal(world.HostId, placed.Lines.Single(l => l.MenuItemId == world.Menu.Coffee).OnBehalfOfParticipantId);
        Assert.Equal(world.GuestId, placed.Lines.Single(l => l.MenuItemId == world.Menu.Khachapuri).OnBehalfOfParticipantId);
        Assert.DoesNotContain(placed.Lines, l => l.IsTableAttributed);

        var shares = await SharesAsync(world);

        Assert.Equal(TestMenu.CoffeeAmd, Share(shares, world.HostId).OwnItemsAmd);
        Assert.Equal(TestMenu.KhachapuriAmd, Share(shares, world.GuestId).OwnItemsAmd);
        Assert.Equal(0L, Share(shares, world.HostId).SharedItemsAmd);
    }

    /// <summary>
    /// A line naming somebody the order-level field would refuse is refused the same way, whole.
    /// </summary>
    /// <remarks>
    /// One check for both fields, so the tablet reads one set of answers: a participant who is not
    /// on this tab - made up, or sitting at another table - is not found, and one still waiting on
    /// the host cannot have an order put in their name yet. The send is refused whole, for the
    /// sold-out dish's reason: a partial round is a decision made on the table's behalf.
    /// </remarks>
    [SkippableFact]
    public async Task A_line_for_somebody_the_order_level_field_refuses_is_refused_the_same_way_and_nothing_is_placed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var pending = await JoinWithoutApprovalAsync(world, "phone-pending", "Vahe");
        var elsewhere = await ParticipantAtAnotherTableAsync(world);

        foreach (var (who, participantId) in new[]
                 {
                     ("nobody", Guid.CreateVersion7()),
                     ("a guest at another table", elsewhere),
                     ("a guest the host has not let on", pending),
                 })
        {
            await using var orderLevelDb = fixture.CreateContext(world.Clock);
            var orderLevel = await Record.ExceptionAsync(
                () => fixture.CreateOrderService(orderLevelDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
                    .PlaceOrderAsync(new PlaceOrderCommand(
                        world.TabId,
                        [new OrderItemInput(world.Menu.Coffee, 1)],
                        Guid.CreateVersion7(),
                        OnBehalfOfParticipantId: participantId)));

            await using var lineLevelDb = fixture.CreateContext(world.Clock);
            var lineLevel = await Record.ExceptionAsync(
                () => fixture.CreateOrderService(lineLevelDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
                    .PlaceOrderAsync(new PlaceOrderCommand(
                        world.TabId,
                        [
                            new OrderItemInput(world.Menu.Khachapuri, 1, ParticipantId: world.GuestId),
                            new OrderItemInput(world.Menu.Coffee, 1, ParticipantId: participantId),
                        ],
                        Guid.CreateVersion7())));

            Assert.NotNull(orderLevel);
            Assert.NotNull(lineLevel);
            Assert.True(
                orderLevel.GetType() == lineLevel.GetType() && orderLevel.Message == lineLevel.Message,
                $"Naming {who} on a line was answered with {lineLevel.GetType().Name} ({lineLevel.Message}), "
                + $"and on the order with {orderLevel.GetType().Name} ({orderLevel.Message}).");
            if (participantId == pending)
            {
                var refusal = Assert.IsType<NamedGuestNotOrderableException>(lineLevel);
                Assert.Equal(ParticipantStatus.PendingApproval, refusal.ParticipantStatus);
                Assert.Equal("items[1].participantId", refusal.Field);
                Assert.Equal("onBehalfOfParticipantId", ((NamedGuestNotOrderableException)orderLevel).Field);
            }
            else
            {
                var refusal = Assert.IsType<NamedGuestNotOnTabException>(lineLevel);
                Assert.Equal("items[1].participantId", refusal.Field);
                Assert.Equal(participantId, refusal.ParticipantId);
                Assert.Equal("onBehalfOfParticipantId", ((NamedGuestNotOnTabException)orderLevel).Field);
            }
        }

        await using var verify = fixture.CreateContext(world.Clock);
        Assert.False(await verify.TabOrders.AnyAsync(o => o.TabId == world.TabId));
    }

    /// <summary>
    /// A shared line has no owner, so a guest named on one is refused rather than either dropped.
    /// </summary>
    /// <remarks>
    /// The rule the order-level field already follows: <c>onBehalfOfParticipantId</c> is the owner of
    /// the lines that have one, and a shared line is not one of them - an order for Narine with the
    /// table's bottle on it splits the bottle. A line that says both "the table's" and "Narine's" is
    /// a contradiction in one line, and quietly keeping either half bills somebody for what the
    /// waiter did not mean, so it is a 400 that names the line.
    /// </remarks>
    [SkippableFact]
    public async Task A_shared_line_naming_a_guest_is_refused_by_line_and_the_orders_guest_still_skips_a_shared_line()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiter = fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => waiter.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId,
                [
                    new OrderItemInput(world.Menu.Coffee, 1, ParticipantId: world.HostId),
                    new OrderItemInput(world.Menu.Wine, 1, IsShared: true, ParticipantId: world.GuestId),
                ],
                Guid.CreateVersion7())));

        Assert.Equal("items[1].participantId", refused.ParamName);

        await using (var verify = fixture.CreateContext(world.Clock))
        {
            Assert.False(await verify.TabOrders.AnyAsync(o => o.TabId == world.TabId));
        }

        // The order-level guest and a shared line, as before: the bottle splits, the coffee is theirs.
        await using var orderDb = fixture.CreateContext(world.Clock);
        await fixture.CreateOrderService(orderDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId,
                [new OrderItemInput(world.Menu.Coffee, 1), new OrderItemInput(world.Menu.Wine, 1, IsShared: true)],
                Guid.CreateVersion7(),
                OnBehalfOfParticipantId: world.GuestId));

        var shares = await SharesAsync(world);

        Assert.Equal(TestMenu.CoffeeAmd, Share(shares, world.GuestId).OwnItemsAmd);
        Assert.Equal(4_750L, Share(shares, world.GuestId).SharedItemsAmd);
        Assert.Equal(4_750L, Share(shares, world.HostId).SharedItemsAmd);
    }

    /// <summary>
    /// A phone cannot put a line in anybody's name, its own included, exactly as it cannot an order.
    /// </summary>
    [SkippableFact]
    public async Task A_diner_cannot_put_a_line_in_anybodys_name()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        foreach (var named in new[] { world.HostId, world.GuestId })
        {
            await using var orderDb = fixture.CreateContext(world.Clock);
            var diner = fixture.CreateOrderService(orderDb, world.Clock, TestActor.Participant(world.GuestId));

            var refused = await Assert.ThrowsAsync<TabPermissionException>(
                () => diner.PlaceOrderAsync(new PlaceOrderCommand(
                    world.TabId,
                    [new OrderItemInput(world.Menu.Coffee, 1, ParticipantId: named)],
                    Guid.CreateVersion7())));

            Assert.Contains("staff", refused.Message, StringComparison.Ordinal);
        }

        await using var verify = fixture.CreateContext(world.Clock);
        Assert.False(await verify.TabOrders.AnyAsync(o => o.TabId == world.TabId));
    }

    /// <summary>
    /// A send retried after a lost answer comes back as the original, each line still on its guest.
    /// </summary>
    /// <remarks>
    /// The tablet queues per device and resends under whoever is signed in, so the retry can carry a
    /// different body - a draft edited in the meantime. The command id is the order; the retry is
    /// answered with what was placed, and nothing is placed twice or moved between guests.
    /// </remarks>
    [SkippableFact]
    public async Task A_replayed_send_with_a_line_per_guest_answers_with_the_original_lines_and_owners()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var commandId = Guid.CreateVersion7();

        var first = await fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId,
                [
                    new OrderItemInput(world.Menu.Coffee, 1, ParticipantId: world.HostId),
                    new OrderItemInput(world.Menu.Khachapuri, 1, ParticipantId: world.GuestId),
                ],
                commandId));

        // The retry, from another waiter, with the guests swapped.
        await using var retryDb = fixture.CreateContext(world.Clock);
        var colleague = await AnotherWaiterAsync(retryDb, world);

        var again = await fixture.CreateOrderService(retryDb, world.Clock, TestActor.Waiter(colleague))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId,
                [
                    new OrderItemInput(world.Menu.Coffee, 1, ParticipantId: world.GuestId),
                    new OrderItemInput(world.Menu.Khachapuri, 1, ParticipantId: world.HostId),
                ],
                commandId));

        Assert.True(again.WasReplay);
        Assert.Equal(first.OrderId, again.OrderId);
        Assert.Equal(
            first.Lines.Select(l => (l.LineId, l.OnBehalfOfParticipantId)).Order(),
            again.Lines.Select(l => (l.LineId, l.OnBehalfOfParticipantId)).Order());
        Assert.Equal(world.HostId, again.Lines.Single(l => l.MenuItemId == world.Menu.Coffee).OnBehalfOfParticipantId);

        await using var verify = fixture.CreateContext(world.Clock);
        Assert.Equal(1, await verify.TabOrders.CountAsync(o => o.TabId == world.TabId));

        var shares = await SharesAsync(world);
        Assert.Equal(TestMenu.CoffeeAmd, Share(shares, world.HostId).OwnItemsAmd);
        Assert.Equal(TestMenu.KhachapuriAmd, Share(shares, world.GuestId).OwnItemsAmd);
    }

    /// <summary>
    /// A phone naming a guest on a shared line is refused as a phone, and a waiter's retry whose
    /// body has become contradictory is answered with the order it already placed.
    /// </summary>
    /// <remarks>
    /// The shared-line check is about what a waiter may send, so it runs after the two questions
    /// that come first: is this a retry (then the original is the answer, whatever the body says now),
    /// and is this a phone (then naming anybody at all is the refusal - 403, as the wire says).
    /// </remarks>
    [SkippableFact]
    public async Task A_phone_naming_a_guest_on_a_shared_line_is_refused_as_a_phone_and_a_retry_gets_its_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        await using (var phoneDb = fixture.CreateContext(world.Clock))
        {
            await Assert.ThrowsAsync<TabPermissionException>(
                () => fixture.CreateOrderService(phoneDb, world.Clock, TestActor.Participant(world.GuestId))
                    .PlaceOrderAsync(new PlaceOrderCommand(
                        world.TabId,
                        [new OrderItemInput(world.Menu.Wine, 1, IsShared: true, ParticipantId: world.GuestId)],
                        Guid.CreateVersion7())));
        }

        var commandId = Guid.CreateVersion7();

        var first = await fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Wine, 1, IsShared: true)], commandId));

        await using var retryDb = fixture.CreateContext(world.Clock);
        var again = await fixture.CreateOrderService(retryDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId,
                [new OrderItemInput(world.Menu.Wine, 1, IsShared: true, ParticipantId: world.HostId)],
                commandId));

        Assert.True(again.WasReplay);
        Assert.Equal(first.OrderId, again.OrderId);
    }

    /// <summary>
    /// A shared line is nobody's on every read, whoever the order was for.
    /// </summary>
    /// <remarks>
    /// <see cref="TabOrder.OwnerOf"/> already said so for the money; the per-line
    /// <c>onBehalfOfParticipantId</c> used to repeat the order's guest on a shared line, and the
    /// diner app reads that field as "mine" - so the order's guest saw the table's bottle flagged as
    /// theirs. Every read now says what the bill does: the coffee is the guest's, the bottle nobody's.
    /// </remarks>
    [SkippableFact]
    public async Task A_shared_line_is_nobodys_on_every_read_whoever_the_order_was_for()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var placed = await fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId,
                [new OrderItemInput(world.Menu.Coffee, 1), new OrderItemInput(world.Menu.Wine, 1, IsShared: true)],
                Guid.CreateVersion7(),
                OnBehalfOfParticipantId: world.GuestId));

        var coffee = placed.Lines.Single(l => l.MenuItemId == world.Menu.Coffee);
        var wine = placed.Lines.Single(l => l.MenuItemId == world.Menu.Wine);

        Assert.Equal(world.GuestId, coffee.OnBehalfOfParticipantId);
        Assert.Null(wine.OnBehalfOfParticipantId);

        await using var readDb = fixture.CreateContext(world.Clock);
        var ticket = Assert.Single(
            await fixture.CreateOrderService(readDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
                .GetBranchOrdersAsync(world.Branch.BranchId, null));
        Assert.Null(ticket.Lines.Single(l => l.LineId == wine.LineId).OnBehalfOfParticipantId);

        var staffView = await fixture.CreateTabQuery(readDb).GetForStaffAsync(world.TabId);
        Assert.Null(staffView!.Lines.Single(l => l.LineId == wine.LineId).OnBehalfOfParticipantId);
        Assert.Equal(world.GuestId, staffView.Lines.Single(l => l.LineId == coffee.LineId).OnBehalfOfParticipantId);

        var payload = await readDb.TabEvents
            .AsNoTracking()
            .Where(e => e.TabId == world.TabId && e.Type == TabEventType.OrderPlaced)
            .Select(e => e.PayloadJson)
            .SingleAsync();

        using var document = System.Text.Json.JsonDocument.Parse(payload);
        var wineOnTheStream = document.RootElement.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("lineId").GetGuid() == wine.LineId);
        Assert.False(
            wineOnTheStream.TryGetProperty("onBehalfOfParticipantId", out var owner)
            && owner.ValueKind != System.Text.Json.JsonValueKind.Null,
            "The table's bottle went on the stream in the order's guest's name.");

        // The money never changed: the bottle splits, the coffee is the guest's.
        var shares = await SharesAsync(world);
        Assert.Equal(TestMenu.CoffeeAmd, Share(shares, world.GuestId).OwnItemsAmd);
        Assert.Equal(4_750L, Share(shares, world.GuestId).SharedItemsAmd);
    }

    /// <summary>
    /// A guest the host took off the tab is refused by name as taken off, not as "not on yet".
    /// </summary>
    [SkippableFact]
    public async Task A_line_for_a_guest_taken_off_the_tab_is_refused_as_taken_off()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        await using (var hostDb = fixture.CreateContext(world.Clock))
        {
            await fixture.CreateTabService(hostDb, world.Clock, TestActor.Participant(world.HostId))
                .RemoveParticipantAsync(world.TabId, world.HostId, world.GuestId);
        }

        var refused = await Assert.ThrowsAsync<NamedGuestNotOrderableException>(
            () => fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
                .PlaceOrderAsync(new PlaceOrderCommand(
                    world.TabId,
                    [new OrderItemInput(world.Menu.Coffee, 1, ParticipantId: world.GuestId)],
                    Guid.CreateVersion7())));

        Assert.Contains("taken off the tab", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ParticipantStatus.Removed, refused.ParticipantStatus);
        Assert.Equal(world.GuestId, refused.ParticipantId);
        Assert.Equal("items[0].participantId", refused.Field);
        Assert.DoesNotContain("not on the tab yet", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A comp or a void on a line named for a guest moves that guest's share and nobody else's.
    /// </summary>
    [SkippableFact]
    public async Task A_comp_or_void_on_a_named_line_moves_that_guests_share_only()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var placed = await fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId,
                [
                    new OrderItemInput(world.Menu.Coffee, 1, ParticipantId: world.HostId),
                    new OrderItemInput(world.Menu.Khachapuri, 1, ParticipantId: world.GuestId),
                ],
                Guid.CreateVersion7()));

        var coffee = placed.Lines.Single(l => l.MenuItemId == world.Menu.Coffee);
        var khachapuri = placed.Lines.Single(l => l.MenuItemId == world.Menu.Khachapuri);

        await using (var managerDb = fixture.CreateContext(world.Clock))
        {
            await fixture.CreateOrderService(managerDb, world.Clock, TestActor.Manager(world.Branch.ManagerId))
                .AddAdjustmentAsync(new AddAdjustmentCommand(
                    world.TabId, khachapuri.LineId, AdjustmentKind.Comp, null, 1_000L, "it arrived cold",
                    Guid.CreateVersion7()));
        }

        var afterComp = await SharesAsync(world);
        Assert.Equal(TestMenu.KhachapuriAmd - 1_000L, Share(afterComp, world.GuestId).OwnItemsAmd);
        Assert.Equal(TestMenu.CoffeeAmd, Share(afterComp, world.HostId).OwnItemsAmd);

        await using (var waiterDb = fixture.CreateContext(world.Clock))
        {
            await fixture.CreateOrderService(waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
                .VoidLineAsync(new VoidLineCommand(world.TabId, coffee.LineId, "changed their mind", Guid.CreateVersion7()));
        }

        var afterVoid = await SharesAsync(world);
        Assert.Equal(0L, Share(afterVoid, world.HostId).OwnItemsAmd);
        Assert.Equal(TestMenu.KhachapuriAmd - 1_000L, Share(afterVoid, world.GuestId).OwnItemsAmd);
    }

    // ------------------------------------------------------------ 2. a sold-out dish

    [SkippableFact]
    public async Task An_unavailable_item_is_refused_by_name_and_nothing_else_on_the_order_is_placed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var refused = await Assert.ThrowsAsync<MenuItemUnavailableException>(
            () => diner.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId,
                [
                    new OrderItemInput(world.Menu.Coffee, 2),
                    new OrderItemInput(world.Menu.SoldOut, 1),
                    new OrderItemInput(world.Menu.Khachapuri, 1),
                ],
                Guid.CreateVersion7())));

        // Named, so the client can say which dish rather than failing the order generically.
        Assert.Equal(world.Menu.SoldOut, refused.MenuItemId);
        Assert.Contains("Lamb kebab", refused.Message);

        // All-or-nothing. A partial order is a decision made on the diner's behalf that they find
        // out about when the food arrives.
        await using var verify = fixture.CreateContext(world.Clock);
        Assert.False(await verify.TabOrders.AnyAsync(o => o.TabId == world.TabId));
    }

    // ------------------------------------------------------------ 3. the shared-line snapshot

    [SkippableFact]
    public async Task A_shared_line_snapshots_who_was_there_and_a_later_joiner_is_absent_from_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var host = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var bottle = await host.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [new OrderItemInput(world.Menu.Wine, 1, IsShared: true)],
            Guid.CreateVersion7()));

        var shared = Assert.Single(bottle.Lines);
        Assert.Equal(2, shared.SharedWithParticipantIds.Count);

        // The friend who was fifteen minutes late.
        var latecomer = await JoinAndApproveAsync(world, "phone-late", "Nare");

        Assert.DoesNotContain(latecomer, shared.SharedWithParticipantIds);

        var shares = await SharesAsync(world);

        // Still two ways, not three: they were not there when it was poured.
        Assert.Equal(4_750L, Share(shares, world.HostId).SharedItemsAmd);
        Assert.Equal(4_750L, Share(shares, world.GuestId).SharedItemsAmd);
        Assert.Equal(0L, Share(shares, latecomer).SharedItemsAmd);
    }

    // ------------------------------------------------------------ 4. a tab that is closing

    [SkippableFact]
    public async Task Ordering_on_a_closing_tab_is_refused_with_its_own_error()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        await using (var staffDb = fixture.CreateContext(world.Clock))
        {
            var tabs = fixture.CreateTabService(staffDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));
            await tabs.BeginClosingAsync(world.TabId);
        }

        await using var ordererDb = fixture.CreateContext(world.Clock);
        var diner = fixture.CreateOrderService(ordererDb, world.Clock, TestActor.Participant(world.HostId));

        var refused = await Assert.ThrowsAsync<TabNotAcceptingOrdersException>(
            () => diner.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7())));

        Assert.Equal(TabStatus.Closing, refused.Status);
        Assert.Contains("bill has been asked for", refused.Message);
    }

    // ------------------------------------------------------------ 5. the double tap

    [SkippableFact]
    public async Task A_replayed_order_command_returns_the_original_and_creates_no_second_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));
        var commandId = Guid.CreateVersion7();

        var first = await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 2)], commandId));

        Assert.False(first.WasReplay);

        // The diner saw nothing happen and tapped again, on a fresh request.
        await using var secondDb = fixture.CreateContext(world.Clock);
        var again = fixture.CreateOrderService(secondDb, world.Clock, TestActor.Participant(world.HostId));

        var second = await again.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 2)], commandId));

        Assert.True(second.WasReplay);
        Assert.Equal(first.OrderId, second.OrderId);

        await using var verify = fixture.CreateContext(world.Clock);

        Assert.Equal(1, await verify.TabOrders.CountAsync(o => o.TabId == world.TabId));
        Assert.Equal(2 * TestMenu.KhachapuriAmd, (await TabAsync(verify, world.TabId)).SubtotalAmd);
    }

    // ------------------------------------------------------------ command ids, and what a phone may order

    /// <summary>
    /// A command id another tab already used is not a replay of that tab's order.
    /// </summary>
    /// <remarks>
    /// The replay lookup matched the command id alone, across every tab there is. So a second tab
    /// presenting the same id got the first tab's order back, totals and all, and its own order
    /// was silently never placed.
    /// </remarks>
    [SkippableFact]
    public async Task A_command_id_from_another_tab_places_this_tabs_own_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var theirs = await ArrangeAsync();
        await using var theirDb = theirs.Db;
        var mine = await ArrangeAsync();
        await using var db = mine.Db;

        var commandId = Guid.CreateVersion7();

        var theirOrder = await fixture.CreateOrderService(theirDb, theirs.Clock, TestActor.Participant(theirs.HostId))
            .PlaceOrderAsync(new PlaceOrderCommand(theirs.TabId, [new OrderItemInput(theirs.Menu.Wine, 1)], commandId));

        var myOrder = await fixture.CreateOrderService(db, mine.Clock, TestActor.Participant(mine.HostId))
            .PlaceOrderAsync(new PlaceOrderCommand(mine.TabId, [new OrderItemInput(mine.Menu.Coffee, 1)], commandId));

        Assert.Equal(mine.TabId, myOrder.TabId);
        Assert.False(myOrder.WasReplay);
        Assert.NotEqual(theirOrder.OrderId, myOrder.OrderId);
    }

    /// <summary>
    /// Somebody else's command id on the same tab is refused rather than answered with their order.
    /// </summary>
    [SkippableFact]
    public async Task Another_participants_command_id_on_the_same_tab_is_refused_rather_than_replayed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;
        var commandId = Guid.CreateVersion7();

        await fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId))
            .PlaceOrderAsync(new PlaceOrderCommand(world.TabId, [new OrderItemInput(world.Menu.Wine, 1)], commandId));

        await using var guestDb = fixture.CreateContext(world.Clock);

        await Assert.ThrowsAsync<ClientCommandIdAlreadyUsedException>(
            () => fixture.CreateOrderService(guestDb, world.Clock, TestActor.Participant(world.GuestId))
                .PlaceOrderAsync(new PlaceOrderCommand(
                    world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], commandId)));

        await using var verify = fixture.CreateContext(world.Clock);

        Assert.Equal(1, await verify.TabOrders.CountAsync(o => o.TabId == world.TabId));
    }

    /// <summary>
    /// A waiter's order replayed under another waiter's sign-in answers with the original order.
    /// </summary>
    /// <remarks>
    /// The tablet queues a spoken order per device, not per waiter, and sends it under whoever is
    /// signed in when the connection returns. Waiter A's order reached the kitchen, the answer was
    /// lost, and waiter B unlocked the tablet with a PIN. The replay matched the staff member as well
    /// as the tab, so B's send got 409, the queue filed it as refused, and the natural next step was
    /// keying the order in again - and the kitchen cooking it twice.
    /// </remarks>
    [SkippableFact]
    public async Task A_staff_order_replayed_under_another_waiters_sign_in_returns_the_original()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiterB = new StaffMember(
            world.Branch.VenueId,
            "Second Waiter",
            $"+3743{Guid.NewGuid().ToString("N")[..7]}",
            StaffRole.Waiter,
            "hash",
            world.Branch.BranchId);

        db.StaffMembers.Add(waiterB);
        await db.SaveChangesAsync();

        var command = new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 1)], Guid.CreateVersion7());

        var first = await fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(command);

        await using var tabletDb = fixture.CreateContext(world.Clock);

        var replayed = await fixture.CreateOrderService(tabletDb, world.Clock, TestActor.Waiter(waiterB.Id))
            .PlaceOrderAsync(command);

        Assert.True(replayed.WasReplay);
        Assert.Equal(first.OrderId, replayed.OrderId);

        await using var verify = fixture.CreateContext(world.Clock);

        Assert.Equal(1, await verify.TabOrders.CountAsync(o => o.TabId == world.TabId));
    }

    /// <summary>
    /// A waiter presenting a phone's command id on the same tab is refused rather than replayed.
    /// </summary>
    /// <remarks>
    /// A staff replay matches any staff-placed order on the tab, whichever waiter placed it. It must
    /// stay a staff rule: a diner's order is not the answer to a waiter's command.
    /// </remarks>
    [SkippableFact]
    public async Task A_waiter_presenting_a_diners_command_id_is_refused_rather_than_replayed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;
        var commandId = Guid.CreateVersion7();

        await fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId))
            .PlaceOrderAsync(new PlaceOrderCommand(world.TabId, [new OrderItemInput(world.Menu.Wine, 1)], commandId));

        await using var staffDb = fixture.CreateContext(world.Clock);

        await Assert.ThrowsAsync<ClientCommandIdAlreadyUsedException>(
            () => fixture.CreateOrderService(staffDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
                .PlaceOrderAsync(new PlaceOrderCommand(
                    world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], commandId)));

        await using var verify = fixture.CreateContext(world.Clock);

        Assert.Equal(1, await verify.TabOrders.CountAsync(o => o.TabId == world.TabId));
    }

    /// <summary>
    /// Another branch's waiter presenting a staff command id is refused by branch, not answered with
    /// the order.
    /// </summary>
    /// <remarks>
    /// A staff replay no longer asks which waiter placed the order, so the only thing between that
    /// order and a stranger holding its tab id and command id is the branch check. The service ran
    /// that check on a new order only, after the replay lookup had already answered.
    /// </remarks>
    [SkippableFact]
    public async Task A_staff_command_id_from_another_branchs_waiter_is_refused_by_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;
        var elsewhere = await TestBranchBuilder.CreateAsync(db);

        var command = new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 1)], Guid.CreateVersion7());

        await fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(command);

        await using var strangerDb = fixture.CreateContext(world.Clock);

        await Assert.ThrowsAsync<StaffBranchScopeException>(
            () => fixture.CreateOrderService(strangerDb, world.Clock, TestActor.Waiter(elsewhere.WaiterId))
                .PlaceOrderAsync(command));
    }

    /// <summary>
    /// Two requests with one command id that race past the replay check together place one order.
    /// </summary>
    /// <remarks>
    /// The replay check is a read followed by an insert. A retry sent after a timeout, while the
    /// first attempt is still in flight, passes the read on both - and with nothing unique behind it
    /// both inserts land and the kitchen cooks the order twice. The interceptor makes the race
    /// happen every run rather than on the unlucky evening: the second request commits in full
    /// between the first one's check and its insert.
    /// </remarks>
    [SkippableFact]
    public async Task Two_requests_racing_with_one_command_id_place_one_order()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var command = new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 1)], Guid.CreateVersion7());

        OrderView? overtaker = null;

        var race = new BeforeFirstInsertInto("TabOrders", async () =>
        {
            await using var otherDb = fixture.CreateContext(world.Clock);

            overtaker = await fixture.CreateOrderService(otherDb, world.Clock, TestActor.Participant(world.HostId))
                .PlaceOrderAsync(command);
        });

        await using var racingDb = fixture.CreateContext(world.Clock, race);

        var late = await fixture.CreateOrderService(racingDb, world.Clock, TestActor.Participant(world.HostId))
            .PlaceOrderAsync(command);

        Assert.True(race.Fired, "The race never happened, so this proves nothing.");
        Assert.NotNull(overtaker);

        await using var verify = fixture.CreateContext(world.Clock);

        Assert.Equal(1, await verify.TabOrders.CountAsync(o => o.TabId == world.TabId));
        Assert.Equal(overtaker.OrderId, late.OrderId);
        Assert.True(late.WasReplay);
    }

    /// <summary>
    /// An item the diner menu does not publish cannot be ordered from a phone; a waiter can still
    /// key it in.
    /// </summary>
    /// <remarks>
    /// The menu read drops an unfinished item - no photo, no allergens - but the order endpoint only
    /// checked availability, so anybody holding its id could order it from the table.
    /// </remarks>
    [SkippableFact]
    public async Task An_unfinished_item_is_refused_to_a_diner_and_still_orderable_by_staff()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var unfinished = (await fixture.CreateMenuService(db).CreateItemAsync(
            world.Branch.BranchId, world.Menu.CategoryId, new CreateMenuItemCommand("Chef's mystery", 4_500L))).Id;

        await using (var dinerDb = fixture.CreateContext(world.Clock))
        {
            var refused = await Assert.ThrowsAsync<MenuItemUnavailableException>(
                () => fixture.CreateOrderService(dinerDb, world.Clock, TestActor.Participant(world.HostId))
                    .PlaceOrderAsync(new PlaceOrderCommand(
                        world.TabId,
                        [new OrderItemInput(world.Menu.Coffee, 1), new OrderItemInput(unfinished, 1)],
                        Guid.CreateVersion7())));

            Assert.Equal(unfinished, refused.MenuItemId);
            Assert.Equal("Chef's mystery", refused.ItemName);
        }

        await using (var verify = fixture.CreateContext(world.Clock))
        {
            // Refused whole, like a sold-out dish: the coffee did not go through on its own.
            Assert.Equal(0, await verify.TabOrders.CountAsync(o => o.TabId == world.TabId));
        }

        await using var staffDb = fixture.CreateContext(world.Clock);

        var keyedIn = await fixture.CreateOrderService(staffDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(new PlaceOrderCommand(world.TabId, [new OrderItemInput(unfinished, 1)], Guid.CreateVersion7()));

        Assert.False(keyedIn.WasReplay);
        Assert.Equal(unfinished, Assert.Single(keyedIn.Lines).MenuItemId);
    }

    // ------------------------------------------------------------ 6 and 7. voids

    [SkippableFact]
    public async Task A_voided_line_stays_on_the_bill_labelled_and_the_total_drops()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var order = await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [new OrderItemInput(world.Menu.Coffee, 1), new OrderItemInput(world.Menu.Khachapuri, 1)],
            Guid.CreateVersion7()));

        var doomed = order.Lines.First(l => l.Name.Contains("khachapuri", StringComparison.OrdinalIgnoreCase));

        await using var waiterDb = fixture.CreateContext(world.Clock);
        var waiter = fixture.CreateOrderService(waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var after = await waiter.VoidLineAsync(
            new VoidLineCommand(world.TabId, doomed.LineId, "sent to the wrong table", Guid.CreateVersion7()));

        // Still there, and the diner can see it - nothing silently disappears from a bill somebody
        // is watching on their phone.
        var voided = after.Lines.Single(l => l.LineId == doomed.LineId);

        Assert.Equal(0L, voided.LineTotalAmd);
        Assert.Equal(2, after.Lines.Count);

        await using var verify = fixture.CreateContext(world.Clock);

        var row = await verify.TabOrderLines.AsNoTracking().FirstAsync(l => l.Id == doomed.LineId);

        Assert.True(row.IsVoided);
        Assert.Equal("sent to the wrong table", row.VoidReason);
        Assert.Equal(world.Branch.WaiterId, row.VoidedByStaffId);

        // And the money is right: only the coffee is left.
        var tab = await TabAsync(verify, world.TabId);

        Assert.Equal(TestMenu.CoffeeAmd, tab.SubtotalAmd);
        Assert.Equal(120L, tab.ServiceChargeAmd);
        Assert.Equal(1_320L, tab.TotalAmd);
    }

    [SkippableFact]
    public async Task Voiding_a_line_on_a_tab_that_has_been_paid_against_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var order = await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Coffee, 2)], Guid.CreateVersion7()));

        await using (var payDb = fixture.CreateContext(world.Clock))
        {
            var payments = fixture.CreatePaymentService(
                payDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

            await payments.RecordCashAsync(
                new RecordCashPaymentCommand(world.TabId, 1_000L, null, 0L, Guid.CreateVersion7()));
        }

        await using var waiterDb = fixture.CreateContext(world.Clock);
        var waiter = fixture.CreateOrderService(waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var refused = await Assert.ThrowsAsync<LineAlreadyPaidException>(
            () => waiter.VoidLineAsync(new VoidLineCommand(
                world.TabId, order.Lines[0].LineId, "changed their mind", Guid.CreateVersion7())));

        Assert.Contains("refund", refused.Message);
    }

    // ------------------------------------------------------------ 8. a comp, end to end

    [SkippableFact]
    public async Task A_comp_reduces_the_service_charge_with_the_dish_it_takes_off()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var diner = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var order = await diner.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [new OrderItemInput(world.Menu.Coffee, 1), new OrderItemInput(world.Menu.Khachapuri, 1)],
            Guid.CreateVersion7()));

        // 1,200 + 3,200 = 4,400, service 440.
        await using var verifyBefore = fixture.CreateContext(world.Clock);
        var before = await TabAsync(verifyBefore, world.TabId);

        Assert.Equal(4_400L, before.SubtotalAmd);
        Assert.Equal(440L, before.ServiceChargeAmd);

        var khachapuri = order.Lines.First(l => l.UnitPriceAmd == TestMenu.KhachapuriAmd);

        await using var managerDb = fixture.CreateContext(world.Clock);
        var manager = fixture.CreateOrderService(
            managerDb, world.Clock, TestActor.Manager(world.Branch.ManagerId));

        var comp = await manager.AddAdjustmentAsync(new AddAdjustmentCommand(
            world.TabId, khachapuri.LineId, AdjustmentKind.Comp, null, TestMenu.KhachapuriAmd,
            "it arrived cold", Guid.CreateVersion7()));

        Assert.Equal(TestMenu.KhachapuriAmd, comp.ReductionAmd);

        await using var verifyAfter = fixture.CreateContext(world.Clock);
        var after = await TabAsync(verifyAfter, world.TabId);

        // The service charge came off with it. Charging service on a dish you have just apologised
        // for is the opposite of an apology.
        Assert.Equal(1_200L, after.SubtotalAmd);
        Assert.Equal(120L, after.ServiceChargeAmd);
        Assert.Equal(1_320L, after.TotalAmd);
    }

    [SkippableFact]
    public async Task A_waiter_cannot_comp_a_dish()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var waiter = fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        var refused = await Assert.ThrowsAsync<StaffPermissionException>(
            () => waiter.AddAdjustmentAsync(new AddAdjustmentCommand(
                world.TabId, null, AdjustmentKind.Discount, 10m, null, "a regular", Guid.CreateVersion7())));

        Assert.Equal(StaffRole.Manager, refused.RequiredRole);
        Assert.Equal(StaffRole.Waiter, refused.ActualRole);
    }

    // ------------------------------------------------------------ 11. the cache agrees with the lines

    /// <summary>
    /// <b>Test 11.</b> A complex tab, recomputed from its rows in a fresh context, equals the
    /// cached columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cached totals are a denormalisation, exactly like <c>DiningTable.Status</c>: the
    /// authoritative number is the sum of the lines. This is the test that says so, and the one
    /// that fails if a future mutation forgets to recompute.
    /// </para>
    /// <para>
    /// It matters more since the cache moved out of the order-insert transaction, not less. Orders
    /// now refresh it in a second transaction afterwards while voids and adjustments still write it
    /// inline, so this exercises a tab built by both kinds of write and insists they end up
    /// agreeing.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Recomputing_a_complex_tab_from_its_lines_equals_the_cached_totals()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var host = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var first = await host.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId,
            [
                new OrderItemInput(world.Menu.Coffee, 3),
                new OrderItemInput(world.Menu.Wine, 1, IsShared: true),
            ],
            Guid.CreateVersion7()));

        await using (var guestDb = fixture.CreateContext(world.Clock))
        {
            var guest = fixture.CreateOrderService(guestDb, world.Clock, TestActor.Participant(world.GuestId));

            await guest.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 2)], Guid.CreateVersion7()));
        }

        await using (var waiterDb = fixture.CreateContext(world.Clock))
        {
            var waiter = fixture.CreateOrderService(
                waiterDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

            await waiter.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));

            await waiter.VoidLineAsync(new VoidLineCommand(
                world.TabId, first.Lines[0].LineId, "wrong order", Guid.CreateVersion7()));
        }

        await using (var managerDb = fixture.CreateContext(world.Clock))
        {
            var manager = fixture.CreateOrderService(
                managerDb, world.Clock, TestActor.Manager(world.Branch.ManagerId));

            await manager.AddAdjustmentAsync(new AddAdjustmentCommand(
                world.TabId, null, AdjustmentKind.Discount, 12.5m, null, "a regular",
                Guid.CreateVersion7()));
        }

        // Recompute from the rows, in a context that has never seen the cache written.
        await using var fresh = fixture.CreateContext(world.Clock);

        var ledger = fixture.CreateLedger(fresh, world.Clock, TestActor.Waiter(world.Branch.WaiterId));
        var tab = await ledger.LoadForWriteAsync(world.TabId, default);
        var recomputed = ledger.Compute(tab);

        Assert.Equal(tab.SubtotalAmd, recomputed.SubtotalAmd);
        Assert.Equal(tab.ServiceChargeAmd, recomputed.ServiceChargeAmd);
        Assert.Equal(tab.TotalAmd, recomputed.TotalAmd);
        Assert.Equal(tab.PaidAmd, recomputed.PaidAmd);
        Assert.Equal(tab.RemainingAmd, recomputed.RemainingAmd);

        // And it is a real bill, not an accidentally empty one.
        Assert.True(recomputed.TotalAmd > 0L);
        Assert.Equal(recomputed.TotalAmd, recomputed.Shares.Sum(s => s.ShareAmd));
    }

    // ------------------------------------------------------------ 11. a guest without the total

    [SkippableFact]
    public async Task A_guest_without_the_table_total_gets_their_own_share_and_no_aggregate()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var host = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        await host.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 2)], Guid.CreateVersion7()));

        await using (var guestDb = fixture.CreateContext(world.Clock))
        {
            var guest = fixture.CreateOrderService(guestDb, world.Clock, TestActor.Participant(world.GuestId));

            await guest.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));
        }

        // The host turns the total off for their guest.
        await using (var hostDb = fixture.CreateContext(world.Clock))
        {
            var tabs = fixture.CreateTabService(hostDb, world.Clock, TestActor.Participant(world.HostId));

            await tabs.SetPermissionsAsync(
                world.TabId, world.HostId, world.GuestId,
                new SetParticipantPermissionsCommand(CanOrder: true, CanSeeTableTotal: false, CanPay: false));
        }

        await using var readDb = fixture.CreateContext(world.Clock);
        var billing = fixture.CreateBillingQuery(readDb, world.Clock, TestActor.Participant(world.GuestId));

        var view = await billing.GetSharesAsync(world.TabId, world.GuestId);

        // Their own number is always there - "I didn't order that" is settled by them being able to
        // see what they did order.
        Assert.NotNull(view.MyShare);
        Assert.Equal(TestMenu.CoffeeAmd, view.MyShare!.OwnItemsAmd);

        // And the aggregate is ABSENT, not zeroed. A zero reads as "nothing owed".
        Assert.False(view.TableTotalVisible);
        Assert.Null(view.Totals);
        Assert.Null(view.Shares);
    }

    // ------------------------------------------------------------ 12. a removed guest

    [SkippableFact]
    public async Task A_removed_participants_items_stay_on_the_bill_and_fall_to_the_host()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        await using (var guestDb = fixture.CreateContext(world.Clock))
        {
            var guest = fixture.CreateOrderService(guestDb, world.Clock, TestActor.Participant(world.GuestId));

            await guest.PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 1)], Guid.CreateVersion7()));
        }

        await using (var hostDb = fixture.CreateContext(world.Clock))
        {
            var tabs = fixture.CreateTabService(hostDb, world.Clock, TestActor.Participant(world.HostId));
            await tabs.RemoveParticipantAsync(world.TabId, world.HostId, world.GuestId);
        }

        var shares = await SharesAsync(world);

        // Still on the bill: the food was eaten.
        Assert.Equal(TestMenu.KhachapuriAmd, shares.Totals!.SubtotalAmd);

        // And it falls to the host, stated rather than folded in silently.
        Assert.Equal(TestMenu.KhachapuriAmd, Share(shares, world.HostId).AbsorbedFromRemovedAmd);
        Assert.Equal(TestMenu.KhachapuriAmd, shares.AbsorbedFromRemovedAmd);
        Assert.Equal(0L, Share(shares, world.GuestId).ShareAmd);
        Assert.Equal(shares.Totals.TotalAmd, shares.Shares!.Sum(s => s.ShareAmd));
    }

    // ------------------------------------------------------------ 9 and 10. ten orders at once

    /// <summary>How many orders race in the concurrency test. Above the old retry ceiling of three.</summary>
    private const int SimultaneousOrders = 10;

    /// <summary>
    /// <b>Tests 9 and 10.</b> Ten genuinely simultaneous orders on one tab all succeed, and the
    /// totals cache converges on the right number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be two orders, and two was the number the old design could survive. The order
    /// insert wrote the tab's totals cache in the same transaction, so every concurrent order
    /// contended for one row and lost the row-version race; three attempts absorbed a couple of
    /// writers and six exhausted them, surfacing a concurrency error <b>to a diner</b> for adding a
    /// coffee. That is the worst-looking failure in the product, because the fault is invisible and
    /// the app simply appears broken.
    /// </para>
    /// <para>
    /// Raising the ceiling would have moved it. The fix was to stop writing the shared row: the
    /// stored totals are a cache, the authoritative total is the sum of the lines, and the insert
    /// does not need to touch the tab to be correct. Ten writers now have nothing in common to
    /// collide on, so the number here could be a hundred - ten is simply comfortably past the
    /// ceiling that used to exist.
    /// </para>
    /// <para>
    /// Ten separate contexts, and therefore ten connections, released by one gate. Anything sharing
    /// a context would serialise on the context itself and prove nothing.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Ten_simultaneous_orders_all_succeed_and_the_totals_cache_converges()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var contexts = new List<YallaDbContext>();
        var services = new List<Yalla.Infrastructure.Services.TabOrderService>();

        try
        {
            for (var i = 0; i < SimultaneousOrders; i++)
            {
                var context = fixture.CreateContext(world.Clock);
                contexts.Add(context);

                // Alternating between the two people on the tab. Which of them orders is beside the
                // point - what is under test is what the writers share, and they share the tab.
                var participant = i % 2 == 0 ? world.HostId : world.GuestId;
                services.Add(fixture.CreateOrderService(context, world.Clock, TestActor.Participant(participant)));
            }

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var placements = services
                .Select(service => Task.Run(async () =>
                {
                    await gate.Task;

                    return await service.PlaceOrderAsync(new PlaceOrderCommand(
                        world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));
                }))
                .ToList();

            gate.SetResult();

            // Test 9. Task.WhenAll rethrows, so a concurrency error reaching any one of these
            // diners fails the test here rather than showing up as a wrong total below.
            var results = await Task.WhenAll(placements);

            Assert.All(results, r => Assert.False(r.WasReplay));
            Assert.Equal(SimultaneousOrders, results.Select(r => r.OrderId).Distinct().Count());

            // Test 10. The cache converges: every writer recomputes after its own insert, and the
            // row version on the tab is what makes the last one to commit the one that saw
            // everything.
            var expectedSubtotal = SimultaneousOrders * TestMenu.CoffeeAmd;

            await using var verify = fixture.CreateContext(world.Clock);
            var tab = await TabAsync(verify, world.TabId);

            Assert.Equal(expectedSubtotal, tab.SubtotalAmd);
            Assert.Equal(expectedSubtotal / 10L, tab.ServiceChargeAmd);
            Assert.Equal(expectedSubtotal + (expectedSubtotal / 10L), tab.TotalAmd);

            // And the cache agrees with the lines, which are the truth.
            await using var recomputeDb = fixture.CreateContext(world.Clock);
            var ledger = fixture.CreateLedger(recomputeDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId));
            var recomputed = ledger.Compute(await ledger.LoadForWriteAsync(world.TabId, default));

            Assert.Equal(tab.SubtotalAmd, recomputed.SubtotalAmd);
            Assert.Equal(tab.TotalAmd, recomputed.TotalAmd);

            // The event stream numbered all ten, contiguously. Renumbering after a lost place is
            // the other race ten simultaneous writers provoke, and a gap or a duplicate here would
            // mean a catching-up phone silently missing an order.
            var sequences = await verify.TabEvents
                .AsNoTracking()
                .Where(e => e.TabId == world.TabId && e.Type == TabEventType.OrderPlaced)
                .Select(e => e.Sequence)
                .OrderBy(s => s)
                .ToListAsync();

            Assert.Equal(SimultaneousOrders, sequences.Count);
            Assert.Equal(sequences.Count, sequences.Distinct().Count());
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    // ------------------------------------------------------------ 13. who sent a ticket

    /// <summary>
    /// Each ticket on the kitchen queue names the waiter who sent it; a phone's names nobody.
    /// </summary>
    /// <remarks>
    /// The kitchen screen's ticket says "Narek" or "from the app" (Հավելվածից) under the table, so a
    /// cook with a question knows whom to ask. The name is joined onto the queue's one query rather
    /// than looked up per ticket: a Friday-night queue is forty tickets, and forty lookups on every
    /// poll is the kind of cost that only shows up in production. The command count with three
    /// tickets from three senders is the count with one.
    /// </remarks>
    [SkippableFact]
    public async Task Each_kitchen_ticket_names_the_waiter_who_sent_it_and_a_phones_names_nobody_in_one_query()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        await fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));

        // The queue with the phone's ticket alone: the baseline.
        var baseline = new CommandCountingInterceptor();
        await using (var countDb = fixture.CreateContext(world.Clock, baseline))
        {
            var alone = await fixture.CreateOrderService(countDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
                .GetBranchOrdersAsync(world.Branch.BranchId, null);

            Assert.Null(Assert.Single(alone).PlacedByName);
        }

        var colleague = await AnotherWaiterAsync(db, world, "Ani Petrosyan");

        foreach (var (staffId, item) in new[] { (world.Branch.WaiterId, world.Menu.Khachapuri), (colleague, world.Menu.Wine) })
        {
            world.Clock.Advance(TimeSpan.FromMinutes(1));

            await using var orderDb = fixture.CreateContext(world.Clock);
            await fixture.CreateOrderService(orderDb, world.Clock, TestActor.Waiter(staffId))
                .PlaceOrderAsync(new PlaceOrderCommand(world.TabId, [new OrderItemInput(item, 1)], Guid.CreateVersion7()));
        }

        var waiterName = await db.StaffMembers
            .Where(s => s.Id == world.Branch.WaiterId)
            .Select(s => s.FullName)
            .SingleAsync();

        var counter = new CommandCountingInterceptor();
        await using var readDb = fixture.CreateContext(world.Clock, counter);
        var queue = await fixture.CreateOrderService(readDb, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .GetBranchOrdersAsync(world.Branch.BranchId, null);

        // Oldest first: the phone's, then each waiter's, by the name the roster shows them under.
        Assert.Equal([null, waiterName, "Ani Petrosyan"], queue.Select(o => o.PlacedByName));
        Assert.Equal(baseline.Count, counter.Count);
    }

    /// <summary>
    /// A ticket names its waiter as they are now - renamed, or no longer working here - and the
    /// answer to moving it says the same as the queue.
    /// </summary>
    /// <remarks>
    /// Read at the time of reading, not snapshotted onto the order: the ticket is a question about
    /// who to ask right now, and a corrected spelling should reach the screen. A waiter deactivated
    /// mid-shift still sent that order, so their name stays on it rather than turning it into an
    /// app order nobody sent. Only the name: the queue carries no other column of the staff row.
    /// </remarks>
    [SkippableFact]
    public async Task A_ticket_names_its_waiter_as_they_are_now_renamed_or_deactivated_and_moving_it_says_the_same()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var world = await ArrangeAsync();
        await using var db = world.Db;

        var spoken = await fixture.CreateOrderService(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Khachapuri, 1)], Guid.CreateVersion7()));

        await using (var staffDb = fixture.CreateContext(world.Clock))
        {
            var waiter = await staffDb.StaffMembers.SingleAsync(s => s.Id == world.Branch.WaiterId);
            waiter.Rename("Narek Hakobyan");
            waiter.SetActive(false);
            await staffDb.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext(world.Clock);
        var kitchen = fixture.CreateOrderService(readDb, world.Clock, TestActor.Manager(world.Branch.ManagerId));

        var ticket = Assert.Single(await kitchen.GetBranchOrdersAsync(world.Branch.BranchId, null));
        Assert.Equal("Narek Hakobyan", ticket.PlacedByName);

        var moved = await kitchen.MoveOrderStatusAsync(spoken.OrderId, TabOrderStatus.InKitchen);
        Assert.Equal("Narek Hakobyan", moved.PlacedByName);

        // And a phone's order, moved, still names nobody.
        await using var phoneDb = fixture.CreateContext(world.Clock);
        var fromPhone = await fixture.CreateOrderService(phoneDb, world.Clock, TestActor.Participant(world.GuestId))
            .PlaceOrderAsync(new PlaceOrderCommand(
                world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));

        await using var moveDb = fixture.CreateContext(world.Clock);
        var movedFromPhone = await fixture.CreateOrderService(moveDb, world.Clock, TestActor.Manager(world.Branch.ManagerId))
            .MoveOrderStatusAsync(fromPhone.OrderId, TabOrderStatus.InKitchen);

        Assert.Null(movedFromPhone.PlacedByName);
    }

    // ------------------------------------------------------------ helpers

    // ------------------------------------------------------------ the clock does not gate a tab

    /// <summary>
    /// A seated party keeps ordering past closing time. The tab decides, not the opening hours.
    /// </summary>
    /// <remarks>
    /// Pinned after QA 2026-10-16 (parity P3) asked whether an order at 01:35 on a branch whose
    /// hours end at 01:00 should be refused. It should not: the tab was opened by a scan at the
    /// table, so the party is physically there, and "last orders" is the staff's call - marking the
    /// tab closing refuses every further order (<see cref="TabNotAcceptingOrdersException"/>). A
    /// clock gate would instead refuse a real table over hours an owner mistyped, with the kitchen
    /// still working. See docs/tabs.md, "Ordering after closing time".
    /// </remarks>
    [SkippableFact]
    public async Task A_seated_party_keeps_ordering_after_closing_time_because_the_tab_decides_not_the_clock()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // Now is 22:00 in Yerevan; the branch's hours ended at noon.
        var world = await ArrangeAsync(openingHours: [(new TimeOnly(9, 0), new TimeOnly(12, 0), false)]);
        await using var db = world.Db;

        var host = fixture.CreateOrderService(db, world.Clock, TestActor.Participant(world.HostId));

        var placed = await host.PlaceOrderAsync(new PlaceOrderCommand(
            world.TabId, [new OrderItemInput(world.Menu.Coffee, 1)], Guid.CreateVersion7()));

        Assert.Equal(TabOrderStatus.New, placed.Status);
    }

    /// <summary>Somebody approved on a tab at the branch's second table - a real participant, not this tab's.</summary>
    private async Task<Guid> ParticipantAtAnotherTableAsync(World world)
    {
        await using var db = fixture.CreateContext(world.Clock);
        var tabs = fixture.CreateTabService(db, world.Clock, new TestActor(ActorType.Diner, null, null, null));

        var qr = await db.DiningTables
            .Where(t => t.Id == world.Branch.TableIds[1])
            .Select(t => t.QrToken)
            .FirstAsync();

        var opened = await tabs.OpenAsync(new OpenTabCommand(qr, "phone-next-table", Guid.CreateVersion7(), "Gor"));

        Assert.NotEqual(world.TabId, opened.Tab.TabId);
        Assert.Equal(ParticipantStatus.Approved, opened.Tab.Me.Status);

        return opened.Tab.Me.ParticipantId;
    }

    /// <summary>A second waiter on the same branch, under <paramref name="fullName"/>.</summary>
    private static async Task<Guid> AnotherWaiterAsync(YallaDbContext db, World world, string fullName = "Second Waiter")
    {
        var colleague = new StaffMember(
            world.Branch.VenueId,
            fullName,
            $"+3743{Random.Shared.Next(1_000_000, 9_999_999)}",
            StaffRole.Waiter,
            "hash",
            world.Branch.BranchId);

        db.StaffMembers.Add(colleague);
        await db.SaveChangesAsync();

        return colleague.Id;
    }

    private sealed record World(
        YallaDbContext Db,
        TestClock Clock,
        TestBranch Branch,
        TestMenu Menu,
        Guid TabId,
        Guid HostId,
        Guid GuestId);

    /// <summary>A seated table with a tab, a host, one approved guest, and a menu.</summary>
    private async Task<World> ArrangeAsync(
        IReadOnlyList<(TimeOnly Opens, TimeOnly Closes, bool ClosesNextDay)>? openingHours = null)
    {
        var clock = new TestClock(Now);
        var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, openingHours: openingHours);
        var menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

        var qr = await db.DiningTables
            .Where(t => t.Id == branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        await using var tabDb = fixture.CreateContext(clock);
        var tabs = fixture.CreateTabService(tabDb, clock, new TestActor(ActorType.Diner, null, null, null));

        var opened = await tabs.OpenAsync(new OpenTabCommand(qr, "phone-host", Guid.CreateVersion7(), "Aram"));
        var joined = await tabs.OpenAsync(new OpenTabCommand(qr, "phone-guest", Guid.CreateVersion7(), "Nune"));

        await tabs.ApproveParticipantAsync(
            opened.Tab.TabId, opened.Tab.Me.ParticipantId, joined.Tab.Me.ParticipantId);

        return new World(
            db, clock, branch, menu, opened.Tab.TabId, opened.Tab.Me.ParticipantId, joined.Tab.Me.ParticipantId);
    }

    private async Task<Guid> JoinAndApproveAsync(World world, string deviceId, string name)
    {
        await using var db = fixture.CreateContext(world.Clock);
        var tabs = fixture.CreateTabService(db, world.Clock, new TestActor(ActorType.Diner, null, null, null));

        var qr = await db.DiningTables
            .Where(t => t.Id == world.Branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        var joined = await tabs.OpenAsync(new OpenTabCommand(qr, deviceId, Guid.CreateVersion7(), name));

        await tabs.ApproveParticipantAsync(world.TabId, world.HostId, joined.Tab.Me.ParticipantId);

        return joined.Tab.Me.ParticipantId;
    }

    /// <summary>A phone that scanned the code and is waiting on the host.</summary>
    private async Task<Guid> JoinWithoutApprovalAsync(World world, string deviceId, string name)
    {
        await using var db = fixture.CreateContext(world.Clock);
        var tabs = fixture.CreateTabService(db, world.Clock, new TestActor(ActorType.Diner, null, null, null));

        var qr = await db.DiningTables
            .Where(t => t.Id == world.Branch.FirstTableId)
            .Select(t => t.QrToken)
            .FirstAsync();

        var joined = await tabs.OpenAsync(new OpenTabCommand(qr, deviceId, Guid.CreateVersion7(), name));

        Assert.Equal(ParticipantStatus.PendingApproval, joined.Tab.Me.Status);

        return joined.Tab.Me.ParticipantId;
    }

    private async Task<TabSharesView> SharesAsync(World world)
    {
        await using var db = fixture.CreateContext(world.Clock);
        var billing = fixture.CreateBillingQuery(db, world.Clock, TestActor.Waiter(world.Branch.WaiterId));

        return await billing.GetSharesAsync(world.TabId, null);
    }

    private static ParticipantShareView Share(TabSharesView view, Guid participantId) =>
        view.Shares!.Single(s => s.ParticipantId == participantId);

    private static async Task<Tab> TabAsync(YallaDbContext db, Guid tabId) =>
        await db.Tabs.AsNoTracking().FirstAsync(t => t.Id == tabId);
}
