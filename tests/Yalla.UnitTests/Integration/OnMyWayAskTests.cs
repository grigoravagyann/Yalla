using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Messaging;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Infrastructure.Persistence;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;
using static Yalla.UnitTests.Integration.PreOrderTests;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// "I'm coming" on a booking: the guest's "running late", the venue's "Ask: are you coming?" with its rules, and both
/// exposed on the booking the app reads and the floor the tablet reads. See <c>docs/reservations.md</c>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class OnMyWayAskTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The venue asks from two hours before - not earlier - and not again within fifteen minutes; the guest says late by
    /// fifteen; asking again is refused as answered; the booking and the floor show all of it.
    /// </summary>
    [SkippableFact]
    public async Task The_venue_asks_within_the_window_once_a_quarter_hour_until_the_guest_answers_late()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        var (branch, managerToken) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(managerToken);
        using var other = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var second = await CreatedAsync(other, AppBooking(factory, branch, daysAhead: 2, tableIndex: 1));
        var secondId = second.GetProperty("id").GetGuid();
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();
        Assert.Equal(startUtc.AddHours(-2), booking.GetProperty("onMyWayOpensAtUtc").GetDateTime());

        factory.Clock.UtcNow = startUtc.AddHours(-3);

        var early = await ProblemAsync(
            await waiter.PostAsync($"/api/reservations/{id}/ask-on-my-way", null), HttpStatusCode.Conflict, "ask-on-my-way-too-early");
        Assert.Equal(startUtc.AddHours(-2), early.GetProperty("context").GetProperty("opensAtUtc").GetDateTime());

        await ProblemAsync(
            await diner.PostAsJsonAsync($"/api/reservations/{id}/running-late", new { minutes = 15 }),
            HttpStatusCode.Conflict,
            "on-my-way-not-open");

        factory.Clock.UtcNow = startUtc.AddMinutes(-60);

        var asked = await OkAsync(await waiter.PostAsync($"/api/reservations/{id}/ask-on-my-way", null));
        Assert.True(asked.GetProperty("notified").GetBoolean());
        Assert.Equal(startUtc.AddMinutes(-60), asked.GetProperty("staffAskedOnMyWayAtUtc").GetDateTime());
        Assert.Equal(startUtc.AddMinutes(-45), asked.GetProperty("nextAskAllowedAtUtc").GetDateTime());

        factory.Clock.UtcNow = startUtc.AddMinutes(-50);

        var recent = await ProblemAsync(
            await waiter.PostAsync($"/api/reservations/{id}/ask-on-my-way", null), HttpStatusCode.Conflict, "ask-on-my-way-asked-recently");
        Assert.Equal(startUtc.AddMinutes(-45), recent.GetProperty("context").GetProperty("nextAllowedAtUtc").GetDateTime());

        var mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal(startUtc.AddMinutes(-60), mine.GetProperty("staffAskedOnMyWayAtUtc").GetDateTime());
        Assert.False(mine.TryGetProperty("guestRunningLateMinutes", out _));

        var floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        var table = floor.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("tableId").GetGuid() == branch.TableIds[0]);
        Assert.Equal(startUtc.AddMinutes(-60), table.GetProperty("nextReservationStaffAskedOnMyWayAtUtc").GetDateTime());

        await ProblemAsync(
            await diner.PostAsJsonAsync($"/api/reservations/{id}/running-late", new { minutes = 20 }),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed");

        factory.Clock.UtcNow = startUtc.AddMinutes(-48);

        var late = await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/running-late", new { minutes = 15 }));
        Assert.Equal(15, late.GetProperty("guestRunningLateMinutes").GetInt32());
        Assert.Equal(startUtc.AddMinutes(15), late.GetProperty("expectedArrivalUtc").GetDateTime());

        factory.Clock.UtcNow = startUtc.AddMinutes(-45);

        var answered = await ProblemAsync(
            await waiter.PostAsync($"/api/reservations/{id}/ask-on-my-way", null), HttpStatusCode.Conflict, "guest-already-answered");
        Assert.Equal(15, answered.GetProperty("context").GetProperty("runningLateMinutes").GetInt32());

        mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal(15, mine.GetProperty("guestRunningLateMinutes").GetInt32());
        Assert.Equal(startUtc.AddMinutes(-48), mine.GetProperty("guestRunningLateAtUtc").GetDateTime());

        floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        table = floor.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("tableId").GetGuid() == branch.TableIds[0]);
        Assert.Equal(15, table.GetProperty("nextReservationGuestRunningLateMinutes").GetInt32());

        // "On my way" after "late": on time after all - the late answer goes.
        await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }));
        mine = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.False(mine.TryGetProperty("guestRunningLateMinutes", out _));
        Assert.Equal(startUtc.AddMinutes(-45), mine.GetProperty("guestConfirmedOnWayAtUtc").GetDateTime());

        // The venue's console is not the floor: a manager may ask too (manager or above is floor staff), a cancelled
        // booking may not be asked about.
        await OkAsync(await other.PostAsJsonAsync($"/api/reservations/{secondId}/cancel", new { }));
        await ProblemAsync(
            await manager.PostAsync($"/api/reservations/{secondId}/ask-on-my-way", null), HttpStatusCode.Conflict, "ask-on-my-way-not-open");
    }

    /// <summary>
    /// The venue's ask is the automatic question's push and feed entry, sent now with <c>askedByVenue</c>; the automatic
    /// question is then not asked on top at 30 minutes.
    /// </summary>
    [SkippableFact]
    public async Task The_venues_ask_pushes_the_question_now_and_the_automatic_one_is_not_asked_on_top()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        await fixture.ClearOutboxAsync(factory.Clock);
        var (branch, _) = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();

        factory.Clock.UtcNow = startUtc.AddMinutes(-60);
        await OkAsync(await waiter.PostAsync($"/api/reservations/{id}/ask-on-my-way", null));

        var channel = new RecordingChannel();
        var clock = new TestClock(startUtc.AddMinutes(-60));

        await using (var db = fixture.CreateContext(clock))
        {
            await Dispatcher(db, clock, channel).RunOnceAsync();

            var entry = await db.DinerNotifications
                .Where(n => n.ReservationId == id && n.Kind == DinerNotificationKinds.BookingOnYourWay)
                .Select(n => new { n.ParamsJson, n.CreatedAtUtc })
                .ToListAsync();

            Assert.Contains(entry, e => e.ParamsJson.Contains("\"askedByVenue\":\"true\"") && e.CreatedAtUtc == startUtc.AddMinutes(-60));
        }

        var push = Assert.Single(channel.OfKind("reservation-on-your-way"));
        Assert.Equal(id.ToString(), push.Data["reservationId"]);

        clock.UtcNow = startUtc.AddMinutes(-30);

        await using (var db = fixture.CreateContext(clock))
        {
            await Dispatcher(db, clock, channel).RunOnceAsync();
            await Dispatcher(db, clock, channel).RunOnceAsync();
        }

        Assert.Single(channel.OfKind("reservation-on-your-way"));
    }

    private async Task<(AuthBranch Branch, string ManagerToken)> ArrangeAsync(YallaApiFactory factory)
    {
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2);
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, acceptsOnlineBookings: true);
        }

        return (branch, await SignInManagerAsync(factory, branch));
    }

    private static OutboxDispatcher Dispatcher(YallaDbContext db, TestClock clock, RecordingChannel channel) =>
        new(
            db,
            clock,
            new OutboxOptions(),
            [new ReservationOnYourWayHandler(db, channel, NullLogger<ReservationOnYourWayHandler>.Instance)],
            NullLogger<OutboxDispatcher>.Instance);
}
