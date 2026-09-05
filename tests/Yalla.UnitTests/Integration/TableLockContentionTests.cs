using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Bookings and seatings contend for the same table, and say so differently from a lost race.
/// </summary>
/// <remarks>
/// <para>
/// Until Prompt 7 only bookings took the table lock, on the reasoning that two seatings collide on
/// the <c>DiningTables</c> row and are caught by its <c>RowVersion</c> for free. That stopped being
/// true when booking's re-check started reading <c>TableSessions</c>: an unlocked seating can commit
/// its session in the window between that read and the booking's commit, and both succeed. Two
/// parties then hold table 7 at eight o'clock and nothing in the system knows it.
/// </para>
/// <para>
/// So both take the lock, and the waiting one has to be able to tell "I never got my turn" from
/// "the table went". The first is retryable and the second never will be.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TableLockContentionTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly BookingDate = new(2026, 9, 13);

    private static readonly TimeOnly SixPm = new(18, 0);

    /// <summary>A wait short enough for a test. Five seconds is right for a venue, not for xUnit.</summary>
    private static BookingLockOptions Impatient => new() { LockTimeoutMilliseconds = 250 };

    // ------------------------------------------------------------ 20. the seat gives up

    [SkippableFact]
    public async Task A_walk_in_seated_while_a_booking_holds_the_table_fails_fast_and_says_it_is_retryable()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var holder = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(holder);
        var tableId = branch.FirstTableId;

        // A booking transaction, mid-flight, holding the table's write lock.
        await using var booking = await fixture.CreateTableLock(holder).AcquireAsync(tableId, null, default);

        // A waiter taps "seat walk-in" on the same table at that moment. Separate context, so this
        // is genuinely a second connection racing the first rather than one queue serialising.
        await using var seater = fixture.CreateContext(clock);
        var machine = fixture.CreateService(seater, clock, TestActor.Waiter(branch.WaiterId), Impatient);

        var stopwatch = Stopwatch.StartNew();

        var timedOut = await Assert.ThrowsAsync<TableLockTimeoutException>(
            () => machine.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7())));

        stopwatch.Stop();

        // Fast: it gave up on its own terms rather than hanging until something upstream killed it.
        // The bound is loose because CI machines are not fast; the point is that 250ms was honoured
        // and the shipped five-second default was not silently in force.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"The seat waited {stopwatch.Elapsed.TotalSeconds:F1}s; it should have given up after 250ms.");

        // Distinguishable from losing the table. A conflict is final; this is not, and the client
        // is told so on the exception itself rather than having to know which codes are retryable.
        Assert.IsNotType<TableStateConflictException>(timedOut);
        Assert.IsNotType<CommandPreconditionFailedException>(timedOut);
        Assert.True(timedOut.Retryable);
        Assert.Equal(tableId, timedOut.TableId);
        Assert.Equal(250, timedOut.TimeoutMilliseconds);

        // The label was looked up for the message even though the seat never got far enough to
        // read the table itself - "Table 1 was busy" is actionable on a tablet, "this table" is not.
        Assert.Equal("1", timedOut.TableLabel);
        Assert.Contains("Table 1 was busy", timedOut.Message);

        // And nothing landed: no session, no audit row, no processed-command record.
        await using var verify = fixture.CreateContext(clock);

        Assert.False(await verify.TableSessions.AnyAsync(s => s.DiningTableId == tableId));
        Assert.False(await verify.TableStateChanges.AnyAsync(c => c.DiningTableId == tableId));
        Assert.Equal(
            TableStatus.Free,
            (await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId)).Status);
    }

    /// <summary>
    /// The same contention the other way round, because a seating that could not be waited on
    /// would leave bookings free to commit straight through it.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_made_while_a_seating_holds_the_table_gets_the_booking_flavoured_timeout()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var holder = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(holder);
        var tableId = branch.FirstTableId;

        await using var seating = await fixture.CreateTableLock(holder).AcquireAsync(tableId, null, default);

        await using var booker = fixture.CreateContext(clock);
        var service = fixture.CreateReservationService(
            booker, clock, TestActor.Diner(), lockOptions: Impatient);

        var timedOut = await Assert.ThrowsAsync<ReservationLockTimeoutException>(
            () => service.CreateAsync(new CreateReservationCommand(
                branch.BranchId,
                tableId,
                BookingDate,
                SixPm,
                PartySize: 2,
                GuestName: "Ani Test",
                GuestPhone: "+37411223344",
                ClientCommandId: Guid.CreateVersion7())));

        // Still a TableLockTimeoutException - same 503, same retryable flag - but worded and coded
        // for a diner on a phone rather than a waiter on the floor.
        Assert.IsAssignableFrom<TableLockTimeoutException>(timedOut);
        Assert.True(timedOut.Retryable);
        Assert.Contains("clientCommandId", timedOut.Message);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.Reservations.AnyAsync(r => r.DiningTableId == tableId));
    }

    /// <summary>
    /// The lock is per table, so contention on one must not stall the rest of the room. A venue
    /// where one slow booking froze every table would be worse than no locking at all.
    /// </summary>
    [SkippableFact]
    public async Task A_held_lock_on_one_table_does_not_delay_a_seating_on_another()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var holder = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(holder);

        await using var booking = await fixture.CreateTableLock(holder)
            .AcquireAsync(branch.FirstTableId, null, default);

        await using var seater = fixture.CreateContext(clock);
        var machine = fixture.CreateService(seater, clock, TestActor.Waiter(branch.WaiterId), Impatient);

        var elsewhere = branch.TableIds[1];

        var seated = await machine.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, elsewhere, 2, Guid.CreateVersion7()));

        Assert.Equal(TableStatus.Occupied, seated.ToStatus);
        Assert.NotNull(seated.TableSessionId);
    }
}
