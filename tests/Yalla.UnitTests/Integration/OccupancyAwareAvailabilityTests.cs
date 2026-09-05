using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The people physically at a table are part of availability.
/// </summary>
/// <remarks>
/// <para>
/// <c>TableSession</c> is the authoritative occupancy record, and until Prompt 7 the conflict rule
/// could not see it. A waiter seated a walk-in at seven, a phone booked the same table for eight,
/// nothing objected, and the diner arrived to find the table still occupied - the exact failure the
/// product exists to prevent, on the traffic that makes up most of a cafe's evening.
/// </para>
/// <para>
/// The other half of the same defect ran the other way: physical status is <i>now</i>, so a query
/// about tomorrow evening reported every table someone is sitting at tonight as occupied, and the
/// diner app dimmed tables that were free.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class OccupancyAwareAvailabilityTests(SqlServerFixture fixture)
{
    /// <summary>18:00 in Yerevan, comfortably inside the fixture's 10:00-23:00 opening hours.</summary>
    private static readonly DateTime Now = new(2026, 9, 5, 14, 0, 0, DateTimeKind.Utc);

    private static readonly TimeZoneInfo Yerevan = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");

    // ------------------------------------------------------------ 3. a walk-in blocks a booking

    [SkippableFact]
    public async Task A_walk_in_seated_now_blocks_a_booking_on_that_table_in_forty_five_minutes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.CreateVersion7()));

        await using var bookDb = fixture.CreateContext(clock);
        var booking = fixture.CreateReservationService(bookDb, clock, TestActor.Diner());

        var refused = await Assert.ThrowsAsync<TableCurrentlyOccupiedException>(
            () => booking.CreateAsync(Booking(branch, tableId, Now.AddMinutes(45))));

        Assert.Equal(tableId, refused.TableId);
        Assert.Equal(ReservationRejectionReason.TableCurrentlyOccupied, refused.Reason);
        Assert.Equal(Now, refused.SeatedAtUtc);

        // The projected finish is the branch's turn time from when they sat down - an estimate, and
        // the message says so, because they may well leave sooner.
        Assert.Equal(Now.AddMinutes(90), refused.ProjectedFreeAtUtc);
        Assert.Contains("may leave sooner", refused.Message);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(0, await verify.Reservations.CountAsync(r => r.DiningTableId == tableId));
    }

    // ------------------------------------------------------------ 4. but only for tonight

    [SkippableFact]
    public async Task A_walk_in_seated_now_does_not_block_a_booking_on_that_table_tomorrow()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.CreateVersion7()));

        await using var bookDb = fixture.CreateContext(clock);

        var booked = await fixture.CreateReservationService(bookDb, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, tableId, Now.AddDays(1)));

        Assert.Equal(ReservationStatus.Confirmed, booked.Status);
        Assert.Equal(tableId, booked.TableId);
    }

    /// <summary>
    /// The projection is the branch's turn time, so a sitting stops blocking once it is over -
    /// the table is bookable again for a slot after the clearing time.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_after_the_sitting_and_its_clearing_time_is_allowed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));
        var secondTableId = branch.TableIds[1];

        // Two tables seated at the same moment, so each boundary case gets an untouched table.
        await machine.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.CreateVersion7()));
        await machine.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, secondTableId, PartySize: 2, Guid.CreateVersion7()));

        // Seated 18:00 local, 90-minute turn, 15-minute clearing: bookable from exactly +105.
        await using var bookDb = fixture.CreateContext(clock);

        var booked = await fixture.CreateReservationService(bookDb, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, tableId, Now.AddMinutes(105)));

        Assert.Equal(ReservationStatus.Confirmed, booked.Status);

        // One minute earlier leaves fourteen minutes to clear a table the branch says needs fifteen.
        await using var tooSoonDb = fixture.CreateContext(clock);

        await Assert.ThrowsAsync<TableCurrentlyOccupiedException>(
            () => fixture.CreateReservationService(tooSoonDb, clock, TestActor.Diner())
                .CreateAsync(Booking(branch, secondTableId, Now.AddMinutes(104))));
    }

    /// <summary>A closed sitting is history and blocks nothing.</summary>
    [SkippableFact]
    public async Task A_sitting_that_has_ended_does_not_block_anything()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        await machine.SeatWalkInAsync(
            new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.CreateVersion7()));

        await machine.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, Guid.CreateVersion7()));

        await using var bookDb = fixture.CreateContext(clock);

        var booked = await fixture.CreateReservationService(bookDb, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, tableId, Now.AddMinutes(45)));

        Assert.Equal(ReservationStatus.Confirmed, booked.Status);
    }

    // ------------------------------------------------------------ 5 and 6. now versus later

    /// <summary>
    /// Test 5. Physical status is a fact about this moment. Reporting it for tomorrow is what made
    /// the diner app dim tables that were free.
    /// </summary>
    [SkippableFact]
    public async Task Floor_state_for_tomorrow_shows_a_table_occupied_right_now_as_free()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.CreateVersion7()));

        await using var readDb = fixture.CreateContext(clock);
        var floorQuery = fixture.CreateFloorQuery(readDb, clock);

        var tomorrowAt20 = Now.AddDays(1).Date.AddHours(16); // 20:00 in Yerevan
        var tomorrow = await floorQuery.GetFloorStateAsync(branch.BranchId, tomorrowAt20);

        var table = tomorrow!.Tables.Single(t => t.TableId == tableId);

        Assert.Equal(DerivedTableState.Free, table.State);
        Assert.Equal(tomorrowAt20, tomorrow.AsOfUtc);

        // The physical row still says Occupied - the projection is what stops it leaking forward.
        Assert.Equal(TableStatus.Occupied, table.PhysicalStatus);
    }

    /// <summary>Test 6. The same table, five minutes out, is still occupied - that is now.</summary>
    [SkippableFact]
    public async Task Floor_state_for_five_minutes_from_now_shows_that_table_as_occupied()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.CreateVersion7()));

        await using var readDb = fixture.CreateContext(clock);
        var floorQuery = fixture.CreateFloorQuery(readDb, clock);

        var soon = await floorQuery.GetFloorStateAsync(branch.BranchId, Now.AddMinutes(5));
        Assert.Equal(DerivedTableState.Occupied, soon!.Tables.Single(t => t.TableId == tableId).State);

        // And right now, the plain case.
        var now = await floorQuery.GetFloorStateAsync(branch.BranchId, Now);
        Assert.Equal(DerivedTableState.Occupied, now!.Tables.Single(t => t.TableId == tableId).State);
    }

    /// <summary>
    /// Just past the horizon the sitting is still projected to be running, so the table is occupied
    /// for a different reason - the projection, not the momentary status.
    /// </summary>
    [SkippableFact]
    public async Task Beyond_the_horizon_the_projected_sitting_decides_and_it_ends_with_the_turn_time()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.CreateVersion7()));

        await using var readDb = fixture.CreateContext(clock);
        var floorQuery = fixture.CreateFloorQuery(readDb, clock);

        // 30 minutes out: past the 5-minute horizon, still inside the 90-minute sitting.
        var during = await floorQuery.GetFloorStateAsync(branch.BranchId, Now.AddMinutes(30));
        Assert.Equal(DerivedTableState.Occupied, during!.Tables.Single(t => t.TableId == tableId).State);

        // 91 minutes out: the sitting is projected to be over, so the table is free again.
        var after = await floorQuery.GetFloorStateAsync(branch.BranchId, Now.AddMinutes(91));
        Assert.Equal(DerivedTableState.Free, after!.Tables.Single(t => t.TableId == tableId).State);
    }

    /// <summary>A table taken out of service stays that way tomorrow: it is not a fact about now.</summary>
    [SkippableFact]
    public async Task Out_of_service_survives_the_horizon_but_a_hold_does_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        var brokenId = branch.FirstTableId;
        var heldId = branch.TableIds[1];

        await machine.MarkOutOfServiceAsync(new TableStateCommand(branch.BranchId, brokenId, Guid.CreateVersion7()));
        await machine.HoldForLatePartyAsync(new TableStateCommand(branch.BranchId, heldId, Guid.CreateVersion7()));

        await using var readDb = fixture.CreateContext(clock);
        var tomorrow = await fixture.CreateFloorQuery(readDb, clock)
            .GetFloorStateAsync(branch.BranchId, Now.AddDays(1));

        Assert.Equal(DerivedTableState.OutOfService, tomorrow!.Tables.Single(t => t.TableId == brokenId).State);

        // A hold is for a party arriving now and means nothing tomorrow.
        Assert.Equal(DerivedTableState.Free, tomorrow.Tables.Single(t => t.TableId == heldId).State);
    }

    // ------------------------------------------------------------ 7. both left joins

    /// <summary>
    /// The table nobody is sitting at and nobody has booked is the most available table in the
    /// room, and it is exactly the one an accidental inner join would drop.
    /// </summary>
    [SkippableFact]
    public async Task A_table_with_neither_a_sitting_nor_a_booking_still_appears()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var seatedId = branch.TableIds[0];
        var bookedId = branch.TableIds[1];
        var untouchedId = branch.TableIds[2];

        // One table with a sitting and no booking, one with a booking and no sitting, one with
        // neither - so a join that dropped either side would be visible.
        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, seatedId, PartySize: 2, Guid.CreateVersion7()));

        await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, bookedId, Now.AddHours(3));

        // 45 minutes out: past the branch's 30-minute lead time, and still inside the sitting that
        // started now, so the seated table has a reason to be refused.
        var slot = TimeZoneInfo.ConvertTimeFromUtc(Now.AddMinutes(45), Yerevan);

        await using var readDb = fixture.CreateContext(clock);
        var availability = await fixture.CreateAvailabilityQuery(readDb, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(
                branch.BranchId,
                PartySize: 2,
                LocalDate: DateOnly.FromDateTime(slot),
                LocalTime: TimeOnly.FromDateTime(slot)));

        Assert.Equal(3, availability!.Tables.Count);

        var untouched = availability.Tables.Single(t => t.TableId == untouchedId);
        Assert.True(untouched.IsAvailable);
        Assert.True(untouched.Window!.HasNoLaterBooking);

        // And the seated one is refused for the right reason - the sitting, not a booking.
        var seated = availability.Tables.Single(t => t.TableId == seatedId);
        Assert.False(seated.IsAvailable);
        Assert.Equal(ReservationRejectionReason.TableCurrentlyOccupied, seated.UnavailableReason);
        Assert.Null(seated.Window);
    }

    // ------------------------------------------------------------ 8. the holdback warning

    /// <summary>
    /// Seating a walk-in close to a booking is allowed and warned about. The waiter knows things
    /// the system does not - that the walk-in is two people wanting a coffee, say - so this is
    /// never a block.
    /// </summary>
    [SkippableFact]
    public async Task Seating_a_walk_in_within_the_holdback_window_succeeds_with_a_warning()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        // Booked for six, twenty minutes away - inside the 30-minute default holdback.
        var booking = await TestBranchBuilder.AddConfirmedReservationAsync(
            db, branch, tableId, Now.AddMinutes(20), partySize: 6);

        var result = await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.CreateVersion7()));

        // Seated. The warning informs; it does not refuse.
        Assert.Equal(TableStatus.Occupied, result.ToStatus);

        var holdback = Assert.Single(result.Warnings, w => w.Code == TableStateWarning.WalkInHoldback);

        // 20:20 in Yerevan, and the size, because that is what the waiter judges with.
        Assert.Contains("18:20", holdback.Message);
        Assert.Contains("for 6", holdback.Message);
        Assert.Contains("30 minutes", holdback.Message);

        _ = booking;
    }

    /// <summary>Well clear of the booking, neither warning fires.</summary>
    [SkippableFact]
    public async Task Seating_a_walk_in_well_clear_of_a_booking_warns_about_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        // Four hours out: beyond both the 90-minute turn time and the 30-minute holdback.
        await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, tableId, Now.AddHours(4));

        var result = await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, PartySize: 2, Guid.CreateVersion7()));

        Assert.Empty(result.Warnings);
    }

    private static CreateReservationCommand Booking(TestBranch branch, Guid tableId, DateTime startUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, Yerevan);

        return new CreateReservationCommand(
            BranchId: branch.BranchId,
            TableId: tableId,
            LocalDate: DateOnly.FromDateTime(local),
            LocalTime: TimeOnly.FromDateTime(local),
            PartySize: 2,
            GuestName: "Ani Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7());
    }
}
