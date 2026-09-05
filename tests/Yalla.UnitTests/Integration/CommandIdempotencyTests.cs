using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Offline replay: applied once, answered the same way twice, and refused when it has gone stale.
/// </summary>
/// <remarks>
/// The staff tablet queues state changes when the cafe wifi drops. Idempotency stops a queued
/// command being applied twice; it says nothing about a queued command being <b>out of date</b> -
/// "free table 7" tapped at 20:05 and synced at 20:40, by which time the table has been seated
/// again. Applying that frees an occupied table on the busiest screen in the venue.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class CommandIdempotencyTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 5, 14, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ 11. the stale replay

    [SkippableFact]
    public async Task A_queued_command_whose_expected_status_no_longer_holds_is_refused_and_changes_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        // The waiter tapped "hold this for the Sarkisyans" while table 1 was free. The wifi was
        // down. By the time the tablet syncs, somebody has seated a walk-in there.
        await machine.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 4, Guid.CreateVersion7()));

        var stale = new TableStateCommand(
            branch.BranchId,
            tableId,
            Guid.CreateVersion7(),
            Reason: "queued at 20:05",
            Queued: true,
            ExpectedFromStatus: TableStatus.Free);

        var refused = await Assert.ThrowsAsync<CommandPreconditionFailedException>(
            () => machine.HoldForLatePartyAsync(stale));

        Assert.Equal(TableStatus.Free, refused.ExpectedFromStatus);
        Assert.Equal(TableStatus.Occupied, refused.CurrentStatus);
        Assert.Equal(stale.ClientCommandId, refused.ClientCommandId);

        // The message has to be readable on a tablet by somebody who was not there.
        Assert.Contains("queued while table", refused.Message);
        Assert.Contains("Occupied now", refused.Message);

        // Nothing was written: not the change, not an audit row, not a processed-command record.
        await using var verify = fixture.CreateContext(clock);

        var table = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId);
        Assert.Equal(TableStatus.Occupied, table.Status);

        Assert.False(await verify.ProcessedCommands.AnyAsync(c => c.ClientCommandId == stale.ClientCommandId));
        Assert.False(await verify.TableStateChanges.AnyAsync(c => c.ClientCommandId == stale.ClientCommandId));
    }

    // ------------------------------------------------------------ 12. the queued command that still holds

    [SkippableFact]
    public async Task A_queued_command_whose_expected_status_still_holds_applies_normally()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        var queued = new SeatWalkInCommand(
            branch.BranchId,
            tableId,
            PartySize: 2,
            Guid.CreateVersion7(),
            Reason: "queued while free",
            Queued: true,
            ExpectedFromStatus: TableStatus.Free);

        var result = await machine.SeatWalkInAsync(queued);

        Assert.Equal(TableStatus.Occupied, result.ToStatus);
        Assert.NotNull(result.TableSessionId);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(
            TableStatus.Occupied,
            (await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId)).Status);
    }

    /// <summary>A queued command with no expected status is a client bug, and says so.</summary>
    [SkippableFact]
    public async Task A_queued_command_that_does_not_say_what_it_expected_is_rejected()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        var command = new SeatWalkInCommand(
            branch.BranchId, branch.FirstTableId, 2, Guid.CreateVersion7(), Queued: true);

        await Assert.ThrowsAsync<ArgumentException>(() => machine.SeatWalkInAsync(command));
    }

    // ------------------------------------------------------------ 13. the stored answer

    /// <summary>
    /// A replay gets the answer the command was first given, not one recomputed against a world
    /// that has moved. The tablet finishing its queue needs the session id its seating created;
    /// "the table is free now" is a different fact and no use to it.
    /// </summary>
    [SkippableFact]
    public async Task A_replay_returns_the_stored_body_rather_than_a_recomputed_one()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;
        var commandId = Guid.CreateVersion7();

        var original = await machine.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, 2, commandId));

        Assert.False(original.WasReplay);
        Assert.NotNull(original.TableSessionId);

        // The world moves on: the party leaves and the table is freed.
        await machine.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, Guid.CreateVersion7()));

        await using var replayDb = fixture.CreateContext(clock);
        var replayed = await fixture.CreateService(replayDb, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, commandId));

        // The stored answer, verbatim - including the session id, which is the field the tablet
        // actually needs and the one the audit row was never going to be asked for.
        Assert.True(replayed.WasReplay);
        Assert.Equal(original.TableSessionId, replayed.TableSessionId);
        Assert.Equal(TableStatus.Occupied, replayed.ToStatus);
        Assert.Equal(TableStatus.Free, replayed.FromStatus);

        // A recomputed answer would describe the table as it is now, which is Free.
        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(
            TableStatus.Free,
            (await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId)).Status);

        // And the status code was stored beside the body, so an HTTP replay answers identically.
        var record = await verify.ProcessedCommands.AsNoTracking()
            .SingleAsync(c => c.ClientCommandId == commandId);

        Assert.Equal(200, record.StatusCode);
        Assert.Equal("table.seat-walk-in", record.CommandType);
        Assert.Equal(ActorType.Staff, record.ActorType);
        Assert.Equal(branch.WaiterId, record.ActorId);

        // The body really is the response, not a summary of it.
        var storedBody = JsonSerializer.Deserialize<TableStateChangeResult>(
            record.ResponseJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(original.TableSessionId, storedBody.TableSessionId);
    }

    // ------------------------------------------------------------ 14. nothing lands unrecorded

    [SkippableFact]
    public async Task Every_command_that_had_effects_left_a_processed_command_row()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        var ids = new List<Guid>();

        async Task<Guid> Run(Func<Guid, Task> command)
        {
            var id = Guid.CreateVersion7();
            await command(id);
            ids.Add(id);
            return id;
        }

        await Run(id => machine.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, id)));
        await Run(id => machine.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, id)));
        await Run(id => machine.HoldForLatePartyAsync(new TableStateCommand(branch.BranchId, tableId, id)));
        await Run(id => machine.ReleaseHoldAsync(new TableStateCommand(branch.BranchId, tableId, id)));
        await Run(id => machine.MarkOutOfServiceAsync(new TableStateCommand(branch.BranchId, tableId, id)));
        await Run(id => machine.ReturnToServiceAsync(new TableStateCommand(branch.BranchId, tableId, id)));

        await using var verify = fixture.CreateContext(clock);

        // One record per command...
        foreach (var id in ids)
        {
            Assert.True(
                await verify.ProcessedCommands.AnyAsync(c => c.ClientCommandId == id),
                $"Command {id} had effects but left no ProcessedCommand row.");
        }

        // ...and one audit row per command, so neither side can exist without the other.
        var changes = await verify.TableStateChanges.AsNoTracking()
            .CountAsync(c => c.DiningTableId == tableId);

        var records = await verify.ProcessedCommands.AsNoTracking()
            .CountAsync(c => ids.Contains(c.ClientCommandId));

        Assert.Equal(ids.Count, changes);
        Assert.Equal(ids.Count, records);
    }
}
