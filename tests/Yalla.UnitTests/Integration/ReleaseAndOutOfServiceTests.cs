using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Versioned preconditions, releasing a late booking, and taking a table out of service.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ReleaseAndOutOfServiceTests(SqlServerFixture fixture)
{
    /// <summary>Midday UTC is four in the afternoon in Yerevan, so an evening booking is ahead of
    /// the branch's thirty-minute lead time and inside its opening hours.</summary>
    private static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly BookingDate = new(2026, 9, 13);

    /// <summary>
    /// A threshold of two, so that three no-shows cross it and three venue cancellations do not.
    /// </summary>
    /// <remarks>
    /// The shipped default is three, and <c>RequiresApproval</c> is <c>&gt;</c> rather than
    /// <c>&gt;=</c> - so with the default it is the <i>fourth</i> no-show that costs a diner instant
    /// confirmation. That is deliberate and stays as it is; the test states its own threshold rather
    /// than quietly relying on the shipped one, so it is testing the rule and not the number.
    /// </remarks>
    private static NoShowPolicy Threshold => new() { Threshold = 2 };

    // ------------------------------------------------------------ 4. status is not a version

    /// <summary>
    /// <b>Test 4.</b> A queued command whose status matches but whose row version does not is
    /// refused, and the refusal says which half failed.
    /// </summary>
    /// <remarks>
    /// The case a status check cannot see. The waiter tapped while table 1 was Free; by the time the
    /// tablet synced, a party had been seated, served and cleared, and the table was Free again. The
    /// status matches. The world does not.
    /// </remarks>
    [SkippableFact]
    public async Task A_matching_status_with_a_stale_row_version_is_refused_for_a_different_reason()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tableId = branch.FirstTableId;

        // What the waiter's floor screen showed at the moment of the tap.
        var atTapTime = await FloorVersionAsync(branch, tableId, clock);

        var machine = fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId));

        // Free to Occupied and back to Free while the command sat in the queue.
        await machine.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));
        await machine.FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, Guid.CreateVersion7()));

        var nowVersion = await FloorVersionAsync(branch, tableId, clock);

        Assert.NotEqual(atTapTime, nowVersion);

        await using var syncDb = fixture.CreateContext(clock);
        var sync = fixture.CreateService(syncDb, clock, TestActor.Waiter(branch.WaiterId));

        var stale = new TableStateCommand(
            branch.BranchId,
            tableId,
            Guid.CreateVersion7(),
            Reason: "queued at 20:05",
            Queued: true,
            ExpectedFromStatus: TableStatus.Free,
            ExpectedRowVersion: atTapTime);

        var refused = await Assert.ThrowsAsync<CommandPreconditionFailedException>(
            () => sync.HoldForLatePartyAsync(stale));

        // The status matched. Only the version caught it, and the reason says so - a waiter
        // resolving a conflict list needs to know this is not the same as "somebody took it".
        Assert.Equal(PreconditionFailure.TableChangedAndChangedBack, refused.Failure);
        Assert.Equal(TableStatus.Free, refused.ExpectedFromStatus);
        Assert.Equal(TableStatus.Free, refused.CurrentStatus);
        Assert.Contains("has been used since", refused.Message);

        // A plain status mismatch still reports the other reason.
        await machine.SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));

        await using var otherDb = fixture.CreateContext(clock);
        var other = fixture.CreateService(otherDb, clock, TestActor.Waiter(branch.WaiterId));

        var mismatched = await Assert.ThrowsAsync<CommandPreconditionFailedException>(
            () => other.HoldForLatePartyAsync(new TableStateCommand(
                branch.BranchId, tableId, Guid.CreateVersion7(),
                Queued: true, ExpectedFromStatus: TableStatus.Free)));

        Assert.Equal(PreconditionFailure.StatusChanged, mismatched.Failure);

        // And a current version alongside a matching status is accepted, so the check is not simply
        // refusing everything.
        await using var freeDb = fixture.CreateContext(clock);
        await fixture.CreateService(freeDb, clock, TestActor.Waiter(branch.WaiterId))
            .FreeTableAsync(new TableStateCommand(branch.BranchId, tableId, Guid.CreateVersion7()));

        var current = await FloorVersionAsync(branch, tableId, clock);

        await using var goodDb = fixture.CreateContext(clock);
        var applied = await fixture.CreateService(goodDb, clock, TestActor.Waiter(branch.WaiterId))
            .HoldForLatePartyAsync(new TableStateCommand(
                branch.BranchId, tableId, Guid.CreateVersion7(),
                Queued: true, ExpectedFromStatus: TableStatus.Free, ExpectedRowVersion: current));

        Assert.Equal(TableStatus.Held, applied.ToStatus);
    }

    // ------------------------------------------------------------ 5. the tab is reachable on a cold load

    [SkippableFact]
    public async Task The_floor_carries_the_open_tab_id_for_an_occupied_table_and_null_for_one_without()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);

        var withTab = branch.FirstTableId;
        var seatedNoTab = branch.TableIds[1];

        var qr = await db.DiningTables.Where(t => t.Id == withTab).Select(t => t.QrToken).FirstAsync();

        // One table scanned, so it has a session and a tab.
        Guid tabId;

        await using (var tabDb = fixture.CreateContext(clock))
        {
            var tabs = fixture.CreateTabService(tabDb, clock, new TestActor(ActorType.Diner, null, null, null));
            var opened = await tabs.OpenAsync(new Yalla.Application.Tabs.OpenTabCommand(
                qr, "phone-a", Guid.CreateVersion7(), "Aram"));

            tabId = opened.Tab.TabId;
        }

        // One seated by a waiter, who never scanned: a session and no tab.
        await using (var seatDb = fixture.CreateContext(clock))
        {
            await fixture.CreateService(seatDb, clock, TestActor.Waiter(branch.WaiterId))
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, seatedNoTab, 2, Guid.CreateVersion7()));
        }

        await using var readDb = fixture.CreateContext(clock);
        var floor = await fixture.CreateFloorQuery(readDb, clock)
            .GetFloorStateAsync(branch.BranchId, clock.UtcNow);

        var billed = floor.Tables.Single(t => t.TableId == withTab);
        var unbilled = floor.Tables.Single(t => t.TableId == seatedNoTab);
        var empty = floor.Tables.Single(t => t.TableId == branch.TableIds[2]);

        // The whole point: a cold-loaded staff app can reach the bill without a second endpoint.
        Assert.Equal(tabId, billed.OpenTabId);
        Assert.NotNull(billed.CurrentSessionId);

        Assert.Null(unbilled.OpenTabId);
        Assert.NotNull(unbilled.CurrentSessionId);

        Assert.Null(empty.OpenTabId);
        Assert.Null(empty.CurrentSessionId);

        // And every table carries a version to send back as a precondition.
        Assert.All(floor.Tables, t => Assert.False(string.IsNullOrWhiteSpace(t.RowVersion)));
    }

    // ------------------------------------------------------------ 6, 7 and 8. releasing

    /// <summary>
    /// <b>Test 6.</b> A no-show release frees the held table and goes on the diner's record.
    /// </summary>
    [SkippableFact]
    public async Task Releasing_as_a_no_show_frees_the_table_and_counts_against_the_diner()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 6);
        var diner = TestActor.Diner();

        // Three no-shows: the threshold is three, so the fourth booking is the one that costs it.
        for (var i = 0; i < 3; i++)
        {
            var booking = await BookAsync(branch, branch.TableIds[i], new TimeOnly(18 + i, 0), clock, diner);
            await HoldForAsync(branch, branch.TableIds[i], clock);

            await using var releaseDb = fixture.CreateContext(clock);
            var released = await fixture.CreateReservationService(
                    releaseDb, clock, TestActor.Waiter(branch.WaiterId))
                .ReleaseAsync(new ReleaseReservationCommand(
                    booking, ReleaseOutcome.NoShow, Guid.CreateVersion7()));

            Assert.True(released.TableFreed);
            Assert.True(released.CountsTowardNoShowThreshold);
            Assert.Equal(ReservationStatus.NoShow, released.Reservation.Status);
            Assert.Equal(TableStatus.Free, released.TableStatus);

            // Freed through the state machine, so there is an audit row and the change stream moved.
            await using var verify = fixture.CreateContext(clock);

            Assert.True(await verify.TableStateChanges.AnyAsync(
                c => c.DiningTableId == branch.TableIds[i] && c.ToStatus == TableStatus.Free));
        }

        // The next booking now waits for a human.
        await using var nextDb = fixture.CreateContext(clock);
        var next = await fixture.CreateReservationService(nextDb, clock, diner, Threshold)
            .CreateAsync(Booking(branch, branch.TableIds[4], new TimeOnly(21, 0)));

        Assert.Equal(ReservationStatus.PendingApproval, next.Status);
    }

    /// <summary>
    /// <b>Test 7.</b> The venue's own cancellation frees the table and costs the diner nothing.
    /// </summary>
    /// <remarks>
    /// The reason there are two buttons. A diner who telephoned to say they could not come must not
    /// lose instant confirmation on their next booking, and with one button a busy waiter would tap
    /// the same one for both.
    /// </remarks>
    [SkippableFact]
    public async Task Releasing_as_a_venue_cancellation_frees_the_table_and_costs_the_diner_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 6);
        var diner = TestActor.Diner();

        for (var i = 0; i < 3; i++)
        {
            var booking = await BookAsync(branch, branch.TableIds[i], new TimeOnly(18 + i, 0), clock, diner);
            await HoldForAsync(branch, branch.TableIds[i], clock);

            await using var releaseDb = fixture.CreateContext(clock);
            var released = await fixture.CreateReservationService(
                    releaseDb, clock, TestActor.Waiter(branch.WaiterId))
                .ReleaseAsync(new ReleaseReservationCommand(
                    booking, ReleaseOutcome.CancelledByVenue, Guid.CreateVersion7(), "they telephoned"));

            Assert.True(released.TableFreed);
            Assert.False(released.CountsTowardNoShowThreshold);
            Assert.Equal(ReservationStatus.CancelledByVenue, released.Reservation.Status);
        }

        // Three of them change nothing, under the same threshold that three no-shows crossed in the
        // test above. That contrast is the whole reason there are two buttons.
        await using var nextDb = fixture.CreateContext(clock);
        var next = await fixture.CreateReservationService(nextDb, clock, diner, Threshold)
            .CreateAsync(Booking(branch, branch.TableIds[4], new TimeOnly(21, 0)));

        Assert.Equal(ReservationStatus.Confirmed, next.Status);
    }

    /// <summary>
    /// <b>Test 8.</b> Releasing a booking whose table somebody is sitting at records the outcome and
    /// leaves the table exactly as it is.
    /// </summary>
    [SkippableFact]
    public async Task Releasing_a_booking_on_an_occupied_table_leaves_the_table_alone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);
        var tableId = branch.TableIds[1];

        var booking = await BookAsync(branch, tableId, new TimeOnly(20, 0), clock, TestActor.Diner());

        // Somebody else is sitting there - the booking was late and the table was given away.
        await using (var seatDb = fixture.CreateContext(clock))
        {
            await fixture.CreateService(seatDb, clock, TestActor.Waiter(branch.WaiterId))
                .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableId, 2, Guid.CreateVersion7()));
        }

        await using var releaseDb = fixture.CreateContext(clock);
        var released = await fixture.CreateReservationService(
                releaseDb, clock, TestActor.Waiter(branch.WaiterId))
            .ReleaseAsync(new ReleaseReservationCommand(
                booking, ReleaseOutcome.NoShow, Guid.CreateVersion7()));

        // The booking is released either way - that is a fact about the booking.
        Assert.Equal(ReservationStatus.NoShow, released.Reservation.Status);

        // The table is not touched. Freeing it would make the floor plan lie about where people are,
        // which costs more than a stale hold.
        Assert.False(released.TableFreed);
        Assert.Equal(TableStatus.Occupied, released.TableStatus);

        await using var verify = fixture.CreateContext(clock);

        Assert.Equal(
            TableStatus.Occupied,
            (await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId)).Status);

        // Idempotent: a tablet replaying its queue must not put a second no-show on the record.
        await using var againDb = fixture.CreateContext(clock);
        var again = await fixture.CreateReservationService(
                againDb, clock, TestActor.Waiter(branch.WaiterId))
            .ReleaseAsync(new ReleaseReservationCommand(
                booking, ReleaseOutcome.NoShow, Guid.CreateVersion7()));

        Assert.True(again.WasReplay);
        Assert.Equal(ReservationStatus.NoShow, again.Reservation.Status);
    }

    // ------------------------------------------------------------ 9. out of service surfaces its bookings

    [SkippableFact]
    public async Task Marking_a_table_out_of_service_reports_its_future_bookings_and_cancels_none()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);
        var broken = branch.FirstTableId;

        var eight = await BookAsync(branch, broken, new TimeOnly(18, 0), clock, TestActor.Diner());
        var nine = await BookAsync(branch, broken, new TimeOnly(21, 0), clock, TestActor.Diner());

        // And one on another table, which must not appear.
        await BookAsync(branch, branch.TableIds[1], new TimeOnly(20, 0), clock, TestActor.Diner());

        await using var machineDb = fixture.CreateContext(clock);
        var machine = fixture.CreateService(machineDb, clock, TestActor.Waiter(branch.WaiterId));

        var result = await machine.MarkOutOfServiceAsync(new TableStateCommand(
            branch.BranchId, broken, Guid.CreateVersion7(), "a leg came off"));

        Assert.Equal(TableStatus.OutOfService, result.ToStatus);

        // The whole point: a waiter marking a table broken at six sees who they have to telephone.
        Assert.Equal(2, result.AffectedReservations.Count);
        Assert.Equal([eight, nine], result.AffectedReservations.Select(r => r.ReservationId));
        Assert.All(result.AffectedReservations, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.GuestPhone));
            Assert.False(string.IsNullOrWhiteSpace(r.Code));
            Assert.Equal(ReservationStatus.Confirmed, r.Status);
        });

        // And none of them was cancelled. Ten minutes for a chair and a week for a floor look the
        // same from here; only a person knows which this is.
        await using var verify = fixture.CreateContext(clock);

        Assert.Equal(
            2,
            await verify.Reservations.CountAsync(
                r => r.DiningTableId == broken && r.Status == ReservationStatus.Confirmed));
    }

    // ------------------------------------------------------------ 10. the race Prompt 7 exempted

    /// <summary>
    /// <b>Test 10.</b> A booking racing <c>MarkOutOfService</c> does not commit onto a broken table.
    /// </summary>
    /// <remarks>
    /// Real commits over separate connections. Prompt 7 argued that freeing, holding and marking out
    /// of service only ever narrow what a booking finds; the first two do, and this one does not - a
    /// booking validated while the table was Free and committing afterwards leaves a confirmed
    /// reservation on a table nobody can sit at.
    /// </remarks>
    [SkippableFact]
    public async Task A_booking_racing_out_of_service_does_not_land_on_the_broken_table()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);
        var tableId = branch.FirstTableId;

        await using var bookDb = fixture.CreateContext(clock);
        await using var breakDb = fixture.CreateContext(clock);

        var bookings = fixture.CreateReservationService(bookDb, clock, TestActor.Diner());
        var machine = fixture.CreateService(breakDb, clock, TestActor.Waiter(branch.WaiterId));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var booking = Task.Run(async () =>
        {
            await gate.Task;

            try
            {
                return (object)await bookings.CreateAsync(
                    Booking(branch, tableId, new TimeOnly(20, 0)));
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        var breaking = Task.Run(async () =>
        {
            await gate.Task;

            try
            {
                return (object)await machine.MarkOutOfServiceAsync(new TableStateCommand(
                    branch.BranchId, tableId, Guid.CreateVersion7(), "a leg came off"));
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        gate.SetResult();

        await Task.WhenAll(booking, breaking);

        await using var verify = fixture.CreateContext(clock);

        var table = await verify.DiningTables.AsNoTracking().FirstAsync(t => t.Id == tableId);

        var live = await verify.Reservations
            .AsNoTracking()
            .CountAsync(r => r.DiningTableId == tableId
                             && (r.Status == ReservationStatus.Confirmed
                                 || r.Status == ReservationStatus.PendingApproval));

        // The invariant, whichever of the two won: a broken table has no live bookings on it that
        // nobody was told about. Either the booking landed first and is reported as affected, or the
        // table broke first and the booking never committed.
        if (table.Status == TableStatus.OutOfService)
        {
            var reported = (breaking.Result as TableStateChangeResult)?.AffectedReservations.Count ?? 0;

            Assert.True(
                live == reported,
                $"Table is out of service with {live} live booking(s) but only {reported} were "
                + "reported to the waiter. A confirmed reservation on a broken table that nobody was "
                + "shown is the failure this lock exists to prevent.");
        }
        else
        {
            // The lock made them serial, so a failed out-of-service means the table is still usable
            // and the booking is fine where it is.
            Assert.True(live <= 1);
        }
    }

    // ------------------------------------------------------------ helpers

    private async Task<string> FloorVersionAsync(TestBranch branch, Guid tableId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var floor = await fixture.CreateFloorQuery(db, clock)
            .GetFloorStateAsync(branch.BranchId, clock.UtcNow);

        return floor.Tables.Single(t => t.TableId == tableId).RowVersion;
    }

    private async Task<Guid> BookAsync(
        TestBranch branch,
        Guid tableId,
        TimeOnly at,
        TestClock clock,
        TestActor diner)
    {
        await using var db = fixture.CreateContext(clock);

        var view = await fixture.CreateReservationService(db, clock, diner)
            .CreateAsync(Booking(branch, tableId, at));

        return view.Id;
    }

    /// <summary>Holds the table, which records which booking the hold is for.</summary>
    private async Task HoldForAsync(TestBranch branch, Guid tableId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .HoldForLatePartyAsync(new TableStateCommand(
                branch.BranchId, tableId, Guid.CreateVersion7(), "held for a late booking"));
    }

    private static CreateReservationCommand Booking(TestBranch branch, Guid tableId, TimeOnly at) =>
        new(
            branch.BranchId,
            tableId,
            BookingDate,
            at,
            PartySize: 2,
            GuestName: "Ani Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7());
}
