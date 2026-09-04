using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The derived floor read model, against a real SQL Server.
/// </summary>
/// <remarks>
/// <c>GET /floor</c> is the most-called endpoint in the product, so two separate things are worth
/// proving: that the reservation overlay is derived correctly for every state, and that the whole
/// floor still arrives in <b>one</b> round trip. The second is the one that regresses silently -
/// an innocent-looking change to the projection turns it into a query per table and nothing fails
/// except the pilot venue's Friday night.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class FloorQueryTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 4, 15, 15, 0, DateTimeKind.Utc);

    /// <summary>The default restaurant policy: a table reads as reserved 15 minutes ahead.</summary>
    private const int BufferMinutes = 15;

    [SkippableFact]
    public async Task The_floor_derives_every_state_for_a_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 5);

        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        // Table 1 stays free with nothing booked.
        // Table 2: free, but booked inside the buffer - the overlay should show it.
        var soonStart = Now.AddMinutes(10);
        await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, branch.TableIds[1], soonStart);

        // Table 3: occupied by a walk-in.
        await service.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, branch.TableIds[2], PartySize: 2, Guid.NewGuid()));

        // Table 4: held for a late party.
        await service.HoldForLatePartyAsync(
            new TableStateCommand(branch.BranchId, branch.TableIds[3], Guid.NewGuid()));

        // Table 5: out of service.
        await service.MarkOutOfServiceAsync(
            new TableStateCommand(branch.BranchId, branch.TableIds[4], Guid.NewGuid(), "broken chair"));

        await using var readDb = fixture.CreateContext(clock);
        var floor = await fixture.CreateFloorQuery(readDb, clock).GetFloorAsync(branch.BranchId);

        Assert.NotNull(floor);
        Assert.Equal(branch.BranchId, floor.BranchId);
        Assert.Equal(branch.TimeZoneId, floor.TimeZoneId);
        Assert.Equal(Now, floor.AsOfUtc);
        Assert.Equal(5, floor.Tables.Count);

        // Ordered by area then natural label order, so "10" would not sort before "7".
        Assert.Equal(["1", "2", "3", "4", "5"], floor.Tables.Select(t => t.Label));

        var free = floor.Tables[0];
        Assert.Equal(TableStatus.Free, free.PhysicalStatus);
        Assert.Equal(DerivedTableState.Free, free.State);
        Assert.Null(free.NextReservationStartUtc);
        Assert.Null(free.FreeUntilUtc);
        Assert.Null(free.CurrentSessionId);

        // Physically free, presented as reserved: this is the state that is never stored.
        var reservedSoon = floor.Tables[1];
        Assert.Equal(TableStatus.Free, reservedSoon.PhysicalStatus);
        Assert.Equal(DerivedTableState.ReservedSoon, reservedSoon.State);
        Assert.Equal(soonStart, reservedSoon.NextReservationStartUtc);
        Assert.Equal(soonStart.AddMinutes(-BufferMinutes), reservedSoon.FreeUntilUtc);

        var occupied = floor.Tables[2];
        Assert.Equal(TableStatus.Occupied, occupied.PhysicalStatus);
        Assert.Equal(DerivedTableState.Occupied, occupied.State);
        Assert.NotNull(occupied.CurrentSessionId);
        Assert.Equal(2, occupied.PartySize);
        Assert.Equal(TableSessionSource.WalkIn, occupied.OccupancySource);
        Assert.Equal(Now, occupied.SeatedAtUtc);

        Assert.Equal(DerivedTableState.Held, floor.Tables[3].State);
        Assert.Equal(DerivedTableState.OutOfService, floor.Tables[4].State);
    }

    /// <summary>
    /// A booking beyond the buffer must not grey the table out - it is genuinely sellable now, and
    /// the free-until window is what tells the diner how long they have.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_beyond_the_buffer_leaves_the_table_free_but_reports_its_window()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

        var start = Now.AddHours(3);
        await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, branch.FirstTableId, start);

        await using var readDb = fixture.CreateContext(clock);
        var floor = await fixture.CreateFloorQuery(readDb, clock).GetFloorAsync(branch.BranchId);

        var table = Assert.Single(floor!.Tables);
        Assert.Equal(DerivedTableState.Free, table.State);
        Assert.Equal(start, table.NextReservationStartUtc);
        Assert.Equal(start.AddMinutes(-BufferMinutes), table.FreeUntilUtc);
    }

    /// <summary>
    /// The same rows, read at a later instant, present differently with no write in between. This
    /// is the whole argument for deriving the state rather than storing it.
    /// </summary>
    [SkippableFact]
    public async Task The_same_table_becomes_reserved_as_the_clock_moves_with_no_write()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

        // Comfortably outside the 15-minute buffer, for now.
        await TestBranchBuilder.AddConfirmedReservationAsync(
            db, branch, branch.FirstTableId, Now.AddMinutes(40));

        await using var readDb = fixture.CreateContext(clock);
        var floorQuery = fixture.CreateFloorQuery(readDb, clock);

        var before = await floorQuery.GetFloorAsync(branch.BranchId);
        Assert.Equal(DerivedTableState.Free, before!.Tables[0].State);

        // No write of any kind - only time passing.
        clock.Advance(TimeSpan.FromMinutes(30));

        var after = await floorQuery.GetFloorAsync(branch.BranchId);
        Assert.Equal(DerivedTableState.ReservedSoon, after!.Tables[0].State);
        Assert.Equal(TableStatus.Free, after.Tables[0].PhysicalStatus);
    }

    /// <summary>
    /// A booking that is already over must not hold a table hostage. Only live bookings count
    /// towards the overlay - and a stored <c>Reserved</c> flag is exactly what would get this
    /// wrong, because nothing happens at the moment a booking stops mattering.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_that_has_already_ended_does_not_reserve_the_table()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);

        // A 90-minute turn starting four hours ago: over well before now.
        await TestBranchBuilder.AddConfirmedReservationAsync(
            db, branch, branch.FirstTableId, Now.AddHours(-4));

        await using var readDb = fixture.CreateContext(clock);
        var floor = await fixture.CreateFloorQuery(readDb, clock).GetFloorAsync(branch.BranchId);

        var table = Assert.Single(floor!.Tables);
        Assert.Equal(DerivedTableState.Free, table.State);
        Assert.Null(table.NextReservationStartUtc);
    }

    /// <summary>
    /// Once a booking has been seated it is no longer "upcoming": the table is occupied, and
    /// showing its own booking as the next one would make the floor read as double-booked.
    /// </summary>
    [SkippableFact]
    public async Task A_seated_booking_is_no_longer_the_next_reservation()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 1);
        var tableId = branch.FirstTableId;

        var reservation = await TestBranchBuilder.AddConfirmedReservationAsync(
            db, branch, tableId, Now.AddMinutes(10));

        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        await service.SeatReservationAsync(
            new SeatReservationCommand(branch.BranchId, tableId, reservation.Id, Guid.NewGuid()));

        await using var readDb = fixture.CreateContext(clock);
        var floor = await fixture.CreateFloorQuery(readDb, clock).GetFloorAsync(branch.BranchId);

        var table = Assert.Single(floor!.Tables);
        Assert.Equal(DerivedTableState.Occupied, table.State);
        Assert.Null(table.NextReservationId);
        Assert.Null(table.NextReservationStartUtc);
    }

    [SkippableFact]
    public async Task An_unknown_branch_returns_null_rather_than_an_empty_floor()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        Assert.Null(await fixture.CreateFloorQuery(db, clock).GetFloorAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// The floor arrives in one round trip, however many tables it has.
    /// </summary>
    /// <remarks>
    /// Counting executed commands is the only assertion that actually catches an N+1: the results
    /// are identical either way, so a correctness test cannot see the difference. The interceptor
    /// counts what the provider really sent, not what the expression tree looks like.
    /// </remarks>
    [SkippableFact]
    public async Task The_whole_floor_is_read_in_a_single_sql_statement()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 12);
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        // Give every table something to fold in, so any per-table query would show up.
        foreach (var tableId in branch.TableIds.Take(6))
        {
            await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, tableId, Now.AddMinutes(20));
        }

        foreach (var tableId in branch.TableIds.Skip(6).Take(3))
        {
            await service.SeatWalkInAsync(
                new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.NewGuid()));
        }

        var counter = new CommandCountingInterceptor();
        await using var readDb = fixture.CreateContext(clock, counter);

        var floor = await fixture.CreateFloorQuery(readDb, clock).GetFloorAsync(branch.BranchId);

        Assert.Equal(12, floor!.Tables.Count);
        Assert.Equal(1, counter.Count);
    }

    /// <summary>
    /// Reported rather than asserted against a golden string: the exact SQL is EF's business and
    /// changes between versions, but it must stay a single statement with no correlated loop.
    /// </summary>
    [SkippableFact]
    public async Task The_floor_query_sql_is_one_statement()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);

        var sql = fixture.CreateFloorQuery(db, clock).GetFloorQuerySql(branch.BranchId);

        var dumpTo = Environment.GetEnvironmentVariable("YALLA_FLOOR_SQL_DUMP");
        if (!string.IsNullOrWhiteSpace(dumpTo))
        {
            await File.WriteAllTextAsync(dumpTo, sql);
        }

        // ToQueryString prefixes a DECLARE per parameter; the statement itself is what follows.
        var statement = string.Join(
            '\n',
            sql.Split('\n').SkipWhile(line => line.TrimStart().StartsWith("DECLARE", StringComparison.Ordinal)));

        Assert.DoesNotContain(";", statement, StringComparison.Ordinal);

        // Tables, the open session and the next booking are all folded into that one statement.
        Assert.Contains("[DiningTables]", statement, StringComparison.Ordinal);
        Assert.Contains("[TableSessions]", statement, StringComparison.Ordinal);
        Assert.Contains("[Reservations]", statement, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(statement, "FROM [Branches]"));
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;

        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

/// <summary>Counts the SQL commands a context actually executes.</summary>
internal sealed class CommandCountingInterceptor : DbCommandInterceptor
{
    private int _count;

    public int Count => _count;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
