using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yalla.Api.Errors;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Booking, against a real SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// The concurrency tests here are the reason this file cannot use the in-memory provider. They
/// race two genuine transactions on two genuine connections, because a test that merely asserts an
/// exception type without two commits actually competing proves nothing about the thing that
/// matters - and the shape being tested, <c>UPDLOCK, HOLDLOCK</c>, does not exist in any fake.
/// </para>
/// <para>
/// The branch throughout is in <c>Asia/Yerevan</c> (UTC+4) with the shipped restaurant policy: a
/// 90-minute turn, 15 minutes of clearing time, 30 minutes of lead, a 14-day window, approval
/// above 8 guests, and at most 2 seats left empty.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ReservationServiceTests(SqlServerFixture fixture)
{
    /// <summary>12:00 on Thursday 10 September 2026 in Yerevan.</summary>
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Three days out: comfortably past the lead time and inside the booking window.</summary>
    private static readonly DateOnly BookingDate = new(2026, 9, 13);

    private static readonly TimeOnly SixPm = new(18, 0);

    // ---------------------------------------------------------------- the happy path

    [SkippableFact]
    public async Task A_booking_is_confirmed_and_carries_the_interval_the_branch_policy_fixes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var view = await service.CreateAsync(Booking(branch, branch.FirstTableId));

        Assert.Equal(ReservationStatus.Confirmed, view.Status);
        Assert.Equal(BookingDate, view.LocalDate);
        Assert.Equal(SixPm, view.LocalStartTime);

        // Yerevan is UTC+4 and the shipped restaurant turn time is 90 minutes.
        Assert.Equal(new DateTime(2026, 9, 13, 14, 0, 0, DateTimeKind.Utc), view.StartUtc);
        Assert.Equal(new DateTime(2026, 9, 13, 15, 30, 0, DateTimeKind.Utc), view.EndUtc);
        Assert.Equal(new TimeOnly(19, 30), view.LocalEndTime);

        // The door code: short, uppercase, and free of the characters that do not survive being
        // read out over the phone.
        Assert.True(ReservationCode.IsWellFormed(view.Code), $"'{view.Code}' is not a readable code.");
        Assert.False(view.WasReplay);
    }

    [SkippableFact]
    public async Task A_second_sitting_starting_exactly_at_the_end_plus_the_buffer_is_accepted()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        // 18:00 + 90 = 19:30, plus 15 minutes of clearing time = 19:45.
        await service.CreateAsync(Booking(branch, branch.FirstTableId));
        var second = await service.CreateAsync(Booking(branch, branch.FirstTableId, new TimeOnly(19, 45)));

        Assert.Equal(ReservationStatus.Confirmed, second.Status);
    }

    [SkippableFact]
    public async Task One_minute_before_the_boundary_is_refused_as_a_conflict()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var first = await service.CreateAsync(Booking(branch, branch.FirstTableId));

        var conflict = await Assert.ThrowsAsync<TableAlreadyBookedException>(
            () => service.CreateAsync(Booking(branch, branch.FirstTableId, new TimeOnly(19, 44))));

        Assert.Equal(first.Id, conflict.ConflictingReservationId);
        Assert.Equal(first.StartUtc, conflict.Conflicting.StartUtc);
        Assert.Equal(first.EndUtc, conflict.Conflicting.EndUtc);

        // The 409 carries the floor as it stands, so the app can redraw and show what changed
        // rather than only saying no.
        Assert.NotNull(conflict.Availability);
        Assert.Contains(conflict.Availability.Tables, t => t.TableId == branch.FirstTableId);

        Assert.Equal(StatusCodes.Status409Conflict, ApiExceptionMapper.Map(conflict).Status);
    }

    [SkippableFact]
    public async Task A_cancelled_booking_stops_blocking_the_slot()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = TestActor.Diner();
        var service = fixture.CreateReservationService(db, clock, diner);

        var first = await service.CreateAsync(Booking(branch, branch.FirstTableId));

        await Assert.ThrowsAsync<TableAlreadyBookedException>(
            () => service.CreateAsync(Booking(branch, branch.FirstTableId)));

        await service.CancelAsync(new CancelReservationCommand(first.Id, "plans changed"));

        // Same slot, same table, and now free - which is the whole point of the status filter.
        var replacement = await service.CreateAsync(Booking(branch, branch.FirstTableId));

        Assert.Equal(ReservationStatus.Confirmed, replacement.Status);
    }

    // ---------------------------------------------------------------- every rule, its own error

    [SkippableFact]
    public async Task Each_branch_rule_refuses_with_its_own_named_error_and_its_own_code()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var eightSeater = await TestBranchBuilder.AddTableAsync(db, branch, "8A", seats: 8);
        var barStool = await TestBranchBuilder.AddTableAsync(db, branch, "B1", seats: 2, isBookable: false);
        var broken = await TestBranchBuilder.AddTableAsync(db, branch, "X1", seats: 4);

        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .MarkOutOfServiceAsync(
                new TableStateCommand(branch.BranchId, broken.Id, Guid.NewGuid(), "broken chair"));

        var failures = new List<ReservationRejectedException>
        {
            // Today at 12:10 local, with 30 minutes of lead required.
            await Refused(() => service.CreateAsync(
                Booking(branch, branch.FirstTableId, new TimeOnly(12, 10), new DateOnly(2026, 9, 10)))),

            // Twenty days out, with a 14-day window.
            await Refused(() => service.CreateAsync(
                Booking(branch, branch.FirstTableId, SixPm, BookingDate.AddDays(20)))),

            // 07:00, three hours before the branch opens.
            await Refused(() => service.CreateAsync(
                Booking(branch, branch.FirstTableId, new TimeOnly(7, 0)))),

            // Six people at a four-seater.
            await Refused(() => service.CreateAsync(
                Booking(branch, branch.FirstTableId, partySize: 6))),

            // Two people at an eight-seater, with at most two seats allowed to sit empty.
            await Refused(() => service.CreateAsync(
                Booking(branch, eightSeater.Id, partySize: 2))),

            await Refused(() => service.CreateAsync(Booking(branch, barStool.Id, partySize: 2))),

            await Refused(() => service.CreateAsync(Booking(branch, broken.Id, partySize: 2))),
        };

        // Seven rules, seven exception types, seven wire codes. A client that could not tell them
        // apart could only ever show "invalid booking".
        Assert.Equal(7, failures.Select(f => f.GetType()).Distinct().Count());
        Assert.Equal(7, failures.Select(f => f.Code).Distinct().Count());
        Assert.Equal(7, failures.Select(f => f.Reason).Distinct().Count());

        Assert.Collection(
            failures,
            f => Assert.IsType<LeadTimeTooShortException>(f),
            f => Assert.IsType<OutsideBookingWindowException>(f),
            f => Assert.IsType<OutsideOpeningHoursException>(f),
            f => Assert.IsType<PartyExceedsTableCapacityException>(f),
            f => Assert.IsType<SeatOverhangExceededException>(f),
            f => Assert.IsType<TableNotBookableException>(f),
            f => Assert.IsType<TableOutOfServiceException>(f));

        // All 422, each with its own code and the numbers behind it.
        Assert.All(failures, f =>
        {
            var mapped = ApiExceptionMapper.Map(f);

            Assert.Equal(StatusCodes.Status422UnprocessableEntity, mapped.Status);
            Assert.Equal(f.Code, mapped.Code);
            Assert.NotNull(mapped.Context);
        });
    }

    [SkippableFact]
    public async Task Two_people_may_take_the_eight_seater_when_the_branch_sets_no_overhang_limit()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db, policy: PolicyWith(maxSeatOverhang: null));
        var eightSeater = await TestBranchBuilder.AddTableAsync(db, branch, "8A", seats: 8);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var view = await service.CreateAsync(Booking(branch, eightSeater.Id, partySize: 2));

        Assert.Equal(ReservationStatus.Confirmed, view.Status);
    }

    [SkippableFact]
    public async Task A_2230_booking_at_a_venue_open_until_0100_succeeds()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(
            db, openingHours: TestBranchBuilder.LateNightEveryDay);

        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        // 22:30 to 00:00 local, inside the block that opened at 10:00 the previous day.
        var view = await service.CreateAsync(
            Booking(branch, branch.FirstTableId, new TimeOnly(22, 30)));

        Assert.Equal(ReservationStatus.Confirmed, view.Status);
        Assert.Equal(new TimeOnly(0, 0), view.LocalEndTime);
    }

    // ---------------------------------------------------------------- approval, not rejection

    [SkippableFact]
    public async Task A_party_above_the_approval_threshold_waits_for_staff_rather_than_being_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db);
        var bigTable = await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        // Nine guests, threshold eight. Two seats left empty, which is inside the overhang limit.
        var view = await service.CreateAsync(Booking(branch, bigTable.Id, partySize: 9));

        Assert.Equal(ReservationStatus.PendingApproval, view.Status);
        Assert.Equal(ApprovalTrigger.LargeParty, view.AwaitingApprovalBecause);
    }

    [SkippableFact]
    public async Task A_branch_that_confirms_nothing_automatically_sends_every_booking_for_approval()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db, policy: PolicyWith(autoConfirm: false));
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var view = await service.CreateAsync(Booking(branch, branch.FirstTableId));

        Assert.Equal(ReservationStatus.PendingApproval, view.Status);
        Assert.Equal(ApprovalTrigger.BranchApprovesEveryBooking, view.AwaitingApprovalBecause);
    }

    [SkippableFact]
    public async Task A_pending_booking_still_holds_the_slot_against_everyone_else()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var branch = await TestBranchBuilder.CreateAsync(db, policy: PolicyWith(autoConfirm: false));
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        await service.CreateAsync(Booking(branch, branch.FirstTableId));

        // Offering the slot to somebody else while a manager decides is how one of the two parties
        // gets turned away at the door.
        await Assert.ThrowsAsync<TableAlreadyBookedException>(
            () => service.CreateAsync(Booking(branch, branch.FirstTableId)));
    }

    [SkippableFact]
    public async Task A_diner_over_the_no_show_threshold_loses_instant_confirmation_and_nothing_else()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 6);
        var diner = TestActor.Diner();

        await AddNoShowsAsync(db, branch, diner.DinerUserId!.Value, count: 4);

        var service = fixture.CreateReservationService(db, clock, diner);
        var view = await service.CreateAsync(Booking(branch, branch.FirstTableId));

        Assert.Equal(ReservationStatus.PendingApproval, view.Status);
        Assert.Equal(ApprovalTrigger.NoShowHistory, view.AwaitingApprovalBecause);
    }

    [SkippableFact]
    public async Task The_no_show_rule_switches_off_with_one_setting()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 6);
        var diner = TestActor.Diner();

        await AddNoShowsAsync(db, branch, diner.DinerUserId!.Value, count: 4);

        var service = fixture.CreateReservationService(
            db, clock, diner, noShowPolicy: new NoShowPolicy { Enabled = false });

        var view = await service.CreateAsync(Booking(branch, branch.FirstTableId));

        Assert.Equal(ReservationStatus.Confirmed, view.Status);
    }

    // ---------------------------------------------------------------- concurrency

    [SkippableFact]
    public async Task Two_concurrent_bookings_for_the_same_table_and_overlapping_times_leave_exactly_one_winner()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var setup = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(setup);

        // Two contexts, two connections, two real transactions. Sharing one context would
        // serialise on the context itself and prove nothing about the database.
        await using var dbA = fixture.CreateContext(clock);
        await using var dbB = fixture.CreateContext(clock);

        var serviceA = fixture.CreateReservationService(dbA, clock, TestActor.Diner());
        var serviceB = fixture.CreateReservationService(dbB, clock, TestActor.Diner());

        var (a, b) = await RaceAsync(
            () => serviceA.CreateAsync(Booking(branch, branch.FirstTableId, SixPm)),

            // Overlapping: 18:30 falls inside the 18:00-19:30 sitting.
            () => serviceB.CreateAsync(Booking(branch, branch.FirstTableId, new TimeOnly(18, 30))));

        var winners = new[] { a, b }.Where(r => r.View is not null).ToList();
        var losers = new[] { a, b }.Where(r => r.Error is not null).ToList();

        Assert.Single(winners);
        Assert.Single(losers);

        var conflict = Assert.IsType<TableAlreadyBookedException>(losers[0].Error);
        Assert.Equal(StatusCodes.Status409Conflict, ApiExceptionMapper.Map(conflict).Status);

        // And the database agrees: one booking on that table, not two.
        await using var verify = fixture.CreateContext(clock);
        var stored = await verify.Reservations
            .AsNoTracking()
            .CountAsync(r => r.DiningTableId == branch.FirstTableId);

        Assert.Equal(1, stored);
    }

    [SkippableFact]
    public async Task Two_concurrent_bookings_for_the_same_table_at_different_times_both_succeed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // This is the test that fails if somebody reaches for optimistic concurrency here. Bumping
        // the parent table's RowVersion to force a clash would refuse this booking too - and these
        // are exactly the tables a venue most wants booked twice in an evening.
        var clock = new TestClock(Now);
        await using var setup = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(setup);

        await using var dbA = fixture.CreateContext(clock);
        await using var dbB = fixture.CreateContext(clock);

        var serviceA = fixture.CreateReservationService(dbA, clock, TestActor.Diner());
        var serviceB = fixture.CreateReservationService(dbB, clock, TestActor.Diner());

        var (a, b) = await RaceAsync(
            () => serviceA.CreateAsync(Booking(branch, branch.FirstTableId, SixPm)),

            // 19:45 is exactly 18:00 + 90 minutes of sitting + 15 minutes of clearing time.
            () => serviceB.CreateAsync(Booking(branch, branch.FirstTableId, new TimeOnly(19, 45))));

        Assert.Null(a.Error);
        Assert.Null(b.Error);
        Assert.NotNull(a.View);
        Assert.NotNull(b.View);
        Assert.NotEqual(a.View!.Id, b.View!.Id);

        await using var verify = fixture.CreateContext(clock);
        var stored = await verify.Reservations
            .AsNoTracking()
            .CountAsync(r => r.DiningTableId == branch.FirstTableId);

        Assert.Equal(2, stored);
    }

    [SkippableFact]
    public async Task Replaying_the_same_client_command_id_returns_the_original_and_creates_no_second_row()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var setup = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(setup);

        var diner = TestActor.Diner();
        var commandId = Guid.CreateVersion7();
        var command = Booking(branch, branch.FirstTableId, SixPm) with { ClientCommandId = commandId };

        await using var dbA = fixture.CreateContext(clock);
        await using var dbB = fixture.CreateContext(clock);

        var first = await fixture.CreateReservationService(dbA, clock, diner).CreateAsync(command);

        // A phone that never saw the first response and sent the whole thing again.
        var replay = await fixture.CreateReservationService(dbB, clock, diner).CreateAsync(command);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.Code, replay.Code);
        Assert.True(replay.WasReplay);
        Assert.False(first.WasReplay);

        await using var verify = fixture.CreateContext(clock);
        var stored = await verify.Reservations
            .AsNoTracking()
            .CountAsync(r => r.ClientCommandId == commandId);

        Assert.Equal(1, stored);
    }

    [SkippableFact]
    public async Task Two_racing_replays_of_one_command_still_produce_a_single_booking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // The case a check-then-insert loses: both retries look up the command id, both find
        // nothing, and both insert. The unique index is what settles it.
        var clock = new TestClock(Now);
        await using var setup = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(setup);

        var diner = TestActor.Diner();
        var commandId = Guid.CreateVersion7();
        var command = Booking(branch, branch.FirstTableId, SixPm) with { ClientCommandId = commandId };

        await using var dbA = fixture.CreateContext(clock);
        await using var dbB = fixture.CreateContext(clock);

        var serviceA = fixture.CreateReservationService(dbA, clock, diner);
        var serviceB = fixture.CreateReservationService(dbB, clock, diner);

        var (a, b) = await RaceAsync(
            () => serviceA.CreateAsync(command),
            () => serviceB.CreateAsync(command));

        Assert.Null(a.Error);
        Assert.Null(b.Error);
        Assert.Equal(a.View!.Id, b.View!.Id);

        await using var verify = fixture.CreateContext(clock);
        var stored = await verify.Reservations
            .AsNoTracking()
            .CountAsync(r => r.ClientCommandId == commandId);

        Assert.Equal(1, stored);
    }

    // ---------------------------------------------------------------- cancelling and deciding

    [SkippableFact]
    public async Task A_cancellation_before_the_deadline_is_not_recorded_as_late()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var booking = await service.CreateAsync(Booking(branch, branch.FirstTableId));
        var cancelled = await service.CancelAsync(new CancelReservationCommand(booking.Id, "plans changed"));

        Assert.Equal(ReservationStatus.CancelledByDiner, cancelled.Status);
        Assert.False(cancelled.CancelledAfterDeadline);
        Assert.Equal("plans changed", cancelled.CancellationReason);
    }

    [SkippableFact]
    public async Task A_cancellation_past_the_deadline_is_still_allowed_and_recorded_as_late()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var booking = await service.CreateAsync(Booking(branch, branch.FirstTableId));

        // One hour before the sitting, with a two-hour free-cancellation deadline.
        clock.UtcNow = booking.StartUtc.AddHours(-1);

        var cancelled = await service.CancelAsync(new CancelReservationCommand(booking.Id));

        // Refusing would convert this into a no-show, which costs the venue the same table plus
        // the chance to resell it.
        Assert.Equal(ReservationStatus.CancelledByDiner, cancelled.Status);
        Assert.True(cancelled.CancelledAfterDeadline);
    }

    [SkippableFact]
    public async Task A_diner_may_not_cancel_somebody_elses_booking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var booking = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId));

        var stranger = fixture.CreateReservationService(db, clock, TestActor.Diner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => stranger.CancelAsync(new CancelReservationCommand(booking.Id)));
    }

    [SkippableFact]
    public async Task A_manager_approves_and_rejects_bookings_that_are_waiting()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, policy: PolicyWith(autoConfirm: false));
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var toApprove = await diner.CreateAsync(Booking(branch, branch.TableIds[0]));
        var toReject = await diner.CreateAsync(Booking(branch, branch.TableIds[1]));

        var manager = fixture.CreateReservationService(db, clock, TestActor.Manager(branch.ManagerId));

        var approved = await manager.ApproveAsync(new DecideReservationCommand(toApprove.Id));
        var rejected = await manager.RejectAsync(new DecideReservationCommand(toReject.Id, "no space"));

        Assert.Equal(ReservationStatus.Confirmed, approved.Status);
        Assert.NotNull(approved.ConfirmedAtUtc);
        Assert.Equal(ReservationStatus.CancelledByVenue, rejected.Status);
        Assert.Equal("no space", rejected.CancellationReason);
    }

    [SkippableFact]
    public async Task A_waiter_may_not_approve_a_booking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, policy: PolicyWith(autoConfirm: false));

        var booking = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId));

        var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId));

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => waiter.ApproveAsync(new DecideReservationCommand(booking.Id)));
    }

    [SkippableFact]
    public async Task A_manager_from_another_venue_may_not_decide_this_branchs_bookings()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, policy: PolicyWith(autoConfirm: false));
        var otherVenue = await TestBranchBuilder.CreateAsync(db);

        var booking = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId));

        var outsider = fixture.CreateReservationService(
            db, clock, TestActor.Manager(otherVenue.ManagerId));

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => outsider.ApproveAsync(new DecideReservationCommand(booking.Id)));
    }

    [SkippableFact]
    public async Task A_diner_sees_their_own_bookings_split_into_upcoming_and_past()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 4);
        var diner = TestActor.Diner();
        var service = fixture.CreateReservationService(db, clock, diner);

        var upcoming = await service.CreateAsync(Booking(branch, branch.TableIds[0]));
        var cancelled = await service.CreateAsync(Booking(branch, branch.TableIds[1]));
        await service.CancelAsync(new CancelReservationCommand(cancelled.Id));

        // Somebody else's booking, which must not appear at all.
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[2]));

        var mine = await service.GetMineAsync();

        Assert.Equal(new[] { upcoming.Id }, mine.Upcoming.Select(r => r.Id));

        // A booking cancelled for a future date belongs in the history, not at the top of the
        // screen next to the ones that are still happening.
        Assert.Equal(new[] { cancelled.Id }, mine.Past.Select(r => r.Id));
    }

    [SkippableFact]
    public async Task Booking_requires_a_verified_diner()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => waiter.CreateAsync(Booking(branch, branch.FirstTableId)));
    }

    // ---------------------------------------------------------------- helpers

    private static CreateReservationCommand Booking(
        TestBranch branch,
        Guid tableId,
        TimeOnly? localTime = null,
        DateOnly? localDate = null,
        int partySize = 2) =>
        new(
            branch.BranchId,
            tableId,
            localDate ?? BookingDate,
            localTime ?? SixPm,
            partySize,
            GuestName: "Ani Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7());

    /// <summary>The shipped restaurant policy with one value moved.</summary>
    private static ReservationPolicy PolicyWith(
        bool autoConfirm = true,
        int? maxSeatOverhang = 2,
        int? approvalRequiredAbovePartySize = 8,
        int cancellationDeadlineMinutes = 120) => new(
        turnTimeMinutes: 90,
        bufferMinutes: 15,
        graceMinutes: 15,
        lateNudgeAfterMinutes: 10,
        graceExtensionMinutes: 10,
        minLeadMinutes: 30,
        bookingWindowDays: 14,
        cancellationDeadlineMinutes: cancellationDeadlineMinutes,
        autoConfirm: autoConfirm,
        serviceChargePercent: 10m,
        pricesIncludeVat: true,
        maxSeatOverhang: maxSeatOverhang,
        approvalRequiredAbovePartySize: approvalRequiredAbovePartySize);

    private static async Task<ReservationRejectedException> Refused(Func<Task> attempt)
    {
        try
        {
            await attempt();
        }
        catch (ReservationRejectedException expected)
        {
            return expected;
        }

        throw new Xunit.Sdk.XunitException("The booking was accepted when a branch rule should have refused it.");
    }

    /// <summary>
    /// Runs two bookings at the same moment and reports what each one got.
    /// </summary>
    /// <remarks>
    /// Both wait on the same gate before starting, so they are genuinely in flight together rather
    /// than one finishing before the other begins. A test that lets them run sequentially would
    /// pass against code with no locking at all.
    /// </remarks>
    private static async Task<(Outcome First, Outcome Second)> RaceAsync(
        Func<Task<ReservationView>> first,
        Func<Task<ReservationView>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = Task.Run(async () =>
        {
            await gate.Task;
            return await CaptureAsync(first);
        });

        var b = Task.Run(async () =>
        {
            await gate.Task;
            return await CaptureAsync(second);
        });

        gate.SetResult();

        var results = await Task.WhenAll(a, b);

        return (results[0], results[1]);
    }

    private static async Task<Outcome> CaptureAsync(Func<Task<ReservationView>> attempt)
    {
        try
        {
            return new Outcome(await attempt(), null);
        }
        catch (Exception ex)
        {
            return new Outcome(null, ex);
        }
    }

    /// <summary>
    /// Bookings this diner did not turn up for, written straight to the database - marking a
    /// no-show is a scheduled sweep that is not built yet, and the rolling count only reads rows.
    /// </summary>
    private static async Task AddNoShowsAsync(
        YallaDbContext db,
        TestBranch branch,
        Guid dinerUserId,
        int count)
    {
        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => b.ReservationPolicy)
            .FirstAsync();

        for (var i = 0; i < count; i++)
        {
            // Well in the past, and inside the 90-day rolling window.
            var startUtc = Now.AddDays(-(i + 1) * 7);

            var missed = Reservation.Create(
                branchId: branch.BranchId,
                diningTableId: branch.FirstTableId,
                startUtc: startUtc,
                localDate: DateOnly.FromDateTime(startUtc),
                localStartTime: TimeOnly.FromDateTime(startUtc),
                partySize: 2,
                guestName: "Ani Test",
                guestPhone: "+37411223344",
                code: ReservationCode.Generate(),
                policy: policy,
                dinerUserId: dinerUserId);

            missed.MarkNoShow(startUtc.AddMinutes(30));
            db.Reservations.Add(missed);
        }

        await db.SaveChangesAsync();
    }

    private readonly record struct Outcome(ReservationView? View, Exception? Error);
}
