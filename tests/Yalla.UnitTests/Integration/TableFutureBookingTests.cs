using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A table somebody has booked and not yet sat down at cannot be deleted - through its own route or
/// by leaving it out of a plan save. Deactivating it would strand the booking on a table that no
/// longer appears on the floor; the venue must move or cancel the booking first.
/// </summary>
/// <remarks>
/// Past, cancelled and completed bookings are history, as before: the table is deactivated.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TableFutureBookingTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    [SkippableTheory]
    [InlineData(ReservationStatus.Confirmed)]
    [InlineData(ReservationStatus.PendingApproval)]
    public async Task Deleting_a_table_with_an_upcoming_booking_is_refused_naming_it_and_nothing_changes(ReservationStatus status)
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await CreateBranchAsync(clock);
        var tableId = branch.TableIds[1];
        var code = await BookAsync(clock, branch, tableId, Now.AddDays(1), status);
        var label = await LabelAsync(clock, tableId);
        var before = await GetPlanAsync(clock, branch);

        var refused = await Assert.ThrowsAsync<TableHasFutureBookingsException>(
            () => WithServiceAsync(clock, branch, s => s.DeleteTableAsync(branch.BranchId, tableId)));

        Assert.Equal([label], refused.Tables);
        Assert.Equal([code], refused.Bookings);
        Assert.Equal($"Table {label} has upcoming bookings ({code}). Move or cancel them first.", refused.Message);

        var after = await GetPlanAsync(clock, branch);
        Assert.Equal(before.Version, after.Version);
        Assert.True(after.Tables.Single(t => t.Id == tableId).IsActive);
    }

    [SkippableFact]
    public async Task Deleting_a_table_whose_bookings_are_past_or_cancelled_deactivates_it_as_before()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await CreateBranchAsync(clock);
        var tableId = branch.TableIds[1];
        await BookAsync(clock, branch, tableId, Now.AddDays(-2), ReservationStatus.Confirmed);
        await BookAsync(clock, branch, tableId, Now.AddDays(1), ReservationStatus.Confirmed, cancel: true);

        var result = await WithServiceAsync(clock, branch, s => s.DeleteTableAsync(branch.BranchId, tableId));

        Assert.False(result.Deleted);
        Assert.True(result.Deactivated);
    }

    [SkippableFact]
    public async Task A_plan_save_leaving_out_tables_with_upcoming_bookings_is_refused_naming_them_and_nothing_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await CreateBranchAsync(clock);
        var first = branch.TableIds[1];
        var second = branch.TableIds[2];
        var codeA = await BookAsync(clock, branch, first, Now.AddDays(1), ReservationStatus.Confirmed);
        var codeB = await BookAsync(clock, branch, second, Now.AddDays(2), ReservationStatus.PendingApproval);
        var labels = new[] { await LabelAsync(clock, first), await LabelAsync(clock, second) }.Order(StringComparer.Ordinal).ToList();

        var before = await GetPlanAsync(clock, branch);

        var refused = await Assert.ThrowsAsync<TableHasFutureBookingsException>(() => WithServiceAsync(clock, branch, s => s.ReplaceFloorPlanAsync(
            branch.BranchId,
            new ReplaceFloorPlanCommand(Areas(before), [.. Tables(before).Where(t => t.Id != first && t.Id != second)], before.Version))));

        Assert.Equal(labels, refused.Tables);
        Assert.Equal(new[] { codeA, codeB }.Order(StringComparer.Ordinal), refused.Bookings);

        var after = await GetPlanAsync(clock, branch);
        Assert.Equal(before.Version, after.Version);
        Assert.True(after.Tables.Single(t => t.Id == first).IsActive);
        Assert.True(after.Tables.Single(t => t.Id == second).IsActive);
    }

    [SkippableFact]
    public async Task A_plan_save_leaving_out_a_table_with_only_past_bookings_deactivates_it_as_before()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await CreateBranchAsync(clock);
        var tableId = branch.TableIds[1];
        await BookAsync(clock, branch, tableId, Now.AddDays(-2), ReservationStatus.Confirmed);
        var label = await LabelAsync(clock, tableId);

        var before = await GetPlanAsync(clock, branch);
        var saved = await WithServiceAsync(clock, branch, s => s.ReplaceFloorPlanAsync(
            branch.BranchId,
            new ReplaceFloorPlanCommand(Areas(before), [.. Tables(before).Where(t => t.Id != tableId)], before.Version)));

        Assert.Equal([label], saved.DeactivatedTables);
    }

    // ------------------------------------------------------------ helpers

    private async Task<TestBranch> CreateBranchAsync(TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        return await TestBranchBuilder.CreateAsync(db);
    }

    private async Task<string> BookAsync(
        TestClock clock, TestBranch branch, Guid tableId, DateTime startUtc, ReservationStatus status, bool cancel = false)
    {
        await using var db = fixture.CreateContext(clock);

        var policy = await db.Branches.AsNoTracking().Where(b => b.Id == branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();

        var booking = Yalla.Domain.Occupancy.Reservation.Create(
            branchId: branch.BranchId,
            diningTableId: tableId,
            startUtc: startUtc,
            localDate: DateOnly.FromDateTime(startUtc),
            localStartTime: TimeOnly.FromDateTime(startUtc),
            partySize: 2,
            guestName: "Ani Test",
            guestPhone: "+37411223344",
            code: Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            policy: policy,
            initialStatus: status);

        if (cancel)
        {
            booking.CancelByVenue(Now, "Test");

            // History, written straight to the row: what the cancellation told is not this test's subject.
            booking.DequeueEvents();
        }

        db.Reservations.Add(booking);
        await db.SaveChangesAsync();

        return booking.Code;
    }

    private async Task<string> LabelAsync(TestClock clock, Guid tableId)
    {
        await using var db = fixture.CreateContext(clock);
        return await db.DiningTables.Where(t => t.Id == tableId).Select(t => t.Label).SingleAsync();
    }

    private Task<FloorPlanView> GetPlanAsync(TestClock clock, TestBranch branch) =>
        WithServiceAsync(clock, branch, s => s.GetFloorPlanAsync(branch.BranchId));

    private async Task<T> WithServiceAsync<T>(TestClock clock, TestBranch branch, Func<Yalla.Infrastructure.Services.BranchSettingsService, Task<T>> call)
    {
        await using var db = fixture.CreateContext(clock);
        return await call(fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId)));
    }

    private static List<FloorAreaInput> Areas(FloorPlanView plan) =>
        [.. plan.Areas.Select(a => new FloorAreaInput(a.Id, a.Name, a.DisplayOrder, [.. a.Photos.Select(p => p.PhotoId)]))];

    private static List<FloorTableInput> Tables(FloorPlanView plan) =>
    [
        .. plan.Tables.Select(t => new FloorTableInput(
            t.Id,
            t.Label,
            t.Seats,
            plan.Areas.FirstOrDefault(a => a.Id == t.FloorAreaId)?.Name,
            t.IsBookable,
            t.PhotoId,
            t.PhotoX,
            t.PhotoY)),
    ];
}
