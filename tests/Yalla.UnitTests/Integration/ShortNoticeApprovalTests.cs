using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Api.Errors;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Short-notice bookings, against a real SQL Server: which ones wait for the floor, the fifteen
/// minutes the floor has to answer, the sweep that declines what nobody answered, who may answer, the
/// floor's list, and what a move does.
/// </summary>
/// <remarks>
/// <para>
/// The branch is in <c>Asia/Yerevan</c> (UTC+4) on the shipped restaurant policy - 90-minute turn,
/// 15 minutes clearing, 30 of lead, approval above 8 guests, at most 2 seats left empty - open
/// 10:00-23:00 unless a test says otherwise. "Now" is noon on Thursday 10 September 2026 there.
/// </para>
/// <para>
/// The races are real, as in <see cref="ReservationServiceTests"/>: two contexts on two connections,
/// with the competing write committed at the moment the other is about to save.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ShortNoticeApprovalTests(SqlServerFixture fixture)
{
    /// <summary>12:00 on Thursday 10 September 2026 in Yerevan.</summary>
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 9, 10);

    private static readonly TimeOnly EightPm = new(20, 0);

    /// <summary>A branch that never shuts, for the hours either side of midnight.</summary>
    private static readonly IReadOnlyList<(TimeOnly Opens, TimeOnly Closes, bool ClosesNextDay)> AroundTheClock =
        [(new TimeOnly(0, 0), new TimeOnly(23, 59), false)];

    // ---------------------------------------------------------------- which bookings wait

    [SkippableFact]
    public async Task A_booking_for_tonight_waits_for_the_floor_with_its_reason_and_deadline_stored()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var tonight = await diner.CreateAsync(Booking(branch, branch.TableIds[0], Today, EightPm));
        var inThreeDays = await diner.CreateAsync(Booking(branch, branch.TableIds[1], Today.AddDays(3), EightPm));

        Assert.Equal(ReservationStatus.PendingApproval, tonight.Status);
        Assert.Equal(ApprovalTrigger.ShortNotice, tonight.AwaitingApprovalBecause);
        Assert.Equal(Now.AddMinutes(ShortNoticeRule.AnswerWindowMinutes), tonight.ApprovalExpiresAtUtc);
        Assert.Null(tonight.DeclinedBecause);

        // Nothing about next week changed: confirmed on the spot, no deadline.
        Assert.Equal(ReservationStatus.Confirmed, inThreeDays.Status);
        Assert.Null(inThreeDays.AwaitingApprovalBecause);
        Assert.Null(inThreeDays.ApprovalExpiresAtUtc);

        var stored = await StoredAsync(tonight.Id, clock);

        Assert.Equal(ApprovalTrigger.ShortNotice, stored.ApprovalTrigger);
        Assert.Equal(Now.AddMinutes(15), stored.ApprovalExpiresAtUtc);

        var confirmed = await StoredAsync(inThreeDays.Id, clock);

        Assert.Null(confirmed.ApprovalTrigger);
        Assert.Null(confirmed.ApprovalExpiresAtUtc);
    }

    /// <summary>
    /// The channel is the client's own report, so it cannot switch the rule off: a diner's booking
    /// that says it was taken by staff still waits for the floor.
    /// </summary>
    [SkippableFact]
    public async Task A_diners_booking_waits_whatever_channel_it_claims()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var claimsStaff = await diner.CreateAsync(
            Booking(branch, branch.FirstTableId, Today, EightPm) with { Channel = ReservationChannel.Staff });

        Assert.Equal(ReservationStatus.PendingApproval, claimsStaff.Status);
        Assert.Equal(ApprovalTrigger.ShortNotice, claimsStaff.AwaitingApprovalBecause);
    }

    [SkippableFact]
    public async Task Tomorrow_under_six_hours_waits_and_six_hours_or_more_does_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // 22:00 on the 10th in Yerevan.
        var clock = new TestClock(new DateTime(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, openingHours: AroundTheClock);
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var tomorrow = Today.AddDays(1);

        var fiveFiftyNine = await diner.CreateAsync(Booking(branch, branch.TableIds[0], tomorrow, new TimeOnly(3, 59)));
        var sixHours = await diner.CreateAsync(Booking(branch, branch.TableIds[1], tomorrow, new TimeOnly(4, 0)));

        Assert.Equal(ReservationStatus.PendingApproval, fiveFiftyNine.Status);
        Assert.Equal(ApprovalTrigger.ShortNotice, fiveFiftyNine.AwaitingApprovalBecause);
        Assert.Equal(ReservationStatus.Confirmed, sixHours.Status);
    }

    [SkippableFact]
    public async Task Just_after_midnight_today_is_the_new_day_at_the_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // 00:30 on the 11th in Yerevan, which is still the 10th in UTC.
        var clock = new TestClock(new DateTime(2026, 9, 10, 20, 30, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, openingHours: AroundTheClock);
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var thisEvening = await diner.CreateAsync(Booking(branch, branch.TableIds[0], Today.AddDays(1), EightPm));
        var tomorrowEvening = await diner.CreateAsync(Booking(branch, branch.TableIds[1], Today.AddDays(2), EightPm));

        // Nineteen and a half hours away, and still today's at the branch.
        Assert.Equal(ReservationStatus.PendingApproval, thisEvening.Status);
        Assert.Equal(ReservationStatus.Confirmed, tomorrowEvening.Status);
    }

    /// <summary>
    /// Short notice comes first among the reasons, because it is the one with a deadline - and the
    /// stored reason, not one worked out from today's policy, is what every later read says.
    /// </summary>
    [SkippableFact]
    public async Task Short_notice_wins_over_the_other_reasons_and_the_stored_reason_outlives_a_policy_change()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tenSeater = await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10);
        var strict = await TestBranchBuilder.CreateAsync(db, policy: PolicyWith(autoConfirm: false));

        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var largeTonight = await diner.CreateAsync(Booking(branch, tenSeater.Id, Today, EightPm, partySize: 9));
        var largeNextWeek = await diner.CreateAsync(Booking(branch, tenSeater.Id, Today.AddDays(7), EightPm, partySize: 9));
        var strictTonight = await diner.CreateAsync(Booking(strict, strict.TableIds[0], Today, EightPm));
        var strictNextWeek = await diner.CreateAsync(Booking(strict, strict.TableIds[0], Today.AddDays(7), EightPm));

        Assert.Equal(ApprovalTrigger.ShortNotice, largeTonight.AwaitingApprovalBecause);
        Assert.NotNull(largeTonight.ApprovalExpiresAtUtc);
        Assert.Equal(ApprovalTrigger.LargeParty, largeNextWeek.AwaitingApprovalBecause);
        Assert.Null(largeNextWeek.ApprovalExpiresAtUtc);
        Assert.Equal(ApprovalTrigger.ShortNotice, strictTonight.AwaitingApprovalBecause);
        Assert.Equal(ApprovalTrigger.BranchApprovesEveryBooking, strictNextWeek.AwaitingApprovalBecause);

        // The venue switches to approving every booking after the large party asked. The old read
        // would now call next week's party "the branch approves everything"; the stored reason is
        // what the diner was told.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.Branches SET ReservationPolicy_AutoConfirm = 0 WHERE Id = {branch.BranchId}");

        var manager = fixture.CreateReservationService(db, clock, TestActor.Manager(branch.ManagerId));
        var pending = await manager.ListForBranchAsync(branch.BranchId, ReservationStatus.PendingApproval);

        Assert.Equal(ApprovalTrigger.LargeParty, pending.Single(r => r.Id == largeNextWeek.Id).AwaitingApprovalBecause);
        Assert.Equal(ApprovalTrigger.ShortNotice, pending.Single(r => r.Id == largeTonight.Id).AwaitingApprovalBecause);

        // A row from before the column has no stored reason and still gets one, from the policy.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.Reservations SET ApprovalTrigger = NULL WHERE Id = {largeNextWeek.Id}");

        var legacy = await manager.ListForBranchAsync(branch.BranchId, ReservationStatus.PendingApproval);

        Assert.Equal(
            ApprovalTrigger.BranchApprovesEveryBooking,
            legacy.Single(r => r.Id == largeNextWeek.Id).AwaitingApprovalBecause);
    }

    [SkippableFact]
    public async Task The_answer_window_closes_at_the_start_when_the_start_is_sooner()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, policy: PolicyWith(minLeadMinutes: 0));
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        // Ten minutes from now.
        var soon = await diner.CreateAsync(Booking(branch, branch.FirstTableId, Today, new TimeOnly(12, 10)));

        Assert.Equal(ReservationStatus.PendingApproval, soon.Status);
        Assert.Equal(soon.StartUtc, soon.ApprovalExpiresAtUtc);
        Assert.Equal(Now.AddMinutes(10), soon.ApprovalExpiresAtUtc);
    }

    // ---------------------------------------------------------------- the sweep

    [SkippableFact]
    public async Task The_sweep_declines_exactly_the_lapsed_requests_once_and_apologises_to_each_diner()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, now, today) = await SweepWorldAsync(month: 1);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 4);
        var tenSeater = await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10);

        // The dispatcher below is table-wide, so these bookings must be the only cause of anything due.
        await fixture.ClearOutboxAsync(clock);

        var englishActor = await DinerWithDeviceAsync(db, clock, "en");
        var english = fixture.CreateReservationService(db, clock, englishActor);
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId));

        var first = await english.CreateAsync(Booking(branch, branch.TableIds[0], today, EightPm));

        clock.Advance(TimeSpan.FromMinutes(10));
        var second = await diner.CreateAsync(Booking(branch, branch.TableIds[1], today, EightPm));
        var largeNextWeek = await diner.CreateAsync(Booking(branch, tenSeater.Id, today.AddDays(7), EightPm, partySize: 9));
        var answered = await diner.CreateAsync(Booking(branch, branch.TableIds[2], today, EightPm));

        clock.Advance(TimeSpan.FromMinutes(1));
        await waiter.ApproveAsync(new DecideReservationCommand(answered.Id));

        // The link a web diner holds says the same as the app: waiting, until when.
        var link = new PublicBookingService(db, clock, english);
        Assert.Equal(first.ApprovalExpiresAtUtc, (await link.GetAsync(first.ManageToken!)).ApprovalExpiresAtUtc);

        // A minute past the first deadline.
        clock.UtcNow = now.AddMinutes(16);

        Assert.Equal(1, await SweepAsync(clock));
        Assert.Equal(0, await SweepAsync(clock));

        var declined = await StoredAsync(first.Id, clock);

        Assert.Equal(ReservationStatus.CancelledByVenue, declined.Status);
        Assert.Equal(DeclineReason.NoAnswer, declined.DeclineReason);
        Assert.Equal(now.AddMinutes(16), declined.CancelledAtUtc);
        Assert.Null(declined.ApprovalExpiresAtUtc);

        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(second.Id, clock)).Status);
        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(largeNextWeek.Id, clock)).Status);
        Assert.Equal(ReservationStatus.Confirmed, (await StoredAsync(answered.Id, clock)).Status);

        // Told once, as an apology - and nothing else of the booking's is left to send.
        var messages = await MessagesForAsync(first.Id, clock);
        var decided = Assert.Single(messages, m => m.Type == OutboxMessageTypes.ReservationDecided);

        Assert.Equal(OutboxMessageTypes.KeyFor("reservation", first.Id, "expired"), decided.IdempotencyKey);
        Assert.Contains("\"expired\":true", decided.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"approved\":false", decided.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(messages, m => m.Type == OutboxMessageTypes.ReservationLateNudge);

        await using (var check = fixture.CreateContext(clock))
        {
            var entry = await check.DinerNotifications.AsNoTracking()
                .SingleAsync(n => n.ReservationId == first.Id && n.Kind == DinerNotificationKinds.BookingDeclined);

            Assert.Contains("\"expired\":\"true\"", entry.ParamsJson, StringComparison.Ordinal);
        }

        // The diner's own list and the link say why.
        var mine = await english.GetMineAsync();
        var past = Assert.Single(mine.Past, r => r.Id == first.Id);

        Assert.Equal(DeclineReason.NoAnswer, past.DeclinedBecause);
        Assert.Null(past.ApprovalExpiresAtUtc);
        Assert.Null(past.AwaitingApprovalBecause);

        var linked = await link.GetAsync(first.ManageToken!);
        Assert.Equal(DeclineReason.NoAnswer, linked.DeclinedBecause);
        Assert.Null(linked.ApprovalExpiresAtUtc);

        // The push the diner reads, in their own language.
        var channel = new RecordingChannel();

        await using (var passDb = fixture.CreateContext(clock))
        {
            await Dispatcher(passDb, clock, channel).RunOnceAsync();
        }

        // The waiter's approval of the third went out on the same pass; this is the first one's.
        var push = Assert.Single(
            channel.OfKind("reservation-decided"), m => m.Data["reservationId"] == first.Id.ToString());
        var (title, body) = NotificationText.ReservationExpired("en", await VenueNameAsync(branch, clock));

        Assert.Equal(englishActor.DinerUserId, push.DinerUserId);
        Assert.Equal(title, push.Title);
        Assert.Equal(body, push.Body);
        Assert.Equal("true", push.Data["expired"]);
        Assert.Equal("false", push.Data["approved"]);

        // The second request runs out ten minutes later, and goes the same way.
        clock.UtcNow = now.AddMinutes(26);
        Assert.Equal(1, await SweepAsync(clock));
        Assert.Equal(DeclineReason.NoAnswer, (await StoredAsync(second.Id, clock)).DeclineReason);
    }

    /// <summary>
    /// The waiter's approval lands just before the sweep saves. The sweep's write matches no row - the
    /// approval moved the row version - so the booking stays confirmed and the diner hears one thing.
    /// </summary>
    [SkippableFact]
    public async Task A_waiter_approving_as_the_sweep_saves_wins_and_the_sweep_writes_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, now, today) = await SweepWorldAsync(month: 2);
        Guid bookingId;
        TestBranch branch;

        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
            bookingId = (await fixture.CreateReservationService(db, clock, TestActor.Diner())
                .CreateAsync(Booking(branch, branch.FirstTableId, today, EightPm))).Id;
        }

        // The waiter's tablet reads its clock a second before the deadline; the sweep runs a second after.
        var waiterClock = new TestClock(now.AddMinutes(15).AddSeconds(-1));
        var sweepClock = new TestClock(now.AddMinutes(15).AddSeconds(1));

        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDATE [Reservations]", StringComparison.Ordinal),
            async () =>
            {
                await using var waiterDb = fixture.CreateContext(waiterClock);
                await fixture.CreateReservationService(waiterDb, waiterClock, TestActor.Waiter(branch.WaiterId))
                    .ApproveAsync(new DecideReservationCommand(bookingId));
            });

        int declined;

        await using (var sweepDb = fixture.CreateContext(sweepClock, race))
        {
            declined = await new ApprovalExpirySweep(
                    sweepDb, sweepClock, fixture.CreateOutbox(sweepDb, sweepClock), NullLogger<ApprovalExpirySweep>.Instance)
                .RunOnceAsync();
        }

        Assert.True(race.Fired);
        Assert.Equal(0, declined);

        var stored = await StoredAsync(bookingId, sweepClock);
        Assert.Equal(ReservationStatus.Confirmed, stored.Status);
        Assert.Null(stored.DeclineReason);

        var decided = (await MessagesForAsync(bookingId, sweepClock))
            .Where(m => m.Type == OutboxMessageTypes.ReservationDecided)
            .Select(m => m.IdempotencyKey)
            .ToList();

        Assert.Equal([OutboxMessageTypes.KeyFor("reservation", bookingId, "approved")], decided);
    }

    /// <summary>
    /// The other way round: the sweep commits while the waiter's approval is about to save. The
    /// approval's write matches no row, and the tablet is told it was too late - not that the record
    /// merely changed.
    /// </summary>
    [SkippableFact]
    public async Task The_sweep_declining_as_a_waiter_approves_answers_the_waiter_too_late()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, now, today) = await SweepWorldAsync(month: 3);
        Guid bookingId;
        TestBranch branch;

        await using (var db = fixture.CreateContext(clock))
        {
            branch = await TestBranchBuilder.CreateAsync(db);
            bookingId = (await fixture.CreateReservationService(db, clock, TestActor.Diner())
                .CreateAsync(Booking(branch, branch.FirstTableId, today, EightPm))).Id;
        }

        var waiterClock = new TestClock(now.AddMinutes(15).AddSeconds(-1));
        var sweepClock = new TestClock(now.AddMinutes(15).AddSeconds(1));
        var swept = 0;

        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("UPDATE [Reservations]", StringComparison.Ordinal),
            async () => swept = await SweepAsync(sweepClock));

        ReservationApprovalExpiredException tooLate;

        await using (var waiterDb = fixture.CreateContext(waiterClock, race))
        {
            tooLate = await Assert.ThrowsAsync<ReservationApprovalExpiredException>(
                () => fixture.CreateReservationService(waiterDb, waiterClock, TestActor.Waiter(branch.WaiterId))
                    .ApproveAsync(new DecideReservationCommand(bookingId)));
        }

        Assert.True(race.Fired);
        Assert.Equal(1, swept);
        Assert.Equal(bookingId, tooLate.ReservationId);

        var mapped = ApiExceptionMapper.Map(tooLate);
        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal(ErrorCodes.ReservationApprovalExpired, mapped.Code);

        var stored = await StoredAsync(bookingId, sweepClock);
        Assert.Equal(DeclineReason.NoAnswer, stored.DeclineReason);

        var decided = (await MessagesForAsync(bookingId, sweepClock))
            .Where(m => m.Type == OutboxMessageTypes.ReservationDecided)
            .Select(m => m.IdempotencyKey)
            .ToList();

        Assert.Equal([OutboxMessageTypes.KeyFor("reservation", bookingId, "expired")], decided);
    }

    // ---------------------------------------------------------------- who answers, and until when

    [SkippableFact]
    public async Task A_waiter_decides_at_their_own_branch_and_nobody_else_on_the_floor_does()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var otherVenue = await TestBranchBuilder.CreateAsync(db);
        var siblingWaiterId = await SiblingBranchWaiterAsync(db, branch);

        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var toApprove = await diner.CreateAsync(Booking(branch, branch.TableIds[0], Today, EightPm));
        var toDecline = await diner.CreateAsync(Booking(branch, branch.TableIds[1], Today, EightPm));

        // Another venue's waiter, a waiter at a sibling branch of the same venue, and a cook.
        await Assert.ThrowsAsync<StaffPermissionException>(() =>
            fixture.CreateReservationService(db, clock, TestActor.Waiter(otherVenue.WaiterId))
                .ApproveAsync(new DecideReservationCommand(toApprove.Id)));

        await Assert.ThrowsAsync<StaffPermissionException>(() =>
            fixture.CreateReservationService(db, clock, TestActor.Waiter(siblingWaiterId))
                .RejectAsync(new DecideReservationCommand(toDecline.Id)));

        await Assert.ThrowsAsync<StaffPermissionException>(() =>
            fixture.CreateReservationService(db, clock, new TestActor(ActorType.Staff, Guid.CreateVersion7(), StaffRole.Kitchen))
                .ApproveAsync(new DecideReservationCommand(toApprove.Id)));

        // The branch's own waiter, both ways.
        var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId));

        var approved = await waiter.ApproveAsync(new DecideReservationCommand(toApprove.Id));
        var declined = await waiter.RejectAsync(new DecideReservationCommand(toDecline.Id, "the terrace is closed tonight"));

        Assert.Equal(ReservationStatus.Confirmed, approved.Status);
        Assert.Null(approved.ApprovalExpiresAtUtc);
        Assert.Null(approved.AwaitingApprovalBecause);

        Assert.Equal(ReservationStatus.CancelledByVenue, declined.Status);
        Assert.Equal(DeclineReason.StaffDeclined, declined.DeclinedBecause);
        Assert.Equal("the terrace is closed tonight", declined.CancellationReason);

        // The diner of the declined one is told it was the venue's no, not the clock.
        var decided = Assert.Single(
            await MessagesForAsync(toDecline.Id, clock), m => m.Type == OutboxMessageTypes.ReservationDecided);

        Assert.Contains("\"expired\":false", decided.PayloadJson, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Approving_or_declining_after_the_window_is_too_late_and_so_is_approving_after_the_start()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var (clock, _, today) = await SweepWorldAsync(month: 4);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tenSeater = await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10);
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId));

        var tonight = await diner.CreateAsync(Booking(branch, branch.FirstTableId, today, EightPm));
        var large = await diner.CreateAsync(Booking(branch, tenSeater.Id, today.AddDays(2), EightPm, partySize: 9));

        // On the deadline, before the sweep has run.
        clock.UtcNow = tonight.ApprovalExpiresAtUtc!.Value;

        var approve = await Assert.ThrowsAsync<ReservationApprovalExpiredException>(
            () => waiter.ApproveAsync(new DecideReservationCommand(tonight.Id)));
        await Assert.ThrowsAsync<ReservationApprovalExpiredException>(
            () => waiter.RejectAsync(new DecideReservationCommand(tonight.Id)));

        Assert.Equal(tonight.ApprovalExpiresAtUtc, approve.ApprovalExpiresAtUtc);
        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(tonight.Id, clock)).Status);

        // After the sweep, the same answer rather than "not pending".
        Assert.Equal(1, await SweepAsync(clock));
        await Assert.ThrowsAsync<ReservationApprovalExpiredException>(
            () => fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId))
                .ApproveAsync(new DecideReservationCommand(tonight.Id)));

        // A booking with no window cannot be approved once it has started - it can still be declined.
        clock.UtcNow = large.StartUtc.AddMinutes(1);
        var started = fixture.CreateReservationService(db, clock, TestActor.Manager(branch.ManagerId));

        await Assert.ThrowsAsync<ReservationApprovalExpiredException>(
            () => started.ApproveAsync(new DecideReservationCommand(large.Id)));

        var declined = await started.RejectAsync(new DecideReservationCommand(large.Id));
        Assert.Equal(DeclineReason.StaffDeclined, declined.DeclinedBecause);
    }

    // ---------------------------------------------------------------- the floor's list

    [SkippableFact]
    public async Task The_floor_sees_each_request_soonest_deadline_first_with_its_tables_evening_in_one_query()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var tenSeater = await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10);
        var elsewhere = await TestBranchBuilder.CreateAsync(db);
        var tableOne = branch.TableIds[0];

        var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId));

        // Somebody the waiter sat at table 1 a moment ago.
        await fixture.CreateService(db, clock, TestActor.Waiter(branch.WaiterId))
            .SeatWalkInAsync(new SeatWalkInCommand(branch.BranchId, tableOne, 3, Guid.CreateVersion7()));

        // Table 1's evening: a confirmed 17:00, then the request at 19:00, then another at 21:00.
        var before = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, tableOne, Today, new TimeOnly(17, 0), partySize: 4, guestName: "Anna", guestPhone: "+37499111222"));
        await waiter.ApproveAsync(new DecideReservationCommand(before.Id));

        var request = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, tableOne, Today, new TimeOnly(19, 0), partySize: 2, guestName: "Gor", guestPhone: "+37499333444"));

        clock.Advance(TimeSpan.FromMinutes(1));
        var later = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, tableOne, Today, new TimeOnly(21, 0), partySize: 3, guestName: "Lilit"));

        // No deadline: a large party next week waits as long as it takes, after the timed ones.
        var large = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, tenSeater.Id, Today.AddDays(7), EightPm, partySize: 9));

        // Not this branch's.
        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(elsewhere, elsewhere.FirstTableId, Today, EightPm));

        var counter = new CommandCountingInterceptor();
        await using var countedDb = fixture.CreateContext(clock, counter);
        var floor = fixture.CreateReservationService(countedDb, clock, TestActor.Waiter(branch.WaiterId));

        var awaiting = await floor.ListAwaitingApprovalAsync(branch.BranchId);

        // One statement to check the waiter's own staff row, and one for the whole list - however many
        // requests there are and however much of each table's evening comes with them.
        Assert.Equal(2, counter.Count);

        Assert.Equal([request.Id, later.Id, large.Id], awaiting.Select(a => a.Reservation.Id).ToList());

        var first = awaiting[0];
        Assert.Equal(ApprovalTrigger.ShortNotice, first.Trigger);
        Assert.Equal(Now.AddMinutes(15), first.ApprovalExpiresAtUtc);
        Assert.Equal(first.ApprovalExpiresAtUtc, first.Reservation.ApprovalExpiresAtUtc);
        Assert.Equal("1", first.Reservation.TableLabel);
        Assert.Equal("Windows", first.Reservation.RoomName);
        Assert.Equal("Gor", first.Reservation.GuestName);
        Assert.Equal("+37499333444", first.Reservation.GuestPhone);

        var previous = Assert.IsType<AwaitingPreviousBooking>(first.PreviousBooking);
        Assert.Equal(before.Id, previous.ReservationId);
        Assert.Equal(new TimeOnly(17, 0), previous.LocalStartTime);
        Assert.Equal(before.StartUtc, previous.StartUtc);
        Assert.Equal(4, previous.PartySize);
        Assert.Equal("Anna", previous.GuestName);
        Assert.Equal("+37499111222", previous.GuestPhone);
        Assert.Equal(ReservationStatus.Confirmed, previous.Status);

        var next = Assert.IsType<AwaitingNextBooking>(first.NextBooking);
        Assert.Equal(later.Id, next.ReservationId);
        Assert.Equal(new TimeOnly(21, 0), next.LocalStartTime);
        Assert.Equal(3, next.PartySize);

        var sitting = Assert.IsType<AwaitingCurrentSitting>(first.CurrentSitting);
        Assert.Equal(Now, sitting.SeatedAtUtc);
        Assert.Equal(3, sitting.PartySize);

        // The 21:00 request's previous booking is the 19:00 one, still waiting itself.
        Assert.Equal(request.Id, awaiting[1].PreviousBooking!.ReservationId);
        Assert.Equal(ReservationStatus.PendingApproval, awaiting[1].PreviousBooking!.Status);
        Assert.Null(awaiting[1].NextBooking);

        Assert.Equal(ApprovalTrigger.LargeParty, awaiting[2].Trigger);
        Assert.Null(awaiting[2].ApprovalExpiresAtUtc);
        Assert.Null(awaiting[2].PreviousBooking);
        Assert.Null(awaiting[2].CurrentSitting);

        // Past the first deadline, before any sweep: nobody can answer it, so it is not offered.
        clock.UtcNow = Now.AddMinutes(15);
        var afterDeadline = await floor.ListAwaitingApprovalAsync(branch.BranchId);

        Assert.Equal([later.Id, large.Id], afterDeadline.Select(a => a.Reservation.Id).ToList());
    }

    [SkippableFact]
    public async Task The_platform_reads_the_floors_list_without_anybodys_number_and_other_staff_not_at_all()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var otherVenue = await TestBranchBuilder.CreateAsync(db);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);

        var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId));
        var before = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId, Today, new TimeOnly(17, 0), guestPhone: "+37499111222"));
        await waiter.ApproveAsync(new DecideReservationCommand(before.Id));

        await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(branch, branch.FirstTableId, Today, new TimeOnly(19, 0), guestPhone: "+37499333444"));

        var asAdmin = await fixture.CreateReservationService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId))
            .ListAwaitingApprovalAsync(branch.BranchId);

        var item = Assert.Single(asAdmin);
        Assert.Equal(string.Empty, item.Reservation.GuestPhone);
        Assert.Null(item.PreviousBooking!.GuestPhone);
        Assert.Equal(before.Id, item.PreviousBooking.ReservationId);

        // The branch's own manager sees the numbers; another venue's waiter sees nothing at all.
        var asManager = await fixture.CreateReservationService(db, clock, TestActor.Manager(branch.ManagerId))
            .ListAwaitingApprovalAsync(branch.BranchId);

        Assert.Equal("+37499333444", Assert.Single(asManager).Reservation.GuestPhone);

        await Assert.ThrowsAsync<StaffPermissionException>(
            () => fixture.CreateReservationService(db, clock, TestActor.Waiter(otherVenue.WaiterId))
                .ListAwaitingApprovalAsync(branch.BranchId));
    }

    // ---------------------------------------------------------------- moving a booking

    [SkippableFact]
    public async Task A_confirmed_booking_that_keeps_its_table_and_start_and_does_not_grow_stays_confirmed_tonight()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // Booked two days ago, for tonight.
        var clock = new TestClock(Now.AddDays(-2));
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var booked = await diner.CreateAsync(Booking(branch, branch.FirstTableId, Today, EightPm, partySize: 3));
        Assert.Equal(ReservationStatus.Confirmed, booked.Status);

        clock.UtcNow = Now;

        // One fewer, same table, same time: nothing the venue said yes to got harder to keep.
        var fewer = await diner.RescheduleAsync(Move(booked.Id, branch.FirstTableId, Today, EightPm, partySize: 2));

        Assert.Equal(ReservationStatus.Confirmed, fewer.Status);
        Assert.Null(fewer.ApprovalExpiresAtUtc);
        Assert.Equal(booked.ConfirmedAtUtc, fewer.ConfirmedAtUtc);

        // Another table at the same time is a new question about a table: the floor's, with a fresh window.
        clock.Advance(TimeSpan.FromMinutes(5));
        var otherTable = await diner.RescheduleAsync(Move(booked.Id, branch.TableIds[1], Today, EightPm, partySize: 2));

        Assert.Equal(ReservationStatus.PendingApproval, otherTable.Status);
        Assert.Equal(ApprovalTrigger.ShortNotice, otherTable.AwaitingApprovalBecause);
        Assert.Equal(Now.AddMinutes(5 + 15), otherTable.ApprovalExpiresAtUtc);
        Assert.Null(otherTable.ConfirmedAtUtc);
    }

    [SkippableFact]
    public async Task A_move_into_tonight_waits_with_a_fresh_window_and_a_move_out_of_it_is_confirmed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());

        var booked = await diner.CreateAsync(Booking(branch, branch.FirstTableId, Today.AddDays(3), EightPm));
        Assert.Equal(ReservationStatus.Confirmed, booked.Status);

        clock.Advance(TimeSpan.FromMinutes(7));
        var intoTonight = await diner.RescheduleAsync(Move(booked.Id, branch.FirstTableId, Today, EightPm));

        Assert.Equal(ReservationStatus.PendingApproval, intoTonight.Status);
        Assert.Equal(ApprovalTrigger.ShortNotice, intoTonight.AwaitingApprovalBecause);
        Assert.Equal(Now.AddMinutes(7 + 15), intoTonight.ApprovalExpiresAtUtc);

        // Moved again within tonight: a new request, so a new fifteen minutes.
        clock.Advance(TimeSpan.FromMinutes(3));
        var again = await diner.RescheduleAsync(Move(booked.Id, branch.FirstTableId, Today, new TimeOnly(21, 0)));
        Assert.Equal(Now.AddMinutes(10 + 15), again.ApprovalExpiresAtUtc);

        // And back to another day: confirmed on the spot, the window gone, so the sweep never finds it.
        var outOfIt = await diner.RescheduleAsync(Move(booked.Id, branch.FirstTableId, Today.AddDays(4), EightPm));

        Assert.Equal(ReservationStatus.Confirmed, outOfIt.Status);
        Assert.Null(outOfIt.ApprovalExpiresAtUtc);
        Assert.Null(outOfIt.AwaitingApprovalBecause);
        Assert.Null((await StoredAsync(booked.Id, clock)).ApprovalExpiresAtUtc);

        // An hour on, the sweep passes it by: it is waiting for nobody.
        clock.Advance(TimeSpan.FromHours(1));
        await SweepAsync(clock);
        Assert.Equal(ReservationStatus.Confirmed, (await StoredAsync(booked.Id, clock)).Status);
    }

    // ---------------------------------------------------------------- what the diner is told

    [SkippableFact]
    public async Task Availability_says_which_times_wait_for_the_floor_slot_by_slot()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // 22:00 on the 10th.
        var clock = new TestClock(new DateTime(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, openingHours: AroundTheClock);
        var availability = fixture.CreateAvailabilityQuery(db, clock);
        var tomorrow = Today.AddDays(1);

        var day = await availability.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, tomorrow, 2));

        Assert.NotNull(day);

        // Under six hours from 22:00 is before 04:00 tomorrow.
        Assert.All(day.Slots.Where(s => string.CompareOrdinal(s.Time, "04:00") < 0), s => Assert.True(s.RequiresApproval, s.Time));
        Assert.All(day.Slots.Where(s => string.CompareOrdinal(s.Time, "04:00") >= 0), s => Assert.False(s.RequiresApproval, s.Time));
        Assert.Contains(day.Slots, s => s.Time == "03:30" && s.IsAvailable);

        // Today's are all today's, struck or not.
        var today = await availability.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, Today, 2));
        Assert.All(today!.Slots, s => Assert.True(s.RequiresApproval, s.Time));

        // The single-slot read agrees with the picker, table by table.
        var early = await availability.GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, tomorrow, new TimeOnly(3, 30)));
        var later = await availability.GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, tomorrow, new TimeOnly(4, 0)));

        Assert.All(early!.Tables, t => Assert.True(t.RequiresApproval));
        Assert.All(later!.Tables, t => Assert.False(t.RequiresApproval));
    }

    /// <summary>
    /// A reminder due while the floor is still deciding is not written at all - the answer says it -
    /// and one due later is not sent for a booking still waiting on its short-notice answer.
    /// </summary>
    [SkippableFact]
    public async Task A_short_notice_booking_is_reminded_only_once_somebody_has_said_yes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 3);

        await fixture.ClearOutboxAsync(clock);

        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId));

        // 15:10 is three hours and ten minutes away: its reminder would fall at 12:10, inside the window.
        var soon = await diner.CreateAsync(Booking(branch, branch.TableIds[0], Today, new TimeOnly(15, 10)));
        var unanswered = await diner.CreateAsync(Booking(branch, branch.TableIds[1], Today, EightPm));
        var approved = await diner.CreateAsync(Booking(branch, branch.TableIds[2], Today, EightPm));

        Assert.DoesNotContain(
            await MessagesForAsync(soon.Id, clock), m => m.Type == OutboxMessageTypes.ReservationReminder);

        clock.Advance(TimeSpan.FromMinutes(5));
        await waiter.ApproveAsync(new DecideReservationCommand(approved.Id));

        // Three hours before eight, and no sweep has run: the unanswered one is still waiting.
        clock.UtcNow = new DateTime(2026, 9, 10, 13, 0, 0, DateTimeKind.Utc);

        var channel = new RecordingChannel();

        await using (var passDb = fixture.CreateContext(clock))
        {
            await Dispatcher(passDb, clock, channel).RunOnceAsync();
        }

        var reminder = Assert.Single(channel.OfKind("reservation-reminder"));
        Assert.Equal(approved.Id.ToString(), reminder.Data["reservationId"]);
        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(unanswered.Id, clock)).Status);
    }

    // ---------------------------------------------------------------- helpers

    private static CreateReservationCommand Booking(
        TestBranch branch,
        Guid tableId,
        DateOnly date,
        TimeOnly at,
        int partySize = 2,
        string guestName = "Ani Test",
        string guestPhone = "+37411223344") =>
        new(
            branch.BranchId,
            tableId,
            date,
            at,
            PartySize: partySize,
            GuestName: guestName,
            GuestPhone: guestPhone,
            ClientCommandId: Guid.CreateVersion7());

    private static RescheduleReservationCommand Move(
        Guid reservationId, Guid tableId, DateOnly date, TimeOnly at, int partySize = 2) =>
        new(reservationId, tableId, date, at, partySize, Guid.CreateVersion7());

    private static ReservationPolicy PolicyWith(bool autoConfirm = true, int minLeadMinutes = 30) => new(
        turnTimeMinutes: 90,
        bufferMinutes: 15,
        graceMinutes: 15,
        lateNudgeAfterMinutes: 10,
        graceExtensionMinutes: 10,
        minLeadMinutes: minLeadMinutes,
        bookingWindowDays: 14,
        cancellationDeadlineMinutes: 120,
        autoConfirm: autoConfirm,
        serviceChargePercent: 10m,
        pricesIncludeVat: true,
        maxSeatOverhang: 2,
        approvalRequiredAbovePartySize: 8);

    /// <summary>
    /// A clock on a date of this test's own, with every request other tests left lapsed before it
    /// already declined.
    /// </summary>
    /// <remarks>
    /// The sweep reads the whole table - that is its job - and the database is shared by every test in
    /// the collection, which runs one test at a time. So a test that counts what a pass declined stands
    /// on a date nothing else books on - the 10th of its own month in 2030 - and first sweeps away what
    /// earlier tests left behind.
    /// </remarks>
    private async Task<(TestClock Clock, DateTime Now, DateOnly Today)> SweepWorldAsync(int month)
    {
        var now = new DateTime(2030, month, 10, 8, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(now);

        while (await SweepAsync(clock) > 0)
        {
        }

        return (clock, now, DateOnly.FromDateTime(now));
    }

    /// <summary>One sweep pass on its own context, as the hosted service runs it.</summary>
    private async Task<int> SweepAsync(IClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        return await new ApprovalExpirySweep(db, clock, fixture.CreateOutbox(db, clock), NullLogger<ApprovalExpirySweep>.Instance)
            .RunOnceAsync();
    }

    private async Task<Reservation> StoredAsync(Guid reservationId, IClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservationId);
    }

    private async Task<List<Domain.Messaging.OutboxMessage>> MessagesForAsync(Guid reservationId, IClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var prefix = OutboxMessageTypes.PrefixFor("reservation", reservationId);

        return await db.OutboxMessages.AsNoTracking().Where(m => m.IdempotencyKey.StartsWith(prefix)).ToListAsync();
    }

    private async Task<string> VenueNameAsync(TestBranch branch, IClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.Venues.AsNoTracking().Where(v => v.Id == branch.VenueId).Select(v => v.Name).SingleAsync();
    }

    /// <summary>A waiter enrolled at another branch of the same venue.</summary>
    private static async Task<Guid> SiblingBranchWaiterAsync(YallaDbContext db, TestBranch branch)
    {
        var siblingId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
        var unique = Guid.NewGuid().ToString("N")[..7];
        var waiter = new StaffMember(branch.VenueId, "Sibling Waiter", $"+3743{unique}", StaffRole.Waiter, "hash", siblingId);

        db.StaffMembers.Add(waiter);
        await db.SaveChangesAsync();

        return waiter.Id;
    }

    /// <summary>A verified diner with one phone in <paramref name="deviceLocale"/>, and the actor for them.</summary>
    private static async Task<TestActor> DinerWithDeviceAsync(YallaDbContext db, IClock clock, string deviceLocale)
    {
        var unique = Guid.NewGuid().ToString("N")[..9];
        var diner = new DinerUser($"+3749{unique}", "hy");

        diner.ProveNumber(clock.UtcNow);

        db.DinerUsers.Add(diner);
        db.DinerDevices.Add(new DinerDevice(
            diner.Id, $"ExponentPushToken[{unique}]", DevicePlatform.Ios, deviceLocale, clock.UtcNow));

        await db.SaveChangesAsync();

        return TestActor.Diner(diner.Id);
    }

    private static OutboxDispatcher Dispatcher(YallaDbContext db, IClock clock, INotificationChannel channel) =>
        new(
            db,
            clock,
            new OutboxOptions(),
            [
                new ReservationReminderHandler(db, channel, NullLogger<ReservationReminderHandler>.Instance),
                new ReservationLateNudgeHandler(db, channel, NullLogger<ReservationLateNudgeHandler>.Instance),
                new ReservationDecidedHandler(db, channel),
            ],
            NullLogger<OutboxDispatcher>.Instance);
}
