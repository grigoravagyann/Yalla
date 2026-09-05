using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The change log as an ordered stream, which is the seam the realtime hub will plug into.
/// </summary>
/// <remarks>
/// Every state change already wrote exactly one audit row in the same transaction as the change.
/// Giving those rows an order is what lets a client that dropped its connection ask "what have I
/// missed since 4,812?" instead of refetching the whole floor - and adding an identity column to a
/// live, growing audit table later is a far worse migration than adding it now.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ChangeStreamTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 5, 14, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ 19. catching up

    [SkippableFact]
    public async Task Changes_after_a_sequence_come_back_in_order_and_the_floor_agrees_on_where_the_stream_ends()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var first = branch.TableIds[0];
        var second = branch.TableIds[1];

        // Three changes, in a known order.
        await machine.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, first, 2, Guid.CreateVersion7()));
        await machine.HoldForLatePartyAsync(new TableStateCommand(branch.BranchId, second, Guid.CreateVersion7()));
        await machine.FreeTableAsync(new TableStateCommand(branch.BranchId, first, Guid.CreateVersion7()));

        await using var readDb = fixture.CreateContext(clock);
        var floorQuery = fixture.CreateFloorQuery(readDb, clock);

        var all = await floorQuery.GetChangesAsync(branch.BranchId, afterSequence: 0, limit: 100);

        Assert.Equal(3, all!.Changes.Count);
        Assert.False(all.HasMore);

        // Strictly increasing, and in the order the changes were made.
        Assert.Equal(all.Changes.Select(c => c.Sequence).OrderBy(x => x), all.Changes.Select(c => c.Sequence));
        Assert.Equal([first, second, first], all.Changes.Select(c => c.DiningTableId));
        Assert.Equal(TableStatus.Occupied, all.Changes[0].ToStatus);
        Assert.Equal(TableStatus.Held, all.Changes[1].ToStatus);
        Assert.Equal(TableStatus.Free, all.Changes[2].ToStatus);

        // The stream's end is the last entry's sequence, and the floor agrees - which is what lets
        // a client know its position without a second call.
        Assert.Equal(all.Changes[^1].Sequence, all.MaxSequence);

        var floor = await floorQuery.GetFloorStateAsync(branch.BranchId, clock.UtcNow);
        Assert.Equal(all.MaxSequence, floor!.MaxSequence);

        // Catching up from the first entry returns only what came after it.
        var later = await floorQuery.GetChangesAsync(branch.BranchId, all.Changes[0].Sequence, limit: 100);

        Assert.Equal(2, later!.Changes.Count);
        Assert.All(later.Changes, c => Assert.True(c.Sequence > all.Changes[0].Sequence));
        Assert.Equal(all.MaxSequence, later.MaxSequence);

        // Already up to date: nothing to apply, and the stream end still reported.
        var caughtUp = await floorQuery.GetChangesAsync(branch.BranchId, all.MaxSequence, limit: 100);

        Assert.Empty(caughtUp!.Changes);
        Assert.False(caughtUp.HasMore);
        Assert.Equal(all.MaxSequence, caughtUp.MaxSequence);
    }

    /// <summary>
    /// A capped page says so, and the end of the stream is still reported - so a client that is far
    /// behind learns how far without paging all the way there.
    /// </summary>
    [SkippableFact]
    public async Task A_capped_page_reports_that_more_remain_and_still_names_the_end_of_the_stream()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        for (var i = 0; i < 4; i++)
        {
            await machine.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));
            await machine.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, Guid.CreateVersion7()));
        }

        await using var readDb = fixture.CreateContext(clock);

        var page = await fixture.CreateFloorQuery(readDb, clock)
            .GetChangesAsync(branch.BranchId, afterSequence: 0, limit: 3);

        Assert.Equal(3, page!.Changes.Count);
        Assert.True(page.HasMore);

        // The end of the stream, not the end of the page.
        Assert.True(page.MaxSequence > page.Changes[^1].Sequence);
    }

    /// <summary>Branch-scoped like every other floor route: a neighbour's stream is not readable.</summary>
    [SkippableFact]
    public async Task The_changes_endpoint_is_branch_scoped_and_waiter_or_above()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        AuthBranch mine;
        AuthBranch theirs;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
        }

        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, mine));

        var own = await waiter.GetAsync($"/api/branches/{mine.BranchId}/tables/changes?afterSequence=0");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        var body = await own.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(mine.BranchId, body.GetProperty("branchId").GetGuid());
        Assert.True(body.TryGetProperty("maxSequence", out _));
        Assert.True(body.TryGetProperty("hasMore", out _));

        var neighbours = await waiter.GetAsync($"/api/branches/{theirs.BranchId}/tables/changes?afterSequence=0");
        Assert.Equal(HttpStatusCode.Forbidden, neighbours.StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/branches/{mine.BranchId}/tables/changes")).StatusCode);
    }

    /// <summary>
    /// Every change that landed has exactly one entry: the audit row and the state change are one
    /// transaction, so the stream cannot miss one or invent one.
    /// </summary>
    [SkippableFact]
    public async Task The_stream_has_one_entry_per_change_and_no_more()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        await machine.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));

        // A replayed command changes nothing, so it must not add an entry either.
        var replayId = Guid.CreateVersion7();
        await machine.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, replayId));
        await machine.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, replayId));

        await using var readDb = fixture.CreateContext(clock);

        var page = await fixture.CreateFloorQuery(readDb, clock)
            .GetChangesAsync(branch.BranchId, afterSequence: 0, limit: 100);

        Assert.Equal(2, page!.Changes.Count);

        var rows = await readDb.TableStateChanges.AsNoTracking()
            .CountAsync(c => c.BranchId == branch.BranchId);

        Assert.Equal(rows, page.Changes.Count);
    }
}
