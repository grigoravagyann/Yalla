using Microsoft.EntityFrameworkCore;
using Yalla.Api.Errors;
using Yalla.Application.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The table's evening where the first version got it wrong: a confirmed booking refused its own slot,
/// the floor answering for a guest who is being asked, clocks restarted by moves, the earlier guest's
/// own change held hostage by a question, a booking left waiting when the guest before went away, and
/// the edges around them.
/// </summary>
/// <remarks>
/// As <see cref="TableSequencingFlowTests"/>: the shipped restaurant policy (90-minute turn, 5 minutes of
/// clearing), open 10:00-23:00 in <c>Asia/Yerevan</c>, "now" noon on Tuesday 20 October 2026 there, and
/// the evening booked is Thursday the 22nd's. Refusals are asserted by their wire code, as a client sees
/// them.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TableSequencingReviewTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 20);

    private static readonly DateOnly Thursday = new(2026, 10, 22);

    private static DateTime ThursdayAt(int hour, int minute) =>
        new DateTime(2026, 10, 22, hour, minute, 0, DateTimeKind.Utc).AddHours(-4);

    // ---------------------------------------------------------------- 1. a confirmed booking keeps its slot

    [SkippableFact]
    public async Task A_confirmed_booking_is_not_refused_its_own_slot_after_the_booking_before_promised_to_leave_in_time()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);

        // Y at 21:30 first; X at 20:45 after it, promising to be gone by 21:25.
        var yan = TestActor.Diner();
        var y = await BookAsync(clock, yan, branch, new TimeOnly(21, 30), partySize: 3);
        var x = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(20, 45), acceptsFreeBy: ThursdayAt(21, 25));

        Assert.Equal(ThursdayAt(21, 25), x.PromisedFreeByUtc);

        // Y's own slot, read leaving Y out, is free - not "just booked" forty-five minutes before.
        await using (var db = fixture.CreateContext(clock))
        {
            var slot = await fixture.CreateAvailabilityQuery(db, clock).GetAvailabilityAsync(
                new AvailabilityRequest(branch.BranchId, 3, Thursday, new TimeOnly(21, 30), y.Id));

            var table = slot!.Tables.Single();
            Assert.True(table.IsAvailable);
            Assert.Null(table.UnavailableReason);
            Assert.False(table.IsRequestable);
        }

        // One fewer person: same table, same time - still confirmed.
        var smaller = await MoveAsync(clock, yan, y, new TimeOnly(21, 30), partySize: 2);
        Assert.Equal(ReservationStatus.Confirmed, smaller.Status);

        // One more: judged again, and the promise before it still leaves the table free.
        var bigger = await MoveAsync(clock, yan, y, new TimeOnly(21, 30), partySize: 4);
        Assert.Equal(ReservationStatus.Confirmed, bigger.Status);
        Assert.Null(bigger.AwaitingApprovalBecause);
    }

    // ---------------------------------------------------------------- 2. the floor does not answer for the guest

    [SkippableFact]
    public async Task Staff_cannot_approve_or_decline_a_booking_waiting_on_the_earlier_guests_own_answer()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock);
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 30));

        foreach (var approve in new[] { true, false })
        {
            await using var db = fixture.CreateContext(clock);
            var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId));

            var refused = await Record.ExceptionAsync(() => approve
                ? waiter.ApproveAsync(new DecideReservationCommand(later.Id))
                : waiter.RejectAsync(new DecideReservationCommand(later.Id)));

            Assert.NotNull(refused);
            var mapped = ApiExceptionMapper.Map(refused);
            Assert.Equal(409, mapped.Status);
            Assert.Equal("reservation-awaiting-earlier-guest", mapped.Code);
        }

        // Nothing changed, and Anna was told nothing she did not say.
        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(clock, later.Id)).Status);

        var hers = await StoredAsync(clock, earlier.Id);
        Assert.Null(hers.PromisedFreeByUtc);
        Assert.Equal(ThursdayAt(19, 30), hers.EndUtc);

        await using var check = fixture.CreateContext(clock);
        Assert.False(await check.OutboxMessages.AnyAsync(m => m.Type == "reservation.table-request-decided"
                                                              && m.IdempotencyKey.StartsWith("reservation:" + earlier.Id)));
    }

    // ---------------------------------------------------------------- 3. no clock restarts

    [SkippableFact]
    public async Task A_waiting_booking_moved_into_tonight_gets_the_fifteen_minutes_not_the_two_hours_it_had()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, tableCount: 2);
        var anna = await DinerWithDeviceAsync(clock);
        await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var gor = TestActor.Diner();
        var later = await BookAsync(clock, gor, branch, new TimeOnly(19, 30));

        Assert.Equal(Now.AddHours(2), later.ApprovalExpiresAtUtc);

        clock.Advance(TimeSpan.FromMinutes(10));

        // Tonight at 20:00 on the other table, which nobody has: the floor's, for fifteen minutes.
        var tonight = await MoveAsync(clock, gor, later, new TimeOnly(20, 0), date: Today, tableId: branch.TableIds[1]);

        Assert.Equal(ApprovalTrigger.ShortNotice, tonight.AwaitingApprovalBecause);
        Assert.Equal(Now.AddMinutes(25), tonight.ApprovalExpiresAtUtc);
    }

    [SkippableFact]
    public async Task Moving_back_and_forth_never_restarts_the_earlier_guests_two_hours_nor_asks_them_twice()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock);
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var gor = TestActor.Diner();
        var later = await BookAsync(clock, gor, branch, new TimeOnly(19, 30));

        var deadline = later.ApprovalExpiresAtUtc;
        Assert.Equal(Now.AddHours(2), deadline);

        foreach (var (minutes, time) in new[] { (20, new TimeOnly(19, 45)), (40, new TimeOnly(19, 30)), (60, new TimeOnly(20, 0)) })
        {
            clock.UtcNow = Now.AddMinutes(minutes);
            var moved = await MoveAsync(clock, gor, later, time);

            Assert.Equal(ApprovalTrigger.EarlierGuestConsent, moved.AwaitingApprovalBecause);
            Assert.Equal(deadline, moved.ApprovalExpiresAtUtc);
        }

        await using var db = fixture.CreateContext(clock);

        // One question, one push to Anna, ever.
        Assert.Equal(1, await db.TableShareRequests.CountAsync(r => r.LaterReservationId == later.Id));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(
            m => m.Type == "reservation.table-request" && m.IdempotencyKey.StartsWith("reservation:" + earlier.Id)));
    }

    // ---------------------------------------------------------------- 4. the card names the guest asked

    [SkippableFact]
    public async Task The_awaiting_card_names_the_guest_being_asked_even_when_their_booking_is_the_day_before()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);

        // Open 10:00 to 03:00: a 22:45 on Thursday and a 00:30 on Friday share an evening.
        var branch = await BranchAsync(clock, openingHours: [(new TimeOnly(10, 0), new TimeOnly(3, 0), true)]);
        var earlier = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(22, 45));
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(0, 30), date: Thursday.AddDays(1));

        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, later.AwaitingApprovalBecause);

        var card = Assert.Single(await AwaitingAsync(clock, branch));

        Assert.Equal(later.Id, card.Reservation.Id);
        Assert.Equal(earlier.Id, card.PreviousBooking?.ReservationId);
        Assert.Equal(new TimeOnly(22, 45), card.PreviousBooking?.LocalStartTime);
    }

    // ---------------------------------------------------------------- 5. the earlier guest moves

    [SkippableFact]
    public async Task When_the_earlier_guest_moves_early_enough_the_later_booking_is_confirmed_and_otherwise_still_waits()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, tableCount: 2);
        var anna = await DinerWithDeviceAsync(clock);

        // Table 1: Anna 18:00, Gor 20:00 asking her. She moves to 16:30 - three and a half hours before.
        var annas = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var gors = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(20, 0));

        await MoveAsync(clock, anna, annas, new TimeOnly(16, 30));

        Assert.Equal(ReservationStatus.Confirmed, (await StoredAsync(clock, gors.Id)).Status);
        Assert.Equal(TableShareRequestStatus.Withdrawn, (await RequestStatusAsync(clock, gors.Id)).Status);

        // Table 2: the same, but Mari moves only to 17:30 - two and a half hours before: still asked. (Mari,
        // not Anna: 17:30 would run into Anna's own 16:30, and one diner holds one table at a time.)
        var mari = await DinerWithDeviceAsync(clock);
        var hers = await BookAsync(clock, mari, branch, new TimeOnly(18, 0), tableId: branch.TableIds[1]);
        var his = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(20, 0), tableId: branch.TableIds[1]);

        await MoveAsync(clock, mari, hers, new TimeOnly(17, 30));

        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(clock, his.Id)).Status);
        Assert.Equal(TableShareRequestStatus.Pending, (await RequestStatusAsync(clock, his.Id)).Status);
    }

    [SkippableFact]
    public async Task The_earlier_guests_own_change_is_not_made_to_promise_a_time_for_a_booking_only_asking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock);
        var annas = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var gors = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 30));

        // Five minutes later: a full sitting would run into Gor's 19:30. Gor is only asking, so Anna is
        // not asked to promise 19:25 - her change stands, and is her answer: Gor is declined.
        var moved = await MoveAsync(clock, anna, annas, new TimeOnly(18, 5));

        Assert.Equal(ReservationStatus.Confirmed, moved.Status);
        Assert.Null(moved.PromisedFreeByUtc);
        Assert.Equal(ThursdayAt(19, 35), moved.EndUtc);

        var declined = await StoredAsync(clock, gors.Id);
        Assert.Equal(ReservationStatus.CancelledByVenue, declined.Status);
        Assert.Equal(DeclineReason.EarlierGuestDeclined, declined.DeclineReason);
        Assert.Equal(TableShareRequestStatus.Declined, (await RequestStatusAsync(clock, gors.Id)).Status);

        // And a change of party size alone asks nothing of anybody either.
        var gor2 = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45), acceptsFreeBy: null);
        var fewer = await MoveAsync(clock, anna, moved, new TimeOnly(18, 5), partySize: 2);

        Assert.Equal(ReservationStatus.Confirmed, fewer.Status);
        Assert.Null(fewer.PromisedFreeByUtc);
        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(clock, gor2.Id)).Status);
    }

    // ---------------------------------------------------------------- 6 and e. the floor's questions

    [SkippableFact]
    public async Task A_booking_the_floor_decides_has_two_hours_and_is_confirmed_when_the_web_guest_before_cancels_within_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, acceptsWebBookings: true);
        var anna = await DinerWithDeviceAsync(clock);
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0), channel: ReservationChannel.Web);
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45));

        // The floor's to decide, with the two hours the guest would have had in the app - not for days.
        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, later.AwaitingApprovalBecause);
        Assert.Equal(Now.AddHours(2), later.ApprovalExpiresAtUtc);
        Assert.False(later.AwaitsEarlierGuestAnswer);

        clock.Advance(TimeSpan.FromHours(1));
        await SweepAsync(clock);
        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(clock, later.Id)).Status);

        // She cancels: nothing else about his booking needs a human, so it is confirmed.
        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(db, clock, anna).CancelAsync(new CancelReservationCommand(earlier.Id));
        }

        var confirmed = await StoredAsync(clock, later.Id);
        Assert.Equal(ReservationStatus.Confirmed, confirmed.Status);
        Assert.Null(confirmed.ApprovalExpiresAtUtc);
    }

    [SkippableFact]
    public async Task Moving_a_booking_the_floor_decides_never_restarts_its_two_hours()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var earlier = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(18, 0));
        var gor = TestActor.Diner();
        var later = await BookAsync(clock, gor, branch, new TimeOnly(19, 30));

        Assert.Equal(Now.AddHours(2), later.ApprovalExpiresAtUtc);

        foreach (var (minutes, time) in new[] { (20, new TimeOnly(19, 45)), (50, new TimeOnly(20, 0)) })
        {
            clock.UtcNow = Now.AddMinutes(minutes);
            var moved = await MoveAsync(clock, gor, later, time);

            Assert.Equal(ApprovalTrigger.EarlierGuestConsent, moved.AwaitingApprovalBecause);
            Assert.Equal(Now.AddHours(2), moved.ApprovalExpiresAtUtc);
        }

        // One question, retimed, whose deadline is the booking's.
        var request = await RequestStatusAsync(clock, later.Id);
        Assert.Equal(earlier.Id, request.EarlierReservationId);
        Assert.Equal(TableShareRequestStatus.Pending, request.Status);
        Assert.Equal(Now.AddHours(2), request.ExpiresAtUtc);
        Assert.Equal(ThursdayAt(20, 0), request.RequestedStartUtc);

        await using var db = fixture.CreateContext(clock);
        Assert.Equal(1, await db.TableShareRequests.CountAsync(r => r.LaterReservationId == later.Id));
    }

    [SkippableFact]
    public async Task At_a_branch_that_approves_everything_the_later_booking_still_waits_for_that_when_the_guest_before_goes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, approvesEverything: true);
        var anna = TestActor.Diner();

        // Her own booking waits for the branch too; his waits on her as well.
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45));

        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, later.AwaitingApprovalBecause);
        Assert.Equal(Now.AddHours(2), later.ApprovalExpiresAtUtc);

        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(db, clock, anna).CancelAsync(new CancelReservationCommand(earlier.Id));
        }

        var stillWaiting = await StoredAsync(clock, later.Id);
        Assert.Equal(ReservationStatus.PendingApproval, stillWaiting.Status);
        Assert.Equal(ApprovalTrigger.BranchApprovesEveryBooking, stillWaiting.ApprovalTrigger);

        // The two hours were the question's; waiting for a branch that approves every booking has no clock.
        Assert.Null(stillWaiting.ApprovalExpiresAtUtc);
    }

    // ---------------------------------------------------------------- 7. saving the policy

    [SkippableFact]
    public async Task Saving_the_policy_does_not_report_a_booking_cut_short_by_its_promise_as_outside_the_rules()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(21, 30));
        var cut = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(20, 45), acceptsFreeBy: ThursdayAt(21, 25));

        await using var db = fixture.CreateContext(clock);
        var settings = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var current = await settings.GetReservationPolicyAsync(branch.BranchId);

        var result = await settings.UpdateReservationPolicyAsync(branch.BranchId, Command(current));

        Assert.DoesNotContain(cut.Id, result.AffectedReservationIds);
        Assert.Equal(0, result.AffectedExistingReservations);

        // A turn time shorter than its cut sitting does report it.
        var shorter = await settings.UpdateReservationPolicyAsync(branch.BranchId, Command(current) with { TurnTimeMinutes = 30 });
        Assert.Contains(cut.Id, shorter.AffectedReservationIds);
    }

    // ---------------------------------------------------------------- 8. two quick yeses

    [SkippableFact]
    public async Task Two_quick_yeses_with_different_ids_are_both_answered_with_the_booking()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock);
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 30));

        Guid requestId;
        await using (var db = fixture.CreateContext(clock))
        {
            requestId = await db.TableShareRequests.Where(r => r.LaterReservationId == later.Id).Select(r => r.Id).SingleAsync();
        }

        // The second tap's save lands just after the first tap's whole answer committed.
        var first = new BeforeFirstInsertInto("ProcessedCommands", () => AnswerAsync(clock, anna, earlier.Id, requestId, accept: true));

        await using (var racing = fixture.CreateContext(clock, first))
        {
            var second = await fixture.CreateReservationService(racing, clock, anna).AnswerTableRequestAsync(
                new AnswerTableRequestCommand(earlier.Id, requestId, true, Guid.CreateVersion7()));

            Assert.True(first.Fired);
            Assert.Equal(earlier.Id, second.Id);
            Assert.Equal(ThursdayAt(19, 25), second.PromisedFreeByUtc);
        }

        Assert.Equal(ReservationStatus.Confirmed, (await StoredAsync(clock, later.Id)).Status);
    }

    // ---------------------------------------------------------------- a. a yes about a booking that moved

    [SkippableFact]
    public async Task A_yes_for_a_guest_whose_booking_moved_to_another_table_meanwhile_is_a_409_not_a_500()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, tableCount: 2, acceptsWebBookings: true);
        var earlier = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(18, 0), channel: ReservationChannel.Web);
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45));

        // A move committed under the other table's lock, between the floor reading the request and saying yes.
        await using (var db = fixture.CreateContext(clock))
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.Reservations SET DiningTableId = {branch.TableIds[1]} WHERE Id = {earlier.Id}");
        }

        await using var decide = fixture.CreateContext(clock);
        var refused = await Record.ExceptionAsync(() => fixture
            .CreateReservationService(decide, clock, TestActor.Waiter(branch.WaiterId))
            .ApproveAsync(new DecideReservationCommand(later.Id)));

        Assert.NotNull(refused);
        Assert.Equal(409, ApiExceptionMapper.Map(refused).Status);
        Assert.Null((await StoredAsync(clock, earlier.Id)).PromisedFreeByUtc);
    }

    // ---------------------------------------------------------------- b. going back to a slot is judged as booking it

    [SkippableFact]
    public async Task A_declined_change_goes_back_to_its_slot_only_when_the_tables_evening_would_take_it_now()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, tableCount: 2);
        var yan = TestActor.Diner();

        // Thursday 18:00 on table 1, confirmed; moved to tonight's 20:00 on table 2, which waits.
        var y = await BookAsync(clock, yan, branch, new TimeOnly(18, 0));
        var waiting = await MoveAsync(clock, yan, y, new TimeOnly(20, 0), date: Today, tableId: branch.TableIds[1]);
        Assert.Equal(ReservationStatus.PendingApproval, waiting.Status);

        // Meanwhile somebody takes Thursday 16:25 on table 1 - clear of 18:00 by the overlap rule alone,
        // but ninety-five minutes before it: going back would need their guest's yes.
        await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(16, 25));

        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId))
                .RejectAsync(new DecideReservationCommand(y.Id));
        }

        var stored = await StoredAsync(clock, y.Id);
        Assert.Equal(ReservationStatus.CancelledByVenue, stored.Status);
    }

    // ---------------------------------------------------------------- c. a party seated late, both reads

    [SkippableFact]
    public async Task A_party_seated_hours_late_is_judged_the_same_by_the_writer_and_the_room_screen()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // 17:00 at the branch. A 13:00 booking whose party only sat down now.
        var clock = new TestClock(Now.AddHours(5));
        var branch = await BranchAsync(clock);

        Reservation lateParty;
        await using (var db = fixture.CreateContext(clock))
        {
            lateParty = await TestBranchBuilder.AddConfirmedReservationAsync(db, branch, branch.FirstTableId, Now.AddHours(1));
            db.TableSessions.Add(TableSession.SeatReservation(branch.BranchId, branch.FirstTableId, lateParty.Id, 2, clock.UtcNow));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE dbo.Reservations SET Status = 4 WHERE Id = {lateParty.Id}");
        }

        // 18:30 tonight: the sitting runs to 18:30, so the table can be asked for - on the screen and when booking.
        await using (var db = fixture.CreateContext(clock))
        {
            var slot = await fixture.CreateAvailabilityQuery(db, clock).GetAvailabilityAsync(
                new AvailabilityRequest(branch.BranchId, 2, Today, new TimeOnly(18, 30)));

            var table = slot!.Tables.Single();
            Assert.True(table.IsAvailable);
            Assert.True(table.IsRequestable);
        }

        var booked = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(18, 30), date: Today);
        Assert.Equal(ReservationStatus.PendingApproval, booked.Status);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<TestBranch> BranchAsync(
        IClock clock,
        int tableCount = 1,
        bool acceptsWebBookings = false,
        bool approvesEverything = false,
        IReadOnlyList<(TimeOnly Opens, TimeOnly Closes, bool ClosesNextDay)>? openingHours = null)
    {
        await using var db = fixture.CreateContext(clock);

        var policy = approvesEverything
            ? new ReservationPolicy(90, 5, 15, 10, 10, 10, 14, 120, autoConfirm: false, 10m, true, 2, 8)
            : null;

        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: tableCount, policy: policy, openingHours: openingHours);

        if (acceptsWebBookings)
        {
            await db.Database.ExecuteSqlAsync($"UPDATE dbo.Branches SET AcceptsWebBookings = 1 WHERE Id = {branch.BranchId}");
        }

        return branch;
    }

    private async Task<ReservationView> BookAsync(
        IClock clock,
        TestActor diner,
        TestBranch branch,
        TimeOnly time,
        Guid? tableId = null,
        DateTime? acceptsFreeBy = null,
        ReservationChannel channel = ReservationChannel.Unknown,
        DateOnly? date = null,
        int partySize = 2)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(db, clock, diner).CreateAsync(
            new CreateReservationCommand(
                branch.BranchId,
                tableId ?? branch.FirstTableId,
                date ?? Thursday,
                time,
                PartySize: partySize,
                GuestName: "Ani Review",
                GuestPhone: "+37411223344",
                ClientCommandId: Guid.CreateVersion7(),
                Channel: channel,
                AcceptsFreeByUtc: acceptsFreeBy));
    }

    private async Task<ReservationView> MoveAsync(
        IClock clock,
        TestActor diner,
        ReservationView booking,
        TimeOnly time,
        int partySize = 2,
        DateOnly? date = null,
        Guid? tableId = null)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(db, clock, diner).RescheduleAsync(
            new RescheduleReservationCommand(
                booking.Id, tableId ?? booking.TableId, date ?? booking.LocalDate, time, partySize, Guid.CreateVersion7()));
    }

    private async Task<ReservationView> AnswerAsync(IClock clock, TestActor diner, Guid reservationId, Guid requestId, bool accept)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(db, clock, diner).AnswerTableRequestAsync(
            new AnswerTableRequestCommand(reservationId, requestId, accept, Guid.CreateVersion7()));
    }

    private async Task<IReadOnlyList<AwaitingApproval>> AwaitingAsync(IClock clock, TestBranch branch)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId))
            .ListAwaitingApprovalAsync(branch.BranchId);
    }

    private async Task SweepAsync(IClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        await fixture.CreateApprovalExpirySweep(db, clock).RunOnceAsync();
    }

    private async Task<Reservation> StoredAsync(IClock clock, Guid reservationId)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservationId);
    }

    private async Task<TableShareRequest> RequestStatusAsync(IClock clock, Guid laterReservationId)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.TableShareRequests
            .AsNoTracking()
            .Where(r => r.LaterReservationId == laterReservationId)
            .OrderBy(r => r.Status == TableShareRequestStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.CreatedAtUtc)
            .FirstAsync();
    }

    private async Task<TestActor> DinerWithDeviceAsync(IClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var unique = Guid.NewGuid().ToString("N")[..9];
        var diner = new DinerUser($"+3749{unique}", "hy");

        diner.ProveNumber(clock.UtcNow);

        db.DinerUsers.Add(diner);
        db.DinerDevices.Add(new DinerDevice(diner.Id, $"ExponentPushToken[{unique}]", DevicePlatform.Ios, "en", clock.UtcNow));

        await db.SaveChangesAsync();

        return TestActor.Diner(diner.Id);
    }

    private static ReservationPolicyCommand Command(ReservationPolicyView p) => new(
        p.TurnTimeMinutes, p.BufferMinutes, p.GraceMinutes, p.LateNudgeAfterMinutes, p.GraceExtensionMinutes,
        p.MinLeadMinutes, p.BookingWindowDays, p.CancellationDeadlineMinutes, p.AutoConfirm, p.ServiceChargePercent,
        p.PricesIncludeVat, p.MaxSeatOverhang, p.ApprovalRequiredAbovePartySize);
}
