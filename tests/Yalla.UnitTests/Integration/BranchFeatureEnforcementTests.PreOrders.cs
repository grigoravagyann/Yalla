using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

public sealed partial class BranchFeatureEnforcementTests
{
    /// <summary>Thursday 10 September 2026, noon in Yerevan.</summary>
    private static readonly DateTime PreOrderThursday = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Sunday 13 September 2026, 19:00 in Yerevan.</summary>
    private static readonly DateTime PreOrderSundayUtc = new(2026, 9, 13, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// PreOrders: blocked, a guest's first save and an edit of one already saved are refused (and the booking stops
    /// offering one); clearing stays open, and the floor still sends a pre-order saved before the block to the kitchen.
    /// </summary>
    [SkippableFact]
    public async Task Blocked_pre_orders_refuse_saves_and_edits_while_the_floor_still_sends_one_already_saved()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(PreOrderThursday);
        TestBranch branch;
        TestMenu menu;

        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);
            menu = await TestMenuBuilder.CreateAsync(db, branch.BranchId);

            var row = await db.Branches.FirstAsync(b => b.Id == branch.BranchId);
            row.SetPreOrders(true, 30, DateTime.UnixEpoch);
            row.DequeueEvents();
            await db.SaveChangesAsync();
        }

        var kept = TestActor.Diner(Guid.CreateVersion7());
        var cleared = TestActor.Diner(Guid.CreateVersion7());
        var late = TestActor.Diner(Guid.CreateVersion7());

        var keptBooking = await BookSundayAsync(branch, branch.TableIds[0], clock, kept);
        var clearedBooking = await BookSundayAsync(branch, branch.TableIds[1], clock, cleared);
        var lateBooking = await BookSundayAsync(branch, branch.TableIds[2], clock, late);

        await SavePreOrderAsync(keptBooking, menu, clock, kept);
        await SavePreOrderAsync(clearedBooking, menu, clock, cleared);

        await using (var db = fixture.CreateContext(clock))
        {
            await BlockAsync(db, branch.BranchId, BranchFeature.PreOrders);
        }

        // A first save, and an edit of one already saved: refused.
        AssertRefused(
            await Assert.ThrowsAnyAsync<Exception>(() => SavePreOrderAsync(lateBooking, menu, clock, late)),
            BranchFeature.PreOrders,
            branch.BranchId);

        AssertRefused(
            await Assert.ThrowsAnyAsync<Exception>(() => SavePreOrderAsync(keptBooking, menu, clock, kept)),
            BranchFeature.PreOrders,
            branch.BranchId);

        // The guest's booking stops offering a pre-order; the one saved is still shown.
        await using (var db = fixture.CreateContext(clock))
        {
            var mine = await fixture.CreateReservationService(db, clock, kept).GetMineAsync();
            var booking = mine.Upcoming.Single(b => b.Id == keptBooking);

            Assert.Null(booking.PreOrderEditableUntilUtc);
            Assert.NotNull(booking.PreOrder);
        }

        // Clearing is never refused: a guest is not stuck with a pre-order.
        await using (var db = fixture.CreateContext(clock))
        {
            var result = await fixture.CreatePreOrderService(db, clock, cleared).ClearAsync(clearedBooking, Guid.CreateVersion7());
            Assert.Null(result.PreOrder);
        }

        // The floor's rail still has the saved one, and sends it to the kitchen.
        clock.UtcNow = PreOrderSundayUtc.AddMinutes(-20);

        await using (var db = fixture.CreateContext(clock))
        {
            var waiter = fixture.CreatePreOrderService(db, clock, TestActor.Waiter(branch.WaiterId));

            Assert.Contains((await waiter.ListDueAsync(branch.BranchId)).Items, c => c.ReservationId == keptBooking);

            var sent = await waiter.SendAsync(new PreOrderStaffCommand(keptBooking, Guid.CreateVersion7()));
            Assert.NotNull(sent.PreOrder.SentAtUtc);
        }
    }

    private async Task<Guid> BookSundayAsync(TestBranch branch, Guid tableId, TestClock clock, TestActor diner)
    {
        await using var db = fixture.CreateContext(clock);

        var view = await fixture.CreateReservationService(db, clock, diner).CreateAsync(
            new CreateReservationCommand(
                branch.BranchId,
                tableId,
                DateOnly.FromDateTime(PreOrderSundayUtc),
                new TimeOnly(19, 0),
                2,
                GuestName: "Bella Test",
                GuestPhone: "+37411223344",
                ClientCommandId: Guid.CreateVersion7()));

        return view.Id;
    }

    private async Task SavePreOrderAsync(Guid reservationId, TestMenu menu, TestClock clock, TestActor diner)
    {
        await using var db = fixture.CreateContext(clock);

        await fixture.CreatePreOrderService(db, clock, diner).ReplaceAsync(
            new ReplacePreOrderCommand(
                reservationId,
                [new PreOrderLineCommand(menu.Coffee, 2, null)],
                null,
                Guid.CreateVersion7()));
    }
}
