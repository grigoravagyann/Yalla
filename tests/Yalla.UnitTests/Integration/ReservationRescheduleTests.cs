using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yalla.Api.Errors;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Moving a booking, against a real SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// A move is a booking made a second time: every rule creation applies, the clash check under the
/// target table's lock - minus the booking itself - and the status decided again. What it must not do
/// is behave like a cancellation and a new booking: the code, the manage link and the id stay.
/// </para>
/// <para>
/// The races are real, as in <see cref="ReservationServiceTests"/>: two contexts on two connections,
/// or a competing write committed at the exact moment the lock is about to be taken.
/// </para>
/// <para>
/// The branch is in <c>Asia/Yerevan</c> (UTC+4) on the shipped restaurant policy: a 90-minute turn, 15
/// minutes of clearing time, 30 of lead, approval above 8 guests, at most 2 seats left empty, the
/// reminder three hours ahead and the late nudge ten minutes after the start.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ReservationRescheduleTests(SqlServerFixture fixture)
{
    /// <summary>12:00 on Thursday 10 September 2026 in Yerevan.</summary>
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly BookingDate = new(2026, 9, 13);

    private static readonly TimeOnly SixPm = new(18, 0);

    private static readonly TimeOnly EightPm = new(20, 0);

    // ---------------------------------------------------------------- the move itself

    [SkippableFact]
    public async Task A_moved_booking_keeps_its_id_code_and_link_and_lands_on_the_new_slot()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = TestActor.Diner();
        var service = fixture.CreateReservationService(db, clock, diner);

        var booked = await service.CreateAsync(Booking(branch, branch.TableIds[0], SixPm));
        var before = await StoredAsync(booked.Id, clock);

        var moved = await service.RescheduleAsync(Move(booked.Id, branch.TableIds[1], EightPm, partySize: 3));

        // The same booking: the code the diner quotes at the door does not change under them.
        Assert.Equal(booked.Id, moved.Id);
        Assert.Equal(booked.Code, moved.Code);
        Assert.False(moved.WasReplay);

        // At the new slot, with the interval the branch's turn time fixes. 20:00 in Yerevan is 16:00Z.
        Assert.Equal(branch.TableIds[1], moved.TableId);
        Assert.Equal(BookingDate, moved.LocalDate);
        Assert.Equal(EightPm, moved.LocalStartTime);
        Assert.Equal(new DateTime(2026, 9, 13, 16, 0, 0, DateTimeKind.Utc), moved.StartUtc);
        Assert.Equal(moved.StartUtc.AddMinutes(90), moved.EndUtc);
        Assert.Equal(3, moved.PartySize);
        Assert.Equal(ReservationStatus.Confirmed, moved.Status);

        // In the room the new table is in.
        Assert.Equal("Windows", moved.RoomName);
        Assert.Equal(await RoomOfAsync(branch, clock), moved.RoomId);

        // The link already sent keeps working, and the command that made the booking is still its own.
        var after = await StoredAsync(booked.Id, clock);
        Assert.Equal(before.ManageTokenHash, after.ManageTokenHash);
        Assert.Equal(before.ClientCommandId, after.ClientCommandId);

        var mine = await service.GetMineAsync();
        var listed = Assert.Single(mine.Upcoming);
        Assert.Equal(moved.StartUtc, listed.StartUtc);
        Assert.Equal(branch.TableIds[1], listed.TableId);

        // And the floor agrees: the old table is free at six, the new one taken at eight.
        await using var readDb = fixture.CreateContext(clock);
        var availability = fixture.CreateAvailabilityQuery(readDb, clock);

        var atSix = await availability.GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, SixPm));
        Assert.True(atSix!.Tables.Single(t => t.TableId == branch.TableIds[0]).IsAvailable);

        // Taken at the very start of the moved booking: refused as just booked (TableSequencing).
        var atEight = await availability.GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, BookingDate, EightPm));
        Assert.Equal(
            ReservationRejectionReason.TableJustBooked,
            atEight!.Tables.Single(t => t.TableId == branch.TableIds[1]).UnavailableReason);
    }

    [SkippableFact]
    public async Task Moving_by_half_an_hour_on_the_same_table_is_not_refused_because_of_itself()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var booked = await service.CreateAsync(Booking(branch, branch.FirstTableId, SixPm));

        // 18:30 overlaps 18:00-19:30 by an hour. The only booking it overlaps is the one being moved.
        var moved = await service.RescheduleAsync(Move(booked.Id, branch.FirstTableId, new TimeOnly(18, 30)));

        Assert.Equal(new TimeOnly(18, 30), moved.LocalStartTime);

        // Same slot, same table, bigger party: still nothing in its own way.
        var bigger = await service.RescheduleAsync(Move(booked.Id, branch.FirstTableId, new TimeOnly(18, 30), partySize: 4));

        Assert.Equal(4, bigger.PartySize);
    }

    [SkippableFact]
    public async Task A_slot_another_booking_holds_is_a_conflict_and_the_floor_shows_the_diners_own_table_free()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var mine = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var theirs = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[1], new TimeOnly(18, 30)));

        var booked = await mine.CreateAsync(Booking(branch, branch.TableIds[0], SixPm));
        var keysBefore = await OutboxKeysAsync(booked.Id, clock);

        var conflict = await Assert.ThrowsAsync<TableAlreadyBookedException>(
            () => mine.RescheduleAsync(Move(booked.Id, branch.TableIds[1], SixPm)));

        Assert.Equal(theirs.Id, conflict.ConflictingReservationId);
        Assert.Equal(StatusCodes.Status409Conflict, ApiExceptionMapper.Map(conflict).Status);
        Assert.Equal("table-already-booked", ApiExceptionMapper.Map(conflict).Code);

        // The floor the 409 carries leaves the diner's own booking out, as the clash check did: the
        // table they are on reads free at six, which is how they were looking at it.
        Assert.NotNull(conflict.Availability);
        Assert.True(conflict.Availability.Tables.Single(t => t.TableId == branch.TableIds[0]).IsAvailable);

        // Nothing moved, and nothing about its reminders changed.
        var stored = await StoredAsync(booked.Id, clock);
        Assert.Equal(branch.TableIds[0], stored.DiningTableId);
        Assert.Equal(booked.StartUtc, stored.StartUtc);
        Assert.Equal(keysBefore, await OutboxKeysAsync(booked.Id, clock));
    }

    [SkippableFact]
    public async Task A_move_is_judged_by_every_rule_a_new_booking_is()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var unpinned = await TestBranchBuilder.AddTableAsync(db, branch, "U1", seats: 4, pinned: false);
        var elsewhere = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var booked = await service.CreateAsync(Booking(branch, branch.FirstTableId, SixPm));

        await Assert.ThrowsAsync<PartyExceedsTableCapacityException>(
            () => service.RescheduleAsync(Move(booked.Id, branch.FirstTableId, SixPm, partySize: 6)));

        await Assert.ThrowsAsync<OutsideOpeningHoursException>(
            () => service.RescheduleAsync(Move(booked.Id, branch.FirstTableId, new TimeOnly(22, 0))));

        // Today at 12:05, inside the 10-minute lead.
        await Assert.ThrowsAsync<LeadTimeTooShortException>(
            () => service.RescheduleAsync(Move(booked.Id, branch.FirstTableId, new TimeOnly(12, 5), date: new DateOnly(2026, 9, 10))));

        await Assert.ThrowsAsync<OutsideBookingWindowException>(
            () => service.RescheduleAsync(Move(booked.Id, branch.FirstTableId, SixPm, date: new DateOnly(2026, 9, 30))));

        // A table a diner could not have tapped is not one they can move to.
        await Assert.ThrowsAsync<TableNotBookableException>(
            () => service.RescheduleAsync(Move(booked.Id, unpinned.Id, SixPm)));

        // A move changes the slot, never the venue.
        var foreign = await Assert.ThrowsAsync<ArgumentException>(
            () => service.RescheduleAsync(Move(booked.Id, elsewhere.FirstTableId, SixPm)));
        Assert.Equal(StatusCodes.Status400BadRequest, ApiExceptionMapper.Map(foreign).Status);

        var stored = await StoredAsync(booked.Id, clock);
        Assert.Equal(booked.StartUtc, stored.StartUtc);
        Assert.Equal(2, stored.PartySize);
    }

    [SkippableFact]
    public async Task A_booking_that_is_cancelled_or_has_started_is_not_changeable()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var cancelled = await service.CreateAsync(Booking(branch, branch.TableIds[0], SixPm));
        await service.CancelAsync(new CancelReservationCommand(cancelled.Id));

        var refused = await Assert.ThrowsAsync<ReservationNotChangeableException>(
            () => service.RescheduleAsync(Move(cancelled.Id, branch.TableIds[0], EightPm)));

        var mapped = ApiExceptionMapper.Map(refused);
        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal("reservation-not-changeable", mapped.Code);
        Assert.Equal((int)ReservationStatus.CancelledByDiner, mapped.Context!["status"]);

        // Today at 13:00, booked at noon - short notice, so the floor confirms it - and then the clock
        // passes the start.
        var today = await service.CreateAsync(
            Booking(branch, branch.TableIds[1], new TimeOnly(13, 0)) with { LocalDate = new DateOnly(2026, 9, 10) });

        await fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId))
            .ApproveAsync(new DecideReservationCommand(today.Id));

        clock.Advance(TimeSpan.FromMinutes(61));

        // Checked before the new slot is looked at: a perfectly good slot is still refused.
        var started = await Assert.ThrowsAsync<ReservationNotChangeableException>(
            () => service.RescheduleAsync(Move(today.Id, branch.TableIds[1], EightPm)));

        Assert.Equal(ReservationStatus.Confirmed, started.Status);
    }

    [SkippableFact]
    public async Task Somebody_elses_booking_is_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var booked = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId, SixPm));

        var stranger = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var refused = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => stranger.RescheduleAsync(Move(booked.Id, branch.FirstTableId, EightPm)));

        Assert.Equal(StatusCodes.Status404NotFound, ApiExceptionMapper.Map(refused).Status);

        // Word for word what an id that is no booking at all gets.
        Assert.Equal($"Reservation {booked.Id} was not found.", refused.Message);

        var missingId = Guid.CreateVersion7();
        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => stranger.RescheduleAsync(Move(missingId, branch.FirstTableId, EightPm)));

        Assert.Equal($"Reservation {missingId} was not found.", missing.Message);
    }

    // ---------------------------------------------------------------- the status, decided again

    [SkippableFact]
    public async Task The_status_is_decided_again_and_a_second_approval_is_its_own_message()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tenSeater = await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10);

        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var manager = fixture.CreateReservationService(db, clock, TestActor.Manager(branch.ManagerId));

        var booked = await diner.CreateAsync(Booking(branch, branch.FirstTableId, SixPm));
        Assert.Equal(ReservationStatus.Confirmed, booked.Status);

        // Nine people is over the branch's threshold of eight: a human looks at it again.
        var grown = await diner.RescheduleAsync(Move(booked.Id, tenSeater.Id, SixPm, partySize: 9));

        Assert.Equal(ReservationStatus.PendingApproval, grown.Status);
        Assert.Equal(ApprovalTrigger.LargeParty, grown.AwaitingApprovalBecause);

        var approved = await manager.ApproveAsync(new DecideReservationCommand(booked.Id));
        Assert.Equal(ReservationStatus.Confirmed, approved.Status);
        Assert.NotNull(approved.ConfirmedAtUtc);

        // The approval push went out before the diner changed their mind again.
        await MarkSentAsync(OutboxMessageTypes.KeyFor("reservation", booked.Id, "approved"), clock);

        // Nine people the venue said yes to are still nine people it said yes to at eight.
        var later = await diner.RescheduleAsync(Move(booked.Id, tenSeater.Id, EightPm, partySize: 9));

        Assert.Equal(ReservationStatus.Confirmed, later.Status);
        Assert.Equal(approved.ConfirmedAtUtc, later.ConfirmedAtUtc);

        // Ten is more than anybody approved: a human looks again.
        var grownAgain = await diner.RescheduleAsync(Move(booked.Id, tenSeater.Id, EightPm, partySize: 10));

        Assert.Equal(ReservationStatus.PendingApproval, grownAgain.Status);
        Assert.Equal(ApprovalTrigger.LargeParty, grownAgain.AwaitingApprovalBecause);
        Assert.Null(grownAgain.ConfirmedAtUtc);

        // The second approval is its own message, not a collision with the first one's key.
        clock.Advance(TimeSpan.FromMinutes(5));
        var again = await manager.ApproveAsync(new DecideReservationCommand(booked.Id));
        Assert.Equal(ReservationStatus.Confirmed, again.Status);

        var decided = await OutboxKeysAsync(booked.Id, clock, OutboxMessageTypes.ReservationDecided);
        Assert.Equal(2, decided.Count);
        Assert.Contains(OutboxMessageTypes.KeyFor("reservation", booked.Id, "approved"), decided);

        // And a party that shrinks under the threshold is confirmed without anybody.
        var smaller = await diner.RescheduleAsync(Move(booked.Id, branch.FirstTableId, EightPm, partySize: 4));

        Assert.Equal(ReservationStatus.Confirmed, smaller.Status);
        Assert.Null(smaller.AwaitingApprovalBecause);
    }

    /// <summary>
    /// A large party a human confirmed keeps that confirmation when it moves to another table or time
    /// without growing - the venue said yes to that many people, not to a slot.
    /// </summary>
    [SkippableFact]
    public async Task A_confirmed_large_party_that_moves_without_growing_stays_confirmed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tenSeater = await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10);
        var otherTenSeater = await TestBranchBuilder.AddTableAsync(db, branch, "14", seats: 10);

        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var manager = fixture.CreateReservationService(db, clock, TestActor.Manager(branch.ManagerId));

        var booked = await diner.CreateAsync(Booking(branch, tenSeater.Id, SixPm) with { PartySize = 10 });
        Assert.Equal(ApprovalTrigger.LargeParty, booked.AwaitingApprovalBecause);

        var approved = await manager.ApproveAsync(new DecideReservationCommand(booked.Id));
        Assert.Equal(ReservationStatus.Confirmed, approved.Status);

        // The approval push went out before the diner moved the booking.
        await MarkSentAsync(OutboxMessageTypes.KeyFor("reservation", booked.Id, "approved"), clock);

        // Another table, another time, the same ten.
        var moved = await diner.RescheduleAsync(Move(booked.Id, otherTenSeater.Id, EightPm, partySize: 10));

        Assert.Equal(ReservationStatus.Confirmed, moved.Status);
        Assert.Null(moved.AwaitingApprovalBecause);
        Assert.Equal(approved.ConfirmedAtUtc, moved.ConfirmedAtUtc);

        // One fewer, still over the threshold of eight: still approved.
        var fewer = await diner.RescheduleAsync(Move(booked.Id, otherTenSeater.Id, SixPm, partySize: 9));
        Assert.Equal(ReservationStatus.Confirmed, fewer.Status);

        // Nothing went back to the venue, so no second decision was written - only the one sent.
        Assert.Single(await OutboxKeysAsync(booked.Id, clock, OutboxMessageTypes.ReservationDecided));

        // Growing past the booking as it stands is the size question asked afresh.
        var grown = await diner.RescheduleAsync(Move(booked.Id, otherTenSeater.Id, SixPm, partySize: 10));
        Assert.Equal(ReservationStatus.PendingApproval, grown.Status);
        Assert.Equal(ApprovalTrigger.LargeParty, grown.AwaitingApprovalBecause);
        Assert.Null(grown.ConfirmedAtUtc);
    }

    /// <summary>
    /// The confirmation a large party keeps is from the size threshold only. A venue that approves
    /// every booking looks at every move, and a diner whose no-shows cost them instant confirmation
    /// loses it on a move as on a new booking.
    /// </summary>
    [SkippableFact]
    public async Task A_kept_confirmation_does_not_get_past_approve_everything_or_a_no_show_history()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        // A venue that approves every booking.
        var strict = await TestBranchBuilder.CreateAsync(db, policy: ApprovesEveryBooking());
        var strictDiner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var small = await strictDiner.CreateAsync(Booking(strict, strict.FirstTableId, SixPm));
        await fixture.CreateReservationService(db, clock, TestActor.Manager(strict.ManagerId))
            .ApproveAsync(new DecideReservationCommand(small.Id));

        var movedAtStrict = await strictDiner.RescheduleAsync(Move(small.Id, strict.FirstTableId, EightPm));

        Assert.Equal(ReservationStatus.PendingApproval, movedAtStrict.Status);
        Assert.Equal(ApprovalTrigger.BranchApprovesEveryBooking, movedAtStrict.AwaitingApprovalBecause);

        // A diner who has since missed four bookings.
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tenSeater = await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10);
        var dinerActor = TestActor.Diner();
        var diner = fixture.CreateReservationService(db, clock, dinerActor);

        var large = await diner.CreateAsync(Booking(branch, tenSeater.Id, SixPm) with { PartySize = 9 });
        await fixture.CreateReservationService(db, clock, TestActor.Manager(branch.ManagerId))
            .ApproveAsync(new DecideReservationCommand(large.Id));

        await AddNoShowsAsync(branch, dinerActor.DinerUserId!.Value, count: 4, clock);

        var movedWithHistory = await diner.RescheduleAsync(Move(large.Id, tenSeater.Id, EightPm, partySize: 9));

        Assert.Equal(ReservationStatus.PendingApproval, movedWithHistory.Status);
        Assert.Equal(ApprovalTrigger.NoShowHistory, movedWithHistory.AwaitingApprovalBecause);
    }

    // ---------------------------------------------------------------- idempotency

    [SkippableFact]
    public async Task A_retry_of_the_same_change_returns_the_booking_as_it_stands_and_moves_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var dinerId = Guid.CreateVersion7();
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner(dinerId));

        var booked = await service.CreateAsync(Booking(branch, branch.TableIds[0], SixPm));
        var change = Move(booked.Id, branch.TableIds[1], EightPm);

        var first = await service.RescheduleAsync(change);
        var keys = await OutboxKeysAsync(booked.Id, clock);

        // The phone never saw the answer and sent the change again, from a fresh context.
        await using var retryDb = fixture.CreateContext(clock);
        var replay = await fixture.CreateReservationService(retryDb, clock, TestActor.Diner(dinerId)).RescheduleAsync(change);

        Assert.True(replay.WasReplay);
        Assert.False(first.WasReplay);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.StartUtc, replay.StartUtc);
        Assert.Equal(first.TableId, replay.TableId);
        Assert.Equal(keys, await OutboxKeysAsync(booked.Id, clock));

        await using (var verify = fixture.CreateContext(clock))
        {
            var recorded = await verify.ProcessedCommands.AsNoTracking()
                .SingleAsync(c => c.ClientCommandId == change.ClientCommandId);

            Assert.Equal(ReservationService.RescheduleCommandType, recorded.CommandType);
            Assert.Equal(dinerId, recorded.ActorId);

            // Which booking and where it went - never the guest's name or number.
            Assert.DoesNotContain("Ani Test", recorded.ResponseJson, StringComparison.Ordinal);
            Assert.DoesNotContain("+37411223344", recorded.ResponseJson, StringComparison.Ordinal);
        }

        // The answer is the booking as it stands: a retry after the diner cancelled says so.
        await service.CancelAsync(new CancelReservationCommand(booked.Id));
        var afterCancel = await service.RescheduleAsync(change);

        Assert.True(afterCancel.WasReplay);
        Assert.Equal(ReservationStatus.CancelledByDiner, afterCancel.Status);

        // The same id naming somewhere else for the same booking is not a retry either: answering
        // wasReplay would tell the phone a move had happened that had not.
        foreach (var different in new[]
                 {
                     change with { LocalTime = new TimeOnly(20, 30) },
                     change with { LocalDate = BookingDate.AddDays(1) },
                     change with { TableId = branch.TableIds[2] },
                     change with { PartySize = 3 },
                 })
        {
            var reused = await Assert.ThrowsAsync<ClientCommandIdAlreadyUsedException>(
                () => service.RescheduleAsync(different));

            Assert.Equal("client-command-id-in-use", ApiExceptionMapper.Map(reused).Code);
        }

        // And none of them moved it: the booking is where the first change put it, cancelled.
        var untouched = await StoredAsync(booked.Id, clock);
        Assert.Equal(first.StartUtc, untouched.StartUtc);
        Assert.Equal(first.TableId, untouched.DiningTableId);
        Assert.Equal(first.PartySize, untouched.PartySize);

        // The same id on a different booking is not a retry of this change.
        var other = await service.CreateAsync(Booking(branch, branch.TableIds[2], SixPm));

        await Assert.ThrowsAsync<ClientCommandIdAlreadyUsedException>(
            () => service.RescheduleAsync(change with { ReservationId = other.Id }));

        // Nor is it from another diner, on their own booking - and they learn nothing of this one.
        var stranger = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var theirs = await stranger.CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(21, 0)));

        var collision = await Assert.ThrowsAsync<ClientCommandIdAlreadyUsedException>(
            () => stranger.RescheduleAsync(change with { ReservationId = theirs.Id }));

        Assert.Equal("client-command-id-in-use", ApiExceptionMapper.Map(collision).Code);
        Assert.DoesNotContain(booked.Code, collision.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Two_racing_retries_of_one_change_move_the_booking_once()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var setup = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(setup);
        var dinerId = Guid.CreateVersion7();

        var booked = await fixture.CreateReservationService(setup, clock, TestActor.Diner(dinerId))
            .CreateAsync(Booking(branch, branch.TableIds[0], SixPm));

        var change = Move(booked.Id, branch.TableIds[1], EightPm);

        await using var dbA = fixture.CreateContext(clock);
        await using var dbB = fixture.CreateContext(clock);

        var (a, b) = await RaceAsync(
            () => fixture.CreateReservationService(dbA, clock, TestActor.Diner(dinerId)).RescheduleAsync(change),
            () => fixture.CreateReservationService(dbB, clock, TestActor.Diner(dinerId)).RescheduleAsync(change));

        Assert.Null(a.Error);
        Assert.Null(b.Error);
        Assert.Equal(1, new[] { a.View!, b.View! }.Count(v => v.WasReplay));
        Assert.Equal(a.View!.StartUtc, b.View!.StartUtc);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, await verify.ProcessedCommands.CountAsync(c => c.ClientCommandId == change.ClientCommandId));

        // One reminder, one nudge and one "are you on your way?" for the new time - not two of each.
        Assert.Equal(3, (await OutboxKeysAsync(booked.Id, clock)).Count);
    }

    // ---------------------------------------------------------------- races with other writers

    [SkippableFact]
    public async Task A_cancel_that_lands_while_the_move_is_in_flight_wins_and_the_move_is_a_concurrent_update()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var setup = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(setup);
        var dinerId = Guid.CreateVersion7();

        var booked = await fixture.CreateReservationService(setup, clock, TestActor.Diner(dinerId))
            .CreateAsync(Booking(branch, branch.TableIds[0], SixPm));

        // The diner taps Cancel on their other phone at the exact moment the move goes for the
        // table's lock: after the move has read the booking, before it writes it.
        var cancelLands = new BeforeFirstCommandMatching(
            sql => sql.Contains("DiningTables WITH (UPDLOCK", StringComparison.Ordinal),
            async () =>
            {
                await using var other = fixture.CreateContext(clock);
                await fixture.CreateReservationService(other, clock, TestActor.Diner(dinerId))
                    .CancelAsync(new CancelReservationCommand(booked.Id, "plans changed"));
            });

        await using var db = fixture.CreateContext(clock, cancelLands);
        var mover = fixture.CreateReservationService(db, clock, TestActor.Diner(dinerId));

        var lost = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => mover.RescheduleAsync(Move(booked.Id, branch.TableIds[1], EightPm)));

        Assert.True(cancelLands.Fired, "The cancel never landed, so this proved nothing about the race.");

        var mapped = ApiExceptionMapper.Map(lost);
        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal("concurrent-update", mapped.Code);

        // The cancel stands, at the old slot: a move must never resurrect a booking at a new time.
        var stored = await StoredAsync(booked.Id, clock);
        Assert.Equal(ReservationStatus.CancelledByDiner, stored.Status);
        Assert.Equal(booked.StartUtc, stored.StartUtc);
        Assert.Equal(branch.TableIds[0], stored.DiningTableId);

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.ProcessedCommands.AnyAsync(c => c.ActorId == dinerId));
        Assert.Empty(await OutboxKeysAsync(booked.Id, clock));
    }

    [SkippableFact]
    public async Task Two_diners_moving_onto_the_same_slot_leave_exactly_one_winner()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var setup = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(setup);
        var ani = Guid.CreateVersion7();
        var aram = Guid.CreateVersion7();

        var hers = await fixture.CreateReservationService(setup, clock, TestActor.Diner(ani))
            .CreateAsync(Booking(branch, branch.TableIds[0], SixPm));
        var his = await fixture.CreateReservationService(setup, clock, TestActor.Diner(aram))
            .CreateAsync(Booking(branch, branch.TableIds[1], SixPm));

        await using var dbA = fixture.CreateContext(clock);
        await using var dbB = fixture.CreateContext(clock);

        // Both onto table 3: eight and half past eight overlap.
        var (a, b) = await RaceAsync(
            () => fixture.CreateReservationService(dbA, clock, TestActor.Diner(ani))
                .RescheduleAsync(Move(hers.Id, branch.TableIds[2], EightPm)),
            () => fixture.CreateReservationService(dbB, clock, TestActor.Diner(aram))
                .RescheduleAsync(Move(his.Id, branch.TableIds[2], new TimeOnly(20, 30))));

        Assert.Single(new[] { a, b }, r => r.View is not null);
        Assert.IsType<TableAlreadyBookedException>(Assert.Single(new[] { a, b }, r => r.Error is not null).Error);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(
            1,
            await verify.Reservations.CountAsync(r => r.DiningTableId == branch.TableIds[2]));
    }

    // ---------------------------------------------------------------- what the diner is told

    [SkippableFact]
    public async Task The_reminder_and_the_nudge_are_replaced_with_ones_for_the_new_time()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var booked = await service.CreateAsync(Booking(branch, branch.TableIds[0], SixPm));

        // Every booking is also asked "are you on your way?" 30 minutes before (2026-10-21), keyed by its start.
        Assert.Equal(
            new[]
            {
                OutboxMessageTypes.KeyFor("reservation", booked.Id, "late-nudge"),
                OutboxMessageTypes.KeyFor(
                    "reservation", booked.Id, $"on-your-way:{booked.StartUtc:yyyyMMddHHmm}:30"),
                OutboxMessageTypes.KeyFor("reservation", booked.Id, "reminder"),
            },
            await OutboxKeysAsync(booked.Id, clock));

        var moved = await service.RescheduleAsync(Move(booked.Id, branch.TableIds[1], EightPm));

        // Keyed by the start they are for, because a message for an earlier start keeps its key once
        // sent. 20:00 in Yerevan is 16:00Z.
        var reminderKey = OutboxMessageTypes.KeyFor("reservation", booked.Id, "reminder:202609131600");
        var nudgeKey = OutboxMessageTypes.KeyFor("reservation", booked.Id, "late-nudge:202609131600");
        var onYourWayKey = OutboxMessageTypes.KeyFor("reservation", booked.Id, "on-your-way:202609131600:30");

        Assert.Equal(new[] { nudgeKey, onYourWayKey, reminderKey }, await OutboxKeysAsync(booked.Id, clock));

        await using (var verify = fixture.CreateContext(clock))
        {
            var reminder = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == reminderKey);
            var nudge = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == nudgeKey);

            Assert.Equal(moved.StartUtc.AddHours(-3), reminder.ScheduledForUtc);
            Assert.Equal(moved.StartUtc.AddMinutes(10), nudge.ScheduledForUtc);
            Assert.Contains("\"localStartTime\":\"20:00:00\"", reminder.PayloadJson, StringComparison.Ordinal);
            Assert.Contains($"\"reservationCode\":\"{booked.Code}\"", reminder.PayloadJson, StringComparison.Ordinal);

            // The feed: one reminder entry, appearing when the new reminder is due, and the question 30 minutes before.
            var feed = await verify.DinerNotifications.AsNoTracking()
                .Where(n => n.ReservationId == booked.Id)
                .ToListAsync();

            var entry = Assert.Single(feed, n => n.Kind == DinerNotificationKinds.BookingReminder);
            Assert.Equal(moved.StartUtc.AddHours(-3), entry.CreatedAtUtc);
            Assert.Contains("\"time\":\"20:00\"", entry.ParamsJson, StringComparison.Ordinal);

            var question = Assert.Single(feed, n => n.Kind == DinerNotificationKinds.BookingOnYourWay);
            Assert.Equal(moved.StartUtc.AddMinutes(-30), question.CreatedAtUtc);
            Assert.Equal(2, feed.Count);
        }

        // The new reminder went out. Moving to another table at the same time writes no second one,
        // and the nudge - still unsent, under the same key - is replaced in the same save.
        await MarkSentAsync(reminderKey, clock);

        await service.RescheduleAsync(Move(booked.Id, branch.TableIds[2], EightPm));

        Assert.Equal(new[] { nudgeKey, onYourWayKey, reminderKey }, await OutboxKeysAsync(booked.Id, clock));

        await using (var verify = fixture.CreateContext(clock))
        {
            var nudge = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.IdempotencyKey == nudgeKey);

            Assert.Contains("\"tableLabel\":\"3\"", nudge.PayloadJson, StringComparison.Ordinal);
            Assert.Equal(
                1,
                await verify.OutboxMessages.CountAsync(m => m.IdempotencyKey == reminderKey && m.SentAtUtc != null));
        }
    }

    [SkippableFact]
    public async Task A_web_bookings_moved_reminder_keeps_its_manage_link()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var stored = await db.Branches.SingleAsync(b => b.Id == branch.BranchId);
        stored.SetAcceptsWebBookings(true);
        await db.SaveChangesAsync();

        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var booked = await service.CreateAsync(
            Booking(branch, branch.FirstTableId, SixPm) with { Channel = ReservationChannel.Web });

        Assert.NotNull(booked.ManageToken);

        var moved = await service.RescheduleAsync(Move(booked.Id, branch.FirstTableId, EightPm));

        // The token's plaintext exists only in the create response and the messages written then,
        // so the moved reminder carries the link its old one did - and the change does not hand the
        // token out again.
        Assert.Null(moved.ManageToken);

        await using var verify = fixture.CreateContext(clock);
        var reminder = await verify.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.IdempotencyKey == OutboxMessageTypes.KeyFor("reservation", booked.Id, "reminder:202609131600"));

        Assert.Contains(Uri.EscapeDataString(booked.ManageToken), reminder.PayloadJson, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static CreateReservationCommand Booking(TestBranch branch, Guid tableId, TimeOnly localTime) =>
        new(
            branch.BranchId,
            tableId,
            BookingDate,
            localTime,
            2,
            GuestName: "Ani Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7());

    private static RescheduleReservationCommand Move(
        Guid reservationId,
        Guid tableId,
        TimeOnly localTime,
        int partySize = 2,
        DateOnly? date = null) =>
        new(reservationId, tableId, date ?? BookingDate, localTime, partySize, Guid.CreateVersion7());

    /// <summary>The shipped restaurant policy, except that a human approves every booking.</summary>
    private static ReservationPolicy ApprovesEveryBooking() => new(
        turnTimeMinutes: 90,
        bufferMinutes: 15,
        graceMinutes: 15,
        lateNudgeAfterMinutes: 10,
        graceExtensionMinutes: 10,
        minLeadMinutes: 30,
        bookingWindowDays: 14,
        cancellationDeadlineMinutes: 120,
        autoConfirm: false,
        serviceChargePercent: 10m,
        pricesIncludeVat: true,
        maxSeatOverhang: 2,
        approvalRequiredAbovePartySize: 8);

    /// <summary>Bookings this diner did not turn up for, in the past and inside the rolling window.</summary>
    private async Task AddNoShowsAsync(TestBranch branch, Guid dinerUserId, int count, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => b.ReservationPolicy)
            .FirstAsync();

        for (var i = 0; i < count; i++)
        {
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

    private async Task<Reservation> StoredAsync(Guid reservationId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservationId);
    }

    private async Task<Guid> RoomOfAsync(TestBranch branch, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.FloorAreas.AsNoTracking().Where(a => a.BranchId == branch.BranchId).Select(a => a.Id).SingleAsync();
    }

    /// <summary>The booking's outbox keys, sorted, optionally of one message type.</summary>
    private async Task<List<string>> OutboxKeysAsync(Guid reservationId, TestClock clock, string? type = null)
    {
        await using var db = fixture.CreateContext(clock);
        var prefix = OutboxMessageTypes.PrefixFor("reservation", reservationId);

        return await db.OutboxMessages.AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith(prefix) && (type == null || m.Type == type))
            .OrderBy(m => m.IdempotencyKey)
            .Select(m => m.IdempotencyKey)
            .ToListAsync();
    }

    /// <summary>What the dispatcher does once a message is out.</summary>
    private async Task MarkSentAsync(string idempotencyKey, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var message = await db.OutboxMessages.SingleAsync(m => m.IdempotencyKey == idempotencyKey);
        message.MarkSent(clock.UtcNow);

        await db.SaveChangesAsync();
    }

    /// <summary>Runs two changes at the same moment and reports what each one got.</summary>
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

    private readonly record struct Outcome(ReservationView? View, Exception? Error);
}
