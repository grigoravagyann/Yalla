using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Pre-orders below the HTTP surface: the "on your way?" push as the dispatcher sends it, and the reads that must cost
/// the same for one booking as for many.
/// </summary>
public partial class PreOrderTests
{
    /// <summary>Thursday 10 September 2026, noon in Yerevan.</summary>
    private static readonly DateTime Thursday = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Sunday = new(2026, 9, 13);

    /// <summary>Sunday 19:00 in Yerevan.</summary>
    private static readonly DateTime SundayDinnerUtc = new(2026, 9, 13, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// At 45 minutes before, the guest with a pre-order is asked - once - and at 30 the guest without one, in words
    /// that do not mention a kitchen. Not the guest who already said they are coming, not the one the waiter marked not
    /// coming, and nobody a minute early.
    /// </summary>
    [SkippableFact]
    public async Task On_your_way_is_asked_at_45_minutes_with_a_waiting_pre_order_and_at_30_without_one()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Thursday);
        await fixture.ClearOutboxAsync(clock);

        TestBranch branch;
        TestMenu menu;
        var diners = new List<TestActor>();

        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db, tableCount: 4);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
            await AcceptPreOrdersAsync(db, branch.BranchId);

            for (var i = 0; i < 4; i++)
            {
                diners.Add(await DinerWithDeviceAsync(db, clock));
            }
        }

        var asked = await BookWithPreOrderAsync(branch, branch.TableIds[0], menu, clock, diners[0]);
        var answered = await BookWithPreOrderAsync(branch, branch.TableIds[1], menu, clock, diners[1]);
        var notComing = await BookWithPreOrderAsync(branch, branch.TableIds[2], menu, clock, diners[2]);
        var bare = await BookAsync(branch, branch.TableIds[3], clock, diners[3]);

        clock.UtcNow = SundayDinnerUtc.AddMinutes(-60);

        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreatePreOrderService(db, clock, diners[1]).ConfirmOnMyWayAsync(answered);
        }

        clock.UtcNow = SundayDinnerUtc.AddMinutes(-50);

        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreatePreOrderService(db, clock, TestActor.Waiter(branch.WaiterId))
                .MarkNotComingAsync(new PreOrderStaffCommand(notComing, Guid.CreateVersion7()));
        }

        var channel = new RecordingChannel();

        clock.UtcNow = SundayDinnerUtc.AddMinutes(-46);
        await using (var db = fixture.CreateContext(clock))
        {
            await Dispatcher(db, clock, channel).RunOnceAsync();
        }

        Assert.Empty(channel.OfKind("reservation-on-your-way"));

        clock.UtcNow = SundayDinnerUtc.AddMinutes(-45);
        await using (var db = fixture.CreateContext(clock))
        {
            await Dispatcher(db, clock, channel).RunOnceAsync();
            await Dispatcher(db, clock, channel).RunOnceAsync();
        }

        var push = Assert.Single(channel.OfKind("reservation-on-your-way"));
        Assert.Equal(asked.ToString(), push.Data["reservationId"]);
        Assert.Equal(diners[0].DinerUserId, push.DinerUserId);
        Assert.Equal("on-my-way,cancel", push.Data["actions"]);
        Assert.Equal("on-your-way", push.CategoryId);

        await using (var db = fixture.CreateContext(clock))
        {
            // The feed's entry appears with the push; the booking without a pre-order has its own, at 30 minutes.
            Assert.Equal(
                SundayDinnerUtc.AddMinutes(-45),
                await db.DinerNotifications
                    .Where(n => n.ReservationId == asked && n.Kind == DinerNotificationKinds.BookingOnYourWay)
                    .Select(n => n.CreatedAtUtc)
                    .SingleAsync());
            Assert.Equal(
                SundayDinnerUtc.AddMinutes(-30),
                await db.DinerNotifications
                    .Where(n => n.ReservationId == bare && n.Kind == DinerNotificationKinds.BookingOnYourWay)
                    .Select(n => n.CreatedAtUtc)
                    .SingleAsync());
        }

        // A minute early, the booking without a pre-order is not asked yet.
        clock.UtcNow = SundayDinnerUtc.AddMinutes(-31);
        await using (var db = fixture.CreateContext(clock))
        {
            await Dispatcher(db, clock, channel).RunOnceAsync();
        }

        Assert.Single(channel.OfKind("reservation-on-your-way"));

        clock.UtcNow = SundayDinnerUtc.AddMinutes(-30);
        await using (var db = fixture.CreateContext(clock))
        {
            await Dispatcher(db, clock, channel).RunOnceAsync();
            await Dispatcher(db, clock, channel).RunOnceAsync();
        }

        // Asked once more: the bare booking, without a word about a kitchen. Still not the guest who answered, nor the
        // party the waiter marked not coming.
        var pushes = channel.OfKind("reservation-on-your-way").ToList();
        Assert.Equal(2, pushes.Count);
        var plain = pushes[1];
        Assert.Equal(bare.ToString(), plain.Data["reservationId"]);
        Assert.Equal(diners[3].DinerUserId, plain.DinerUserId);
        Assert.Equal("on-my-way,cancel", plain.Data["actions"]);
        Assert.DoesNotContain("pre-order", plain.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("նախապատվեր", plain.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The console's booking list and the floor's rail carry every pre-order in the same number of statements for one
    /// booking as for five.
    /// </summary>
    [SkippableFact]
    public async Task The_booking_list_and_the_rail_read_pre_orders_without_a_query_per_booking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var one = await StatementsFor(1);
        var five = await StatementsFor(5);

        Assert.Equal(one, five);

        async Task<(int List, int Rail)> StatementsFor(int guests)
        {
            var clock = new TestClock(Thursday);
            TestBranch branch;
            TestMenu menu;

            await using (var db = fixture.CreateContext(clock))
            {
                branch = await TestBranchBuilder.CreateAsync(db, tableCount: guests);
                menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);
                await AcceptPreOrdersAsync(db, branch.BranchId);
            }

            for (var i = 0; i < guests; i++)
            {
                await BookWithPreOrderAsync(branch, branch.TableIds[i], menu, clock, TestActor.Diner());
            }

            var listCounter = new CommandCountingInterceptor();
            await using (var readDb = fixture.CreateContext(clock, listCounter))
            {
                var listed = await fixture
                    .CreateReservationService(readDb, clock, TestActor.Manager(branch.ManagerId))
                    .ListForBranchAsync(branch.BranchId, ReservationStatus.Confirmed);

                Assert.Equal(guests, listed.Count);
                Assert.All(listed, r => Assert.Equal(2, r.PreOrder!.Lines.Count));
            }

            var railClock = new TestClock(SundayDinnerUtc.AddMinutes(-20));
            var railCounter = new CommandCountingInterceptor();
            await using (var readDb = fixture.CreateContext(railClock, railCounter))
            {
                var rail = await fixture
                    .CreatePreOrderService(readDb, railClock, TestActor.Waiter(branch.WaiterId))
                    .ListDueAsync(branch.BranchId);

                Assert.Equal(guests, rail.Items.Count);
                Assert.All(rail.Items, c => Assert.Equal(PreOrderState.Locked, c.PreOrder.State));
            }

            return (listCounter.Count, railCounter.Count);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task AcceptPreOrdersAsync(YallaDbContext db, Guid branchId)
    {
        var branch = await db.Branches.FirstAsync(b => b.Id == branchId);
        branch.SetPreOrders(true, 30);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> BookAsync(TestBranch branch, Guid tableId, IClock clock, TestActor diner)
    {
        await using var db = fixture.CreateContext(clock);

        var view = await fixture.CreateReservationService(db, clock, diner).CreateAsync(
            new CreateReservationCommand(
                branch.BranchId,
                tableId,
                Sunday,
                new TimeOnly(19, 0),
                2,
                GuestName: "Bella Test",
                GuestPhone: "+37411223344",
                ClientCommandId: Guid.CreateVersion7()));

        return view.Id;
    }

    private async Task<Guid> BookWithPreOrderAsync(TestBranch branch, Guid tableId, TestMenu menu, IClock clock, TestActor diner)
    {
        var id = await BookAsync(branch, tableId, clock, diner);

        await using var db = fixture.CreateContext(clock);
        await fixture.CreatePreOrderService(db, clock, diner).ReplaceAsync(
            new ReplacePreOrderCommand(
                id,
                [new PreOrderLineCommand(menu.Coffee, 2, null), new PreOrderLineCommand(menu.Khachapuri, 1, "no egg")],
                "Birthday",
                Guid.CreateVersion7()));

        return id;
    }

    /// <summary>A verified diner with one phone registered, and the actor that speaks for them.</summary>
    private static async Task<TestActor> DinerWithDeviceAsync(YallaDbContext db, IClock clock)
    {
        var unique = Guid.NewGuid().ToString("N")[..9];
        var diner = new DinerUser($"+3749{unique}", "en");
        diner.ProveNumber(clock.UtcNow);

        db.DinerUsers.Add(diner);
        db.DinerDevices.Add(new DinerDevice(diner.Id, $"ExponentPushToken[{unique}]", DevicePlatform.Ios, "en", clock.UtcNow));

        await db.SaveChangesAsync();

        return TestActor.Diner(diner.Id);
    }

    private static OutboxDispatcher Dispatcher(YallaDbContext db, IClock clock, INotificationChannel channel) =>
        new(
            db,
            clock,
            new OutboxOptions(),
            [new ReservationOnYourWayHandler(db, channel, NullLogger<ReservationOnYourWayHandler>.Instance)],
            NullLogger<OutboxDispatcher>.Instance);
}
