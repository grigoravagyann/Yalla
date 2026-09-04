using Microsoft.EntityFrameworkCore;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The state machine against a real SQL Server, because the guarantees under test - rowversion
/// concurrency, filtered unique indexes, one transaction per change - only exist there.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class TableStateServiceTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 4, 15, 15, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ 1. every valid transition

    [SkippableFact]
    public async Task SeatWalkIn_occupies_the_table_and_writes_exactly_one_audit_row()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        var result = await service.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.NewGuid()));

        Assert.Equal(TableStatus.Free, result.FromStatus);
        Assert.Equal(TableStatus.Occupied, result.ToStatus);
        Assert.Equal(DerivedTableState.Occupied, result.State);
        Assert.NotNull(result.TableSessionId);
        Assert.False(result.WasReplay);

        await AssertOneAuditRowAsync(tableId, TableStatus.Free, TableStatus.Occupied);

        // The cache and the authoritative record agree.
        await using var verify = fixture.CreateContext(clock);
        var table = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId);
        Assert.Equal(TableStatus.Occupied, table.Status);
        Assert.Equal(result.TableSessionId, table.CurrentSessionId);

        var session = await verify.TableSessions.AsNoTracking().SingleAsync(s => s.DiningTableId == tableId);
        Assert.Equal(TableSessionSource.WalkIn, session.Source);
        Assert.Null(session.ClosedAtUtc);
        Assert.Equal(branch.WaiterId, session.SeatedByStaffId);
    }

    [SkippableFact]
    public async Task SeatReservation_occupies_the_table_and_seats_the_booking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var tableId = branch.FirstTableId;

        // Far enough out that no warning muddies the assertions.
        var reservation = await TestBranchBuilder.AddConfirmedReservationAsync(
            db, branch, tableId, Now.AddHours(6));

        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        var result = await service.SeatReservationAsync(
            new SeatReservationCommand(branch.BranchId, tableId, reservation.Id, Guid.NewGuid()));

        Assert.Equal(TableStatus.Occupied, result.ToStatus);
        Assert.Equal(reservation.Id, result.ReservationId);
        Assert.Empty(result.Warnings);

        await AssertOneAuditRowAsync(tableId, TableStatus.Free, TableStatus.Occupied);

        await using var verify = fixture.CreateContext(clock);

        // The booking moved in the same transaction as the table and the session.
        var stored = await verify.Reservations.AsNoTracking().FirstAsync(r => r.Id == reservation.Id);
        Assert.Equal(ReservationStatus.Seated, stored.Status);

        var session = await verify.TableSessions.AsNoTracking().SingleAsync(s => s.DiningTableId == tableId);
        Assert.Equal(TableSessionSource.Reservation, session.Source);
        Assert.Equal(reservation.Id, session.ReservationId);
    }

    [SkippableFact]
    public async Task HoldForLateParty_then_ReleaseHold_each_write_one_audit_row()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        var held = await service.HoldForLatePartyAsync(
            new TableStateCommand(branch.BranchId, tableId, Guid.NewGuid()));

        Assert.Equal(TableStatus.Held, held.ToStatus);
        Assert.Equal(DerivedTableState.Held, held.State);

        // The clock is frozen, so without advancing it both audit rows share an AtUtc and the
        // OrderBy below has no tie-break. A hold is also released minutes later in reality.
        clock.Advance(TimeSpan.FromMinutes(5));

        var released = await service.ReleaseHoldAsync(
            new TableStateCommand(branch.BranchId, tableId, Guid.NewGuid()));

        Assert.Equal(TableStatus.Held, released.FromStatus);
        Assert.Equal(TableStatus.Free, released.ToStatus);

        await using var verify = fixture.CreateContext(clock);
        var audit = await verify.TableStateChanges.AsNoTracking()
            .Where(c => c.DiningTableId == tableId)
            .OrderBy(c => c.AtUtc)
            .ToListAsync();

        Assert.Equal(2, audit.Count);
        Assert.Equal(TableStatus.Held, audit[0].ToStatus);
        Assert.Equal(TableStatus.Free, audit[1].ToStatus);
    }

    [SkippableFact]
    public async Task SeatHeldParty_occupies_a_held_table()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        await service.HoldForLatePartyAsync(new TableStateCommand(branch.BranchId, tableId, Guid.NewGuid()));

        var result = await service.SeatHeldPartyAsync(
            new SeatHeldPartyCommand(branch.BranchId, tableId, PartySize: 3, Guid.NewGuid()));

        Assert.Equal(TableStatus.Held, result.FromStatus);
        Assert.Equal(TableStatus.Occupied, result.ToStatus);
        Assert.NotNull(result.TableSessionId);

        await using var verify = fixture.CreateContext(clock);
        var session = await verify.TableSessions.AsNoTracking().SingleAsync(s => s.DiningTableId == tableId);
        Assert.Equal(3, session.PartySize);
    }

    [SkippableFact]
    public async Task FreeTable_closes_the_session_and_the_settled_tab()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        var seated = await service.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.NewGuid()));

        // A tab with nothing owed: zero remaining, so freeing the table closes it.
        await using (var tabDb = fixture.CreateContext(clock))
        {
            await TestBranchBuilder.AddOpenTabAsync(
                tabDb, branch, tableId, seated.TableSessionId!.Value, outstandingAmd: 0L, Now);
        }

        clock.Advance(TimeSpan.FromMinutes(75));

        var freed = await service.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, Guid.NewGuid()));

        Assert.Equal(TableStatus.Occupied, freed.FromStatus);
        Assert.Equal(TableStatus.Free, freed.ToStatus);
        Assert.Null(freed.OutstandingAmd);
        Assert.Empty(freed.Warnings);

        await using var verify = fixture.CreateContext(clock);

        var session = await verify.TableSessions.AsNoTracking().SingleAsync(s => s.DiningTableId == tableId);
        Assert.NotNull(session.ClosedAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(75), session.Duration);

        var tab = await verify.Tabs.AsNoTracking().SingleAsync(t => t.TableSessionId == session.Id);
        Assert.Equal(TabStatus.Closed, tab.Status);

        var table = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId);
        Assert.Null(table.CurrentSessionId);
    }

    [SkippableFact]
    public async Task MarkOutOfService_then_ReturnToService_round_trips()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();

        // A waiter, not a manager: taking a broken table out of service is deliberately not
        // gated on a role, because a broken chair is a Friday-night fact.
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        var out1 = await service.MarkOutOfServiceAsync(
            new TableStateCommand(branch.BranchId, tableId, Guid.NewGuid(), "broken chair"));

        Assert.Equal(TableStatus.OutOfService, out1.ToStatus);
        Assert.Equal(DerivedTableState.OutOfService, out1.State);

        var back = await service.ReturnToServiceAsync(
            new TableStateCommand(branch.BranchId, tableId, Guid.NewGuid()));

        Assert.Equal(TableStatus.OutOfService, back.FromStatus);
        Assert.Equal(TableStatus.Free, back.ToStatus);

        await using var verify = fixture.CreateContext(clock);
        var audit = await verify.TableStateChanges.AsNoTracking()
            .Where(c => c.DiningTableId == tableId)
            .ToListAsync();

        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, a => a.Reason == "broken chair");
    }

    // ------------------------------------------------------------ 2 & 7. invalid transitions

    [SkippableFact]
    public async Task MarkOutOfService_on_an_occupied_table_throws()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        await service.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.NewGuid()));

        var ex = await Assert.ThrowsAsync<InvalidTableTransitionException>(() =>
            service.MarkOutOfServiceAsync(new TableStateCommand(branch.BranchId, tableId, Guid.NewGuid())));

        Assert.Equal(TableStatus.Occupied, ex.FromStatus);
        Assert.Equal(TableStatus.OutOfService, ex.ToStatus);

        // And nothing was written: the refusal happened before SaveChanges.
        await using var verify = fixture.CreateContext(clock);
        var audit = await verify.TableStateChanges.AsNoTracking()
            .CountAsync(c => c.DiningTableId == tableId);

        Assert.Equal(1, audit);
    }

    [SkippableFact]
    public async Task Freeing_a_table_nobody_is_sitting_at_throws()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        await Assert.ThrowsAsync<InvalidTableTransitionException>(() =>
            service.FreeTableAsync(new TableStateCommand(branch.BranchId, branch.FirstTableId, Guid.NewGuid())));
    }

    [SkippableFact]
    public async Task Releasing_a_hold_that_was_never_placed_throws()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        await Assert.ThrowsAsync<InvalidTableTransitionException>(() =>
            service.ReleaseHoldAsync(new TableStateCommand(branch.BranchId, branch.FirstTableId, Guid.NewGuid())));
    }

    // ------------------------------------------------------------ 3. warn, never block

    [SkippableFact]
    public async Task Seating_a_table_with_a_booking_soon_succeeds_and_warns()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var tableId = branch.FirstTableId;

        // 16:00 UTC is 20:00 in Yerevan. Now is 15:15, and the restaurant turn time is 90
        // minutes, so seating anyone now runs into it.
        var bookingStartUtc = new DateTime(2026, 9, 4, 16, 0, 0, DateTimeKind.Utc);
        await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, tableId, bookingStartUtc);

        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        var result = await service.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.NewGuid()));

        // Succeeded. The waiter may know the 20:00 party cancelled; the system does not refuse.
        Assert.Equal(TableStatus.Occupied, result.ToStatus);
        Assert.NotNull(result.TableSessionId);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(TableStateWarning.UpcomingReservation, warning.Code);
        Assert.Contains("table 1 reserved", warning.Message);
        Assert.Equal(bookingStartUtc, result.NextReservationStartUtc);

        // Free-until is the booking less the branch's 15-minute turnaround.
        Assert.Equal(bookingStartUtc.AddMinutes(-15), result.FreeUntilUtc);
    }

    [SkippableFact]
    public async Task The_upcoming_reservation_warning_names_the_wall_clock_time_at_the_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var tableId = branch.FirstTableId;
        var bookingStartUtc = new DateTime(2026, 9, 4, 16, 0, 0, DateTimeKind.Utc);

        await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, tableId, bookingStartUtc);

        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var result = await service.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.NewGuid()));

        var warning = Assert.Single(result.Warnings);

        // "reserved 16:00" would be useless to a waiter standing in Yerevan. Asia/Yerevan is
        // UTC+4 with no daylight saving, so this must read 20:00.
        Assert.Equal("table 1 reserved 20:00", warning.Message);
    }

    // ------------------------------------------------------------ 4. concurrency

    [SkippableFact]
    public async Task Two_concurrent_seatings_of_the_same_table_leave_exactly_one_winner()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (setupDb, clock, branch) = await ArrangeAsync();
        var tableId = branch.FirstTableId;
        await setupDb.DisposeAsync();

        // Two units of work, as two devices would be: the diner's app and the waiter's tablet.
        await using var dinerDb = fixture.CreateContext(clock);
        await using var waiterDb = fixture.CreateContext(clock);

        // Both load the table before either saves, so both hold the same RowVersion. EF Core
        // keeps already-tracked instances as they were, so the second service call reuses these.
        var dinerTable = await dinerDb.DiningTables.Include(t => t.Branch).FirstAsync(t => t.Id == tableId);
        var waiterTable = await waiterDb.DiningTables.Include(t => t.Branch).FirstAsync(t => t.Id == tableId);
        Assert.Equal(TableStatus.Free, dinerTable.Status);
        Assert.Equal(TableStatus.Free, waiterTable.Status);

        var dinerService = fixture.CreateService(dinerDb, clock, TestActor.Waiter(branch.WaiterId));
        var waiterService = fixture.CreateService(waiterDb, clock, TestActor.Waiter(branch.WaiterId));

        // First one home wins.
        var winner = await dinerService.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.NewGuid()));

        Assert.Equal(TableStatus.Occupied, winner.ToStatus);

        // The loser is told what actually happened, not handed a 500.
        var conflict = await Assert.ThrowsAsync<TableStateConflictException>(() =>
            waiterService.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 4, Guid.NewGuid())));

        Assert.Equal(tableId, conflict.TableId);
        Assert.Equal(TableStatus.Free, conflict.AttemptedFromStatus);
        Assert.Equal(TableStatus.Occupied, conflict.CurrentStatus);
        Assert.Equal(winner.TableSessionId, conflict.CurrentSessionId);

        // One session, one audit row: the loser's whole transaction rolled back.
        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, await verify.TableSessions.CountAsync(s => s.DiningTableId == tableId));
        Assert.Equal(1, await verify.TableStateChanges.CountAsync(c => c.DiningTableId == tableId));
    }

    // ------------------------------------------------------------ 5. idempotency

    [SkippableFact]
    public async Task Replaying_the_same_client_command_id_produces_one_session_and_one_audit_row()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        // The tablet queued this while the wifi was down, then sent it twice on reconnect.
        var commandId = Guid.NewGuid();
        var command = new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, commandId);

        var first = await service.SeatWalkInAsync(command);
        var second = await service.SeatWalkInAsync(command);

        Assert.False(first.WasReplay);
        Assert.True(second.WasReplay);

        // The replay is not merely "already done" - it is the same answer, session id included.
        Assert.Equal(first.TableSessionId, second.TableSessionId);
        Assert.Equal(first.FromStatus, second.FromStatus);
        Assert.Equal(first.ToStatus, second.ToStatus);
        Assert.Equal(first.AtUtc, second.AtUtc);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, await verify.TableSessions.CountAsync(s => s.DiningTableId == tableId));
        Assert.Equal(1, await verify.TableStateChanges.CountAsync(c => c.ClientCommandId == commandId));
    }

    [SkippableFact]
    public async Task A_replayed_command_does_not_move_the_table_a_second_time()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;
        var commandId = Guid.NewGuid();

        await service.HoldForLatePartyAsync(new TableStateCommand(branch.BranchId, tableId, commandId));

        // Without idempotency this second call would throw - Held cannot go to Held - which is
        // exactly the error a flaky connection would surface to a waiter.
        var replay = await service.HoldForLatePartyAsync(new TableStateCommand(branch.BranchId, tableId, commandId));

        Assert.True(replay.WasReplay);
        Assert.Equal(TableStatus.Held, replay.ToStatus);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, await verify.TableStateChanges.CountAsync(c => c.DiningTableId == tableId));
    }

    // ------------------------------------------------------------ 6. money never blocks the floor

    [SkippableFact]
    public async Task Freeing_a_table_with_an_outstanding_balance_frees_it_and_leaves_the_tab_open()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        var seated = await service.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, 4, Guid.NewGuid()));

        await using (var tabDb = fixture.CreateContext(clock))
        {
            await TestBranchBuilder.AddOpenTabAsync(
                tabDb, branch, tableId, seated.TableSessionId!.Value, outstandingAmd: 2_000L, Now);
        }

        var freed = await service.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, Guid.NewGuid()));

        // The diners have physically left. Refusing would make the floor plan lie.
        Assert.Equal(TableStatus.Free, freed.ToStatus);
        Assert.Equal(2_000L, freed.OutstandingAmd);

        var warning = Assert.Single(freed.Warnings);
        Assert.Equal(TableStateWarning.OutstandingBalance, warning.Code);
        Assert.Contains("2000 AMD outstanding", warning.Message);

        await using var verify = fixture.CreateContext(clock);

        var table = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId);
        Assert.Equal(TableStatus.Free, table.Status);
        Assert.Null(table.CurrentSessionId);

        // The tab is still open, flagged for staff to resolve.
        var tab = await verify.Tabs.AsNoTracking().SingleAsync(t => t.DiningTableId == tableId);
        Assert.Equal(TabStatus.Open, tab.Status);
        Assert.Equal(2_000L, tab.RemainingAmd);

        // The audit row names the tab, so the money is traceable back to the table state change.
        var audit = await verify.TableStateChanges.AsNoTracking()
            .SingleAsync(c => c.DiningTableId == tableId && c.ToStatus == TableStatus.Free);
        Assert.Equal(tab.Id, audit.TabId);
    }

    // ------------------------------------------------------------ 8. the database's own guarantee

    /// <summary>
    /// The at-most-one-open-session invariant belongs to the database, not to the service. This
    /// bypasses the service entirely - inserting a second open session by hand - because the
    /// point is that the floor stays honest even if the service logic is bypassed or buggy.
    /// </summary>
    [SkippableFact]
    public async Task The_filtered_unique_index_rejects_a_second_open_session()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        await service.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.NewGuid()));

        // A second open session on the same table, inserted behind the service's back.
        await using var rogue = fixture.CreateContext(clock);
        rogue.TableSessions.Add(TableSession.SeatWalkIn(
            branchId: branch.BranchId,
            diningTableId: tableId,
            partySize: 4,
            seatedAtUtc: clock.UtcNow,
            seatedByStaffId: branch.WaiterId));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => rogue.SaveChangesAsync());
        Assert.Contains("UX_TableSessions_OpenPerTable", error.InnerException!.Message);

        // Still exactly one open session, and it is the original.
        await using var verify = fixture.CreateContext(clock);
        var open = await verify.TableSessions.AsNoTracking()
            .Where(s => s.DiningTableId == tableId && s.ClosedAtUtc == null)
            .ToListAsync();

        Assert.Single(open);
        Assert.Equal(2, open[0].PartySize);
    }

    /// <summary>
    /// The same index must not stand in the way of the normal case: a table is seated, freed and
    /// seated again all evening, and only the *open* sessions are constrained.
    /// </summary>
    [SkippableFact]
    public async Task A_table_may_be_seated_again_once_its_previous_session_is_closed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var tableId = branch.FirstTableId;

        await service.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.NewGuid()));

        clock.Advance(TimeSpan.FromMinutes(90));
        await service.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, Guid.NewGuid()));

        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await service.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, 4, Guid.NewGuid()));

        Assert.Equal(TableStatus.Occupied, second.ToStatus);

        await using var verify = fixture.CreateContext(clock);
        var sessions = await verify.TableSessions.AsNoTracking()
            .Where(s => s.DiningTableId == tableId)
            .ToListAsync();

        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.ClosedAtUtc == null);
    }

    // ------------------------------------------------------------ permissions

    [SkippableFact]
    public async Task A_waiter_may_not_write_off_a_tab()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var tableId = branch.FirstTableId;

        var waiterService = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var seated = await waiterService.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.NewGuid()));

        Tab tab;
        await using (var tabDb = fixture.CreateContext(clock))
        {
            tab = await TestBranchBuilder.AddOpenTabAsync(
                tabDb, branch, tableId, seated.TableSessionId!.Value, 2_000L, Now);
        }

        var ex = await Assert.ThrowsAsync<StaffPermissionException>(() =>
            waiterService.AbandonTabAsync(new AbandonTabCommand(branch.BranchId, tab.Id, Guid.NewGuid())));

        Assert.Equal(StaffRole.Manager, ex.RequiredRole);
        Assert.Equal(StaffRole.Waiter, ex.ActualRole);
    }

    [SkippableFact]
    public async Task A_manager_may_write_off_a_tab()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var tableId = branch.FirstTableId;

        var waiterService = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var seated = await waiterService.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.NewGuid()));

        Tab tab;
        await using (var tabDb = fixture.CreateContext(clock))
        {
            tab = await TestBranchBuilder.AddOpenTabAsync(
                tabDb, branch, tableId, seated.TableSessionId!.Value, 2_000L, Now);
        }

        await using var managerDb = fixture.CreateContext(clock);
        var managerService = fixture.CreateService(managerDb, clock, TestActor.Manager(branch.ManagerId));

        var result = await managerService.AbandonTabAsync(
            new AbandonTabCommand(branch.BranchId, tab.Id, Guid.NewGuid(), "walked out"));

        Assert.Equal(2_000L, result.WrittenOffAmd);
        Assert.False(result.WasReplay);

        await using var verify = fixture.CreateContext(clock);
        var stored = await verify.Tabs.AsNoTracking().FirstAsync(t => t.Id == tab.Id);
        Assert.Equal(TabStatus.Abandoned, stored.Status);
    }

    [SkippableFact]
    public async Task A_diner_may_not_change_table_state()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (db, clock, branch) = await ArrangeAsync();
        var service = fixture.CreateService(db, clock, TestActor.Diner());

        await Assert.ThrowsAsync<StaffPermissionException>(() =>
            service.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, branch.FirstTableId, 2, Guid.NewGuid())));
    }

    // ------------------------------------------------------------ helpers

    private async Task<(YallaDbContext Db, TestClock Clock, TestBranch Branch)> ArrangeAsync()
    {
        var clock = new TestClock(Now);
        var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        return (db, clock, branch);
    }

    private async Task AssertOneAuditRowAsync(Guid tableId, TableStatus from, TableStatus to)
    {
        await using var verify = fixture.CreateContext(new TestClock(Now));

        var audit = await verify.TableStateChanges.AsNoTracking()
            .Where(c => c.DiningTableId == tableId)
            .ToListAsync();

        var row = Assert.Single(audit);
        Assert.Equal(from, row.FromStatus);
        Assert.Equal(to, row.ToStatus);
        Assert.Equal(ActorType.Staff, row.ActorType);
        Assert.NotNull(row.ActorId);
        Assert.NotEqual(Guid.Empty, row.ClientCommandId);
    }
}
