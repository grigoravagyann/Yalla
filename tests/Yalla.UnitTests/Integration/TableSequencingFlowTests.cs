using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The table's evening against a real SQL Server: the booking writer refusing, cutting short and
/// asking under the table's lock, the earlier guest's yes and no, the two hours running out, the floor
/// deciding for a guest without the app, two later guests at once, and what a move or a cancellation
/// does to an open question.
/// </summary>
/// <remarks>
/// <para>
/// The shipped restaurant policy: a 90-minute turn and 5 minutes of clearing, open 10:00-23:00 in
/// <c>Asia/Yerevan</c> (UTC+4). "Now" is noon on Tuesday 20 October 2026 there, and the evening booked is
/// Thursday the 22nd's - another day, so nothing is at short notice unless a test says so.
/// </para>
/// <para>
/// The earlier booking, <c>P</c>, is at 18:00 on the first table throughout.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TableSequencingFlowTests(SqlServerFixture fixture)
{
    /// <summary>12:00 on Tuesday 20 October 2026 in Yerevan.</summary>
    private static readonly DateTime Now = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 20);

    private static readonly DateOnly Thursday = new(2026, 10, 22);

    /// <summary>A Thursday wall-clock time in Yerevan, as UTC.</summary>
    private static DateTime ThursdayAt(int hour, int minute) =>
        new DateTime(2026, 10, 22, hour, minute, 0, DateTimeKind.Utc).AddHours(-4);

    private static DateTime TodayAt(int hour, int minute) =>
        new DateTime(2026, 10, 20, hour, minute, 0, DateTimeKind.Utc).AddHours(-4);

    // ---------------------------------------------------------------- the rules, as the writer applies them

    [SkippableFact]
    public async Task Eighty_nine_minutes_after_a_booking_is_refused_and_ninety_is_asked_for()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var earlier = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(18, 0));

        var refused = await Assert.ThrowsAsync<TableAlreadyBookedException>(
            () => BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 29)));

        Assert.Equal(ReservationRejectionReason.TableJustBooked, refused.Reason);
        Assert.Equal(earlier.Id, refused.ConflictingReservationId);

        var asked = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 30));

        Assert.Equal(ReservationStatus.PendingApproval, asked.Status);
        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, asked.AwaitingApprovalBecause);

        var request = await RequestForLaterAsync(clock, asked.Id);

        Assert.Equal(earlier.Id, request.EarlierReservationId);
        Assert.Equal(ThursdayAt(19, 25), request.FreeByUtc);
        Assert.Equal(TableShareRequestStatus.Pending, request.Status);

        // The guest before booked with no phone to push to, so the floor asks them - with the same two
        // hours the guest would have had in the app, the booking's deadline and the question's alike.
        Assert.False(request.AsksEarlierGuest);
        Assert.False(asked.AwaitsEarlierGuestAnswer);
        Assert.Equal(Now.AddHours(2), asked.ApprovalExpiresAtUtc);
        Assert.Equal(Now.AddHours(2), request.ExpiresAtUtc);
        Assert.Null(request.FallbackTrigger);
    }

    [SkippableFact]
    public async Task One_hundred_seventy_nine_minutes_after_is_asked_for_and_one_hundred_eighty_is_free()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, tableCount: 2);
        await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(18, 0));
        await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(18, 0), branch.TableIds[1]);

        var asked = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(20, 59));
        var free = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(21, 0), branch.TableIds[1]);

        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, asked.AwaitingApprovalBecause);
        Assert.Equal(ReservationStatus.Confirmed, free.Status);
        Assert.Null(free.PromisedFreeByUtc);
    }

    [SkippableFact]
    public async Task Forty_four_minutes_before_the_next_booking_is_refused_and_forty_five_must_free_the_table_by_next_less_five()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var next = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(21, 30));

        var tooClose = await Assert.ThrowsAsync<TableAlreadyBookedException>(
            () => BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(20, 46)));

        Assert.Equal(ReservationRejectionReason.NextBookingTooSoon, tooClose.Reason);
        Assert.Equal(next.Id, tooClose.ConflictingReservationId);

        // 20:45 runs into it: the diner must accept 21:25 - and not sending it, or sending another time,
        // is refused naming the field with the time required.
        var notAccepted = await Assert.ThrowsAsync<FreeByNotAcceptedException>(
            () => BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(20, 45)));

        Assert.Equal(ThursdayAt(21, 25), notAccepted.RequiredFreeByUtc);
        Assert.Equal(new TimeOnly(21, 25), notAccepted.RequiredFreeByLocal);
        Assert.Equal(ThursdayAt(21, 30), notAccepted.NextBookingStartUtc);
        Assert.Null(notAccepted.AcceptedFreeByUtc);

        var mismatched = await Assert.ThrowsAsync<FreeByNotAcceptedException>(
            () => BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(20, 45), acceptsFreeBy: ThursdayAt(21, 20)));

        Assert.Equal(ThursdayAt(21, 25), mismatched.RequiredFreeByUtc);
        Assert.Equal(ThursdayAt(21, 20), mismatched.AcceptedFreeByUtc);

        var booked = await BookAsync(
            clock, TestActor.Diner(), branch, new TimeOnly(20, 45), acceptsFreeBy: ThursdayAt(21, 25));

        Assert.Equal(ReservationStatus.Confirmed, booked.Status);
        Assert.Equal(ThursdayAt(21, 25), booked.PromisedFreeByUtc);
        Assert.Equal(ThursdayAt(21, 25), booked.EndUtc);
        Assert.Equal(new TimeOnly(21, 25), booked.LocalEndTime);
    }

    [SkippableFact]
    public async Task Five_minutes_of_clearing_lets_a_full_sitting_end_exactly_five_before_the_next()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, tableCount: 2);
        await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(21, 30));
        await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(21, 30), branch.TableIds[1]);

        // 19:55 + 90 = 21:25, five minutes before: a full sitting, no promise asked.
        var full = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 55));

        Assert.Equal(ReservationStatus.Confirmed, full.Status);
        Assert.Null(full.PromisedFreeByUtc);
        Assert.Equal(ThursdayAt(21, 25), full.EndUtc);

        // A minute later and the sitting would run into it.
        var cut = await Assert.ThrowsAsync<FreeByNotAcceptedException>(
            () => BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 56), branch.TableIds[1]));

        Assert.Equal(ThursdayAt(21, 25), cut.RequiredFreeByUtc);
    }

    // ---------------------------------------------------------------- the earlier guest in the app

    [SkippableFact]
    public async Task The_earlier_guest_is_pushed_and_says_yes_so_the_later_booking_is_confirmed_and_theirs_cut()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await fixture.ClearOutboxAsync(clock);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock, "en");
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));

        // 19:30: ninety minutes after hers, so she is asked to leave by 19:25 - sooner than her booking's
        // 19:30 end, which her yes cuts.
        var gor = TestActor.Diner();
        var later = await BookAsync(clock, gor, branch, new TimeOnly(19, 30));
        var request = await RequestForLaterAsync(clock, later.Id);

        Assert.True(request.AsksEarlierGuest);
        Assert.Equal(ThursdayAt(19, 25), request.FreeByUtc);

        // The later booking says whose answer it waits on - hers, in the app - on every view of it, the
        // console's list of pending bookings included, so nobody offers the floor's approve and reject.
        Assert.True(later.AwaitsEarlierGuestAnswer);
        Assert.True(Assert.Single(await PendingForBranchAsync(clock, branch)).AwaitsEarlierGuestAnswer);
        Assert.True((await LinkAsync(clock, later.ManageToken!)).AwaitsEarlierGuestAnswer);
        Assert.True(Assert.Single((await MineAsync(clock, gor)).Upcoming).AwaitsEarlierGuestAnswer);

        // Her booking carries the question while it is open, on her own reads only.
        var mine = await MineAsync(clock, anna);
        var card = Assert.Single(mine.Upcoming).IncomingTableRequest;

        Assert.NotNull(card);
        Assert.Equal(request.Id, card.RequestId);
        Assert.Equal(ThursdayAt(19, 30), card.RequestedStartUtc);
        Assert.Equal(ThursdayAt(19, 25), card.FreeByUtc);
        Assert.Equal(Now.AddHours(2), card.ExpiresAtUtc);
        Assert.Equal(new TimeOnly(19, 30), card.RequestedLocalTime);
        Assert.Equal(new TimeOnly(19, 25), card.FreeByLocalTime);

        // The push, in her language, with the request to answer.
        var channel = new RecordingChannel();
        await DispatchAsync(clock, channel);

        var push = Assert.Single(For(channel, "table-request", earlier.Id));
        Assert.Equal(earlier.Id.ToString(), push.Data["reservationId"]);
        Assert.Equal(request.Id.ToString(), push.Data["requestId"]);
        Assert.Equal("table-request", push.CategoryId);
        Assert.Equal("Someone would like your table at 19:30", push.Title);
        Assert.Contains("will you have left by 19:25?", push.Body, StringComparison.Ordinal);

        Assert.True(await FeedHasAsync(clock, anna, DinerNotificationKinds.TableRequest, earlier.Id));

        // Yes.
        var answered = await AnswerAsync(clock, anna, earlier.Id, request.Id, accept: true);

        Assert.Equal(ThursdayAt(19, 25), answered.PromisedFreeByUtc);
        Assert.Equal(ThursdayAt(19, 25), answered.EndUtc);
        Assert.Equal(new TimeOnly(19, 25), answered.LocalEndTime);
        Assert.Null(answered.IncomingTableRequest);

        var confirmed = await StoredAsync(clock, later.Id);
        Assert.Equal(ReservationStatus.Confirmed, confirmed.Status);
        Assert.Null(confirmed.ApprovalExpiresAtUtc);
        Assert.Equal(TableShareRequestStatus.Accepted, (await RequestAsync(clock, request.Id)).Status);

        // The floor's next-booking line says it that afternoon: "must free by 19:25".
        await using (var db = fixture.CreateContext(clock))
        {
            var floor = await fixture.CreateFloorQuery(db, clock).GetFloorStateAsync(branch.BranchId, ThursdayAt(17, 0));
            var line = Assert.Single(floor!.Tables);

            Assert.Equal(earlier.Id, line.NextReservationId);
            Assert.Equal(ThursdayAt(19, 25), line.NextReservationPromisedFreeByUtc);
        }

        // Both are told: the later guest that they are expected, the earlier one what she promised.
        channel.Clear();
        await DispatchAsync(clock, channel);

        Assert.Equal("true", Assert.Single(For(channel, "reservation-decided", later.Id)).Data["approved"]);
        var receipt = Assert.Single(For(channel, "table-request-decided", earlier.Id));
        Assert.Equal(earlier.Id.ToString(), receipt.Data["reservationId"]);
        Assert.Contains("19:25", receipt.Body, StringComparison.Ordinal);
        Assert.True(await FeedHasAsync(clock, anna, DinerNotificationKinds.TableRequestDecided, earlier.Id));

        // The same answer again is answered with the booking; the other answer is too late.
        var again = await AnswerAsync(clock, anna, earlier.Id, request.Id, accept: true);
        Assert.True(again.WasReplay);

        var contrary = await Assert.ThrowsAsync<TableRequestNotPendingException>(
            () => AnswerAsync(clock, anna, earlier.Id, request.Id, accept: false));
        Assert.Equal(TableShareRequestStatus.Accepted, contrary.Status);
    }

    [SkippableFact]
    public async Task The_earlier_guest_says_no_so_the_later_booking_is_declined_for_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await fixture.ClearOutboxAsync(clock);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock, "en");
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var later = await BookAsync(clock, await DinerWithDeviceAsync(clock, "en"), branch, new TimeOnly(19, 45));
        var request = await RequestForLaterAsync(clock, later.Id);

        var answered = await AnswerAsync(clock, anna, earlier.Id, request.Id, accept: false);

        Assert.Null(answered.PromisedFreeByUtc);
        Assert.Equal(ThursdayAt(19, 30), answered.EndUtc);

        var declined = await StoredAsync(clock, later.Id);
        Assert.Equal(ReservationStatus.CancelledByVenue, declined.Status);
        Assert.Equal(DeclineReason.EarlierGuestDeclined, declined.DeclineReason);
        Assert.Equal(TableShareRequestStatus.Declined, (await RequestAsync(clock, request.Id)).Status);

        var channel = new RecordingChannel();
        await DispatchAsync(clock, channel);

        var told = Assert.Single(For(channel, "reservation-decided", later.Id));
        Assert.Equal("false", told.Data["approved"]);
        Assert.Equal("3", told.Data["declinedBecause"]);
        Assert.Contains("Try another time or table", told.Body, StringComparison.Ordinal);

        // And the table is free again at 19:45 for anybody who asks - this guest has answered.
        var again = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45));
        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, again.AwaitingApprovalBecause);
    }

    [SkippableFact]
    public async Task Two_hours_without_an_answer_declines_the_later_booking_and_leaves_the_earlier_one_alone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await fixture.ClearOutboxAsync(clock);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock, "en");
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45));
        var request = await RequestForLaterAsync(clock, later.Id);

        // A minute short, nothing happens to it.
        clock.UtcNow = Now.AddHours(2).AddMinutes(-1);
        await SweepAsync(clock);
        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(clock, later.Id)).Status);

        clock.UtcNow = Now.AddHours(2);
        await SweepAsync(clock);

        var lapsed = await StoredAsync(clock, later.Id);
        Assert.Equal(ReservationStatus.CancelledByVenue, lapsed.Status);
        Assert.Equal(DeclineReason.EarlierGuestNoAnswer, lapsed.DeclineReason);
        Assert.Equal(TableShareRequestStatus.Expired, (await RequestAsync(clock, request.Id)).Status);

        var untouched = await StoredAsync(clock, earlier.Id);
        Assert.Equal(ReservationStatus.Confirmed, untouched.Status);
        Assert.Null(untouched.PromisedFreeByUtc);
        Assert.Equal(ThursdayAt(19, 30), untouched.EndUtc);

        // Her card is gone, and answering now is too late.
        Assert.Null(Assert.Single((await MineAsync(clock, anna)).Upcoming).IncomingTableRequest);
        await Assert.ThrowsAsync<TableRequestNotPendingException>(
            () => AnswerAsync(clock, anna, earlier.Id, request.Id, accept: true));

        var channel = new RecordingChannel();
        await DispatchAsync(clock, channel);

        var told = Assert.Single(For(channel, "reservation-decided", later.Id));
        Assert.Equal("4", told.Data["declinedBecause"]);
        Assert.Equal("true", told.Data["expired"]);
    }

    [SkippableFact]
    public async Task Only_the_earlier_guest_can_answer_and_only_a_question_put_to_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, tableCount: 2);
        var anna = await DinerWithDeviceAsync(clock, "en");
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45));
        var request = await RequestForLaterAsync(clock, later.Id);

        // A stranger guessing both ids is told there is no such booking.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => AnswerAsync(clock, TestActor.Diner(), earlier.Id, request.Id, accept: true));

        // The floor's question to a guest without the app is not hers to answer either.
        var bob = TestActor.Diner();
        var noApp = await BookAsync(clock, bob, branch, new TimeOnly(18, 0), branch.TableIds[1]);
        var floorsLater = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45), branch.TableIds[1]);
        var floorsQuestion = await RequestForLaterAsync(clock, floorsLater.Id);

        Assert.False(floorsQuestion.AsksEarlierGuest);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => AnswerAsync(clock, bob, noApp.Id, floorsQuestion.Id, accept: true));
    }

    // ---------------------------------------------------------------- the floor asks for them

    [SkippableFact]
    public async Task A_guest_who_booked_on_the_web_is_asked_by_the_floor_whose_yes_holds_them_to_the_time()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock, acceptsWebBookings: true);
        var anna = await DinerWithDeviceAsync(clock, "en");
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0), channel: ReservationChannel.Web);
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45));

        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, later.AwaitingApprovalBecause);
        Assert.Equal(Now.AddHours(2), later.ApprovalExpiresAtUtc);
        Assert.False(later.AwaitsEarlierGuestAnswer);
        Assert.False(Assert.Single(await PendingForBranchAsync(clock, branch)).AwaitsEarlierGuestAnswer);
        Assert.False((await LinkAsync(clock, later.ManageToken!)).AwaitsEarlierGuestAnswer);
        Assert.False((await RequestForLaterAsync(clock, later.Id)).AsksEarlierGuest);

        // Nothing on her booking to answer: the floor calls her.
        Assert.Null(Assert.Single((await MineAsync(clock, anna)).Upcoming).IncomingTableRequest);

        var awaiting = await AwaitingAsync(clock, branch);
        var card = Assert.Single(awaiting);

        Assert.Equal(later.Id, card.Reservation.Id);
        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, card.Trigger);
        Assert.Equal(Now.AddHours(2), card.ApprovalExpiresAtUtc);
        Assert.Equal(ThursdayAt(19, 40), card.EarlierGuestFreeByUtc);
        Assert.Equal(earlier.Id, card.PreviousBooking?.ReservationId);
        Assert.NotNull(card.PreviousBooking?.GuestPhone);

        // The waiter approves after calling her: her booking now ends when she promised.
        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId))
                .ApproveAsync(new DecideReservationCommand(later.Id));
        }

        // 19:40 is after her booking's own 19:30 end, so the end stands and the promise is kept beside it.
        var held = await StoredAsync(clock, earlier.Id);
        Assert.Equal(ThursdayAt(19, 40), held.PromisedFreeByUtc);
        Assert.Equal(ThursdayAt(19, 30), held.EndUtc);
        Assert.Equal(ReservationStatus.Confirmed, (await StoredAsync(clock, later.Id)).Status);
        Assert.Equal(TableShareRequestStatus.Accepted, (await RequestForLaterAsync(clock, later.Id)).Status);
    }

    [SkippableFact]
    public async Task A_guest_with_no_phone_to_push_to_is_asked_by_the_floor_for_two_hours_then_declined_for_the_venue_not_answering()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await fixture.ClearOutboxAsync(clock);
        var branch = await BranchAsync(clock);
        var earlier = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(18, 0));
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45));
        var request = await RequestForLaterAsync(clock, later.Id);

        // The floor has the same two hours the guest would have had in the app, counted down on the card.
        Assert.Equal(Now.AddHours(2), Assert.Single(await AwaitingAsync(clock, branch)).ApprovalExpiresAtUtc);

        // A minute short, nothing has run out.
        clock.UtcNow = Now.AddHours(2).AddMinutes(-1);
        await SweepAsync(clock);
        Assert.Equal(ReservationStatus.PendingApproval, (await StoredAsync(clock, later.Id)).Status);

        clock.UtcNow = Now.AddHours(2);
        await SweepAsync(clock);

        // The venue did not answer: declined as any request the venue let run out, the question with it.
        var lapsed = await StoredAsync(clock, later.Id);
        Assert.Equal(ReservationStatus.CancelledByVenue, lapsed.Status);
        Assert.Equal(DeclineReason.NoAnswer, lapsed.DeclineReason);
        Assert.Equal(TableShareRequestStatus.Expired, (await RequestAsync(clock, request.Id)).Status);
        Assert.Empty(await AwaitingAsync(clock, branch));

        var untouched = await StoredAsync(clock, earlier.Id);
        Assert.Equal(ReservationStatus.Confirmed, untouched.Status);
        Assert.Null(untouched.PromisedFreeByUtc);

        var channel = new RecordingChannel();
        await DispatchAsync(clock, channel);

        var told = Assert.Single(For(channel, "reservation-decided", later.Id));
        Assert.Equal("2", told.Data["declinedBecause"]);
        Assert.Equal("true", told.Data["expired"]);
    }

    [SkippableFact]
    public async Task Tonight_the_floor_decides_within_fifteen_minutes_with_the_time_to_ask_the_guest_before()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);

        Reservation earlier;

        await using (var db = fixture.CreateContext(clock))
        {
            earlier = await TestBranchBuilder.AddConfirmedReservationAsync(db, Branch(branch), branch.FirstTableId, TodayAt(18, 0));
        }

        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45), date: Today);

        Assert.Equal(ApprovalTrigger.ShortNotice, later.AwaitingApprovalBecause);
        Assert.Equal(Now.AddMinutes(15), later.ApprovalExpiresAtUtc);

        var request = await RequestForLaterAsync(clock, later.Id);
        Assert.False(request.AsksEarlierGuest);
        Assert.Equal(Now.AddMinutes(15), request.ExpiresAtUtc);

        var card = Assert.Single(await AwaitingAsync(clock, branch));
        Assert.Equal(TodayAt(19, 40), card.EarlierGuestFreeByUtc);

        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId))
                .ApproveAsync(new DecideReservationCommand(later.Id));
        }

        Assert.Equal(TodayAt(19, 40), (await StoredAsync(clock, earlier.Id)).PromisedFreeByUtc);
    }

    // ---------------------------------------------------------------- one question at a time

    [SkippableFact]
    public async Task While_one_later_guest_waits_on_the_earlier_one_a_second_cannot_ask_and_the_database_agrees()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock, "en");
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));

        // The first asks for 20:15; the second for 19:30, which would need Anna gone by 19:25 - and is
        // itself cut short by the first, so it accepts 20:10 to rule that out as the reason.
        var first = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(20, 15));

        var second = await Assert.ThrowsAsync<TableAlreadyBookedException>(
            () => BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 30), acceptsFreeBy: ThursdayAt(20, 10)));

        Assert.Equal(ReservationRejectionReason.EarlierGuestBeingAsked, second.Reason);
        Assert.Equal(earlier.Id, second.ConflictingReservationId);

        // The room screen says the same.
        await using (var db = fixture.CreateContext(clock))
        {
            var availability = await fixture.CreateAvailabilityQuery(db, clock)
                .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, Thursday, new TimeOnly(19, 30)));

            var table = availability!.Tables.Single(t => t.TableId == branch.FirstTableId);
            Assert.False(table.IsAvailable);
            Assert.Equal(ReservationRejectionReason.EarlierGuestBeingAsked, table.UnavailableReason);
        }

        // And a second open question to the same guest cannot be written around the writer either.
        await using (var db = fixture.CreateContext(clock))
        {
            var other = await TestBranchBuilder.AddConfirmedReservationAsync(
                db, Branch(branch), branch.FirstTableId, ThursdayAt(22, 0));

            db.TableShareRequests.Add(new TableShareRequest(
                earlier.Id, other.Id, ThursdayAt(22, 0), ThursdayAt(21, 55), clock.UtcNow, clock.UtcNow.AddHours(1), true));

            var violation = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(DatabaseIndexNames.OpenTableShareRequestPerEarlierBooking, violation.InnerException?.Message);
        }

        // Once Anna answers, the table can be asked for again.
        await AnswerAsync(clock, anna, earlier.Id, (await RequestForLaterAsync(clock, first.Id)).Id, accept: false);

        var asksNow = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 30));
        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, asksNow.AwaitingApprovalBecause);
    }

    [SkippableFact]
    public async Task Two_later_guests_asking_for_the_same_earlier_guest_at_once_one_asks_and_the_other_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock, "en");
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));

        // 19:30 and 20:15 both need Anna's yes, on two connections at the same moment. Whichever takes
        // the table's lock first asks her; the other then finds her being asked (19:30) or the first
        // booking under ninety minutes before it (20:15).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<object> AttemptAsync(TimeOnly time, DateTime? acceptsFreeBy)
        {
            await gate.Task;

            try
            {
                return await BookAsync(clock, TestActor.Diner(), branch, time, acceptsFreeBy: acceptsFreeBy);
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var first = Task.Run(() => AttemptAsync(new TimeOnly(19, 30), ThursdayAt(20, 10)));
        var second = Task.Run(() => AttemptAsync(new TimeOnly(20, 15), null));

        gate.SetResult();

        var outcomes = await Task.WhenAll(first, second);

        var booked = Assert.Single(outcomes.OfType<ReservationView>());
        var refused = Assert.IsType<TableAlreadyBookedException>(Assert.Single(outcomes.OfType<Exception>()));

        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, booked.AwaitingApprovalBecause);
        Assert.Contains(
            refused.Reason,
            new[] { ReservationRejectionReason.EarlierGuestBeingAsked, ReservationRejectionReason.TableJustBooked });

        // One question to Anna, never two.
        await using var db = fixture.CreateContext(clock);
        Assert.Equal(
            1,
            await db.TableShareRequests.CountAsync(
                r => r.EarlierReservationId == earlier.Id && r.Status == TableShareRequestStatus.Pending));
    }

    // ---------------------------------------------------------------- moves and cancellations

    [SkippableFact]
    public async Task A_later_booking_moved_to_a_free_time_withdraws_its_question_and_is_confirmed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock, "en");
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var gor = TestActor.Diner();
        var later = await BookAsync(clock, gor, branch, new TimeOnly(19, 45));
        var request = await RequestForLaterAsync(clock, later.Id);

        var moved = await MoveAsync(clock, gor, later, new TimeOnly(21, 0));

        Assert.Equal(ReservationStatus.Confirmed, moved.Status);
        Assert.Equal(TableShareRequestStatus.Withdrawn, (await RequestAsync(clock, request.Id)).Status);
        Assert.Null(Assert.Single((await MineAsync(clock, anna)).Upcoming).IncomingTableRequest);
        await Assert.ThrowsAsync<TableRequestNotPendingException>(
            () => AnswerAsync(clock, anna, earlier.Id, request.Id, accept: true));
    }

    [SkippableFact]
    public async Task A_later_booking_moved_to_another_asked_for_time_keeps_its_one_question_and_deadline()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock, "en");
        await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var gor = TestActor.Diner();
        var later = await BookAsync(clock, gor, branch, new TimeOnly(19, 45));
        var first = await RequestForLaterAsync(clock, later.Id);

        // Half an hour on, the diner moves to 20:00 - still asking Anna, the same way.
        clock.Advance(TimeSpan.FromMinutes(30));
        var moved = await MoveAsync(clock, gor, later, new TimeOnly(20, 0));

        Assert.Equal(ApprovalTrigger.EarlierGuestConsent, moved.AwaitingApprovalBecause);

        // The same question at the new time: one request, its deadline not restarted.
        var same = await RequestForLaterAsync(clock, later.Id);
        Assert.Equal(first.Id, same.Id);
        Assert.Equal(TableShareRequestStatus.Pending, same.Status);
        Assert.Equal(ThursdayAt(19, 55), same.FreeByUtc);
        Assert.Equal(ThursdayAt(20, 0), same.RequestedStartUtc);
        Assert.Equal(first.ExpiresAtUtc, same.ExpiresAtUtc);
        Assert.Equal(first.ExpiresAtUtc, moved.ApprovalExpiresAtUtc);

        var card = Assert.Single((await MineAsync(clock, anna)).Upcoming).IncomingTableRequest;
        Assert.Equal(same.Id, card?.RequestId);
        Assert.Equal(new TimeOnly(19, 55), card?.FreeByLocalTime);
    }

    [SkippableFact]
    public async Task When_the_earlier_guest_cancels_the_later_booking_waiting_on_them_is_confirmed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await fixture.ClearOutboxAsync(clock);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock, "en");
        var earlier = await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var later = await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(19, 45));
        var request = await RequestForLaterAsync(clock, later.Id);

        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(db, clock, anna).CancelAsync(new CancelReservationCommand(earlier.Id));
        }

        Assert.Equal(ReservationStatus.Confirmed, (await StoredAsync(clock, later.Id)).Status);
        Assert.Equal(TableShareRequestStatus.Withdrawn, (await RequestAsync(clock, request.Id)).Status);

        var channel = new RecordingChannel();
        await DispatchAsync(clock, channel);

        Assert.Equal("true", Assert.Single(For(channel, "reservation-decided", later.Id)).Data["approved"]);

        // The push asking her, still unsent, went with her booking.
        Assert.Empty(For(channel, "table-request", earlier.Id));
    }

    [SkippableFact]
    public async Task When_the_later_guest_cancels_the_question_goes_with_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        var anna = await DinerWithDeviceAsync(clock, "en");
        await BookAsync(clock, anna, branch, new TimeOnly(18, 0));
        var gor = TestActor.Diner();
        var later = await BookAsync(clock, gor, branch, new TimeOnly(19, 45));
        var request = await RequestForLaterAsync(clock, later.Id);

        await using (var db = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(db, clock, gor).CancelAsync(new CancelReservationCommand(later.Id));
        }

        Assert.Equal(TableShareRequestStatus.Withdrawn, (await RequestAsync(clock, request.Id)).Status);
        Assert.Null(Assert.Single((await MineAsync(clock, anna)).Upcoming).IncomingTableRequest);
    }

    [SkippableFact]
    public async Task A_booking_that_promised_a_time_keeps_it_through_a_change_of_party_size()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        var branch = await BranchAsync(clock);
        await BookAsync(clock, TestActor.Diner(), branch, new TimeOnly(21, 30));
        var gor = TestActor.Diner();
        var booked = await BookAsync(clock, gor, branch, new TimeOnly(20, 45), acceptsFreeBy: ThursdayAt(21, 25), partySize: 3);

        // Fewer people, same table and time: the promise stands, and need not be made again.
        var smaller = await MoveAsync(clock, gor, booked, new TimeOnly(20, 45), partySize: 2);

        Assert.Equal(ReservationStatus.Confirmed, smaller.Status);
        Assert.Equal(ThursdayAt(21, 25), smaller.PromisedFreeByUtc);
        Assert.Equal(ThursdayAt(21, 25), smaller.EndUtc);

        // A move to another start must accept its limit, though it is the same time.
        var refused = await Assert.ThrowsAsync<FreeByNotAcceptedException>(
            () => MoveAsync(clock, gor, booked, new TimeOnly(20, 30)));
        Assert.Equal(ThursdayAt(21, 25), refused.RequiredFreeByUtc);
    }

    // ---------------------------------------------------------------- what the screens say

    [SkippableFact]
    public async Task Availability_says_requestable_and_must_free_by_and_the_day_is_listed_every_quarter_hour()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);

        // One table with an 18:00 on it; another with a 21:30.
        var before = await BranchAsync(clock);
        await BookAsync(clock, TestActor.Diner(), before, new TimeOnly(18, 0));

        var after = await BranchAsync(clock);
        await BookAsync(clock, TestActor.Diner(), after, new TimeOnly(21, 30));

        await using var db = fixture.CreateContext(clock);
        var query = fixture.CreateAvailabilityQuery(db, clock);

        async Task<TableAvailability> TableAt(TestBranch branch, int hour, int minute) =>
            (await query.GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, Thursday, new TimeOnly(hour, minute))))!
                .Tables.Single();

        var asked = await TableAt(before, 19, 45);

        Assert.True(asked.IsAvailable);
        Assert.True(asked.IsRequestable);
        Assert.True(asked.RequiresApproval);
        Assert.Null(asked.MustFreeByUtc);

        Assert.Equal(ReservationRejectionReason.TableJustBooked, (await TableAt(before, 19, 15)).UnavailableReason);

        var free = await TableAt(before, 21, 0);
        Assert.True(free.IsAvailable);
        Assert.False(free.IsRequestable);
        Assert.False(free.RequiresApproval);

        var cut = await TableAt(after, 20, 45);

        Assert.True(cut.IsAvailable);
        Assert.False(cut.IsRequestable);
        Assert.Equal(ThursdayAt(21, 25), cut.MustFreeByUtc);
        Assert.Equal(new TimeOnly(21, 25), cut.MustFreeByLocal);
        Assert.Equal(ThursdayAt(21, 25), cut.Window?.AvailableUntilUtc);

        Assert.Equal(ReservationRejectionReason.NextBookingTooSoon, (await TableAt(after, 21, 0)).UnavailableReason);

        // The day, every quarter of an hour from opening, the last time whose sitting ends by 23:00.
        var day = (await query.GetDaySlotsAsync(new DaySlotsRequest(before.BranchId, Thursday, 2)))!;

        Assert.Equal(15, day.StepMinutes);
        Assert.Equal(new[] { "10:00", "10:15", "10:30", "10:45" }, day.Slots.Take(4).Select(s => s.Time));
        Assert.Equal("21:30", day.Slots[^1].Time);

        var slot = day.Slots.ToDictionary(s => s.Time);

        // Before the 18:00: a full sitting at 16:15, a time to leave by from 16:30, nothing after 17:15.
        Assert.Null(slot["16:15"].MustFreeByLocal);
        Assert.Equal("17:55", slot["16:30"].MustFreeByLocal);
        Assert.Equal("17:55", slot["17:15"].MustFreeByLocal);
        Assert.Equal(ReservationRejectionReason.NextBookingTooSoon, slot["17:30"].UnavailableReason);

        // After it: refused for ninety minutes, then asked for, then free from three hours.
        Assert.Equal(ReservationRejectionReason.TableJustBooked, slot["19:15"].UnavailableReason);
        Assert.True(slot["19:30"].IsAvailable);
        Assert.True(slot["19:30"].IsRequestable);
        Assert.True(slot["19:30"].RequiresApproval);
        Assert.Equal(1, slot["19:30"].RequestableTableCount);
        Assert.Equal(0, slot["19:30"].FreeTableCount);
        Assert.True(slot["20:45"].IsRequestable);
        Assert.False(slot["21:00"].IsRequestable);
        Assert.Equal(1, slot["21:00"].FreeTableCount);
        Assert.False(slot["21:00"].RequiresApproval);

        var evening = (await query.GetDaySlotsAsync(new DaySlotsRequest(after.BranchId, Thursday, 2)))!
            .Slots.ToDictionary(s => s.Time);

        Assert.Equal("21:25", evening["20:45"].MustFreeByLocal);
        Assert.Equal(ThursdayAt(21, 25), evening["20:45"].MustFreeByUtc);
        Assert.Equal(ReservationRejectionReason.NextBookingTooSoon, evening["21:00"].UnavailableReason);
        Assert.Equal(ReservationRejectionReason.TableJustBooked, evening["21:30"].UnavailableReason);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<TestBranch> BranchAsync(IClock clock, int tableCount = 1, bool acceptsWebBookings = false)
    {
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: tableCount);

        if (acceptsWebBookings)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.Branches SET AcceptsWebBookings = 1 WHERE Id = {branch.BranchId}");
        }

        return branch;
    }

    private static TestBranch Branch(TestBranch branch) => branch;

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
                GuestName: "Ani Sequencing",
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
        DateTime? acceptsFreeBy = null)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(db, clock, diner).RescheduleAsync(
            new RescheduleReservationCommand(
                booking.Id, booking.TableId!.Value, booking.LocalDate, time, partySize, Guid.CreateVersion7(), acceptsFreeBy));
    }

    private async Task<ReservationView> AnswerAsync(IClock clock, TestActor diner, Guid reservationId, Guid requestId, bool accept)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(db, clock, diner).AnswerTableRequestAsync(
            new AnswerTableRequestCommand(reservationId, requestId, accept, Guid.CreateVersion7()));
    }

    private async Task<MyReservations> MineAsync(IClock clock, TestActor diner)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(db, clock, diner).GetMineAsync();
    }

    private async Task<IReadOnlyList<AwaitingApproval>> AwaitingAsync(IClock clock, TestBranch branch)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(db, clock, TestActor.Waiter(branch.WaiterId))
            .ListAwaitingApprovalAsync(branch.BranchId);
    }

    /// <summary>The console's list of the branch's pending bookings, as a manager reads it.</summary>
    private async Task<IReadOnlyList<ReservationView>> PendingForBranchAsync(IClock clock, TestBranch branch)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(db, clock, TestActor.Manager(branch.ManagerId))
            .ListForBranchAsync(branch.BranchId, ReservationStatus.PendingApproval);
    }

    /// <summary>The booking as its manage link shows it - what the web confirmation page reads.</summary>
    private async Task<PublicBookingView> LinkAsync(IClock clock, string manageToken)
    {
        await using var db = fixture.CreateContext(clock);

        return await new PublicBookingService(db, clock, fixture.CreateReservationService(db, clock, TestActor.Diner()))
            .GetAsync(manageToken);
    }

    private async Task<int> SweepAsync(IClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        return await fixture.CreateApprovalExpirySweep(db, clock).RunOnceAsync();
    }

    private async Task<Reservation> StoredAsync(IClock clock, Guid reservationId)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservationId);
    }

    private async Task<TableShareRequest> RequestForLaterAsync(IClock clock, Guid laterReservationId)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.TableShareRequests
            .AsNoTracking()
            .Where(r => r.LaterReservationId == laterReservationId)

            // The open one when there is one - two asked at the same instant tie on the time, and SQL
            // Server does not order version-7 ids by when they were made.
            .OrderBy(r => r.Status == TableShareRequestStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.CreatedAtUtc)
            .FirstAsync();
    }

    private async Task<TableShareRequest> RequestAsync(IClock clock, Guid requestId)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.TableShareRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
    }

    private async Task<bool> FeedHasAsync(IClock clock, TestActor diner, string kind, Guid reservationId)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.DinerNotifications.AnyAsync(
            n => n.DinerUserId == diner.DinerUserId && n.Kind == kind && n.ReservationId == reservationId);
    }

    /// <summary>A verified diner with one phone in <paramref name="locale"/>, and the actor for them.</summary>
    private async Task<TestActor> DinerWithDeviceAsync(IClock clock, string locale)
    {
        await using var db = fixture.CreateContext(clock);
        var unique = Guid.NewGuid().ToString("N")[..9];
        var diner = new DinerUser($"+3749{unique}", "hy");

        diner.ProveNumber(clock.UtcNow);

        db.DinerUsers.Add(diner);
        db.DinerDevices.Add(new DinerDevice(diner.Id, $"ExponentPushToken[{unique}]", DevicePlatform.Ios, locale, clock.UtcNow));

        await db.SaveChangesAsync();

        return TestActor.Diner(diner.Id);
    }

    /// <summary>
    /// What was pushed of one kind about one booking. The dispatcher sends whatever is due in the shared
    /// table - and a sweep at this test's clock declines other tests' lapsed requests too - so every
    /// assertion names its booking.
    /// </summary>
    private static IReadOnlyList<NotificationMessage> For(RecordingChannel channel, string kind, Guid reservationId) =>
        [.. channel.OfKind(kind).Where(m => m.Data.TryGetValue("reservationId", out var id) && id == reservationId.ToString())];

    private async Task DispatchAsync(IClock clock, INotificationChannel channel)
    {
        await using var db = fixture.CreateContext(clock);

        await new OutboxDispatcher(
                db,
                clock,
                new OutboxOptions(),
                [
                    new ReservationDecidedHandler(db, channel),
                    new TableRequestHandler(db, clock, channel, NullLogger<TableRequestHandler>.Instance),
                    new TableRequestDecidedHandler(db, channel, NullLogger<TableRequestDecidedHandler>.Instance),
                    new ReservationReminderHandler(db, channel, NullLogger<ReservationReminderHandler>.Instance),
                    new ReservationLateNudgeHandler(db, channel, NullLogger<ReservationLateNudgeHandler>.Instance),
                ],
                NullLogger<OutboxDispatcher>.Instance)
            .RunOnceAsync();
    }
}
