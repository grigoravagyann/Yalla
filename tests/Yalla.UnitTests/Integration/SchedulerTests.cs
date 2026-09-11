using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Yalla.Api.Errors;
using Yalla.Application.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The scheduler seen from the diner's phone: what is sent, when, in which language, and what the
/// two orphan columns finally do.
/// </summary>
/// <remarks>
/// Time moves by advancing a <see cref="FakeClock"/>. Nothing here sleeps, and nothing waits three
/// hours for a reminder.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class SchedulerTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    /// <summary>Midday UTC is four in the afternoon in Yerevan, so an evening booking is bookable.</summary>
    private static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly BookingDate = new(2026, 9, 13);

    /// <summary>Eight in the evening in Yerevan, which is four in the afternoon UTC.</summary>
    private static readonly TimeOnly Dinner = new(20, 0);

    private static readonly DateTime DinnerUtc = new(2026, 9, 13, 16, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A threshold of two, so the third no-show crosses it.
    /// </summary>
    /// <remarks>
    /// The shipped default is three and <c>RequiresApproval</c> is <c>&gt;</c>, so in production it
    /// is the fourth that costs a diner instant confirmation. The test names its own number rather
    /// than relying on the shipped one, so it is testing the rule and not the constant.
    /// </remarks>
    private static NoShowPolicy Threshold => new() { Threshold = 2 };

    /// <summary>
    /// How long a test will wait for the background loop before calling it broken.
    /// </summary>
    /// <remarks>
    /// Real seconds, and the only real ones in this file. The loop's <i>schedule</i> is fake time,
    /// which is what the test drives; this is a deadlock guard on a task that should complete in
    /// milliseconds, not a sleep the happy path pays for.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // ------------------------------------------------------------ 12. whose language

    /// <summary>
    /// <b>Test 12.</b> Two diners, one branch, one booking each: each is written to in the language
    /// of the phone they will read it on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two diners at the <i>same</i> branch is what makes this a test. One diner and one language
    /// would pass just as happily against an implementation that read the venue's language, or
    /// against one that had no language selection at all.
    /// </para>
    /// <para>
    /// The Russian speaker's account says Armenian and their phone says Russian, so this also pins
    /// down which of the two wins: the device, because that is the thing the message is read on.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task The_reminder_is_written_in_each_diners_own_language_not_the_branchs()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new FakeClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);

        // Account in Armenian, phone in Russian. The phone wins.
        var russian = await DinerWithDeviceAsync(db, clock, accountLocale: "hy", deviceLocale: "ru-RU");
        var english = await DinerWithDeviceAsync(db, clock, accountLocale: "hy", deviceLocale: "en");

        // The dispatcher is table-wide, so these two bookings must be the only cause of anything due.
        await fixture.ClearOutboxAsync(clock);

        await BookAsync(branch, branch.TableIds[0], Dinner, clock, russian);
        await BookAsync(branch, branch.TableIds[1], Dinner, clock, english);

        // Three hours before dinner, which is the branch's ReminderHoursBefore.
        clock.Advance(DinnerUtc.AddHours(-3) - clock.UtcNow);

        var channel = new RecordingChannel();

        await using (var passDb = fixture.CreateContext(clock))
        {
            var pass = await Dispatcher(passDb, clock, channel).RunOnceAsync();

            Assert.Equal(2, pass.Sent);
            Assert.Equal(0, pass.Failed);
        }

        var reminders = channel.OfKind("reservation-reminder");

        Assert.Equal(2, reminders.Count);

        var toRussian = Assert.Single(reminders, m => m.DinerUserId == russian.DinerUserId!.Value);
        var toEnglish = Assert.Single(reminders, m => m.DinerUserId == english.DinerUserId!.Value);

        // Rendered from the same words the app ships, so what is asserted is the language that was
        // chosen rather than a copy of the translation.
        var venue = await VenueNameAsync(branch);
        var branchName = await BranchNameAsync(branch);

        var expectedRussian = NotificationText.Reminder("ru", venue, branchName, "1", Dinner);
        var expectedEnglish = NotificationText.Reminder("en", venue, branchName, "2", Dinner);
        var armenian = NotificationText.Reminder("hy", venue, branchName, "1", Dinner);

        Assert.Equal(expectedRussian.Title, toRussian.Title);
        Assert.Equal(expectedRussian.Body, toRussian.Body);
        Assert.Equal(expectedEnglish.Title, toEnglish.Title);
        Assert.Equal(expectedEnglish.Body, toEnglish.Body);

        // And neither of them got the fallback, which is what a locale lookup that silently failed
        // would produce for both.
        Assert.NotEqual(armenian.Title, toRussian.Title);
        Assert.NotEqual(armenian.Title, toEnglish.Title);

        // The cancel action rides along, because cancelling has to be easier than not showing up.
        Assert.Equal("cancel", toRussian.Data["action"]);
        Assert.Equal("reservation-reminder", toRussian.CategoryId);
    }

    // ------------------------------------------------------------ 13. the party that already arrived

    /// <summary>
    /// <b>Test 13.</b> The late nudge goes to the party still missing. The one already eating gets
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The message is still <i>handled</i> - it is marked sent and never retried - because the
    /// decision not to push is the correct outcome, not a failure. A skipped message left pending
    /// would be retried every ten seconds for the rest of the evening.
    /// </remarks>
    [SkippableFact]
    public async Task The_late_nudge_reaches_the_missing_party_and_not_the_one_at_the_table()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new FakeClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 2);

        var missing = await DinerWithDeviceAsync(db, clock, accountLocale: "en", deviceLocale: "en");
        var arrived = await DinerWithDeviceAsync(db, clock, accountLocale: "en", deviceLocale: "en");

        await fixture.ClearOutboxAsync(clock);

        var missingBooking = await BookAsync(branch, branch.TableIds[0], Dinner, clock, missing);
        var arrivedBooking = await BookAsync(branch, branch.TableIds[1], Dinner, clock, arrived);

        var channel = new RecordingChannel();

        // The reminders first, so they are out of the way and the second pass is only about nudges.
        clock.Advance(DinnerUtc.AddHours(-3) - clock.UtcNow);

        await using (var reminderPass = fixture.CreateContext(clock))
        {
            await Dispatcher(reminderPass, clock, channel).RunOnceAsync();
        }

        // One party turns up five minutes late and is seated.
        clock.Advance(DinnerUtc.AddMinutes(5) - clock.UtcNow);

        await using (var seatDb = fixture.CreateContext(clock))
        {
            await fixture.CreateService(seatDb, clock, TestActor.Waiter(branch.WaiterId))
                .SeatReservationAsync(new SeatReservationCommand(
                    branch.BranchId, branch.TableIds[1], arrivedBooking, Guid.CreateVersion7()));
        }

        // Past LateNudgeAfterMinutes, which is ten.
        clock.Advance(DinnerUtc.AddMinutes(11) - clock.UtcNow);

        channel.Clear();

        await using (var nudgePass = fixture.CreateContext(clock))
        {
            var pass = await Dispatcher(nudgePass, clock, channel).RunOnceAsync();

            // Both nudges were handled. Only one of them turned into a push.
            Assert.Equal(2, pass.Sent);
            Assert.Equal(0, pass.Failed);
            Assert.Equal(0, pass.Stale);
        }

        var nudge = Assert.Single(channel.OfKind("reservation-late-nudge"));

        Assert.Equal(missing.DinerUserId!.Value, nudge.DinerUserId);
        Assert.Equal(missingBooking.ToString(), nudge.Data["reservationId"]);
        Assert.Equal("extend-hold", nudge.Data["action"]);
        Assert.Equal("10", nudge.Data["extensionMinutes"]);

        await using var verify = fixture.CreateContext(clock);

        // The seated party's nudge is finished with, not pending and not dead-lettered. Anything
        // else means the dispatcher comes back to it every ten seconds until closing time.
        var skipped = await verify.OutboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.IdempotencyKey
                == OutboxMessageTypes.KeyFor("reservation", arrivedBooking, "late-nudge"));

        Assert.NotNull(skipped.SentAtUtc);
        Assert.Null(skipped.DeadLetteredAtUtc);
    }

    // ------------------------------------------------------------ 14. five more minutes, once

    /// <summary>
    /// <b>Test 14.</b> The hold extends once. Tapping again is refused, and tapping the same
    /// notification twice is not.
    /// </summary>
    [SkippableFact]
    public async Task A_hold_extends_once_and_the_second_attempt_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new FakeClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = TestActor.Diner();

        var booking = await BookAsync(branch, branch.FirstTableId, Dinner, clock, diner);

        // Dinner time, the table is held, and the party is not here.
        clock.Advance(DinnerUtc.AddMinutes(11) - clock.UtcNow);

        await using (var holdDb = fixture.CreateContext(clock))
        {
            await fixture.CreateService(holdDb, clock, TestActor.Waiter(branch.WaiterId))
                .HoldForLatePartyAsync(new TableStateCommand(
                    branch.BranchId, branch.FirstTableId, Guid.CreateVersion7(), "held for a late booking"));
        }

        var firstTap = Guid.CreateVersion7();
        ExtendHoldResult extended;

        await using (var extendDb = fixture.CreateContext(clock))
        {
            extended = await fixture.CreateReservationService(extendDb, clock, diner)
                .ExtendHoldAsync(new ExtendHoldCommand(booking, firstTap));
        }

        Assert.False(extended.WasReplay);
        Assert.Equal(10, extended.ExtensionMinutes);
        Assert.Equal(0, extended.ExtensionsRemaining);
        Assert.Equal(clock.UtcNow.AddMinutes(10), extended.HoldExpiresAtUtc);

        // The same tap again. Not an error - a notification is tappable twice, and the second tap
        // gets the answer the first one got.
        await using (var replayDb = fixture.CreateContext(clock))
        {
            var replay = await fixture.CreateReservationService(replayDb, clock, diner)
                .ExtendHoldAsync(new ExtendHoldCommand(booking, firstTap));

            Assert.True(replay.WasReplay);
            Assert.Equal(0, replay.ExtensionMinutes);
        }

        // A genuinely new attempt is refused, and says why in words a diner can read.
        await using (var secondDb = fixture.CreateContext(clock))
        {
            var refused = await Assert.ThrowsAsync<HoldExtensionRefusedException>(
                () => fixture.CreateReservationService(secondDb, clock, diner)
                    .ExtendHoldAsync(new ExtendHoldCommand(booking, Guid.CreateVersion7())));

            Assert.Contains("already had its one extension", refused.Message, StringComparison.Ordinal);
        }

        await using var verify = fixture.CreateContext(clock);

        var reservation = await verify.Reservations.AsNoTracking().SingleAsync(r => r.Id == booking);

        Assert.Equal(1, reservation.GraceExtensionsUsed);
        Assert.Equal(extended.HoldExpiresAtUtc, reservation.HoldExpiresAtUtc);

        // And the floor screen was told, through the branch change sequence, without the table
        // itself changing status. Held to Held: nothing changed, something happened.
        var change = await verify.TableStateChanges
            .AsNoTracking()
            .Where(c => c.ReservationId == booking && c.ClientCommandId == firstTap)
            .SingleAsync();

        Assert.Equal(TableStatus.Held, change.FromStatus);
        Assert.Equal(TableStatus.Held, change.ToStatus);
    }

    /// <summary>
    /// "Keep my table" before the booking has started is refused and spends nothing.
    /// </summary>
    /// <remarks>
    /// The app offers the button on every confirmed booking, so a tap three days early spent the
    /// one extension and pinged the floor tablets about a table nobody was holding. When the real
    /// late nudge came, the diner was told they had already let the venue know.
    /// </remarks>
    [SkippableFact]
    public async Task Keeping_the_table_before_the_booking_starts_is_refused_and_spends_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new FakeClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = TestActor.Diner();

        var booking = await BookAsync(branch, branch.FirstTableId, Dinner, clock, diner);
        var earlyTap = Guid.CreateVersion7();

        Assert.Equal("hold-not-active", await RefusedExtensionCodeAsync(booking, earlyTap, clock, diner));

        await using var verify = fixture.CreateContext(clock);

        var reservation = await verify.Reservations.AsNoTracking().SingleAsync(r => r.Id == booking);

        Assert.Equal(0, reservation.GraceExtensionsUsed);
        Assert.False(await verify.TableStateChanges.AnyAsync(c => c.ClientCommandId == earlyTap));
    }

    /// <summary>
    /// Each refusal to extend says which one it is, so the app never infers "you already let them
    /// know" from a bare 409.
    /// </summary>
    [SkippableFact]
    public async Task Each_refused_extension_names_its_own_reason()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new FakeClock(Now);
        await using var db = fixture.CreateContext(clock);
        var offersNone = await TestBranchBuilder.CreateAsync(
            db, policy: WithoutExtensions(ReservationPolicy.DefaultFor(VenueType.Restaurant)));
        var offersOne = await TestBranchBuilder.CreateAsync(db);
        var diner = TestActor.Diner();

        var atOffersNone = await BookAsync(offersNone, offersNone.FirstTableId, Dinner, clock, diner);
        var atOffersOne = await BookAsync(offersOne, offersOne.FirstTableId, Dinner, clock, diner);

        // Dinner time, and the party is not here yet.
        clock.Advance(DinnerUtc.AddMinutes(11) - clock.UtcNow);

        Assert.Equal(
            "extensions-not-offered",
            await RefusedExtensionCodeAsync(atOffersNone, Guid.CreateVersion7(), clock, diner));

        await using (var extendDb = fixture.CreateContext(clock))
        {
            await fixture.CreateReservationService(extendDb, clock, diner)
                .ExtendHoldAsync(new ExtendHoldCommand(atOffersOne, Guid.CreateVersion7()));
        }

        Assert.Equal(
            "hold-already-extended",
            await RefusedExtensionCodeAsync(atOffersOne, Guid.CreateVersion7(), clock, diner));
    }

    /// <summary>
    /// Asks to extend, expects a refusal, and answers with the code the API would send for it.
    /// </summary>
    private async Task<string> RefusedExtensionCodeAsync(Guid booking, Guid tap, IClock clock, TestActor diner)
    {
        await using var db = fixture.CreateContext(clock);

        var refused = await Assert.ThrowsAnyAsync<DomainStateException>(
            () => fixture.CreateReservationService(db, clock, diner)
                .ExtendHoldAsync(new ExtendHoldCommand(booking, tap)));

        var mapped = ApiExceptionMapper.Map(refused);

        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);

        return mapped.Code;
    }

    /// <summary>The given policy with hold extensions switched off, and nothing else changed.</summary>
    private static ReservationPolicy WithoutExtensions(ReservationPolicy policy) =>
        new(
            policy.TurnTimeMinutes,
            policy.BufferMinutes,
            policy.GraceMinutes,
            policy.LateNudgeAfterMinutes,
            graceExtensionMinutes: 0,
            policy.MinLeadMinutes,
            policy.BookingWindowDays,
            policy.CancellationDeadlineMinutes,
            policy.AutoConfirm,
            policy.ServiceChargePercent,
            policy.PricesIncludeVat,
            policy.MaxSeatOverhang,
            policy.ApprovalRequiredAbovePartySize,
            policy.WalkInHoldbackMinutes,
            policy.ReminderHoursBefore);

    // ------------------------------------------------------------ 15. nobody came

    /// <summary>
    /// <b>Test 15.</b> A no-show frees the held table, cancels what was queued for it, and on the
    /// third one the diner's next booking waits for a human.
    /// </summary>
    /// <remarks>
    /// The no-show count has existed since Prompt 4 and nothing ever wrote to it, so this is the
    /// first time the threshold has been reached by the route a venue would actually reach it by.
    /// </remarks>
    [SkippableFact]
    public async Task A_no_show_frees_the_table_and_the_third_one_costs_instant_confirmation()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new FakeClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db, tableCount: 8);
        var diner = TestActor.Diner();

        for (var i = 0; i < 2; i++)
        {
            await NoShowAsync(branch, branch.TableIds[i], new TimeOnly(18 + i, 0), clock, diner);
        }

        // Two is the threshold, not past it. This is the boundary, and asserting only the far side
        // of it would pass against a rule that fired on the first no-show.
        await using (var stillFineDb = fixture.CreateContext(clock))
        {
            var stillFine = await fixture.CreateReservationService(stillFineDb, clock, diner, Threshold)
                .CreateAsync(Booking(branch, branch.TableIds[5], new TimeOnly(21, 0)));

            Assert.Equal(ReservationStatus.Confirmed, stillFine.Status);
        }

        var third = await NoShowAsync(branch, branch.TableIds[2], new TimeOnly(20, 0), clock, diner);

        Assert.True(third.TableFreed);
        Assert.Equal(TableStatus.Free, third.TableStatus);
        Assert.True(third.CountsTowardNoShowThreshold);
        Assert.Equal(ReservationStatus.NoShow, third.Reservation.Status);

        await using (var verify = fixture.CreateContext(clock))
        {
            // Freed through the state machine, so the audit row exists and every other tablet in
            // the room finds out.
            Assert.True(await verify.TableStateChanges.AnyAsync(
                c => c.DiningTableId == branch.TableIds[2] && c.ToStatus == TableStatus.Free));

            // And nothing is still queued to ask this party whether they are on their way.
            Assert.Equal(
                0,
                await verify.OutboxMessages.CountAsync(m => m.IdempotencyKey.StartsWith(
                    OutboxMessageTypes.PrefixFor("reservation", third.Reservation.Id))));
        }

        // The next one waits for a person - and says so, rather than being refused.
        await using var nextDb = fixture.CreateContext(clock);

        var next = await fixture.CreateReservationService(nextDb, clock, diner, Threshold)
            .CreateAsync(Booking(branch, branch.TableIds[6], new TimeOnly(21, 30)));

        Assert.Equal(ReservationStatus.PendingApproval, next.Status);
        Assert.Equal(ApprovalTrigger.NoShowHistory, next.AwaitingApprovalBecause);
    }

    // ------------------------------------------------------------ 16. the app was uninstalled

    /// <summary>
    /// <b>Test 16.</b> Expo says the app is gone. The token is revoked, and the next message does
    /// not go near the network.
    /// </summary>
    /// <remarks>
    /// The second send is the half that matters. Revoking a row and then still posting to it would
    /// satisfy a test that only looked at the database, and would still be a queue retrying a dead
    /// token for ever.
    /// </remarks>
    [SkippableFact]
    public async Task A_device_not_registered_receipt_revokes_the_token_instead_of_retrying_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new FakeClock(Now);
        await using var db = fixture.CreateContext(clock);
        var diner = await DinerWithDeviceAsync(db, clock, accountLocale: "en", deviceLocale: "en");

        var expo = new StubExpo(
            """{"data":[{"status":"error","message":"\"ExponentPushToken[x]\" is not a registered push notification recipient","details":{"error":"DeviceNotRegistered"}}]}""");

        using var http = new HttpClient(expo);

        var channel = new ExpoNotificationChannel(
            db,
            clock,
            http,
            new NotificationOptions { Channel = "Expo" },
            NullLogger<ExpoNotificationChannel>.Instance);

        var message = new NotificationMessage(
            diner.DinerUserId!.Value,
            "Still coming?",
            "Table 1 is being held.",
            new Dictionary<string, string> { ["kind"] = "reservation-late-nudge" });

        var results = await channel.SendAsync(message);

        var result = Assert.Single(results);

        Assert.False(result.Delivered);
        Assert.True(result.ShouldRevokeToken);
        Assert.Equal(1, expo.Requests);

        await using (var verify = fixture.CreateContext(clock))
        {
            var device = await verify.DinerDevices
                .AsNoTracking()
                .SingleAsync(d => d.DinerUserId == diner.DinerUserId!.Value);

            Assert.True(device.IsRevoked);
            Assert.Equal(clock.UtcNow, device.RevokedAtUtc);
            Assert.Contains("DeviceNotRegistered", device.RevokedReason!, StringComparison.Ordinal);
        }

        // The next message finds no live device and never posts. This is what "instead of retrying"
        // means: not one fewer retry, none.
        await using var secondDb = fixture.CreateContext(clock);

        var second = new ExpoNotificationChannel(
            secondDb, clock, http, new NotificationOptions(), NullLogger<ExpoNotificationChannel>.Instance);

        Assert.Empty(await second.SendAsync(message));
        Assert.Equal(1, expo.Requests);
    }

    // ------------------------------------------------------------ when the provider is down

    /// <summary>
    /// A real message, a channel that is genuinely broken, and what the platform view says
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The channel is the shipped Expo one, pointed at a provider answering 503. That is a transport
    /// failure, so it is retried rather than discarded - and when the attempts run out the message is
    /// dead-lettered carrying the reason, the key that names the booking, and how many times it was
    /// tried.
    /// </para>
    /// <para>
    /// The output is written to the test log because it is the answer to "what does an operator
    /// actually see", and a description of it in a document goes stale the first time an exception
    /// message changes.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_broken_channel_dead_letters_with_something_an_operator_can_act_on()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new FakeClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = await DinerWithDeviceAsync(db, clock, accountLocale: "hy", deviceLocale: "hy");

        // So the attempt count below is this message's and not a share of somebody else's batch.
        await fixture.ClearOutboxAsync(clock);

        var booking = await BookAsync(branch, branch.FirstTableId, Dinner, clock, diner);

        clock.Advance(DinnerUtc.AddHours(-3) - clock.UtcNow);

        var expo = new StubExpo("{\"error\":\"service unavailable\"}", HttpStatusCode.ServiceUnavailable);

        using var http = new HttpClient(expo);

        var broken = new ExpoNotificationChannel(
            db, clock, http, new NotificationOptions { Channel = "Expo" },
            NullLogger<ExpoNotificationChannel>.Instance);

        var options = new OutboxOptions { MaxAttempts = 3, BaseBackoffSeconds = 30, StaleAfterMinutes = 10_000 };

        for (var attempt = 0; attempt < options.MaxAttempts; attempt++)
        {
            await using var passDb = fixture.CreateContext(clock);

            await Dispatcher(passDb, clock, broken, options).RunOnceAsync();

            // Past the backoff, so the next pass finds it due rather than waiting.
            clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(options.MaxAttempts, expo.Requests);

        await using var readDb = fixture.CreateContext(clock);
        var health = await fixture.CreateOutbox(readDb, clock).GetHealthAsync(deadLetterLimit: 200);

        var letter = health.DeadLetters.Single(
            d => d.IdempotencyKey == OutboxMessageTypes.KeyFor("reservation", booking, "reminder"));

        Assert.Equal(OutboxMessageTypes.ReservationReminder, letter.Type);
        Assert.Equal(options.MaxAttempts, letter.AttemptCount);
        Assert.NotNull(letter.DeadLetteredAtUtc);
        Assert.Contains("503", letter.LastError!, StringComparison.Ordinal);

        // Exactly what GET /api/platform/outbox returns, for the one dead letter this test made.
        output.WriteLine(JsonSerializer.Serialize(
            new { deadLettered = health.DeadLettered, letter },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    // ------------------------------------------------------------ the loop itself

    /// <summary>
    /// The hosted service ticks off the same clock the domain reads, so advancing time dispatches.
    /// </summary>
    /// <remarks>
    /// Not one of the sixteen, and the reason the fake is a <see cref="TimeProvider"/> rather than a
    /// second stopwatch of our own. Two independent fakes is how a scheduler test passes while the
    /// thing it schedules never fires: the test advances one, the <c>PeriodicTimer</c> reads the
    /// other, and nothing ticks.
    /// </remarks>
    [SkippableFact]
    public async Task The_background_loop_dispatches_when_the_fake_clock_ticks_past_the_poll_interval()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new FakeClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var diner = await DinerWithDeviceAsync(db, clock, accountLocale: "en", deviceLocale: "en");

        await fixture.ClearOutboxAsync(clock);

        await BookAsync(branch, branch.FirstTableId, Dinner, clock, diner);

        var channel = new RecordingChannel();
        var options = new OutboxOptions { PollSeconds = 30 };

        await using var loopDb = fixture.CreateContext(clock);
        var dispatcher = Dispatcher(loopDb, clock, channel, options);

        using var cancellation = new CancellationTokenSource();

        var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = RunLoopAsync(dispatcher, options, clock, woken, cancellation.Token);

        // The wake-up pass, before the first tick. Nothing is due three hours early, so it finds an
        // empty queue - and waiting for it is what makes the advance below a tick rather than a race
        // with the pass that was already running.
        await woken.Task.WaitAsync(Patience);

        Assert.Empty(channel.Messages);

        clock.Advance(DinnerUtc.AddHours(-3) - clock.UtcNow);

        // No polling and no sleeping: the channel completes this the moment it is handed a message.
        await channel.NextMessage.WaitAsync(Patience);

        Assert.Single(channel.OfKind("reservation-reminder"));

        await cancellation.CancelAsync();
        await loop;
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// The hosted service's loop, over one dispatcher.
    /// </summary>
    /// <remarks>
    /// A copy of <c>OutboxHostedService.ExecuteAsync</c> shorn of its service scope, because what is
    /// under test is the <see cref="PeriodicTimer"/> reading the fake provider - and building a
    /// container here would test the container.
    /// </remarks>
    private static async Task RunLoopAsync(
        OutboxDispatcher dispatcher,
        OutboxOptions options,
        FakeClock clock,
        TaskCompletionSource woken,
        CancellationToken cancellationToken)
    {
        await dispatcher.RunOnceAsync(cancellationToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds), clock.Provider);

        // Signalled after the timer exists, not after the pass. Advancing a fake provider fires the
        // timers that exist at that moment, so a test released one line earlier can move the clock
        // past a tick that nothing is yet scheduled to receive - and then wait for ever for a tick
        // that will not come again. The timer buffers a tick that elapses before it is awaited, so
        // here is the earliest safe point.
        woken.SetResult();

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await dispatcher.RunOnceAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private OutboxDispatcher Dispatcher(
        YallaDbContext db,
        IClock clock,
        INotificationChannel channel,
        OutboxOptions? options = null) =>
        new(
            db,
            clock,
            options ?? new OutboxOptions(),
            [
                new ReservationReminderHandler(db, channel, NullLogger<ReservationReminderHandler>.Instance),
                new ReservationLateNudgeHandler(db, channel, NullLogger<ReservationLateNudgeHandler>.Instance),
            ],
            NullLogger<OutboxDispatcher>.Instance);

    /// <summary>A verified diner with one phone registered, and the actor that speaks for them.</summary>
    private static async Task<TestActor> DinerWithDeviceAsync(
        YallaDbContext db,
        IClock clock,
        string accountLocale,
        string deviceLocale)
    {
        var unique = Guid.NewGuid().ToString("N")[..9];
        var diner = new DinerUser($"+3749{unique}", accountLocale);

        db.DinerUsers.Add(diner);
        db.DinerDevices.Add(new DinerDevice(
            diner.Id,
            $"ExponentPushToken[{unique}]",
            DevicePlatform.Ios,
            deviceLocale,
            clock.UtcNow));

        await db.SaveChangesAsync();

        return TestActor.Diner(diner.Id);
    }

    private async Task<Guid> BookAsync(
        TestBranch branch,
        Guid tableId,
        TimeOnly at,
        IClock clock,
        TestActor diner)
    {
        await using var db = fixture.CreateContext(clock);

        var view = await fixture.CreateReservationService(db, clock, diner)
            .CreateAsync(Booking(branch, tableId, at));

        return view.Id;
    }

    /// <summary>Books, holds the table, and then nobody turns up.</summary>
    private async Task<ReservationReleaseResult> NoShowAsync(
        TestBranch branch,
        Guid tableId,
        TimeOnly at,
        IClock clock,
        TestActor diner)
    {
        var booking = await BookAsync(branch, tableId, at, clock, diner);

        await using (var holdDb = fixture.CreateContext(clock))
        {
            await fixture.CreateService(holdDb, clock, TestActor.Waiter(branch.WaiterId))
                .HoldForLatePartyAsync(new TableStateCommand(
                    branch.BranchId, tableId, Guid.CreateVersion7(), "held for a late booking"));
        }

        await using var releaseDb = fixture.CreateContext(clock);

        return await fixture.CreateReservationService(releaseDb, clock, TestActor.Waiter(branch.WaiterId))
            .ReleaseAsync(new ReleaseReservationCommand(booking, ReleaseOutcome.NoShow, Guid.CreateVersion7()));
    }

    private async Task<string> VenueNameAsync(TestBranch branch)
    {
        await using var db = fixture.CreateContext(new TestClock(Now));

        return await db.Venues.AsNoTracking()
            .Where(v => v.Id == branch.VenueId).Select(v => v.Name).SingleAsync();
    }

    private async Task<string> BranchNameAsync(TestBranch branch)
    {
        await using var db = fixture.CreateContext(new TestClock(Now));

        return await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId).Select(b => b.Name).SingleAsync();
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

    /// <summary>Expo, answering with one canned body and counting how often it was asked.</summary>
    private sealed class StubExpo(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
