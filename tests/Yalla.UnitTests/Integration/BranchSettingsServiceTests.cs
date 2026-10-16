using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Application.Tables;
using Yalla.Domain;
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

        // A FieldValidationException rather than a bare ArgumentOutOfRangeException, because the
        // refusal now names the property the console has to highlight. It used to name the English
        // label "Turn time", which is why the console kept a lookup table keyed on server prose.
        var refused = await Assert.ThrowsAsync<FieldValidationException>(
            () => service.UpdateReservationPolicyAsync(branch.BranchId, Command(current) with { TurnTimeMinutes = 5 }));

        Assert.Contains("Turn time", refused.Message);
        Assert.Contains("5 minutes", refused.Message);
        Assert.Equal("turnTimeMinutes", refused.Field);
        Assert.Equal(5, Assert.Single(refused.Violations).Value);

        await Assert.ThrowsAsync<FieldValidationException>(
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

        var overlap = await Assert.ThrowsAsync<FieldValidationException>(
            () => service.ReplaceOpeningHoursAsync(branch.BranchId, overlapping));

        // The body is an array, so the refusal names the block by index and the form can put the
        // message on the row the owner drew rather than at the top.
        Assert.Equal("[1].opensAt", overlap.Field);

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
        kept.Add(new FloorTableInput(null, "4", 6));

        var result = await service.ReplaceFloorPlanAsync(
            branch.BranchId, new ReplaceFloorPlanCommand([new FloorAreaInput(null, "Windows", 0)], kept, plan.Version));

        Assert.Equal(["1"], result.DeactivatedTables);
        Assert.Empty(result.RemovedTables);
        Assert.Equal(4, result.Plan.Tables.Count);
        Assert.False(result.Plan.Tables.Single(t => t.Id == usedTableId).IsActive);
        Assert.True(result.Plan.Tables.Single(t => t.Label == "4").IsActive);
    }

    // ------------------------------------------------------------ 13. edits never touch the QR token

    [SkippableFact]
    public async Task Editing_a_table_and_moving_its_pin_does_not_change_its_QrToken_and_regeneration_is_explicit_and_audited()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var before = await service.GetFloorPlanAsync(branch.BranchId);
        var table = before.Tables.First();
        var originalToken = table.QrToken;
        var room = Assert.Single(before.Areas);
        var photoId = Assert.Single(room.Photos).PhotoId;

        // New seats, a pin across the photo, and no id so it matches by label.
        var edited = before.Tables.Select(t => Pinned(t, room.Name)).ToList();
        var index = edited.FindIndex(t => t.Id == table.Id);
        edited[index] = edited[index] with { Id = null, Seats = 6, PhotoId = photoId, PhotoX = 0.8, PhotoY = 0.25 };

        var result = await service.ReplaceFloorPlanAsync(
            branch.BranchId, new ReplaceFloorPlanCommand(Areas(before), edited, before.Version));

        var moved = result.Plan.Tables.Single(t => t.Id == table.Id);
        Assert.Equal(6, moved.Seats);
        Assert.Equal(photoId, moved.PhotoId);
        Assert.Equal(0.8, moved.PhotoX);
        Assert.Equal(0.25, moved.PhotoY);
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
    public async Task A_plan_with_a_repeated_label_is_rejected_and_nothing_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);

        var tables = before.Tables.Select(Input).ToList();
        tables[1] = tables[1] with { Label = tables[0].Label, Seats = 9 };

        var refused = await Assert.ThrowsAsync<FloorPlanInvalidException>(
            () => service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand([], tables, before.Version)));

        Assert.Equal([tables[0].Label], refused.DuplicateLabels);

        await using var verify = fixture.CreateContext(clock);
        var unchanged = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == before.Tables[1].Id);
        Assert.Equal(before.Tables[1].Seats, unchanged.Seats);
        Assert.Equal(before.Tables[1].PhotoId, unchanged.PhotoId);
    }

    /// <summary>
    /// Staff can seat a table with no pin, so it is saved - but diners are not offered it, and the
    /// editor is told so rather than left to find out from an empty booking screen.
    /// </summary>
    [SkippableFact]
    public async Task A_plan_with_a_bookable_table_that_has_no_pin_succeeds_with_a_warning()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);
        var room = Assert.Single(before.Areas);

        var tables = before.Tables.Select(t => Pinned(t, room.Name)).ToList();
        tables[1] = tables[1] with { PhotoId = null, PhotoX = null, PhotoY = null };
        tables[2] = tables[2] with { PhotoId = null, PhotoX = null, PhotoY = null, IsBookable = false };

        var result = await service.ReplaceFloorPlanAsync(
            branch.BranchId, new ReplaceFloorPlanCommand(Areas(before), tables, before.Version));

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("not pinned", warning);
        Assert.Contains(tables[1].Label, warning);

        // The walk-in-only table is not offered to diners whatever its pin, so it is not warned about.
        Assert.DoesNotContain($" {tables[2].Label},", warning);
        Assert.Null(result.Plan.Tables.Single(t => t.Label == tables[1].Label).PhotoId);
    }

    /// <summary>
    /// A table that was only ever held or taken out of service has no reservation, no session and
    /// no tab - but it does have audit rows, and that foreign key restricts. Deleting it would fail
    /// in the database rather than here.
    /// </summary>
    [SkippableFact]
    public async Task A_table_whose_only_history_is_the_audit_log_is_deactivated_not_deleted()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        // Out of service and back: two TableStateChange rows, no session, no booking, no bill.
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        await machine.MarkOutOfServiceAsync(new TableStateCommand(branch.BranchId, tableId, Guid.CreateVersion7()));
        await machine.ReturnToServiceAsync(new TableStateCommand(branch.BranchId, tableId, Guid.CreateVersion7()));

        await using var editDb = fixture.CreateContext(clock);
        var service = fixture.CreateBranchSettingsService(editDb, clock, TestActor.Manager(branch.ManagerId));

        var result = await service.DeleteTableAsync(branch.BranchId, tableId);

        Assert.False(result.Deleted);
        Assert.True(result.Deactivated);

        await using var verify = fixture.CreateContext(clock);
        Assert.False((await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId)).IsActive);
        Assert.True(await verify.TableStateChanges.AnyAsync(c => c.DiningTableId == tableId));
    }

    /// <summary>The same rule through the whole-plan path, which is how the editor removes a table.</summary>
    [SkippableFact]
    public async Task A_plan_that_omits_an_audited_table_deactivates_it_rather_than_failing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var auditedId = branch.FirstTableId;

        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        await machine.HoldForLatePartyAsync(new TableStateCommand(branch.BranchId, auditedId, Guid.CreateVersion7()));
        await machine.ReleaseHoldAsync(new TableStateCommand(branch.BranchId, auditedId, Guid.CreateVersion7()));

        await using var editDb = fixture.CreateContext(clock);
        var service = fixture.CreateBranchSettingsService(editDb, clock, TestActor.Manager(branch.ManagerId));
        var plan = await service.GetFloorPlanAsync(branch.BranchId);

        var kept = plan.Tables.Where(t => t.Id != auditedId).Select(Input).ToList();
        var result = await service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand([], kept, plan.Version));

        Assert.Equal([plan.Tables.Single(t => t.Id == auditedId).Label], result.DeactivatedTables);
        Assert.Empty(result.RemovedTables);
    }

    /// <summary>
    /// A table with diners at it must not disappear from the plan. The one-at-a-time delete already
    /// refuses this; leaving the table out of a plan is the same act.
    /// </summary>
    [SkippableFact]
    public async Task A_plan_that_omits_an_occupied_table_is_rejected_and_nothing_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var occupiedId = branch.FirstTableId;

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, occupiedId, PartySize: 2, Guid.CreateVersion7()));

        await using var editDb = fixture.CreateContext(clock);
        var service = fixture.CreateBranchSettingsService(editDb, clock, TestActor.Manager(branch.ManagerId));
        var plan = await service.GetFloorPlanAsync(branch.BranchId);
        var label = plan.Tables.Single(t => t.Id == occupiedId).Label;

        var kept = plan.Tables.Where(t => t.Id != occupiedId).Select(Input).ToList();

        var refused = await Assert.ThrowsAsync<FloorPlanInvalidException>(
            () => service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand([], kept, plan.Version)));

        Assert.Contains(label, refused.Message);
        Assert.Contains("seated", refused.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.CreateContext(clock);
        var still = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == occupiedId);
        Assert.True(still.IsActive);
        Assert.Equal(TableStatus.Occupied, still.Status);
    }

    /// <summary>
    /// Renumber table 1 to 13 and add a new table 1 in the same save. The new entry is listed first
    /// on purpose: matched entry by entry, its label reached the stored table 1 before table 1's
    /// own id did, and the save was refused as "matched by more than one entry".
    /// </summary>
    [SkippableFact]
    public async Task Renaming_a_table_and_adding_a_new_one_under_its_old_label_in_one_save_keeps_both()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var before = await service.GetFloorPlanAsync(branch.BranchId);
        var renamed = before.Tables.Single(t => t.Label == "1");

        var tables = before.Tables.Select(Input).ToList();
        tables[tables.FindIndex(t => t.Id == renamed.Id)] = Input(renamed) with { Label = "13" };
        tables.Insert(0, new FloorTableInput(null, "1", 2));

        var result = await service.ReplaceFloorPlanAsync(
            branch.BranchId, new ReplaceFloorPlanCommand([], tables, before.Version));

        // 13 is the old table 1: its id, so its QR code and its history.
        var kept = result.Plan.Tables.Single(t => t.Id == renamed.Id);
        Assert.Equal("13", kept.Label);
        Assert.Equal(renamed.QrToken, kept.QrToken);

        // And 1 is a table that did not exist before.
        var added = result.Plan.Tables.Single(t => t.Label == "1");
        Assert.DoesNotContain(before.Tables, t => t.Id == added.Id);
        Assert.Equal(4, result.Plan.Tables.Count);
    }

    /// <summary>
    /// A new entry carrying an out-of-service table's label used to match that table by label and
    /// bring it back - old QR code, old history - when the editor meant a new table. Putting a
    /// table back is the console's own action, and it sends the table's id; a label alone is refused.
    /// </summary>
    [SkippableFact]
    public async Task A_new_table_under_an_out_of_service_tables_label_is_refused_and_putting_it_back_by_id_works()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var usedTableId = branch.TableIds[0];

        var session = TableSession.SeatWalkIn(branch.BranchId, usedTableId, 2, Now.AddDays(-1), branch.WaiterId);
        session.Close(Now.AddDays(-1).AddHours(1));
        db.TableSessions.Add(session);
        await db.SaveChangesAsync();

        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var plan = await service.GetFloorPlanAsync(branch.BranchId);
        var old = plan.Tables.Single(t => t.Id == usedTableId);

        // Take table 1 out of the plan: it has history, so it is deactivated.
        var active = plan.Tables.Where(t => t.Id != usedTableId).Select(Input).ToList();
        var dropped = await service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand([], active, plan.Version));

        // A new table 1, no id.
        var refused = await Assert.ThrowsAsync<FloorPlanInvalidException>(
            () => service.ReplaceFloorPlanAsync(
                branch.BranchId,
                new ReplaceFloorPlanCommand([], [.. active, new FloorTableInput(null, "1", 2)], dropped.Plan.Version)));

        Assert.Contains(
            "Table 1 is out of service. Put it back instead of adding a new table with the same label.",
            refused.Errors);

        await using (var verify = fixture.CreateContext(clock))
        {
            var row = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == usedTableId);
            Assert.False(row.IsActive);
        }

        // The put-back path: the inactive table's own id.
        var restored = await service.ReplaceFloorPlanAsync(
            branch.BranchId,
            new ReplaceFloorPlanCommand([], [.. active, Input(old)], dropped.Plan.Version));

        var back = restored.Plan.Tables.Single(t => t.Id == usedTableId);
        Assert.True(back.IsActive);
        Assert.Equal(old.QrToken, back.QrToken);
    }

    /// <summary>
    /// The editor never sends an out-of-service table back, so every save omits it. Reporting
    /// "deactivated" for it on every later save tells the manager something happened that did not.
    /// </summary>
    [SkippableFact]
    public async Task A_later_save_does_not_report_a_table_an_earlier_save_deactivated()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var usedTableId = branch.TableIds[0];

        var session = TableSession.SeatWalkIn(branch.BranchId, usedTableId, 2, Now.AddDays(-1), branch.WaiterId);
        session.Close(Now.AddDays(-1).AddHours(1));
        db.TableSessions.Add(session);
        await db.SaveChangesAsync();

        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var plan = await service.GetFloorPlanAsync(branch.BranchId);
        var active = plan.Tables.Where(t => t.Id != usedTableId).Select(Input).ToList();

        var first = await service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand([], active, plan.Version));
        Assert.Equal(["1"], first.DeactivatedTables);

        var second = await service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand([], active, first.Plan.Version));
        Assert.Empty(second.DeactivatedTables);
        Assert.Empty(second.RemovedTables);
        Assert.False(second.Plan.Tables.Single(t => t.Id == usedTableId).IsActive);
    }

    /// <summary>
    /// Renumbering a room swaps labels between existing tables. It is a legal plan, and one a
    /// single pass cannot write - the unique index is checked per statement.
    /// </summary>
    [SkippableFact]
    public async Task Two_tables_can_trade_labels_in_one_plan()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var before = await service.GetFloorPlanAsync(branch.BranchId);
        var first = before.Tables[0];
        var second = before.Tables[1];

        var tables = before.Tables.Select(Input).ToList();
        tables[0] = tables[0] with { Label = second.Label };
        tables[1] = tables[1] with { Label = first.Label };

        var result = await service.ReplaceFloorPlanAsync(
            branch.BranchId, new ReplaceFloorPlanCommand([], tables, before.Version));

        Assert.Equal(second.Label, result.Plan.Tables.Single(t => t.Id == first.Id).Label);
        Assert.Equal(first.Label, result.Plan.Tables.Single(t => t.Id == second.Id).Label);

        // Swapped, not recreated: the QR codes on both tables still work.
        Assert.Equal(first.QrToken, result.Plan.Tables.Single(t => t.Id == first.Id).QrToken);
        Assert.Equal(second.QrToken, result.Plan.Tables.Single(t => t.Id == second.Id).QrToken);

        await using var verify = fixture.CreateContext(clock);
        Assert.DoesNotContain(
            await verify.DiningTables.AsNoTracking().Where(t => t.BranchId == branch.BranchId).Select(t => t.Label).ToListAsync(),
            l => l.StartsWith('~'));
    }

    /// <summary>A payload missing a required member answers with the missing member, not a fault.</summary>
    [SkippableFact]
    public async Task A_plan_with_an_unlabelled_table_or_unnamed_area_is_rejected_cleanly()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var version = (await service.GetFloorPlanAsync(branch.BranchId)).Version;

        var unlabelled = new List<FloorTableInput>
        {
            new(null, null!, 4),
        };

        var noLabel = await Assert.ThrowsAsync<FloorPlanInvalidException>(
            () => service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand([], unlabelled, version)));

        Assert.Contains("no label", noLabel.Message, StringComparison.OrdinalIgnoreCase);

        var noName = await Assert.ThrowsAsync<FloorPlanInvalidException>(
            () => service.ReplaceFloorPlanAsync(
                branch.BranchId,
                new ReplaceFloorPlanCommand([new FloorAreaInput(null, null!, 0)], [], version)));

        Assert.Contains("no name", noName.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The incremental area endpoints, and the promise that deleting an area keeps its tables.</summary>
    [SkippableFact]
    public async Task Floor_areas_can_be_added_renamed_and_removed_without_touching_the_tables()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var created = await service.CreateFloorAreaAsync(branch.BranchId, new FloorAreaCommand("Terrace", 1));
        Assert.Equal("Terrace", created.Name);

        var renamed = await service.UpdateFloorAreaAsync(branch.BranchId, created.Id, new FloorAreaCommand("Garden", 2));
        Assert.Equal("Garden", renamed.Name);
        Assert.Equal(2, renamed.DisplayOrder);

        // The builder's tables live in "Windows"; deleting it must leave them on the floor.
        var plan = await service.GetFloorPlanAsync(branch.BranchId);
        var windows = plan.Areas.Single(a => a.Name == "Windows");
        var inWindows = plan.Tables.Count(t => t.FloorAreaId == windows.Id);
        Assert.True(inWindows > 0);

        await service.DeleteFloorAreaAsync(branch.BranchId, windows.Id);

        var after = await service.GetFloorPlanAsync(branch.BranchId);
        Assert.DoesNotContain(after.Areas, a => a.Id == windows.Id);
        Assert.Equal(plan.Tables.Count, after.Tables.Count);
        Assert.All(after.Tables, t => Assert.NotEqual(windows.Id, t.FloorAreaId));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.DeleteFloorAreaAsync(branch.BranchId, Guid.CreateVersion7()));
    }

    /// <summary>
    /// Equal opening and closing times would derive as "closes after midnight" and then be refused
    /// with a message about after-midnight closing, which is not what the caller got wrong.
    /// </summary>
    [SkippableFact]
    public async Task A_block_that_opens_and_closes_at_the_same_minute_is_refused_by_its_own_name()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var refused = await Assert.ThrowsAsync<FieldValidationException>(
            () => service.ReplaceOpeningHoursAsync(
                branch.BranchId, [new OpeningHoursBlock(DayOfWeek.Monday, new TimeOnly(0, 0), new TimeOnly(0, 0))]));

        Assert.Contains("00:00", refused.Message);
        Assert.Contains("23:59", refused.Message);
        Assert.Equal("[0].closesAt", refused.Field);

        // And the week it suggests instead is accepted.
        var allDay = await service.ReplaceOpeningHoursAsync(
            branch.BranchId, [new OpeningHoursBlock(DayOfWeek.Monday, new TimeOnly(0, 0), new TimeOnly(23, 59))]);

        Assert.False(Assert.Single(allDay).ClosesNextDay);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A table as the editor would send it back with no room and no pin.</summary>
    private static FloorTableInput Input(FloorTableView t) =>
        new(t.Id, t.Label, t.Seats, null, t.IsBookable);

    /// <summary>A table as the editor would send it back: in its room, with the pin it was read with.</summary>
    private static FloorTableInput Pinned(FloorTableView t, string roomName) =>
        new(t.Id, t.Label, t.Seats, roomName, t.IsBookable, t.PhotoId, t.PhotoX, t.PhotoY);

    /// <summary>The rooms as the editor would send them back, each with the photos it was read with.</summary>
    private static List<FloorAreaInput> Areas(FloorPlanView plan) =>
        [.. plan.Areas.Select(a => new FloorAreaInput(a.Id, a.Name, a.DisplayOrder, [.. a.Photos.Select(p => p.PhotoId)]))];

    private static ReservationPolicyCommand Command(ReservationPolicyView p) => new(
        p.TurnTimeMinutes, p.BufferMinutes, p.GraceMinutes, p.LateNudgeAfterMinutes, p.GraceExtensionMinutes,
        p.MinLeadMinutes, p.BookingWindowDays, p.CancellationDeadlineMinutes, p.AutoConfirm, p.ServiceChargePercent,
        p.PricesIncludeVat, p.MaxSeatOverhang, p.ApprovalRequiredAbovePartySize);
}
