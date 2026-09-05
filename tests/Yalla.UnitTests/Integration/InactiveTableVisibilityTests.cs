using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The two opposite requirements that sit on <c>DiningTable.IsActive</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the code says both, which is how a flag ends up meaning one thing in the query
/// somebody was looking at and the other everywhere else. Written down here as two assertions
/// facing in opposite directions:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>The floor-plan editor must see them.</b> A table someone just tried to delete has to appear as
/// deactivated. Dropping it from the response looks like the delete succeeded, and the editor's next
/// save recreates it under a new id and a new QR code - so the printed code on the physical table
/// stops working, for a table nobody meant to touch.
/// </item>
/// <item>
/// <b>Diners must not.</b> Availability and the floor state are what a phone books against, and a
/// deactivated table is not a table any more.
/// </item>
/// </list>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class InactiveTableVisibilityTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 6, 10, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ 21. both directions

    [SkippableFact]
    public async Task The_floor_plan_includes_a_deactivated_table_and_availability_does_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);

        var retired = branch.TableIds[2];

        await using (var edit = fixture.CreateContext(clock))
        {
            var table = await edit.DiningTables.FirstAsync(t => t.Id == retired);
            table.SetActive(false);
            await edit.SaveChangesAsync();
        }

        // The editor sees it, flagged.
        await using var readDb = fixture.CreateContext(clock);

        var settings = fixture.CreateBranchSettingsService(readDb, clock, TestActor.Manager(branch.ManagerId));
        var plan = await settings.GetFloorPlanAsync(branch.BranchId);

        var shown = plan.Tables.SingleOrDefault(t => t.Id == retired);

        Assert.NotNull(shown);
        Assert.False(shown!.IsActive);
        Assert.Equal(3, plan.Tables.Count);

        // The diner-facing floor state does not.
        var floor = await fixture.CreateFloorQuery(readDb, clock)
            .GetFloorStateAsync(branch.BranchId, clock.UtcNow);

        Assert.NotNull(floor);
        Assert.DoesNotContain(floor!.Tables, t => t.TableId == retired);
        Assert.Equal(2, floor.Tables.Count);

        // Nor does availability, so nobody can book it.
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(
                branch.BranchId, 2, DateOnly.FromDateTime(Now).AddDays(1), new TimeOnly(19, 0)));

        Assert.NotNull(availability);
        Assert.DoesNotContain(availability!.Tables, t => t.TableId == retired);
    }

    // ------------------------------------------------------------ isDeletable

    /// <summary>
    /// The editor learns which tables can be deleted without having to try.
    /// </summary>
    /// <remarks>
    /// The flag has to agree with the rule the delete actually applies, audit rows included -
    /// otherwise the editor offers a delete that then refuses, which is the confusion the flag
    /// exists to remove.
    /// </remarks>
    [SkippableFact]
    public async Task A_table_with_history_is_reported_as_not_deletable_and_a_fresh_one_is()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);

        var used = branch.FirstTableId;
        var untouched = branch.TableIds[1];

        // A seating is history, and so is the audit row it writes.
        await using (var seat = fixture.CreateContext(clock))
        {
            var machine = fixture.CreateService(seat, clock, TestActor.Waiter(branch.WaiterId));

            await machine.SeatWalkInAsync(new Yalla.Application.Tables.SeatWalkInCommand(
                branch.BranchId, used, 2, Guid.CreateVersion7()));
        }

        // A table that was only ever held has no booking, no seating and no bill - but it does have
        // audit rows, and that foreign key is Restrict. This is the case the flag gets wrong if it
        // only counts the obvious three.
        await using (var hold = fixture.CreateContext(clock))
        {
            var machine = fixture.CreateService(hold, clock, TestActor.Waiter(branch.WaiterId));

            await machine.HoldForLatePartyAsync(new Yalla.Application.Tables.TableStateCommand(
                branch.BranchId, branch.TableIds[2], Guid.CreateVersion7()));
        }

        await using var readDb = fixture.CreateContext(clock);
        var settings = fixture.CreateBranchSettingsService(readDb, clock, TestActor.Manager(branch.ManagerId));
        var plan = await settings.GetFloorPlanAsync(branch.BranchId);

        Assert.False(plan.Tables.Single(t => t.Id == used).IsDeletable);
        Assert.False(plan.Tables.Single(t => t.Id == branch.TableIds[2]).IsDeletable);
        Assert.True(plan.Tables.Single(t => t.Id == untouched).IsDeletable);

        // And the flag agrees with what deleting actually does: the untouched table goes, and the
        // one with history is deactivated instead of removed.
        var deleted = await settings.DeleteTableAsync(branch.BranchId, untouched);
        Assert.True(deleted.Deleted);

        // The seated one has to be freed first - an occupied table refuses either outcome, which is
        // a live-state rule rather than a history one and is why the two are asserted separately.
        await using (var free = fixture.CreateContext(clock))
        {
            var machine = fixture.CreateService(free, clock, TestActor.Waiter(branch.WaiterId));

            await machine.FreeTableAsync(new Yalla.Application.Tables.TableStateCommand(
                branch.BranchId, used, Guid.CreateVersion7()));
        }

        await using var deleteDb = fixture.CreateContext(clock);
        var deleter = fixture.CreateBranchSettingsService(
            deleteDb, clock, TestActor.Manager(branch.ManagerId));

        var deactivated = await deleter.DeleteTableAsync(branch.BranchId, used);

        Assert.False(deactivated.Deleted);
        Assert.True(deactivated.Deactivated);
    }
}
