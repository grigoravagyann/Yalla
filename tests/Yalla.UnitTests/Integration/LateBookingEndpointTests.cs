using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The two things that happen to a booking whose party is late, through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The diner buys five more minutes; the waiter gives up. Both are covered at service level - the
/// scheduler tests drive <c>ExtendHoldAsync</c> and <c>ReleaseAndOutOfServiceTests</c> drives
/// <c>ReleaseAsync</c> - and neither of those can see any of what these prove, because all of it
/// lives <b>above</b> the service.
/// </para>
/// <para>
/// Three things live up there and nowhere else. <b>Which policy is on the route</b>: no-show is
/// deliberately <c>WaiterOrAbove</c> and not <c>ManagerOrAbove</c>, because a hold that waits for
/// somebody senior to walk past is a hold nobody releases - and a service test takes the actor as
/// an argument, so it cannot tell which policy the endpoint asks for, or whether it asks for one.
/// <b>Which filter the group carries</b>: <c>ClientCommandIdFilter</c> is added per group, so an
/// endpoint mapped onto the wrong one loses idempotency silently. And <b>the outcome the handler
/// chooses</b>: <c>MarkNoShowAsync</c> is the one place that decides <c>ReleaseOutcome.NoShow</c>,
/// and the service receives that decision already made. Change it to <c>CancelledByVenue</c> and
/// every service test still passes while no-shows quietly stop counting against anybody.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class LateBookingEndpointTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The waiter standing in the room may mark a no-show. Nobody else may, including the diner
    /// whose booking it is and a waiter at another venue.
    /// </summary>
    [SkippableFact]
    public async Task A_waiter_marks_a_no_show_and_frees_the_table_where_nobody_else_can()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        AuthBranch elsewhere;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            elsewhere = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        await HoldTableAsync(waiter, branch);

        // The diner cannot declare their own no-show. WaiterOrAbove is the policy on the route and
        // a diner token does not satisfy it.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await diner.PostAsJsonAsync(
                $"/api/reservations/{booking}/no-show",
                new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        // Nor a waiter at another venue. The policy cannot draw this line - the route is addressed
        // by reservation id, so there is no branchId to compare a claim against - and the service
        // resolves the booking's branch itself. This is what proves it does.
        using var outsider = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, elsewhere));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync(
                $"/api/reservations/{booking}/no-show",
                new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        // Their own waiter can, once the party is late - a no-show before the start is refused.
        await RunLateAsync(factory, diner, booking);

        var commandId = Guid.CreateVersion7();
        var released = await waiter.PostAsJsonAsync(
            $"/api/reservations/{booking}/no-show", new { clientCommandId = commandId });

        Assert.Equal(HttpStatusCode.OK, released.StatusCode);

        var body = await released.Content.ReadFromJsonAsync<JsonElement>();

        // The outcome the handler chose, not one the caller supplied. Nothing below this endpoint
        // decides it, so nothing below this endpoint can catch it being the wrong one.
        Assert.Equal((int)ReleaseOutcome.NoShow, body.GetProperty("outcome").GetInt32());
        Assert.True(body.GetProperty("countsTowardNoShowThreshold").GetBoolean());

        Assert.Equal(
            (int)ReservationStatus.NoShow,
            body.GetProperty("reservation").GetProperty("status").GetInt32());

        // The table went back through the state machine rather than being written free.
        Assert.True(body.GetProperty("tableFreed").GetBoolean());
        Assert.Equal((int)TableStatus.Free, body.GetProperty("tableStatus").GetInt32());
        Assert.False(body.GetProperty("wasReplay").GetBoolean());

        // The tablet lost its connection and sent the queue again. One no-show, not two.
        var replayed = await waiter.PostAsJsonAsync(
            $"/api/reservations/{booking}/no-show", new { clientCommandId = commandId });

        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

        var replay = await replayed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(replay.GetProperty("wasReplay").GetBoolean());
        Assert.Equal((int)ReleaseOutcome.NoShow, replay.GetProperty("outcome").GetInt32());
    }

    /// <summary>
    /// The two doors onto one release agree. <c>no-show</c> is documented as "the same action as
    /// release with outcome 1, under the name the tablet's late-booking panel uses".
    /// </summary>
    /// <remarks>
    /// That sentence is the whole argument for having two routes, and until now it was prose. Each
    /// endpoint builds its own <c>ReleaseReservationCommand</c>, so the two can drift apart in the
    /// one place a service test never looks.
    /// </remarks>
    [SkippableFact]
    public async Task The_no_show_door_and_the_release_door_reach_the_same_outcome()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // One booking per table, so the two releases are not competing for the same hold - and per
        // diner, since one diner holds one table at a branch at a time.
        using var another = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var throughNoShow = await BookAsync(diner, factory, branch, branch.TableIds[0]);
        var throughRelease = await BookAsync(
            another, factory, branch, branch.TableIds[1], time: new TimeOnly(19, 0));

        await HoldTableAsync(waiter, branch, branch.TableIds[0]);
        await HoldTableAsync(waiter, branch, branch.TableIds[1]);

        // Past the later of the two starts, so both are no-shows a waiter may record.
        await RunLateAsync(factory, another, throughRelease);

        var byName = await (await waiter.PostAsJsonAsync(
                $"/api/reservations/{throughNoShow}/no-show",
                new { clientCommandId = Guid.CreateVersion7() }))
            .Content.ReadFromJsonAsync<JsonElement>();

        var byOutcome = await (await waiter.PostAsJsonAsync(
                $"/api/reservations/{throughRelease}/release",
                new { outcome = (int)ReleaseOutcome.NoShow, clientCommandId = Guid.CreateVersion7() }))
            .Content.ReadFromJsonAsync<JsonElement>();

        foreach (var field in new[] { "outcome", "tableFreed", "countsTowardNoShowThreshold", "tableStatus" })
        {
            Assert.Equal(
                byOutcome.GetProperty(field).ToString(),
                byName.GetProperty(field).ToString());
        }

        Assert.Equal(
            byOutcome.GetProperty("reservation").GetProperty("status").GetInt32(),
            byName.GetProperty("reservation").GetProperty("status").GetInt32());
    }

    /// <summary>
    /// A release with no idempotency key is refused rather than guessed at.
    /// </summary>
    /// <remarks>
    /// <c>ClientCommandIdFilter</c> is added to the floor group, not to the endpoint, so this is a
    /// statement about the group the route was mapped onto. Without it a missing key binds
    /// <c>Guid.Empty</c> - <c>[Required]</c> cannot fire on a non-nullable <c>Guid</c> - and the
    /// tablet's replayed queue would put a second no-show on a diner's record.
    /// </remarks>
    [SkippableFact]
    public async Task A_no_show_without_a_command_id_is_refused_rather_than_applied_twice()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var refused = await waiter.PostAsJsonAsync($"/api/reservations/{booking}/no-show", new { });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("invalid-request", problem.GetProperty("code").GetString());
        Assert.Contains(
            "clientCommandId",
            problem.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        // And the booking is untouched: still the diner's, still upcoming.
        var mine = await (await diner.GetAsync("/api/reservations/mine"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, mine.GetProperty("upcoming").GetArrayLength());
    }

    /// <summary>
    /// The late nudge's button: the diner who made the booking extends the hold, once, and nobody
    /// else can extend it at all.
    /// </summary>
    [SkippableFact]
    public async Task A_diner_extends_their_own_hold_once_and_a_second_attempt_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        await HoldTableAsync(waiter, branch);

        // A staff token is refused. VerifiedDiner is the policy on this group, and the button
        // belongs to the person whose table it is.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await waiter.PostAsJsonAsync(
                $"/api/reservations/{booking}/extend-hold",
                new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        // So is another diner, who has an account and does satisfy the policy. Ownership is a fact
        // about the row rather than about the token, checked in the service - and this is what
        // proves it is checked.
        using var stranger = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.PostAsJsonAsync(
                $"/api/reservations/{booking}/extend-hold",
                new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        // Dinner time, and the party is late: until the start there was nothing held to keep.
        // Every token this test uses is already issued - see RunLateAsync.
        await RunLateAsync(factory, diner, booking);

        var firstTap = Guid.CreateVersion7();
        var extended = await diner.PostAsJsonAsync(
            $"/api/reservations/{booking}/extend-hold", new { clientCommandId = firstTap });

        Assert.Equal(HttpStatusCode.OK, extended.StatusCode);

        var body = await extended.Content.ReadFromJsonAsync<JsonElement>();

        // ExtendHoldResult, not ReservationView. A generated client binds to this shape.
        Assert.Equal(booking, body.GetProperty("reservationId").GetGuid());
        Assert.False(body.GetProperty("wasReplay").GetBoolean());
        Assert.Equal(0, body.GetProperty("extensionsRemaining").GetInt32());

        var minutes = body.GetProperty("extensionMinutes").GetInt32();

        Assert.True(minutes > 0, "The branch offered no extension, so the rest of this proves nothing.");
        Assert.Equal(
            factory.Clock.UtcNow.AddMinutes(minutes),
            body.GetProperty("holdExpiresAtUtc").GetDateTime());

        // The same notification, tapped twice. Not an error: the second tap gets the first answer.
        var replayed = await diner.PostAsJsonAsync(
            $"/api/reservations/{booking}/extend-hold", new { clientCommandId = firstTap });

        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.True((await replayed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("wasReplay").GetBoolean());

        // A genuinely new attempt is refused - as a 409, not a 500 and not a silent second hold.
        var refused = await diner.PostAsJsonAsync(
            $"/api/reservations/{booking}/extend-hold",
            new { clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(
            "hold-already-extended",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // And the waiter watching that table was told, on the branch change sequence, without the
        // table itself moving. Held to Held: nothing changed, something happened.
        var changes = await (await waiter.GetAsync(
                $"/api/branches/{branch.BranchId}/tables/changes"))
            .Content.ReadFromJsonAsync<JsonElement>();

        // Held to Held is what identifies it: the hold itself was Free to Held, and this booking
        // has had nothing else happen to it.
        var extension = changes.GetProperty("changes").EnumerateArray().Single(
            c => c.GetProperty("reservationId").GetGuid() == booking
                 && c.GetProperty("fromStatus").GetInt32() == (int)TableStatus.Held);

        Assert.Equal((int)TableStatus.Held, extension.GetProperty("toStatus").GetInt32());

        // The diner is the actor, not the waiter whose token asked for the page. A log that cannot
        // say who is not an audit log.
        Assert.Equal((int)ActorType.Diner, extension.GetProperty("actorType").GetInt32());
    }

    /// <summary>
    /// Extending with no idempotency key is refused, and the refusal costs the diner nothing.
    /// </summary>
    /// <remarks>
    /// The second half is the assertion that matters, and it is not obvious why. Without the
    /// filter the missing key binds <c>Guid.Empty</c> and the request travels on;
    /// <c>ExtendHoldAsync</c> spends the booking's one extension and <b>commits it</b> before
    /// calling <c>RecordHoldExtendedAsync</c>, which is where the domain's <c>Guard.NotEmpty</c>
    /// finally throws. The caller still sees a 400 naming <c>clientCommandId</c> - so a test that
    /// stopped at the status code and the message would pass either way - while the diner has
    /// silently lost their one extension to a request that failed. The filter is what keeps that
    /// request from starting.
    /// </remarks>
    [SkippableFact]
    public async Task Extending_a_hold_without_a_command_id_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch);

        var refused = await diner.PostAsJsonAsync($"/api/reservations/{booking}/extend-hold", new { });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("invalid-request", problem.GetProperty("code").GetString());

        // The filter's own words, not merely some 400. A 400 raised further down for a different
        // reason would satisfy the status code and prove nothing about the group.
        Assert.Contains(
            "clientCommandId",
            problem.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        // Nothing was spent. A real extension still lands, which it could not do if the refused
        // request had already used the only one there is.
        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        await HoldTableAsync(waiter, branch);
        await RunLateAsync(factory, diner, booking);

        var extended = await diner.PostAsJsonAsync(
            $"/api/reservations/{booking}/extend-hold",
            new { clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.OK, extended.StatusCode);

        var body = await extended.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("wasReplay").GetBoolean());
        Assert.True(body.GetProperty("extensionMinutes").GetInt32() > 0);
    }

    /// <summary>
    /// A no-show is a statement about a start time that has passed. Before it, both doors refuse with
    /// <c>reservation-not-started</c> and nothing about the booking or its table changes; from the start
    /// itself it is the waiter's call, as it always was.
    /// </summary>
    /// <remarks>
    /// The hold the tablet places attaches to the table's next booking whatever day that is, so without
    /// this a waiter could put a no-show on a diner's record two days before they were due.
    /// </remarks>
    [SkippableFact]
    public async Task A_no_show_before_the_booking_starts_is_refused_through_either_door()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var booking = await BookAsync(diner, factory, branch);
        var startUtc = await StartOfAsync(diner, booking);

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        await HoldTableAsync(waiter, branch);

        async Task AssertRefusedAsync(string door)
        {
            object body = door == "no-show"
                ? new { clientCommandId = Guid.CreateVersion7() }
                : new { outcome = (int)ReleaseOutcome.NoShow, clientCommandId = Guid.CreateVersion7() };

            var refused = await waiter.PostAsJsonAsync($"/api/reservations/{booking}/{door}", body);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
            var context = problem.GetProperty("context");

            Assert.Equal("reservation-not-started", problem.GetProperty("code").GetString());
            Assert.Equal(booking, context.GetProperty("reservationId").GetGuid());
            Assert.Equal(startUtc, context.GetProperty("startUtc").GetDateTime());
        }

        // Two days out, through both doors.
        await AssertRefusedAsync("no-show");
        await AssertRefusedAsync("release");

        // A minute before the start is still before it.
        factory.Clock.UtcNow = startUtc.AddMinutes(-1);
        await AssertRefusedAsync("no-show");

        // Nothing moved: the booking is still the diner's and still confirmed.
        var upcoming = (await diner.GetFromJsonAsync<JsonElement>("/api/reservations/mine"))
            .GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == booking);

        Assert.Equal((int)ReservationStatus.Confirmed, upcoming.GetProperty("status").GetInt32());

        // From the start itself, the waiter's call.
        factory.Clock.UtcNow = startUtc;

        var released = await waiter.PostAsJsonAsync(
            $"/api/reservations/{booking}/no-show", new { clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(
            (int)ReservationStatus.NoShow,
            (await released.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("reservation").GetProperty("status").GetInt32());
    }

    /// <summary>
    /// Only the no-show waits for the start. Letting a booking go because the guest called, and letting
    /// a hold go, are the venue's to do at any time.
    /// </summary>
    [SkippableFact]
    public async Task Before_the_start_the_venue_may_still_let_a_booking_go_and_release_a_hold()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var another = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        var calledAhead = await BookAsync(diner, factory, branch, branch.TableIds[0]);
        await BookAsync(another, factory, branch, branch.TableIds[1], time: new TimeOnly(19, 0));

        await HoldTableAsync(waiter, branch, branch.TableIds[0]);
        await HoldTableAsync(waiter, branch, branch.TableIds[1]);

        var cancelled = await waiter.PostAsJsonAsync(
            $"/api/reservations/{calledAhead}/release",
            new { outcome = (int)ReleaseOutcome.CancelledByVenue, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var body = await cancelled.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            (int)ReservationStatus.CancelledByVenue,
            body.GetProperty("reservation").GetProperty("status").GetInt32());
        Assert.False(body.GetProperty("countsTowardNoShowThreshold").GetBoolean());
        Assert.True(body.GetProperty("tableFreed").GetBoolean());

        var letGo = await waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{branch.TableIds[1]}/release-hold",
            new { clientCommandId = Guid.CreateVersion7(), reason = "not needed yet" });

        Assert.Equal(HttpStatusCode.OK, letGo.StatusCode);
        Assert.Equal(
            (int)TableStatus.Free,
            (await letGo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("toStatus").GetInt32());
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    /// <summary>
    /// A date the branch will accept: two days out, comfortably past the minimum lead time and
    /// inside the booking window.
    /// </summary>
    private static DateOnly BookingDate(YallaApiFactory factory)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        return DateOnly.FromDateTime(localNow).AddDays(2);
    }

    /// <summary>
    /// Moves the API's clock to eleven minutes past a booking's start - when the late nudge that
    /// carries "keep my table" is about - so there is a table being held to keep.
    /// </summary>
    /// <remarks>
    /// Only once every token the test needs has been issued. Tokens are stamped with the API's clock
    /// and checked against the real one, so a token issued after this move would not be valid yet.
    /// </remarks>
    private static async Task RunLateAsync(YallaApiFactory factory, HttpClient diner, Guid booking)
    {
        var mine = await diner.GetFromJsonAsync<JsonElement>("/api/reservations/mine");

        var startUtc = mine.GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == booking)
            .GetProperty("startUtc").GetDateTime();

        factory.Clock.UtcNow = startUtc.AddMinutes(11);
    }

    /// <summary>When a booking starts, as the diner's own list says.</summary>
    private static async Task<DateTime> StartOfAsync(HttpClient diner, Guid booking) =>
        (await diner.GetFromJsonAsync<JsonElement>("/api/reservations/mine"))
            .GetProperty("upcoming").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == booking)
            .GetProperty("startUtc").GetDateTime();

    /// <summary>Books a table through the real endpoint and answers with the booking's id.</summary>
    private static async Task<Guid> BookAsync(
        HttpClient diner,
        YallaApiFactory factory,
        AuthBranch branch,
        Guid? tableId = null,
        TimeOnly? time = null)
    {
        var response = await diner.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                branchId = branch.BranchId,
                tableId = tableId ?? branch.FirstTableId,
                date = BookingDate(factory).ToString("yyyy-MM-dd"),
                time = (time ?? new TimeOnly(18, 0)).ToString("HH\\:mm"),
                partySize = 2,
                guestName = "Ani Test",
                guestPhone = "+37411223344",
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Holds the table the way the tablet does, and checks that it took.</summary>
    private static async Task HoldTableAsync(
        HttpClient waiter,
        AuthBranch branch,
        Guid? tableId = null)
    {
        var response = await waiter.PostAsJsonAsync(
            $"/api/branches/{branch.BranchId}/tables/{tableId ?? branch.FirstTableId}/hold",
            new { clientCommandId = Guid.CreateVersion7(), reason = "held for a late booking" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((int)TableStatus.Held, body.GetProperty("toStatus").GetInt32());
    }

    /// <summary>A diner account, through the real sign-in flow.</summary>
    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync(
            "/api/auth/diner/request-code", new { phoneE164 = phone });

        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }
}
