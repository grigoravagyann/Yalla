using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    public async Task A_tab_participant_is_a_diner_but_still_cannot_book()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        // The distinction PrincipalTypeHandler exists for. Somebody who scanned a QR code at a
        // table is ActorType.Diner in the audit sense, so a check on the actor type would let them
        // through - but they have no DinerUser row, so there would be nothing to list the booking
        // under, nothing to telephone when they are late, and nothing to count a no-show against.
        await using var factory = NewFactory();

        AuthBranch branch;
        AuthTab tab;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            tab = await AuthTestData.CreateOpenTabAsync(
                db, branch, branch.FirstTableId, factory.Clock.UtcNow);
        }

        using var client = factory.CreateClient();

        var joined = await client.PostAsJsonAsync(
            "/api/tabs/join", new { joinToken = tab.JoinToken, displayName = "Ani", deviceId = "device-1" });

        joined.EnsureSuccessStatusCode();

        var token = (await joined.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetProperty("accessToken").GetString()!;

        using var participant = factory.CreateClientWithToken(token);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await participant.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch))).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await participant.GetAsync("/api/reservations/mine")).StatusCode);

        // And they get nowhere near the staff decisions either.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await participant.PostAsJsonAsync(
                $"/api/reservations/{Guid.CreateVersion7()}/approve", new { })).StatusCode);
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
    public async Task Another_diners_command_id_is_a_collision_and_never_hands_back_their_booking()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        // The idempotency key is unique across the whole table, but a replay is the SAME diner
        // sending the same command again. Answering a second diner with the booking that id already
        // names would hand a stranger the door code, the guest's name and their phone number.
        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var ani = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var stranger = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var commandId = Guid.CreateVersion7();
        var booking = NewBooking(factory, branch, commandId: commandId);

        var created = await ani.PostAsJsonAsync("/api/reservations", booking);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var aniBooking = await created.Content.ReadFromJsonAsync<JsonElement>();

        // Ani's own retry still works, because it is genuinely hers.
        var retry = await ani.PostAsJsonAsync("/api/reservations", booking);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True((await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("wasReplay").GetBoolean());

        // The stranger reusing it gets a plain collision - a different table, so nothing else could
        // refuse it - and learns nothing about Ani's booking.
        var collision = await stranger.PostAsJsonAsync(
            "/api/reservations",
            NewBooking(factory, branch, tableId: branch.TableIds[1], commandId: commandId));

        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);

        var problem = await collision.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("client-command-id-in-use", problem.GetProperty("code").GetString());

        var raw = problem.ToString();

        Assert.DoesNotContain(aniBooking.GetProperty("code").GetString()!, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Ani Test", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("+37411223344", raw, StringComparison.Ordinal);
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
    public async Task Approving_needs_staff_of_the_branch_and_staff_of_another_venue_are_refused()
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

        // Nor a waiter of a different venue. WaiterOrAbove is the policy on the route - a short-notice
        // booking is the floor's to decide, so the branch's own waiter may (ShortNoticeApprovalEndpointTests)
        // - and it says nothing about which branch, so this is the service's check refusing them.
        using var otherWaiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, elsewhere));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await otherWaiter.PostAsJsonAsync($"/api/reservations/{id}/approve", new { })).StatusCode);

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

        // The same table at the same start: refused as just booked - the table's evening's own reason,
        // under the same code (TableSequencing).
        Assert.Equal((int)ReservationRejectionReason.TableJustBooked, context.GetProperty("reason").GetInt32());
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

    /// <summary>
    /// Declining is the other half of the same decision, for the same people, and the reason they
    /// type reaches the booking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Approve is tested above; reject is a separate mapping and can lose any of its refusals on
    /// its own. Both are <c>WaiterOrAbove</c> since short-notice bookings became the floor's to
    /// decide, and <c>DecideAsync</c> confines the caller to the booking's branch from the stored
    /// staff row - so another venue's waiter and manager are refused by the service, which is what
    /// these 403s prove. The branch's own waiter declining is in
    /// <c>ShortNoticeApprovalEndpointTests</c>.
    /// </para>
    /// <para>
    /// The <c>reason</c> assertion is the part no service test can make. The service is handed a
    /// <c>DecideReservationCommand</c> already built; whether the field on the wire reaches it is a
    /// question about model binding, and a body field that binds to nothing passes every test that
    /// does not read it back out.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Rejecting_needs_staff_of_this_branch_and_the_reason_reaches_the_booking()
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

            bigTableId = (await TestBranchBuilder.AddTableAsync(db, testBranch, "12", seats: 10)).Id;
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var created = await diner.PostAsJsonAsync(
            "/api/reservations", NewBooking(factory, branch, tableId: bigTableId, partySize: 9));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Not the diner, not a waiter of somewhere else, and not a manager of somewhere else - the
        // same three refusals as approve, because this route is mapped on its own and could have
        // lost any of them without a service test noticing.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await diner.PostAsJsonAsync($"/api/reservations/{id}/reject", new { })).StatusCode);

        using var otherWaiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, elsewhere));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await otherWaiter.PostAsJsonAsync($"/api/reservations/{id}/reject", new { })).StatusCode);

        using var outsider = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, elsewhere));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync($"/api/reservations/{id}/reject", new { })).StatusCode);

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, branch));

        // A reason over the declared 500 is refused before any of that runs. The attribute is
        // written on a positional record parameter, where a stock validator would never find it.
        var tooLong = await manager.PostAsJsonAsync(
            $"/api/reservations/{id}/reject", new { reason = new string('x', 501) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);

        var violation = await tooLong.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("validation-failed", violation.GetProperty("code").GetString());
        Assert.Equal("reason", violation.GetProperty("context").GetProperty("field").GetString());

        const string Reason = "The kitchen is closed for a private event that evening.";

        var rejected = await manager.PostAsJsonAsync(
            $"/api/reservations/{id}/reject", new { reason = Reason });

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var body = await rejected.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((int)ReservationStatus.CancelledByVenue, body.GetProperty("status").GetInt32());
        Assert.Equal(Reason, body.GetProperty("cancellationReason").GetString());

        // Only a pending booking can be declined, and the second attempt says so as a 409 rather
        // than quietly cancelling an already-cancelled booking again.
        var again = await manager.PostAsJsonAsync(
            $"/api/reservations/{id}/reject", new { reason = Reason });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            "conflicting-state",
            (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>
    /// Every booking read names the room its table is in - the create response, the diner's list and
    /// the staff queue - and a table taken out of its room takes the room off its bookings.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_names_the_room_its_table_is_in_on_every_read()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        Guid bigTableId;
        Guid roomId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);

            var testBranch = new TestBranch(
                branch.VenueId, branch.BranchId, branch.WaiterId, branch.ManagerId,
                branch.TableIds, "Asia/Yerevan");

            bigTableId = (await TestBranchBuilder.AddTableAsync(db, testBranch, "12", seats: 10)).Id;
            roomId = await db.FloorAreas.Where(a => a.BranchId == branch.BranchId).Select(a => a.Id).SingleAsync();
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        // Nine people, so it also waits in the staff queue.
        var created = await diner.PostAsJsonAsync(
            "/api/reservations", NewBooking(factory, branch, tableId: bigTableId, partySize: 9));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal(roomId, body.GetProperty("roomId").GetGuid());
        Assert.Equal("Windows", body.GetProperty("roomName").GetString());

        var mine = await diner.GetFromJsonAsync<JsonElement>("/api/reservations/mine");
        var listed = Assert.Single(mine.GetProperty("upcoming").EnumerateArray());

        Assert.Equal(roomId, listed.GetProperty("roomId").GetGuid());
        Assert.Equal("Windows", listed.GetProperty("roomName").GetString());

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        var queue = await manager.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{branch.BranchId}/reservations?status={(int)ReservationStatus.PendingApproval}");
        var queued = Assert.Single(queue.EnumerateArray());

        Assert.Equal(id, queued.GetProperty("id").GetGuid());
        Assert.Equal("Windows", queued.GetProperty("roomName").GetString());

        // Read live from the table: one moved out of its room takes the room off its bookings, and
        // the fields are absent rather than null.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var table = await db.DiningTables.SingleAsync(t => t.Id == bigTableId);
            table.AssignToArea(null);
            await db.SaveChangesAsync();
        }

        var after = Assert.Single(
            (await diner.GetFromJsonAsync<JsonElement>("/api/reservations/mine")).GetProperty("upcoming").EnumerateArray());

        Assert.False(after.TryGetProperty("roomId", out _));
        Assert.False(after.TryGetProperty("roomName", out _));
    }

    /// <summary>
    /// The time picker's read: anonymous, on the app route and its public twin, striking the times the
    /// room is full - and freeing the diner's own table when they say which booking is theirs.
    /// </summary>
    [SkippableFact]
    public async Task The_day_slots_are_anonymous_and_strike_the_times_the_room_is_full()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        var (date, _) = Slot(factory);
        var day = date.ToString("yyyy-MM-dd");
        using var anonymous = factory.CreateClient();

        var empty = await anonymous.GetAsync($"/api/branches/{branch.BranchId}/availability/slots?date={day}&partySize=2");

        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);

        var answer = await empty.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(branch.BranchId, answer.GetProperty("branchId").GetGuid());
        Assert.Equal(day, answer.GetProperty("date").GetString());
        Assert.Equal(2, answer.GetProperty("partySize").GetInt32());
        Assert.Equal(15, answer.GetProperty("stepMinutes").GetInt32());
        Assert.False(answer.TryGetProperty("unavailableReason", out _), "a day with free times has no day-level reason");

        var first = answer.GetProperty("slots")[0];

        Assert.Equal("10:00", first.GetProperty("time").GetString());
        Assert.True(first.GetProperty("isAvailable").GetBoolean());
        Assert.False(first.GetProperty("requiresApproval").GetBoolean());
        Assert.Equal(2, first.GetProperty("freeTableCount").GetInt32());
        Assert.False(first.TryGetProperty("unavailableReason", out _));
        Assert.Equal(DateTimeKind.Utc, first.GetProperty("startUtc").GetDateTime().Kind);

        // Both tables taken at six.
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var mineId = (await (await diner.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch)))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.Created,
            (await diner.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch, tableId: branch.TableIds[1]))).StatusCode);

        var full = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{branch.BranchId}/availability/slots?date={day}&partySize=2");
        var six = SlotAt(full, "18:00");

        Assert.False(six.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(0, six.GetProperty("freeTableCount").GetInt32());
        Assert.Equal((int)ReservationRejectionReason.TableJustBooked, six.GetProperty("unavailableReason").GetInt32());

        // The diner changing their own booking sees their table free, on both reads.
        var mine = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{branch.BranchId}/availability/slots?date={day}&partySize=2&excludeReservationId={mineId}");

        Assert.True(SlotAt(mine, "18:00").GetProperty("isAvailable").GetBoolean());
        Assert.Equal(1, SlotAt(mine, "18:00").GetProperty("freeTableCount").GetInt32());

        var room = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{branch.BranchId}/availability?date={day}&time=18:00&partySize=2&excludeReservationId={mineId}");
        var tables = room.GetProperty("tables").EnumerateArray().ToList();

        Assert.True(tables.Single(t => t.GetProperty("tableId").GetGuid() == branch.FirstTableId).GetProperty("isAvailable").GetBoolean());
        Assert.False(tables.Single(t => t.GetProperty("tableId").GetGuid() == branch.TableIds[1]).GetProperty("isAvailable").GetBoolean());

        // The public twin: the same answer behind the public gate.
        var publicDay = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/public/branches/{branch.BranchId}/availability/slots?date={day}&partySize=2");

        Assert.Equal(full.GetProperty("slots").ToString(), publicDay.GetProperty("slots").ToString());

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/public/branches/{Guid.NewGuid()}/availability/slots?date={day}&partySize=2")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/branches/{Guid.NewGuid()}/availability/slots?date={day}")).StatusCode);

        // A day is the question: without one there is nothing to answer.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await anonymous.GetAsync($"/api/branches/{branch.BranchId}/availability/slots?partySize=2")).StatusCode);
    }

    /// <summary>
    /// Changing a booking through the real pipeline: the policy, the body's binding and the 404, 409
    /// and 422 answers - which no service test can see.
    /// </summary>
    [SkippableFact]
    public async Task A_diner_changes_their_booking_and_keeps_its_code()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var stranger = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var created = await (await diner.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var (date, _) = Slot(factory);
        var change = new
        {
            date = date.ToString("yyyy-MM-dd"),
            time = "20:00",
            partySize = 3,
            diningTableId = branch.TableIds[1],
            clientCommandId = Guid.CreateVersion7(),
        };

        // Not theirs: the same answer as a booking that does not exist.
        var theft = await stranger.PostAsJsonAsync($"/api/reservations/{id}/reschedule", change);

        Assert.Equal(HttpStatusCode.NotFound, theft.StatusCode);
        Assert.Equal("not-found", (await theft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var moved = await diner.PostAsJsonAsync($"/api/reservations/{id}/reschedule", change);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var body = await moved.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(created.GetProperty("code").GetString(), body.GetProperty("code").GetString());
        Assert.Equal(branch.TableIds[1], body.GetProperty("tableId").GetGuid());
        Assert.Equal("20:00:00", body.GetProperty("localStartTime").GetString());
        Assert.Equal(3, body.GetProperty("partySize").GetInt32());
        Assert.Equal((int)ReservationStatus.Confirmed, body.GetProperty("status").GetInt32());
        Assert.Equal("Windows", body.GetProperty("roomName").GetString());
        Assert.False(body.GetProperty("wasReplay").GetBoolean());
        Assert.False(body.TryGetProperty("manageToken", out _));

        // The phone retries: the same booking, moved once.
        var retry = await diner.PostAsJsonAsync($"/api/reservations/{id}/reschedule", change);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True((await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("wasReplay").GetBoolean());

        // The table is required, as on a new booking, and named when it is missing.
        var noTable = await diner.PostAsJsonAsync(
            $"/api/reservations/{id}/reschedule",
            new { date = change.date, time = "20:30", partySize = 2, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noTable.StatusCode);

        var violation = await noTable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation-failed", violation.GetProperty("code").GetString());
        Assert.Equal("diningTableId", violation.GetProperty("context").GetProperty("field").GetString());

        // A branch rule answers with its own code, as for a new booking.
        var tooMany = await diner.PostAsJsonAsync(
            $"/api/reservations/{id}/reschedule", change with { partySize = 6, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Equal(
            "reservation-party-exceeds-capacity",
            (await tooMany.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Somebody else's booking in the way is the 409 a new booking gets.
        Assert.Equal(
            HttpStatusCode.Created,
            (await stranger.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch))).StatusCode);

        var taken = await diner.PostAsJsonAsync(
            $"/api/reservations/{id}/reschedule",
            change with { time = "18:00", diningTableId = branch.FirstTableId, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("table-already-booked", (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Once cancelled, it does not move.
        Assert.Equal(HttpStatusCode.OK, (await diner.PostAsJsonAsync($"/api/reservations/{id}/cancel", new { })).StatusCode);

        var cancelled = await diner.PostAsJsonAsync(
            $"/api/reservations/{id}/reschedule", change with { time = "21:00", clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);

        var problem = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reservation-not-changeable", problem.GetProperty("code").GetString());
        Assert.Equal((int)ReservationStatus.CancelledByDiner, problem.GetProperty("context").GetProperty("status").GetInt32());
        Assert.Equal(id, problem.GetProperty("context").GetProperty("reservationId").GetGuid());
    }

    // ---------------------------------------------------------------- helpers

    private static JsonElement SlotAt(JsonElement day, string time) =>
        day.GetProperty("slots").EnumerateArray().Single(s => s.GetProperty("time").GetString() == time);

    /// <summary>
    /// The free-cancellation deadline is stated before booking and on the booking, as an instant.
    /// </summary>
    /// <remarks>
    /// Neither the availability answer nor the booking carried it, so the app promised free
    /// cancellation until the slot itself - while the server records a cancellation as late from
    /// <c>cancellationDeadlineMinutes</c> before the start. A diner who cancelled when the app said
    /// they still could was written down as a late canceller.
    /// </remarks>
    [SkippableFact]
    public async Task The_free_cancellation_deadline_is_stated_before_booking_and_on_the_booking()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        int deadlineMinutes;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);

            deadlineMinutes = await db.Branches.AsNoTracking()
                .Where(b => b.Id == branch.BranchId)
                .Select(b => b.ReservationPolicy.CancellationDeadlineMinutes)
                .FirstAsync();
        }

        Assert.True(deadlineMinutes > 0, "With no deadline the deadline is the start, and this proves nothing.");

        var (date, time) = Slot(factory);
        using var anonymous = factory.CreateClient();

        var availability = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{branch.BranchId}/availability?date={date.ToString("yyyy-MM-dd")}"
            + $"&time={time.ToString("HH\\:mm")}&partySize=2");

        var requestedStartUtc = availability.GetProperty("requestedStartUtc").GetDateTime();

        Assert.Equal(
            requestedStartUtc.AddMinutes(-deadlineMinutes),
            availability.GetProperty("cancellationDeadlineUtc").GetDateTime());
        Assert.Equal(deadlineMinutes, availability.GetProperty("cancellationDeadlineMinutes").GetInt32());

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var created = await diner.PostAsJsonAsync("/api/reservations", NewBooking(factory, branch));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var booking = await created.Content.ReadFromJsonAsync<JsonElement>();
        var deadline = booking.GetProperty("cancellationDeadlineUtc").GetDateTime();

        Assert.Equal(booking.GetProperty("startUtc").GetDateTime().AddMinutes(-deadlineMinutes), deadline);

        // And every later read of the booking says the same.
        var mine = await diner.GetFromJsonAsync<JsonElement>("/api/reservations/mine");
        var listed = Assert.Single(mine.GetProperty("upcoming").EnumerateArray());

        Assert.Equal(deadline, listed.GetProperty("cancellationDeadlineUtc").GetDateTime());
    }

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
        int partySize = 2,
        Guid? commandId = null)
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
            clientCommandId = commandId ?? Guid.CreateVersion7(),
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
