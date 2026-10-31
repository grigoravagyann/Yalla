using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yalla.Api.Errors;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// One diner, one table at a time at a branch: a booking or a move whose sitting overlaps one of the
/// diner's own live bookings at the same branch is refused, against a real SQL Server.
/// </summary>
/// <remarks>
/// Found in QA: one diner held table 1 at 19:45 and table 2 at 19:30 the same evening. The branch is in
/// <c>Asia/Yerevan</c> (UTC+4) on the shipped restaurant policy - a 90-minute turn; "now" is noon on
/// Thursday 10 September 2026 there and the evening booked is Sunday the 13th's.
/// <para>
/// Since 2026-10-20 one proved number holds at most three active bookings (<see cref="BookingPerNumberTests"/>),
/// and the rule reaches every venue: at another one the two sittings need half an hour between them. These tests
/// are about the rule at one branch - unchanged - and some still mark earlier bookings as held by no number
/// (<c>MakeBookingsFromBeforeTheNumberRuleAsync</c>, from the one-booking rule), which the overlap, keyed on the
/// account as well, still sees.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ReservationOwnOverlapTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Sunday = new(2026, 9, 13);

    /// <summary>The branch's today, Thursday.</summary>
    private static readonly DateOnly Today = new(2026, 9, 10);

    [SkippableFact]
    public async Task A_second_table_at_the_same_branch_while_the_first_booking_runs_is_refused_naming_the_first()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var bella = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var first = await bella.CreateAsync(Booking(branch, branch.TableIds[1], new TimeOnly(19, 30)));

        // The overlap answers first - it says more than the number rule would: which table, at once.
        var refused = await Assert.ThrowsAsync<ReservationOverlapsOwnException>(
            () => bella.CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(19, 45))));

        Assert.Equal(first.Id, refused.ReservationId);
        Assert.Equal(first.Code, refused.Code);
        Assert.Equal(new TimeOnly(19, 30), refused.LocalStartTime);
        Assert.Equal(Sunday, refused.LocalDate);
        Assert.Equal("2", refused.TableLabel);

        var mapped = ApiExceptionMapper.Map(refused);
        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal(ErrorCodes.ReservationOverlapsOwn, mapped.Code);
        Assert.Equal("reservation-overlaps-own", mapped.Code);
        Assert.False(mapped.LogAsError);
        Assert.Equal(first.Id, mapped.Context!["reservationId"]);
        Assert.Equal(first.Code, mapped.Context["code"]);
        Assert.Equal(new TimeOnly(19, 30), mapped.Context["localStartTime"]);
        Assert.Equal(Sunday, mapped.Context["localDate"]);
        Assert.Equal("2", mapped.Context["tableLabel"]);

        Assert.False(string.IsNullOrEmpty((string?)mapped.Context["venueName"]));
        Assert.Equal(first.BranchName, mapped.Context["branchName"]);

        // A booking that does not overlap is fine now (2026-10-20: up to three per number, not one).
        var lunch = await bella.CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(12, 0)));

        // Nothing else was written: the first, and lunch.
        await using var check = fixture.CreateContext(clock);
        var written = await check.Reservations.Where(r => r.BranchId == branch.BranchId).Select(r => r.Id).ToListAsync();
        Assert.Equal(2, written.Count);
        Assert.Contains(first.Id, written);
        Assert.Contains(lunch.Id, written);
    }

    [SkippableFact]
    public async Task Back_to_back_another_diner_and_a_cancelled_booking_are_allowed_and_another_branch_needs_a_gap()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var elsewhere = await TestBranchBuilder.CreateAsync(db);
        var diner = TestActor.Diner();
        var bella = fixture.CreateReservationService(db, clock, diner);

        var first = await bella.CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(18, 0)));
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, first.Id);

        // 19:30 is when the 18:00 sitting ends: one after the other, never both at once.
        var after = await bella.CreateAsync(Booking(branch, branch.TableIds[1], new TimeOnly(19, 30)));
        Assert.Equal(ReservationStatus.Confirmed, after.Status);
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, after.Id);

        // Another branch needs half an hour to get there (2026-10-20): 18:30 elsewhere runs into both of hers,
        // 21:30 - half an hour after the 19:30 sitting ends - does not. Another diner is somebody else.
        var tooClose = await Assert.ThrowsAsync<ReservationOverlapsOwnException>(
            () => bella.CreateAsync(Booking(elsewhere, elsewhere.TableIds[0], new TimeOnly(18, 30))));
        Assert.Equal(first.Id, tooClose.ReservationId);
        Assert.NotNull(tooClose.VenueName);

        var away = await bella.CreateAsync(Booking(elsewhere, elsewhere.TableIds[0], new TimeOnly(21, 30)));
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, away.Id);
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[2], new TimeOnly(18, 30)));

        // A booking she called off holds nothing: 16:45 runs into the cancelled 18:00, and not the 19:30.
        await bella.CancelAsync(new CancelReservationCommand(first.Id));
        var instead = await bella.CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(16, 45)));
        Assert.Equal(ReservationStatus.Confirmed, instead.Status);
    }

    /// <summary>
    /// Another venue's half hour is measured from where the sitting really ends: a 13:00 promising to leave by 13:55
    /// lets the same diner start elsewhere at 14:25, not at 14:20. Two branches of one venue are two places to get
    /// between, so the gap applies to them as to any two branches (review S9).
    /// </summary>
    [SkippableFact]
    public async Task The_gap_to_another_venue_runs_from_a_cut_sitting_and_applies_between_two_branches_of_one_venue()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var here = await TestBranchBuilder.CreateAsync(db);
        var there = await TestBranchBuilder.CreateAsync(db);
        var sibling = await TestBranchBuilder.CreateAsync(db);

        // The sibling is another branch of here's venue.
        await db.Database.ExecuteSqlAsync($"UPDATE dbo.Branches SET VenueId = {here.VenueId} WHERE Id = {sibling.BranchId}");

        // Its staff move with it, or it has nobody of its own venue on the floor and is not taking guests.
        await db.Database.ExecuteSqlAsync($"UPDATE dbo.StaffMembers SET VenueId = {here.VenueId} WHERE BranchId = {sibling.BranchId}");

        // Somebody else has here's table 1 at 14:00, so Bella's 13:00 there is cut to 13:55.
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(here, here.TableIds[0], new TimeOnly(14, 0)));

        var bella = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var lunch = await bella.CreateAsync(
            Booking(here, here.TableIds[0], new TimeOnly(13, 0), acceptsFreeByUtc: SundayAt(13, 55)));
        Assert.Equal(SundayAt(13, 55), lunch.EndUtc);

        var tooSoon = await Assert.ThrowsAsync<ReservationOverlapsOwnException>(
            () => bella.CreateAsync(Booking(there, there.TableIds[0], new TimeOnly(14, 20))));
        Assert.Equal(lunch.Id, tooSoon.ReservationId);

        var afterLunch = await bella.CreateAsync(Booking(there, there.TableIds[0], new TimeOnly(14, 25)));
        Assert.Equal(ReservationStatus.Confirmed, afterLunch.Status);

        // The venue's other branch at 14:00 runs into lunch's half hour, as any other place would.
        var sameVenue = await Assert.ThrowsAsync<ReservationOverlapsOwnException>(
            () => bella.CreateAsync(Booking(sibling, sibling.TableIds[0], new TimeOnly(11, 30))));
        Assert.Equal(lunch.Id, sameVenue.ReservationId);
    }

    [SkippableFact]
    public async Task A_booking_still_waiting_for_the_venue_counts_and_a_replay_of_it_still_replays()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(
            db, policy: new ReservationPolicy(90, 5, 15, 10, 10, 10, 14, 120, autoConfirm: false, 10m, true, 2, 8));
        var bella = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var command = Booking(branch, branch.TableIds[0], new TimeOnly(19, 0));
        var waiting = await bella.CreateAsync(command);
        Assert.Equal(ReservationStatus.PendingApproval, waiting.Status);

        // The same command again is the same booking, not a second one overlapping it.
        var replay = await bella.CreateAsync(command);
        Assert.True(replay.WasReplay);
        Assert.Equal(waiting.Id, replay.Id);

        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, waiting.Id);

        var refused = await Assert.ThrowsAsync<ReservationOverlapsOwnException>(
            () => bella.CreateAsync(Booking(branch, branch.TableIds[1], new TimeOnly(20, 0))));
        Assert.Equal(waiting.Id, refused.ReservationId);
    }

    [SkippableFact]
    public async Task A_move_onto_another_of_her_bookings_is_refused_and_a_move_past_itself_is_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var bella = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var lunch = await bella.CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(13, 0)));
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, lunch.Id);
        var dinner = await bella.CreateAsync(Booking(branch, branch.TableIds[1], new TimeOnly(19, 30)));

        // Half an hour later on its own table: the booking being moved is not in its own way.
        var later = await bella.RescheduleAsync(Move(dinner.Id, branch.TableIds[1], new TimeOnly(20, 0)));
        Assert.Equal(new TimeOnly(20, 0), later.LocalStartTime);

        // Lunch moved into dinner's sitting, on a third table: refused, naming dinner, and lunch stays put.
        var refused = await Assert.ThrowsAsync<ReservationOverlapsOwnException>(
            () => bella.RescheduleAsync(Move(lunch.Id, branch.TableIds[2], new TimeOnly(20, 30))));

        Assert.Equal(dinner.Id, refused.ReservationId);
        Assert.Equal(new TimeOnly(20, 0), refused.LocalStartTime);
        Assert.Equal("2", refused.TableLabel);

        await using var check = fixture.CreateContext(clock);
        var stored = await check.Reservations.AsNoTracking().SingleAsync(r => r.Id == lunch.Id);
        Assert.Equal(new TimeOnly(13, 0), stored.LocalStartTime);
        Assert.Equal(branch.TableIds[0], stored.DiningTableId);
    }

    [SkippableFact]
    public async Task A_sitting_cut_short_by_a_promise_to_leave_ends_there_for_the_new_booking_and_the_one_already_held()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 4);

        // Somebody else has table 1 at 14:00 and table 4 at 16:00.
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(14, 0)));
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.TableIds[3], new TimeOnly(16, 0)));

        // The new booking: Bella holds table 2 at 14:00, then books table 1 at 13:00 promising to leave
        // by 13:55. Its sitting is 13:00-13:55, not the full turn, so it does not run into her 14:00.
        var bella = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var held = await bella.CreateAsync(Booking(branch, branch.TableIds[1], new TimeOnly(14, 0)));
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, held.Id);

        // Not promising, the table asks for the time - not "you already have a booking".
        await Assert.ThrowsAsync<FreeByNotAcceptedException>(
            () => bella.CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(13, 0))));

        var lunch = await bella.CreateAsync(
            Booking(branch, branch.TableIds[0], new TimeOnly(13, 0), acceptsFreeByUtc: SundayAt(13, 55)));
        Assert.Equal(SundayAt(13, 55), lunch.EndUtc);

        // The one already held: Carla books table 4 at 15:00 promising 15:55 - stored as its end - and
        // then table 3 at 16:00.
        var carla = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var first = await carla.CreateAsync(
            Booking(branch, branch.TableIds[3], new TimeOnly(15, 0), acceptsFreeByUtc: SundayAt(15, 55)));
        Assert.Equal(SundayAt(15, 55), first.EndUtc);
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, first.Id);

        var after = await carla.CreateAsync(Booking(branch, branch.TableIds[2], new TimeOnly(16, 0)));
        Assert.Equal(ReservationStatus.Confirmed, after.Status);
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, after.Id);

        // A minute into the cut sitting is still refused.
        var refused = await Assert.ThrowsAsync<ReservationOverlapsOwnException>(
            () => carla.CreateAsync(Booking(branch, branch.TableIds[1], new TimeOnly(15, 50))));
        Assert.Equal(first.Id, refused.ReservationId);
    }

    [SkippableFact]
    public async Task A_change_of_party_size_alone_is_not_refused_even_for_a_pair_that_overlapped_before_the_rule()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = TestActor.Diner();
        var bella = fixture.CreateReservationService(db, clock, diner);

        var dinner = await bella.CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(19, 30)));

        // The pair QA found, written as it was before the rule: her own 19:45 on table 2.
        var policy = await db.Branches.AsNoTracking().Where(b => b.Id == branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();
        var legacy = Reservation.Create(
            branchId: branch.BranchId,
            diningTableId: branch.TableIds[1],
            startUtc: SundayAt(19, 45),
            localDate: Sunday,
            localStartTime: new TimeOnly(19, 45),
            partySize: 2,
            guestName: "Bella Test",
            guestPhone: "+37411223344",
            code: Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            policy: policy,
            dinerUserId: diner.DinerUserId,
            clientCommandId: Guid.CreateVersion7());
        db.Reservations.Add(legacy);
        await db.SaveChangesAsync();

        // Same table, start and end, one more guest: it adds no overlap that was not already there.
        var bigger = await bella.RescheduleAsync(
            new RescheduleReservationCommand(dinner.Id, branch.TableIds[0], Sunday, new TimeOnly(19, 30), 3, Guid.CreateVersion7()));
        Assert.Equal(3, bigger.PartySize);

        // Another table at the same time is a new sitting, and is judged.
        var refused = await Assert.ThrowsAsync<ReservationOverlapsOwnException>(
            () => bella.RescheduleAsync(
                new RescheduleReservationCommand(dinner.Id, branch.TableIds[2], Sunday, new TimeOnly(19, 30), 3, Guid.CreateVersion7())));
        Assert.Equal(legacy.Id, refused.ReservationId);
    }

    [SkippableFact]
    public async Task A_change_nobody_answered_does_not_go_back_to_a_slot_she_has_booked_another_table_for()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var (branch, original, other) = await TwoTablesThroughAChangeAsync(clock);

        // Sixteen minutes on, nobody has answered the change: the sweep declines it. Going back to
        // tomorrow's 20:00 on table 1 would give her two tables at 20:00, so the booking ends.
        clock.Advance(TimeSpan.FromMinutes(16));

        await using (var db = fixture.CreateContext(clock))
        {
            // At least hers: the database is shared, and other tests' lapsed requests are swept too.
            Assert.True(await fixture.CreateApprovalExpirySweep(db, clock).RunOnceAsync() >= 1);
        }

        await AssertEndedAndToldAsync(clock, original, other, expired: true);
    }

    [SkippableFact]
    public async Task A_change_staff_declined_does_not_go_back_to_a_slot_she_has_booked_another_table_for()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var (branch, original, other) = await TwoTablesThroughAChangeAsync(clock);

        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId))
                .RejectAsync(new DecideReservationCommand(original.Id, "Full tonight."));
        }

        await AssertEndedAndToldAsync(clock, original, other, expired: false);
    }

    /// <summary>
    /// Bella has table 1 tomorrow at 20:00, moves it to tonight at 21:00 on table 2 - short notice, so it
    /// waits for the floor and keeps tomorrow's slot to go back to - and meanwhile books table 3 tomorrow
    /// at 20:00, which nothing she holds now overlaps.
    /// </summary>
    private async Task<(TestBranch Branch, ReservationView Original, ReservationView Other)> TwoTablesThroughAChangeAsync(
        TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        await fixture.ClearOutboxAsync(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var bella = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var tomorrow = Today.AddDays(1);

        var original = await bella.CreateAsync(Booking(branch, branch.TableIds[0], new TimeOnly(20, 0), date: tomorrow));
        Assert.Equal(ReservationStatus.Confirmed, original.Status);

        var moved = await bella.RescheduleAsync(
            new RescheduleReservationCommand(original.Id, branch.TableIds[1], Today, new TimeOnly(21, 0), 2, Guid.CreateVersion7()));
        Assert.Equal(ReservationStatus.PendingApproval, moved.Status);
        Assert.Equal(ApprovalTrigger.ShortNotice, moved.AwaitingApprovalBecause);

        // The waiting change holds the number; only a pair from before that rule gets this far.
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, original.Id);

        var other = await bella.CreateAsync(Booking(branch, branch.TableIds[2], new TimeOnly(20, 0), date: tomorrow));
        Assert.Equal(ReservationStatus.Confirmed, other.Status);

        return (branch, original, other);
    }

    private async Task AssertEndedAndToldAsync(TestClock clock, ReservationView original, ReservationView other, bool expired)
    {
        await using var db = fixture.CreateContext(clock);

        var ended = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == original.Id);
        Assert.Equal(ReservationStatus.CancelledByVenue, ended.Status);
        Assert.Equal(expired ? DeclineReason.NoAnswer : DeclineReason.StaffDeclined, ended.DeclineReason);

        Assert.Equal(
            ReservationStatus.Confirmed,
            (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == other.Id)).Status);

        // Told as for any slot that could not be kept: the change declined, and the booking gone with it.
        var told = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == "reservation.decided" && m.IdempotencyKey.StartsWith("reservation:" + original.Id))
            .Select(m => m.PayloadJson)
            .SingleAsync();
        Assert.Contains("\"wasChange\":true", told, StringComparison.Ordinal);
        Assert.Contains("\"originalKept\":false", told, StringComparison.Ordinal);
    }

    /// <summary>A Sunday wall-clock time in Yerevan, as UTC.</summary>
    private static DateTime SundayAt(int hour, int minute) =>
        new DateTime(2026, 9, 13, hour, minute, 0, DateTimeKind.Utc).AddHours(-4);

    private static CreateReservationCommand Booking(
        TestBranch branch,
        Guid tableId,
        TimeOnly localTime,
        DateTime? acceptsFreeByUtc = null,
        DateOnly? date = null) =>
        new(
            branch.BranchId,
            tableId,
            date ?? Sunday,
            localTime,
            2,
            GuestName: "Bella Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7(),
            AcceptsFreeByUtc: acceptsFreeByUtc);

    private static RescheduleReservationCommand Move(Guid reservationId, Guid tableId, TimeOnly localTime) =>
        new(reservationId, tableId, Sunday, localTime, 2, Guid.CreateVersion7());
}
