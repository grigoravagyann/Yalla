using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yalla.Api.Errors;
using Yalla.Application.BranchSettings;
using Yalla.Application.Messaging;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Moving a booking with nothing but its manage link, against a real SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// The link's move is the app's move with another proof: the token instead of the diner's session. So
/// most tests here run one scenario <b>both ways</b> - the diner's <c>RescheduleAsync</c> on one booking,
/// <c>RescheduleByManageTokenAsync</c> on an identical one - and require the same answer and the same
/// side effects: the status and why, the deadline, the reminders and the feed. A rule the link skipped,
/// or a message it forgot to replace, shows up as a difference.
/// </para>
/// <para>
/// The caller on the link is nobody: <see cref="ActorType.System"/> with no ids, which is what an
/// anonymous request's actor is. The branch is in <c>Asia/Yerevan</c> on the shipped restaurant policy
/// - a 90-minute turn, approval above 8 guests, a 14-day window - open 10:00-23:00 every day.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ManageLinkRescheduleTests(SqlServerFixture fixture)
{
    /// <summary>12:00 on Thursday 10 September 2026 in Yerevan.</summary>
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 9, 10);

    private static readonly DateOnly BookingDate = new(2026, 9, 13);

    private static readonly TimeOnly SixPm = new(18, 0);

    private static readonly TimeOnly EightPm = new(20, 0);

    /// <summary>What an anonymous request's actor is: nobody.</summary>
    private static TestActor Anonymous => new(ActorType.System, null, null);

    // ---------------------------------------------------------------- the move, both ways

    [SkippableFact]
    public async Task The_link_moves_a_booking_exactly_as_the_app_does_and_keeps_its_code_and_link()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var app = await ArrangeAsync(db, clock);
        var link = await ArrangeAsync(db, clock);

        var before = await StoredAsync(link.Booked.Id, clock);

        var byApp = await app.Diner.RescheduleAsync(
            new RescheduleReservationCommand(app.Booked.Id, app.Branch.TableIds[1], BookingDate, EightPm, 3, Guid.CreateVersion7()));

        var byLink = await LinkService(db, clock).RescheduleByManageTokenAsync(
            link.Token, new RescheduleByManageTokenCommand(link.Branch.TableIds[1], BookingDate, EightPm, 3, Guid.CreateVersion7()));

        // The same booking, at the new slot, decided the same way.
        Assert.Equal(link.Booked.Id, byLink.Id);
        Assert.Equal(link.Booked.Code, byLink.Code);
        Assert.Equal(link.Branch.TableIds[1], byLink.TableId);
        Assert.Equal(EightPm, byLink.LocalStartTime);
        Assert.Equal(3, byLink.PartySize);
        Assert.False(byLink.WasReplay);
        AssertSameDecision(byApp, byLink);

        // The link it was found by keeps working, and nothing hands its token out again.
        Assert.Null(byLink.ManageToken);
        var after = await StoredAsync(link.Booked.Id, clock);
        Assert.Equal(before.ManageTokenHash, after.ManageTokenHash);
        Assert.Equal(before.ClientCommandId, after.ClientCommandId);
        Assert.Equal(before.DinerUserId, after.DinerUserId);

        // The same side effects: the reminder, the nudge and "are you on your way?" replaced with ones keyed
        // by the new start, and the feed entries for them.
        Assert.Equal(await ShapeOfOutboxAsync(app.Booked.Id, clock), await ShapeOfOutboxAsync(link.Booked.Id, clock));
        Assert.Equal(await ShapeOfFeedAsync(app.Booked.Id, clock), await ShapeOfFeedAsync(link.Booked.Id, clock));
        Assert.Equal(
            new[] { "late-nudge:202609131600", "on-your-way:202609131600:30", "reminder:202609131600" },
            await ShapeOfOutboxAsync(link.Booked.Id, clock));

        // And one thing more on the link only: the account holder is told it was changed from there.
        Assert.Equal("reservation.changed-by-link", Assert.Single(await LinkNoticesAsync(link.Booked.Id, clock)).Type);
        Assert.Empty(await LinkNoticesAsync(app.Booked.Id, clock));

        // Recorded as the booking's diner's change, as the app's is.
        await using var verify = fixture.CreateContext(clock);
        var recorded = await verify.ProcessedCommands.AsNoTracking()
            .Where(c => c.CommandType == ReservationService.RescheduleCommandType && c.ResponseJson.Contains(link.Booked.Id.ToString()))
            .SingleAsync();

        Assert.Equal(link.DinerUserId, recorded.ActorId);
        Assert.Equal(ActorType.Diner, recorded.ActorType);
    }

    [SkippableFact]
    public async Task A_confirmed_party_that_keeps_its_table_and_start_and_does_not_grow_stays_confirmed_tonight()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // Booked two days ago, for tonight - confirmed then, because it was not short notice yet.
        var clock = new TestClock(Now.AddDays(-2));
        await using var db = fixture.CreateContext(clock);

        var app = await ArrangeAsync(db, clock, date: Today, time: EightPm, partySize: 3);
        var link = await ArrangeAsync(db, clock, date: Today, time: EightPm, partySize: 3);
        Assert.Equal(ReservationStatus.Confirmed, link.Booked.Status);

        clock.UtcNow = Now;

        // One fewer, same table, same time: nothing the venue said yes to got harder to keep.
        var appFewer = await app.Diner.RescheduleAsync(Move(app, app.Branch.FirstTableId, Today, EightPm, 2));
        var linkFewer = await LinkService(db, clock).RescheduleByManageTokenAsync(
            link.Token, LinkMove(link.Branch.FirstTableId, Today, EightPm, 2));

        Assert.Equal(ReservationStatus.Confirmed, linkFewer.Status);
        Assert.Null(linkFewer.ApprovalExpiresAtUtc);
        Assert.Equal(link.Booked.ConfirmedAtUtc, linkFewer.ConfirmedAtUtc);
        AssertSameDecision(appFewer, linkFewer);

        // The same party at the same table, an hour later tonight: a new question for the floor.
        clock.Advance(TimeSpan.FromMinutes(5));
        var appLater = await app.Diner.RescheduleAsync(Move(app, app.Branch.FirstTableId, Today, new TimeOnly(21, 0), 2));
        var linkLater = await LinkService(db, clock).RescheduleByManageTokenAsync(
            link.Token, LinkMove(link.Branch.FirstTableId, Today, new TimeOnly(21, 0), 2));

        Assert.Equal(ReservationStatus.PendingApproval, linkLater.Status);
        Assert.Equal(ApprovalTrigger.ShortNotice, linkLater.AwaitingApprovalBecause);
        Assert.Equal(Now.AddMinutes(5 + 15), linkLater.ApprovalExpiresAtUtc);
        AssertSameDecision(appLater, linkLater);

        // The floor is told the same way for both: a waiting booking on its list, and nothing sent yet.
        Assert.Equal(await ShapeOfOutboxAsync(app.Booked.Id, clock), await ShapeOfOutboxAsync(link.Booked.Id, clock));

        // And the floor's no puts it back where it was: confirmed at eight, for two.
        var waiter = fixture.CreateReservationService(db, clock, TestActor.Waiter(link.Branch.WaiterId));
        await waiter.RejectAsync(new DecideReservationCommand(link.Booked.Id));

        var back = await PublicView(db, clock).GetAsync(link.Token);
        Assert.Equal(ReservationStatus.Confirmed, back.Status);
        Assert.Equal(EightPm, back.LocalStartTime);
        Assert.Equal(2, back.PartySize);
    }

    [SkippableFact]
    public async Task Growing_past_the_threshold_or_a_strict_branch_or_a_no_show_history_goes_back_to_the_venue()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        // Nine is over the threshold of eight.
        var app = await ArrangeAsync(db, clock, tenSeater: true);
        var link = await ArrangeAsync(db, clock, tenSeater: true);

        var appGrown = await app.Diner.RescheduleAsync(Move(app, app.TenSeater!.Value, BookingDate, SixPm, 9));
        var linkGrown = await LinkService(db, clock).RescheduleByManageTokenAsync(
            link.Token, LinkMove(link.TenSeater!.Value, BookingDate, SixPm, 9));

        Assert.Equal(ReservationStatus.PendingApproval, linkGrown.Status);
        Assert.Equal(ApprovalTrigger.LargeParty, linkGrown.AwaitingApprovalBecause);
        Assert.Null(linkGrown.ConfirmedAtUtc);
        AssertSameDecision(appGrown, linkGrown);

        // Approved; then moved to another time without growing - the venue said yes to nine people.
        await fixture.CreateReservationService(db, clock, TestActor.Manager(link.Branch.ManagerId))
            .ApproveAsync(new DecideReservationCommand(link.Booked.Id));

        var linkMovedLarge = await LinkService(db, clock).RescheduleByManageTokenAsync(
            link.Token, LinkMove(link.TenSeater.Value, BookingDate, EightPm, 9));

        Assert.Equal(ReservationStatus.Confirmed, linkMovedLarge.Status);

        // A branch that approves every booking sees every move, confirmed or not.
        var strict = await ArrangeAsync(db, clock, policy: ApprovesEveryBooking());
        await fixture.CreateReservationService(db, clock, TestActor.Manager(strict.Branch.ManagerId))
            .ApproveAsync(new DecideReservationCommand(strict.Booked.Id));

        var atStrict = await LinkService(db, clock).RescheduleByManageTokenAsync(
            strict.Token, LinkMove(strict.Branch.FirstTableId, BookingDate, EightPm, 2));

        Assert.Equal(ReservationStatus.PendingApproval, atStrict.Status);
        Assert.Equal(ApprovalTrigger.BranchApprovesEveryBooking, atStrict.AwaitingApprovalBecause);

        // And a diner whose no-shows cost them instant confirmation loses it on the link too: the
        // history counted is the booking's account's, not the anonymous caller's.
        var missed = await ArrangeAsync(db, clock);
        await AddNoShowsAsync(missed, count: 4, clock);

        var withHistory = await LinkService(db, clock).RescheduleByManageTokenAsync(
            missed.Token, LinkMove(missed.Branch.TableIds[1], BookingDate, EightPm, 2));

        Assert.Equal(ReservationStatus.PendingApproval, withHistory.Status);
        Assert.Equal(ApprovalTrigger.NoShowHistory, withHistory.AwaitingApprovalBecause);
    }

    // ---------------------------------------------------------------- every refusal the app has

    [SkippableFact]
    public async Task The_link_is_refused_by_every_rule_the_app_is()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var link = await ArrangeAsync(db, clock);
        var unpinned = await TestBranchBuilder.AddTableAsync(db, link.Branch, "U1", seats: 4, pinned: false);
        var elsewhere = await TestBranchBuilder.CreateAsync(db);
        var service = LinkService(db, clock);

        await Assert.ThrowsAsync<PartyExceedsTableCapacityException>(
            () => service.RescheduleByManageTokenAsync(link.Token, LinkMove(link.Branch.FirstTableId, BookingDate, SixPm, 6)));

        await Assert.ThrowsAsync<OutsideOpeningHoursException>(
            () => service.RescheduleByManageTokenAsync(link.Token, LinkMove(link.Branch.FirstTableId, BookingDate, new TimeOnly(22, 0), 2)));

        await Assert.ThrowsAsync<LeadTimeTooShortException>(
            () => service.RescheduleByManageTokenAsync(link.Token, LinkMove(link.Branch.FirstTableId, Today, new TimeOnly(12, 5), 2)));

        await Assert.ThrowsAsync<OutsideBookingWindowException>(
            () => service.RescheduleByManageTokenAsync(link.Token, LinkMove(link.Branch.FirstTableId, new DateOnly(2026, 9, 30), SixPm, 2)));

        await Assert.ThrowsAsync<TableNotBookableException>(
            () => service.RescheduleByManageTokenAsync(link.Token, LinkMove(unpinned.Id, BookingDate, SixPm, 2)));

        // A move changes the slot, never the venue - and a link cannot point the booking at another one.
        var foreign = await Assert.ThrowsAsync<ArgumentException>(
            () => service.RescheduleByManageTokenAsync(link.Token, LinkMove(elsewhere.FirstTableId, BookingDate, SixPm, 2)));
        Assert.Equal(StatusCodes.Status400BadRequest, ApiExceptionMapper.Map(foreign).Status);

        // A special day: the 14th shut for a private event.
        var settings = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(link.Branch.ManagerId));
        var loaded = await settings.GetSpecialOpeningDaysAsync(link.Branch.BranchId);

        await settings.ReplaceSpecialOpeningDaysAsync(
            link.Branch.BranchId,
            new ReplaceSpecialOpeningDaysCommand(
                [new SpecialOpeningDayInput(new DateOnly(2026, 9, 14), "Private event", [])], loaded.Version));

        await Assert.ThrowsAsync<OutsideOpeningHoursException>(
            () => service.RescheduleByManageTokenAsync(link.Token, LinkMove(link.Branch.FirstTableId, new DateOnly(2026, 9, 14), SixPm, 2)));

        // Somebody else's booking in the way, as for the app: the fresh floor leaves this one out.
        var theirs = await fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(Booking(link.Branch, link.Branch.TableIds[1], BookingDate, EightPm));

        var taken = await Assert.ThrowsAsync<TableAlreadyBookedException>(
            () => service.RescheduleByManageTokenAsync(link.Token, LinkMove(link.Branch.TableIds[1], BookingDate, EightPm, 2)));

        Assert.Equal(theirs.Id, taken.ConflictingReservationId);
        Assert.True(taken.Availability!.Tables.Single(t => t.TableId == link.Branch.FirstTableId).IsAvailable);

        // None of it moved anything.
        var stored = await StoredAsync(link.Booked.Id, clock);
        Assert.Equal(link.Booked.StartUtc, stored.StartUtc);
        Assert.Equal(link.Branch.FirstTableId, stored.DiningTableId);
        Assert.Equal(2, stored.PartySize);
    }

    [SkippableFact]
    public async Task A_booking_that_is_cancelled_declined_started_or_past_its_deadline_is_not_changeable_from_the_link()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var service = LinkService(db, clock);

        // Cancelled from the link itself.
        var cancelled = await ArrangeAsync(db, clock);
        await service.CancelByManageTokenAsync(cancelled.Token);

        var afterCancel = await Assert.ThrowsAsync<ReservationNotChangeableException>(
            () => service.RescheduleByManageTokenAsync(cancelled.Token, LinkMove(cancelled.Branch.FirstTableId, BookingDate, EightPm, 2)));

        var mapped = ApiExceptionMapper.Map(afterCancel);
        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal("reservation-not-changeable", mapped.Code);
        Assert.Equal((int)ReservationStatus.CancelledByDiner, mapped.Context!["status"]);

        // Declined by the floor: tonight at eight waits for them, and they say no.
        var declined = await ArrangeAsync(db, clock, date: Today, time: EightPm);
        Assert.Equal(ReservationStatus.PendingApproval, declined.Booked.Status);
        await fixture.CreateReservationService(db, clock, TestActor.Waiter(declined.Branch.WaiterId))
            .RejectAsync(new DecideReservationCommand(declined.Booked.Id));

        var afterDecline = await Assert.ThrowsAsync<ReservationNotChangeableException>(
            () => service.RescheduleByManageTokenAsync(declined.Token, LinkMove(declined.Branch.FirstTableId, BookingDate, EightPm, 2)));
        Assert.Equal(ReservationStatus.CancelledByVenue, afterDecline.Status);

        // Waiting on the floor past its deadline: the sweep's to decline, not the link's to restart.
        var waiting = await ArrangeAsync(db, clock, date: Today, time: EightPm);
        clock.UtcNow = waiting.Booked.ApprovalExpiresAtUtc!.Value;

        var expired = await Assert.ThrowsAsync<ReservationApprovalExpiredException>(
            () => service.RescheduleByManageTokenAsync(waiting.Token, LinkMove(waiting.Branch.FirstTableId, BookingDate, EightPm, 2)));
        Assert.Equal(ErrorCodes.ReservationApprovalExpired, ApiExceptionMapper.Map(expired).Code);

        // Started: a confirmed booking whose start has passed is the venue's to run.
        clock.UtcNow = Now;
        var started = await ArrangeAsync(db, clock);
        clock.UtcNow = started.Booked.StartUtc.AddMinutes(1);

        var afterStart = await Assert.ThrowsAsync<ReservationNotChangeableException>(
            () => service.RescheduleByManageTokenAsync(started.Token, LinkMove(started.Branch.FirstTableId, BookingDate, new TimeOnly(21, 0), 2)));
        Assert.Equal(ReservationStatus.Confirmed, afterStart.Status);

        // Each one left exactly where it was.
        Assert.Equal(cancelled.Booked.StartUtc, (await StoredAsync(cancelled.Booked.Id, clock)).StartUtc);
        Assert.Equal(waiting.Booked.StartUtc, (await StoredAsync(waiting.Booked.Id, clock)).StartUtc);
        Assert.Equal(started.Booked.StartUtc, (await StoredAsync(started.Booked.Id, clock)).StartUtc);
    }

    // ---------------------------------------------------------------- what the link may not learn or reach

    [SkippableFact]
    public async Task An_unknown_expired_or_blank_link_is_refused_as_the_read_refuses_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var link = await ArrangeAsync(db, clock);
        var service = LinkService(db, clock);
        var move = LinkMove(link.Branch.FirstTableId, BookingDate, EightPm, 2);

        foreach (var token in new[] { "not-a-token", string.Empty, "   " })
        {
            var refused = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RescheduleByManageTokenAsync(token, move));
            Assert.Equal(ManageBookingFailure.Message, refused.Message);
        }

        // Past the end of the booking plus its grace the link stops working - for the move as for the read.
        clock.UtcNow = link.Booked.EndUtc.AddDays(Reservation.ManageTokenGraceDays).AddMinutes(1);

        var expired = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RescheduleByManageTokenAsync(link.Token, move));
        Assert.Equal(ManageBookingFailure.Message, expired.Message);
    }

    [SkippableFact]
    public async Task A_link_moves_only_its_own_booking_and_its_command_ids_are_the_diners()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var service = LinkService(db, clock);

        // Two bookings of one diner at one branch, and one of a stranger's.
        var mine = await ArrangeAsync(db, clock);

        // A pair from before one number held one booking (2026-10-20): the only way one diner holds two.
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, mine.Booked.Id);
        var second = await mine.Diner.CreateAsync(Booking(mine.Branch, mine.Branch.TableIds[2], BookingDate.AddDays(1), SixPm));
        var stranger = await ArrangeAsync(db, clock);

        var change = LinkMove(mine.Branch.TableIds[1], BookingDate, EightPm, 2);
        var moved = await service.RescheduleByManageTokenAsync(mine.Token, change);
        var keys = await ShapeOfOutboxAsync(mine.Booked.Id, clock);

        // The retry is the same change: answered with the booking as it stands, nothing moved twice.
        var replay = await LinkService(db, clock).RescheduleByManageTokenAsync(mine.Token, change);
        Assert.True(replay.WasReplay);
        Assert.Equal(moved.StartUtc, replay.StartUtc);
        Assert.Equal(keys, await ShapeOfOutboxAsync(mine.Booked.Id, clock));

        // And so is the same change from the app, which shares the record: one change, two ways in.
        var fromApp = await mine.Diner.RescheduleAsync(
            new RescheduleReservationCommand(mine.Booked.Id, change.TableId, change.LocalDate, change.LocalTime, change.PartySize, change.ClientCommandId));
        Assert.True(fromApp.WasReplay);

        // The same id naming a different change is not a retry.
        foreach (var different in new[]
                 {
                     change with { LocalTime = new TimeOnly(20, 30) },
                     change with { LocalDate = BookingDate.AddDays(2) },
                     change with { TableId = mine.Branch.FirstTableId },
                     change with { PartySize = 3 },
                 })
        {
            var reused = await Assert.ThrowsAsync<ClientCommandIdAlreadyUsedException>(
                () => service.RescheduleByManageTokenAsync(mine.Token, different));
            Assert.Equal("client-command-id-in-use", ApiExceptionMapper.Map(reused).Code);
        }

        // Nor the same id on the stranger's link: refused, and they learn nothing of this booking.
        var collision = await Assert.ThrowsAsync<ClientCommandIdAlreadyUsedException>(
            () => service.RescheduleByManageTokenAsync(stranger.Token, change with { TableId = stranger.Branch.TableIds[1] }));
        Assert.DoesNotContain(mine.Booked.Code, collision.Message, StringComparison.Ordinal);

        // Nor an id the stranger's diner already spent on their own booking, replayed through this link.
        var theirChange = Guid.CreateVersion7();
        await stranger.Diner.RescheduleAsync(
            new RescheduleReservationCommand(stranger.Booked.Id, stranger.Branch.TableIds[1], BookingDate, EightPm, 2, theirChange));

        await Assert.ThrowsAsync<ClientCommandIdAlreadyUsedException>(
            () => service.RescheduleByManageTokenAsync(mine.Token, LinkMove(mine.Branch.TableIds[1], BookingDate, EightPm, 2) with { ClientCommandId = theirChange }));

        // The diner's other booking at the branch, and the stranger's beyond what they did themselves,
        // are exactly where they were: a link reaches one booking.
        var untouchedSecond = await StoredAsync(second.Id, clock);
        Assert.Equal(second.StartUtc, untouchedSecond.StartUtc);
        Assert.Equal(mine.Branch.TableIds[2], untouchedSecond.DiningTableId);

        var stillMine = await StoredAsync(mine.Booked.Id, clock);
        Assert.Equal(moved.StartUtc, stillMine.StartUtc);
        Assert.Equal(mine.Branch.TableIds[1], stillMine.DiningTableId);
    }

    [SkippableFact]
    public async Task Another_of_the_diners_bookings_in_the_way_is_refused_without_naming_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var mine = await ArrangeAsync(db, clock);

        // A pair from before one number held one booking (2026-10-20): the only way one diner holds two.
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(db, mine.Booked.Id);
        var other = await mine.Diner.CreateAsync(Booking(mine.Branch, mine.Branch.TableIds[2], BookingDate, new TimeOnly(20, 30)));

        // The app names the other booking - it is the diner's own, and they are signed in.
        var inApp = await Assert.ThrowsAsync<ReservationOverlapsOwnException>(
            () => mine.Diner.RescheduleAsync(Move(mine, mine.Branch.TableIds[1], BookingDate, EightPm, 2)));
        Assert.Equal(other.Id, inApp.ReservationId);

        // The link refuses it the same way and names nothing: no code, no time, no table.
        var onLink = await Assert.ThrowsAsync<ManageLinkOverlapsOwnException>(
            () => LinkService(db, clock).RescheduleByManageTokenAsync(mine.Token, LinkMove(mine.Branch.TableIds[1], BookingDate, EightPm, 2)));

        var mapped = ApiExceptionMapper.Map(onLink);
        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal(ErrorCodes.ReservationOverlapsOwn, mapped.Code);
        Assert.Null(mapped.Context);
        Assert.DoesNotContain(other.Code, onLink.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("20:30", onLink.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("table 3", onLink.Message, StringComparison.OrdinalIgnoreCase);

        // And it moved nothing.
        Assert.Equal(mine.Booked.StartUtc, (await StoredAsync(mine.Booked.Id, clock)).StartUtc);
    }

    [SkippableFact]
    public async Task The_phone_gate_is_the_bookings_account_and_a_deleted_account_cannot_move_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var service = LinkService(db, clock);

        // The account deactivated by the platform - the realistic case: the moved booking's reminder and
        // no-show record would land on an account that is not live, so the link is refused as the app
        // would be, in words a visitor with no account can act on.
        var deactivated = await ArrangeAsync(db, clock);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.DinerUsers SET IsActive = 0 WHERE Id = {deactivated.DinerUserId}");

        AssertCannotChangeOnline(await Assert.ThrowsAnyAsync<Exception>(
            () => service.RescheduleByManageTokenAsync(deactivated.Token, LinkMove(deactivated.Branch.FirstTableId, BookingDate, EightPm, 2))));

        await Assert.ThrowsAsync<PhoneNotVerifiedException>(
            () => deactivated.Diner.RescheduleAsync(Move(deactivated, deactivated.Branch.FirstTableId, BookingDate, EightPm, 2)));

        // A number never proved - unreachable for a booking, which needed a proved one, but the gate's
        // own rule - is refused the same way.
        var unproved = await ArrangeAsync(db, clock);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.DinerUsers SET PhoneVerifiedAtUtc = NULL WHERE Id = {unproved.DinerUserId}");

        AssertCannotChangeOnline(await Assert.ThrowsAnyAsync<Exception>(
            () => service.RescheduleByManageTokenAsync(unproved.Token, LinkMove(unproved.Branch.FirstTableId, BookingDate, EightPm, 2))));

        // The account deleted: the booking stays the venue's with the link to the person cut, so there
        // is no account for it to land on - refused the same way.
        var orphaned = await ArrangeAsync(db, clock);
        await db.Reservations
            .Where(r => r.Id == orphaned.Booked.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.DinerUserId, (Guid?)null));

        // A fresh context, as a fresh request is: the one above still tracks the row as it was.
        await using var request = fixture.CreateContext(clock);

        AssertCannotChangeOnline(await Assert.ThrowsAnyAsync<Exception>(
            () => LinkService(request, clock).RescheduleByManageTokenAsync(orphaned.Token, LinkMove(orphaned.Branch.FirstTableId, BookingDate, EightPm, 2))));

        // None moved; all can still be cancelled from the link, which is never gated.
        Assert.Equal(deactivated.Booked.StartUtc, (await StoredAsync(deactivated.Booked.Id, clock)).StartUtc);
        Assert.Equal(unproved.Booked.StartUtc, (await StoredAsync(unproved.Booked.Id, clock)).StartUtc);
        Assert.Equal(orphaned.Booked.StartUtc, (await StoredAsync(orphaned.Booked.Id, clock)).StartUtc);

        Assert.Equal(ReservationStatus.CancelledByDiner, (await LinkService(request, clock).CancelByManageTokenAsync(orphaned.Token)).Status);
        Assert.Equal(ReservationStatus.CancelledByDiner, (await LinkService(request, clock).CancelByManageTokenAsync(deactivated.Token)).Status);
    }

    /// <summary>
    /// The account deactivated <b>between</b> the phone gate and the move's lock - the account's own lock
    /// finds it not live. On the app that is the diner's session ending (401); on an anonymous link there
    /// is no session, so it is the same 403 the gate gives, not a "sign in again".
    /// </summary>
    [SkippableFact]
    public async Task An_account_that_stops_being_live_mid_move_is_the_links_403_not_a_session_401()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        Arranged link;
        await using (var setup = fixture.CreateContext(clock))
        {
            link = await ArrangeAsync(setup, clock);
        }

        // Past the gate, at the moment the move takes the account's lock inside the table's.
        var race = new BeforeFirstCommandMatching(
            sql => sql.Contains("DinerUsers", StringComparison.Ordinal) && sql.Contains("UPDLOCK", StringComparison.Ordinal),
            async () =>
            {
                await using var platform = fixture.CreateContext(clock);
                await platform.Database.ExecuteSqlAsync(
                    $"UPDATE dbo.DinerUsers SET IsActive = 0 WHERE Id = {link.DinerUserId}");
            });

        await using var request = fixture.CreateContext(clock, race);

        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => LinkService(request, clock).RescheduleByManageTokenAsync(link.Token, LinkMove(link.Branch.TableIds[1], BookingDate, EightPm, 2)));

        Assert.True(race.Fired, "The deactivation never ran mid-move, so this proves nothing.");
        AssertCannotChangeOnline(refused);

        var stored = await StoredAsync(link.Booked.Id, clock);
        Assert.Equal(link.Booked.StartUtc, stored.StartUtc);
        Assert.Equal(link.Branch.FirstTableId, stored.DiningTableId);
    }

    // ---------------------------------------------------------------- telling the account holder

    /// <summary>
    /// Whoever holds the link - a friend in the group chat - may move the booking; the account holder is
    /// told, in the same save: a feed entry and a push. The app's own move, made by the account holder,
    /// tells nobody anything new.
    /// </summary>
    [SkippableFact]
    public async Task A_move_from_the_link_tells_the_account_holder_and_the_apps_own_move_does_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var app = await ArrangeAsync(db, clock);
        var link = await ArrangeAsync(db, clock);

        await app.Diner.RescheduleAsync(Move(app, app.Branch.TableIds[1], BookingDate, EightPm, 3));
        await LinkService(db, clock).RescheduleByManageTokenAsync(link.Token, LinkMove(link.Branch.TableIds[1], BookingDate, EightPm, 3));

        Assert.Empty(await LinkNoticesAsync(app.Booked.Id, clock));
        Assert.Empty(await FeedAsync(app.Booked.Id, clock, LinkChanged));

        var push = Assert.Single(await LinkNoticesAsync(link.Booked.Id, clock));
        Assert.Equal("reservation.changed-by-link", push.Type);
        Assert.Contains($"\"dinerUserId\":\"{link.DinerUserId}\"", push.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"localStartTime\":\"20:00:00\"", push.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(Now, push.ScheduledForUtc);

        var entry = Assert.Single(await FeedAsync(link.Booked.Id, clock, LinkChanged));
        Assert.Equal(Now, entry.CreatedAtUtc);
        Assert.Contains("\"time\":\"20:00\"", entry.ParamsJson, StringComparison.Ordinal);
        Assert.Contains("\"partySize\":\"3\"", entry.ParamsJson, StringComparison.Ordinal);
        Assert.Contains($"\"status\":\"{(int)ReservationStatus.Confirmed}\"", entry.ParamsJson, StringComparison.Ordinal);

        // Moved again before the first push went: one push, about where it is now; both moves in the feed.
        clock.Advance(TimeSpan.FromMinutes(1));
        await LinkService(db, clock).RescheduleByManageTokenAsync(link.Token, LinkMove(link.Branch.TableIds[1], BookingDate, new TimeOnly(21, 0), 3));

        var latest = Assert.Single(await LinkNoticesAsync(link.Booked.Id, clock));
        Assert.Contains("\"localStartTime\":\"21:00:00\"", latest.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(2, (await FeedAsync(link.Booked.Id, clock, LinkChanged)).Count);

        // A retry tells nobody twice.
        var retry = LinkMove(link.Branch.TableIds[1], BookingDate, new TimeOnly(21, 30), 3);
        await LinkService(db, clock).RescheduleByManageTokenAsync(link.Token, retry);
        await LinkService(db, clock).RescheduleByManageTokenAsync(link.Token, retry);
        Assert.Equal(3, (await FeedAsync(link.Booked.Id, clock, LinkChanged)).Count);
    }

    /// <summary>
    /// The same for cancelling from the link, which was already possible and was silent too. Cancelling a
    /// cancelled booking changes nothing and tells nobody again; the app's own cancel tells nobody.
    /// </summary>
    [SkippableFact]
    public async Task A_cancel_from_the_link_tells_the_account_holder_and_the_apps_own_cancel_does_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);

        var app = await ArrangeAsync(db, clock);
        var link = await ArrangeAsync(db, clock);

        await app.Diner.CancelAsync(new CancelReservationCommand(app.Booked.Id));
        await LinkService(db, clock).CancelByManageTokenAsync(link.Token);
        await LinkService(db, clock).CancelByManageTokenAsync(link.Token);

        Assert.Empty(await LinkNoticesAsync(app.Booked.Id, clock));
        Assert.Empty(await FeedAsync(app.Booked.Id, clock, LinkCancelled));

        // The reminder went with the booking; the one message left is the one telling the diner.
        var push = Assert.Single(await LinkNoticesAsync(link.Booked.Id, clock));
        Assert.Equal("reservation.cancelled-by-link", push.Type);
        Assert.Contains($"\"dinerUserId\":\"{link.DinerUserId}\"", push.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"localStartTime\":\"18:00:00\"", push.PayloadJson, StringComparison.Ordinal);

        var entry = Assert.Single(await FeedAsync(link.Booked.Id, clock, LinkCancelled));
        Assert.Contains("\"time\":\"18:00\"", entry.ParamsJson, StringComparison.Ordinal);
        Assert.Contains($"\"reservationCode\":\"{link.Booked.Code}\"", entry.ParamsJson, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private const string LinkChanged = "booking-changed-by-link";

    private const string LinkCancelled = "booking-cancelled-by-link";

    /// <summary>What a visitor holding only the link is told when the booking's account cannot take a move.</summary>
    private static void AssertCannotChangeOnline(Exception refused)
    {
        var mapped = ApiExceptionMapper.Map(refused);

        Assert.Equal(StatusCodes.Status403Forbidden, mapped.Status);
        Assert.Equal("phone-not-verified", mapped.Code);
        Assert.Equal(
            "This booking can no longer be changed online. Call the venue, or cancel it and book again.",
            mapped.Message);
        Assert.Null(mapped.Context);
    }

    /// <summary>The booking's unsent pushes about what was done from its link.</summary>
    private async Task<List<Yalla.Domain.Messaging.OutboxMessage>> LinkNoticesAsync(Guid reservationId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var prefix = OutboxMessageTypes.PrefixFor("reservation", reservationId);

        return await db.OutboxMessages.AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith(prefix)
                        && m.SentAtUtc == null
                        && m.Type.EndsWith("-by-link"))
            .ToListAsync();
    }

    /// <summary>The booking's feed entries of one kind.</summary>
    private async Task<List<Yalla.Domain.Identity.DinerNotification>> FeedAsync(Guid reservationId, TestClock clock, string kind)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.DinerNotifications.AsNoTracking()
            .Where(n => n.ReservationId == reservationId && n.Kind == kind)
            .ToListAsync();
    }

    /// <summary>A branch, a diner of its own, and one booking of theirs with its manage token.</summary>
    private sealed record Arranged(
        TestBranch Branch,
        ReservationService Diner,
        Guid DinerUserId,
        ReservationView Booked,
        string Token,
        Guid? TenSeater);

    private async Task<Arranged> ArrangeAsync(
        Yalla.Infrastructure.Persistence.YallaDbContext db,
        TestClock clock,
        DateOnly? date = null,
        TimeOnly? time = null,
        int partySize = 2,
        bool tenSeater = false,
        ReservationPolicy? policy = null)
    {
        var branch = await TestBranchBuilder.CreateAsync(db, policy: policy);
        var big = tenSeater ? (await TestBranchBuilder.AddTableAsync(db, branch, "12", seats: 10)).Id : (Guid?)null;

        var dinerUserId = Guid.CreateVersion7();
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner(dinerUserId));

        var booked = await diner.CreateAsync(
            Booking(branch, branch.FirstTableId, date ?? BookingDate, time ?? SixPm) with { PartySize = partySize });

        return new Arranged(branch, diner, dinerUserId, booked, booked.ManageToken!, big);
    }

    /// <summary>The service as an anonymous request reaches it.</summary>
    private ReservationService LinkService(Yalla.Infrastructure.Persistence.YallaDbContext db, TestClock clock) =>
        fixture.CreateReservationService(db, clock, Anonymous);

    /// <summary>The manage link's read, over the same context.</summary>
    private PublicBookingService PublicView(Yalla.Infrastructure.Persistence.YallaDbContext db, TestClock clock) =>
        new(db, clock, LinkService(db, clock));

    private static CreateReservationCommand Booking(TestBranch branch, Guid tableId, DateOnly date, TimeOnly time) =>
        new(
            branch.BranchId,
            tableId,
            date,
            time,
            2,
            GuestName: "Ani Link",
            GuestPhone: "+37411223366",
            ClientCommandId: Guid.CreateVersion7());

    private static RescheduleReservationCommand Move(Arranged arranged, Guid tableId, DateOnly date, TimeOnly time, int partySize) =>
        new(arranged.Booked.Id, tableId, date, time, partySize, Guid.CreateVersion7());

    private static RescheduleByManageTokenCommand LinkMove(Guid tableId, DateOnly date, TimeOnly time, int partySize) =>
        new(tableId, date, time, partySize, Guid.CreateVersion7());

    /// <summary>The two moves decided alike: the status, why, and the clocks on it.</summary>
    private static void AssertSameDecision(ReservationView app, ReservationView link)
    {
        Assert.Equal(app.Status, link.Status);
        Assert.Equal(app.AwaitingApprovalBecause, link.AwaitingApprovalBecause);
        Assert.Equal(app.ApprovalExpiresAtUtc, link.ApprovalExpiresAtUtc);
        Assert.Equal(app.ConfirmedAtUtc is null, link.ConfirmedAtUtc is null);
        Assert.Equal(app.LocalDate, link.LocalDate);
        Assert.Equal(app.LocalStartTime, link.LocalStartTime);
        Assert.Equal(app.StartUtc, link.StartUtc);
        Assert.Equal(app.EndUtc, link.EndUtc);
        Assert.Equal(app.PartySize, link.PartySize);
        Assert.Equal(app.TableLabel, link.TableLabel);
        Assert.Equal(app.AwaitsEarlierGuestAnswer, link.AwaitsEarlierGuestAnswer);
    }

    /// <summary>
    /// The booking's outbox messages - unsent and sent - by what follows its id in the key, so two
    /// bookings' can be compared. Without the link's own notice to the account holder, which only the
    /// link writes and which has tests of its own.
    /// </summary>
    private async Task<List<string>> ShapeOfOutboxAsync(Guid reservationId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var prefix = OutboxMessageTypes.PrefixFor("reservation", reservationId);

        var keys = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith(prefix))
            .Select(m => m.IdempotencyKey)
            .ToListAsync();

        return
        [
            .. keys.Select(k => k[prefix.Length..].TrimStart(':'))
                .Where(k => !k.Contains("-by-link", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal),
        ];
    }

    /// <summary>The booking's feed entries: their kinds and when they appear. Without the link's own notice, as above.</summary>
    private async Task<List<string>> ShapeOfFeedAsync(Guid reservationId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var entries = await db.DinerNotifications.AsNoTracking()
            .Where(n => n.ReservationId == reservationId)
            .Select(n => new { n.Kind, n.CreatedAtUtc })
            .ToListAsync();

        return
        [
            .. entries.Where(n => !n.Kind.EndsWith("-by-link", StringComparison.Ordinal))
                .Select(n => $"{n.Kind}@{n.CreatedAtUtc:O}")
                .OrderBy(n => n, StringComparer.Ordinal),
        ];
    }

    private async Task<Reservation> StoredAsync(Guid reservationId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservationId);
    }

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

    /// <summary>Bookings the arranged diner did not turn up for, in the past and inside the rolling window.</summary>
    private async Task AddNoShowsAsync(Arranged arranged, int count, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);

        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == arranged.Branch.BranchId)
            .Select(b => b.ReservationPolicy)
            .FirstAsync();

        for (var i = 0; i < count; i++)
        {
            var startUtc = Now.AddDays(-(i + 1) * 7);

            var missed = Reservation.Create(
                branchId: arranged.Branch.BranchId,
                diningTableId: arranged.Branch.FirstTableId,
                startUtc: startUtc,
                localDate: DateOnly.FromDateTime(startUtc),
                localStartTime: TimeOnly.FromDateTime(startUtc),
                partySize: 2,
                guestName: "Ani Link",
                guestPhone: "+37411223366",
                code: ReservationCode.Generate(),
                policy: policy,
                dinerUserId: arranged.DinerUserId);

            missed.MarkNoShow(startUtc.AddMinutes(30));
            db.Reservations.Add(missed);
        }

        await db.SaveChangesAsync();
    }
}
