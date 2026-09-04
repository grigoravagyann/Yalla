using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The booking endpoints through the real pipeline, with the real policies attached.
/// </summary>
/// <remarks>
/// <para>
/// The service tests next door prove the rules. These prove the <b>wiring</b>, and the failure
/// they guard against is the one no unit test can see: a policy that was never applied to an
/// endpoint. A booking route with the attribute missing behaves identically in every service test
/// and is wide open in production.
/// </para>
/// <para>
/// Two of these are boundaries rather than conveniences. A staff token must not be able to book as
/// a diner, and a manager of one venue must not be able to approve another venue's bookings. The
/// second cannot use <c>BranchScoped</c> - approve and reject are addressed by reservation id and
/// that policy compares a claim to a <c>branchId</c> route value - so the service resolves the
/// booking's branch itself, and this is what proves it actually does.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class ReservationEndpointTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Availability_is_anonymous_because_browsing_needs_no_account()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        // No token at all. Somebody deciding whether to eat here has to see the room before they
        // are asked who they are.
        using var anonymous = factory.CreateClient();
        var slot = Slot(factory);

        var response = await anonymous.GetAsync(
            $"/api/branches/{branch.BranchId}/availability"
            + $"?date={slot.Date:yyyy-MM-dd}&time={slot.Time:HH\\:mm}&partySize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, body.GetProperty("tables").GetArrayLength());
        Assert.True(body.GetProperty("tables")[0].GetProperty("isAvailable").GetBoolean());
    }

    [SkippableFact]
    public async Task Booking_refuses_an_anonymous_caller_and_a_staff_token_alike()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var anonymous = factory.CreateClient();
        var anonymousAttempt = await anonymous.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousAttempt.StatusCode);

        // A waiter is authenticated and is an ActorType.Diner's opposite number - but booking is
        // for a diner with an account, so the policy refuses the principal type outright.
        var waiterToken = await StaffAuthTests.SignInWaiterAsync(factory, branch);
        using var waiter = factory.CreateClientWithToken(waiterToken);

        var staffAttempt = await waiter.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch));

        Assert.Equal(HttpStatusCode.Forbidden, staffAttempt.StatusCode);
    }

    [SkippableFact]
    public async Task A_verified_diner_books_and_a_retry_returns_the_same_booking()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = NewBooking(factory, branch);

        var created = await diner.PostAsJsonAsync("/api/reservations", booking);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        // Enums are integers on the wire, as the schema declares.
        Assert.Equal((int)ReservationStatus.Confirmed, body.GetProperty("status").GetInt32());
        Assert.False(body.GetProperty("wasReplay").GetBoolean());

        // The phone never saw the first response and sent the whole thing again.
        var replay = await diner.PostAsJsonAsync("/api/reservations", booking);

        // 200, not 201: the second request created nothing, and saying otherwise is how a retry
        // ends up looking like a second booking in a client's own logs.
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, replayBody.GetProperty("id").GetGuid());
        Assert.True(replayBody.GetProperty("wasReplay").GetBoolean());
    }

    [SkippableFact]
    public async Task A_diner_sees_only_their_own_bookings_and_cannot_cancel_anyone_elses()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var ani = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var stranger = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var created = await ani.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var mine = await (await ani.GetAsync("/api/reservations/mine"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, mine.GetProperty("upcoming").GetArrayLength());
        Assert.Equal(id, mine.GetProperty("upcoming")[0].GetProperty("id").GetGuid());

        // Somebody else's list does not contain it.
        var theirs = await (await stranger.GetAsync("/api/reservations/mine"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, theirs.GetProperty("upcoming").GetArrayLength());

        // And they cannot cancel it. Ownership is a fact about a row, not about a token, so this
        // is the service's check rather than a policy's - which is exactly why it is tested here.
        var theft = await stranger.PostAsJsonAsync($"/api/reservations/{id}/cancel", new { });

        Assert.Equal(HttpStatusCode.Forbidden, theft.StatusCode);

        var owner = await ani.PostAsJsonAsync(
            $"/api/reservations/{id}/cancel", new { reason = "plans changed" });

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        var cancelled = await owner.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((int)ReservationStatus.CancelledByDiner, cancelled.GetProperty("status").GetInt32());
    }

    [SkippableFact]
    public async Task Approving_needs_a_manager_and_a_manager_of_another_venue_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        AuthBranch elsewhere;
        Guid bigTableId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            elsewhere = await AuthTestData.CreateBranchAsync(db);

            var testBranch = new TestBranch(
                branch.VenueId, branch.BranchId, branch.WaiterId, branch.ManagerId,
                branch.TableIds, "Asia/Yerevan");

            // Ten seats, so a party of nine fits and lands over the branch's approval threshold
            // of eight without tripping the seat-overhang rule.
            bigTableId = (await TestBranchBuilder.AddTableAsync(db, testBranch, "12", seats: 10)).Id;
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var created = await diner.PostAsJsonAsync(
            "/api/reservations", NewBooking(factory, branch, tableId: bigTableId, partySize: 9));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal((int)ReservationStatus.PendingApproval, body.GetProperty("status").GetInt32());

        // The diner who made it cannot approve their own booking.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await diner.PostAsJsonAsync($"/api/reservations/{id}/approve", new { })).StatusCode);

        // Neither can a waiter: this is a decision, and ManagerOrAbove is the policy on the route.
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await waiter.PostAsJsonAsync($"/api/reservations/{id}/approve", new { })).StatusCode);

        // Nor a manager of a different venue. BranchScoped cannot guard this route - it is
        // addressed by reservation id and there is no branchId to compare against - so the service
        // resolves the booking's branch and checks it. This is the assertion that proves it does.
        using var outsider = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, elsewhere));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync($"/api/reservations/{id}/approve", new { })).StatusCode);

        // Their own manager can.
        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        var approved = await manager.PostAsJsonAsync($"/api/reservations/{id}/approve", new { });

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(
            (int)ReservationStatus.Confirmed,
            (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
    }

    [SkippableFact]
    public async Task A_lost_race_answers_409_with_the_clashing_window_and_a_fresh_floor()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var first = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var second = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        Assert.Equal(
            HttpStatusCode.Created,
            (await first.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch))).StatusCode);

        var clash = await second.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch));

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);

        var problem = await clash.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("table-already-booked", problem.GetProperty("code").GetString());

        var context = problem.GetProperty("context");

        Assert.Equal((int)ReservationRejectionReason.TableAlreadyBooked, context.GetProperty("reason").GetInt32());
        Assert.True(context.TryGetProperty("conflictingStartUtc", out _));

        // The floor as it stands now, so the app can redraw and show what changed rather than
        // firing a second request into the same contention.
        var availability = context.GetProperty("availability");

        Assert.Equal(2, availability.GetProperty("tables").GetArrayLength());
    }

    [SkippableFact]
    public async Task A_refused_booking_answers_422_with_the_rule_that_refused_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        // Six people at a four-seater. Its own code, not a generic "invalid booking".
        var refused = await diner.PostAsJsonAsync(
            "/api/reservations", NewBooking(factory, branch, partySize: 6));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("reservation-party-exceeds-capacity", problem.GetProperty("code").GetString());
        Assert.Equal(4, problem.GetProperty("context").GetProperty("seats").GetInt32());
        Assert.Equal(6, problem.GetProperty("context").GetProperty("partySize").GetInt32());
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>
    /// A slot the branch will accept: two days out at 18:00 local, comfortably past the minimum
    /// lead time, inside the booking window, and inside the seeded 10:00-23:00 opening hours.
    /// </summary>
    private static (DateOnly Date, TimeOnly Time) Slot(YallaApiFactory factory)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        return (DateOnly.FromDateTime(localNow).AddDays(2), new TimeOnly(18, 0));
    }

    private static object NewBooking(
        YallaApiFactory factory,
        AuthBranch branch,
        Guid? tableId = null,
        int partySize = 2)
    {
        var (date, time) = Slot(factory);

        return new
        {
            branchId = branch.BranchId,
            tableId = tableId ?? branch.FirstTableId,
            date = date.ToString("yyyy-MM-dd"),
            time = time.ToString("HH\\:mm"),
            partySize,
            guestName = "Ani Test",
            guestPhone = "+37411223344",
            clientCommandId = Guid.CreateVersion7(),
        };
    }

    /// <summary>
    /// A diner account, through the real sign-in flow.
    /// </summary>
    /// <remarks>
    /// The verification code comes back in the response because the host runs in Development with
    /// <c>Auth:ReturnVerificationCodeInResponse</c> on - the same affordance the diner auth tests
    /// use, rather than a second way in that only tests know about.
    /// </remarks>
    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync(
            "/api/auth/diner/request-code", new { phoneE164 = phone });

        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        Assert.False(string.IsNullOrWhiteSpace(code), "The development verification code was not returned.");

        var verified = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }
}
