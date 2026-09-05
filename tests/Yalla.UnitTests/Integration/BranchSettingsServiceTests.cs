using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Policy, hours and floor plan against a real database: settings changes leave bookings alone,
/// deleting a used table deactivates it, and editing never touches a QR token.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class BranchSettingsServiceTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 5, 14, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ 8. policy changes are non-destructive

    [SkippableFact]
    public async Task Shortening_the_booking_window_reports_affected_reservations_and_changes_none()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        // Booked six days out under the default 14-day window. Another one tomorrow, which stays inside.
        var farOut = await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, branch.TableIds[0], Now.AddDays(6));
        var tomorrow = await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, branch.TableIds[1], Now.AddHours(20));

        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var current = await service.GetReservationPolicyAsync(branch.BranchId);

        var result = await service.UpdateReservationPolicyAsync(branch.BranchId, Command(current) with { BookingWindowDays = 1 });

        Assert.Equal(1, result.Policy.BookingWindowDays);
        Assert.Equal(1, result.AffectedExistingReservations);
        Assert.Equal([farOut.Id], result.AffectedReservationIds);

        await using var verify = fixture.CreateContext(clock);

        var untouched = await verify.Reservations.AsNoTracking().FirstAsync(r => r.Id == farOut.Id);
        Assert.Equal(ReservationStatus.Confirmed, untouched.Status);
        Assert.Equal(farOut.StartUtc, untouched.StartUtc);
        Assert.Equal(farOut.EndUtc, untouched.EndUtc);
        Assert.Null(untouched.CancelledAtUtc);

        var alsoUntouched = await verify.Reservations.AsNoTracking().FirstAsync(r => r.Id == tomorrow.Id);
        Assert.Equal(ReservationStatus.Confirmed, alsoUntouched.Status);

        var policy = await verify.Branches.AsNoTracking().Where(b => b.Id == branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();
        Assert.Equal(1, policy.BookingWindowDays);
    }

    [SkippableFact]
    public async Task A_turn_time_outside_the_bounds_is_refused_not_clamped()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var current = await service.GetReservationPolicyAsync(branch.BranchId);

        var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.UpdateReservationPolicyAsync(branch.BranchId, Command(current) with { TurnTimeMinutes = 5 }));

        Assert.Contains("Turn time", refused.Message);
        Assert.Contains("5 minutes", refused.Message);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.UpdateReservationPolicyAsync(branch.BranchId, Command(current) with { TurnTimeMinutes = 12 * 60 }));

        await using var verify = fixture.CreateContext(clock);
        var policy = await verify.Branches.AsNoTracking().Where(b => b.Id == branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();
        Assert.Equal(current.TurnTimeMinutes, policy.TurnTimeMinutes);
    }

    // ------------------------------------------------------------ 9. opening hours, through the database

    [SkippableFact]
    public async Task Opening_hours_are_replaced_as_a_whole_week_and_overlaps_are_rejected()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var overlapping = new List<OpeningHoursBlock>
        {
            new(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(15, 0)),
            new(DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(23, 0)),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReplaceOpeningHoursAsync(branch.BranchId, overlapping));

        // The refused call changed nothing: the builder's seven all-day rows are still there.
        Assert.Equal(7, await db.OpeningHours.CountAsync(h => h.BranchId == branch.BranchId));

        var week = new List<OpeningHoursBlock>
        {
            new(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(15, 0)),
            new(DayOfWeek.Monday, new TimeOnly(18, 0), new TimeOnly(23, 0)),
            new(DayOfWeek.Friday, new TimeOnly(10, 0), new TimeOnly(1, 0)),
        };

        var applied = await service.ReplaceOpeningHoursAsync(branch.BranchId, week);

        Assert.Equal(3, applied.Count);
        Assert.True(applied.Single(b => b.Day == DayOfWeek.Friday).ClosesNextDay);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(3, await verify.OpeningHours.CountAsync(h => h.BranchId == branch.BranchId));
    }

    // ------------------------------------------------------------ 12. deleting a used table deactivates it

    [SkippableFact]
    public async Task Deleting_a_table_with_a_past_reservation_deactivates_it_instead_and_says_so()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var usedTableId = branch.TableIds[0];
        var freshTableId = branch.TableIds[1];

        await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, usedTableId, Now.AddDays(-3));

        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var used = await service.DeleteTableAsync(branch.BranchId, usedTableId);

        Assert.False(used.Deleted);
        Assert.True(used.Deactivated);
        Assert.Contains("deactivated", used.Message, StringComparison.OrdinalIgnoreCase);

        var fresh = await service.DeleteTableAsync(branch.BranchId, freshTableId);

        Assert.True(fresh.Deleted);
        Assert.False(fresh.Deactivated);

        await using var verify = fixture.CreateContext(clock);

        var row = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == usedTableId);
        Assert.False(row.IsActive);
        Assert.True(await verify.Reservations.AnyAsync(r => r.DiningTableId == usedTableId));

        Assert.False(await verify.DiningTables.AnyAsync(t => t.Id == freshTableId));
    }

    [SkippableFact]
    public async Task A_floor_plan_that_omits_a_used_table_deactivates_it_and_reports_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var usedTableId = branch.TableIds[0];

        // A seating, closed - history all the same.
        var session = TableSession.SeatWalkIn(branch.BranchId, usedTableId, 2, Now.AddDays(-1), branch.WaiterId);
        session.Close(Now.AddDays(-1).AddHours(1));
        db.TableSessions.Add(session);
        await db.SaveChangesAsync();

        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var plan = await service.GetFloorPlanAsync(branch.BranchId);

        // Keep tables 2 and 3 (by id), drop table 1, add a new table 4.
        var kept = plan.Tables.Where(t => t.Id != usedTableId).Select(Input).ToList();
        kept.Add(new FloorTableInput(null, "4", 6, 600, 400, 120, 120, 0d, TableShape.Rectangle));

        var result = await service.ReplaceFloorPlanAsync(
            branch.BranchId, new ReplaceFloorPlanCommand(1000, 700, [new FloorAreaInput(null, "Windows", 0)], kept));

        Assert.Equal(["1"], result.DeactivatedTables);
        Assert.Empty(result.RemovedTables);
        Assert.Equal(4, result.Plan.Tables.Count);
        Assert.False(result.Plan.Tables.Single(t => t.Id == usedTableId).IsActive);
        Assert.True(result.Plan.Tables.Single(t => t.Label == "4").IsActive);
    }

    // ------------------------------------------------------------ 13. geometry edits never touch the QR token

    [SkippableFact]
    public async Task Editing_a_tables_geometry_does_not_change_its_QrToken_and_regeneration_is_explicit_and_audited()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var before = await service.GetFloorPlanAsync(branch.BranchId);
        var table = before.Tables.First();
        var originalToken = table.QrToken;

        // Move it across the room, resize it, rename it, drop the id so it matches by label.
        var edited = before.Tables.Select(Input).ToList();
        var index = edited.FindIndex(t => t.Id == table.Id);
        edited[index] = edited[index] with { Id = null, X = 800, Y = 500, Width = 150, Height = 120, RotationDegrees = 45d, Label = table.Label };

        var result = await service.ReplaceFloorPlanAsync(
            branch.BranchId, new ReplaceFloorPlanCommand(1000, 700, [], edited));

        var moved = result.Plan.Tables.Single(t => t.Id == table.Id);
        Assert.Equal(800, moved.X);
        Assert.Equal(150, moved.Width);
        Assert.Equal(originalToken, moved.QrToken);

        // The one deliberate way it changes.
        var regenerated = await service.RegenerateQrTokenAsync(table.Id);

        Assert.NotEqual(originalToken, regenerated.QrToken);

        await using var verify = fixture.CreateContext(clock);
        var audit = await verify.PlatformAuditLogs.AsNoTracking()
            .SingleAsync(l => l.TargetId == table.Id && l.Action == "table.regenerate-qr");

        Assert.Equal(branch.ManagerId, audit.ActorStaffMemberId);
        Assert.Contains(originalToken, audit.ChangesJson);
    }

    // ------------------------------------------------------------ 10 and 11, through the database

    [SkippableFact]
    public async Task A_plan_with_a_table_outside_the_canvas_is_rejected_and_nothing_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);

        var tables = before.Tables.Select(Input).ToList();
        tables[0] = tables[0] with { X = 980, Width = 100 };

        var refused = await Assert.ThrowsAsync<FloorPlanInvalidException>(
            () => service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand(1000, 700, [], tables)));

        Assert.Equal([tables[0].Label], refused.TablesOutsideCanvas);

        await using var verify = fixture.CreateContext(clock);
        var unchanged = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == before.Tables[0].Id);
        Assert.Equal(before.Tables[0].X, unchanged.X);
    }

    [SkippableFact]
    public async Task A_plan_with_overlapping_tables_succeeds_with_a_warning()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);

        // Spread the fixture's tables out first - the builder packs them 50 units apart with
        // 90-unit widths, so they overlap already - then sit table 2 on top of table 1.
        var tables = before.Tables.Select(Input).Select((t, i) => t with { X = 100 + (i * 300), Y = 100 }).ToList();
        tables[1] = tables[1] with { X = tables[0].X + 20, Y = tables[0].Y + 20 };

        var result = await service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand(1000, 700, [], tables));

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("overlap", warning);
        Assert.Contains(tables[0].Label, warning);
        Assert.Contains(tables[1].Label, warning);
        Assert.Equal(tables[0].X + 20, result.Plan.Tables.Single(t => t.Label == tables[1].Label).X);
    }

    // ------------------------------------------------------------ helpers

    private static FloorTableInput Input(FloorTableView t) =>
        new(t.Id, t.Label, t.Seats, t.X, t.Y, t.Width, t.Height, t.RotationDegrees, t.Shape, null, t.IsBookable);

    private static ReservationPolicyCommand Command(ReservationPolicyView p) => new(
        p.TurnTimeMinutes, p.BufferMinutes, p.GraceMinutes, p.LateNudgeAfterMinutes, p.GraceExtensionMinutes,
        p.MinLeadMinutes, p.BookingWindowDays, p.CancellationDeadlineMinutes, p.AutoConfirm, p.ServiceChargePercent,
        p.PricesIncludeVat, p.MaxSeatOverhang, p.ApprovalRequiredAbovePartySize);
}
