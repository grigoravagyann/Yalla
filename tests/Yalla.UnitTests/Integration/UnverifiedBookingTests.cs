using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Booking without phone proof (decided 2026-10-20), and one active booking per phone number for everybody.
/// </summary>
/// <remarks>
/// <para>
/// The public page's door for a visitor who will not verify (<c>POST /api/public/branches/{id}/bookings</c>),
/// and the app's for an account whose number was never proved (<c>POST /api/reservations</c>, which used to
/// answer 403). Both are flagged for staff, capped, and wait for the venue when the branch says so.
/// </para>
/// <para>
/// The number rule is everybody's: a number holding a pending or confirmed booking that is not over books
/// nothing else, at any venue, until that one is cancelled or finished.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class UnverifiedBookingTests(SqlServerFixture fixture)
{
    // ---------------------------------------------------------------- the web door

    /// <summary>
    /// A web visitor books with a number and a device id: the booking is flagged, waits for the venue by
    /// default (trigger 6), carries a manage link, sends no reminder, and staff read the flag.
    /// </summary>
    [SkippableFact]
    public async Task A_web_visitor_books_without_verifying_and_the_booking_is_flagged_and_waits_for_the_venue()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        var digits = UniquePhone()[4..];
        var created = await anonymous.PostAsJsonAsync(
            PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: $"+374 {digits[..2]} {digits[2..4]}-{digits[4..6]}-{digits[6..]}"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("phoneUnverified").GetBoolean());
        Assert.Equal(ReservationStatus.PendingApproval, (ReservationStatus)body.GetProperty("status").GetInt32());
        Assert.Equal(ApprovalTrigger.UnverifiedPhone, (ApprovalTrigger)body.GetProperty("awaitingApprovalBecause").GetInt32());

        // A day at most for the venue's answer (review S3).
        Assert.True(body.TryGetProperty("approvalExpiresAtUtc", out _));

        // The number the venue calls, in one form.
        Assert.Equal("+374" + digits, body.GetProperty("guestPhone").GetString());

        var token = body.GetProperty("manageToken").GetString()!;
        var id = body.GetProperty("id").GetGuid();

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id);

            Assert.Null(row.DinerUserId);
            Assert.Equal(ReservationChannel.Web, row.Channel);
            Assert.Equal("+374" + digits, row.BookerPhoneE164);
            Assert.NotNull(row.BookerDeviceHash);
            Assert.DoesNotContain("web-device", row.BookerDeviceHash, StringComparison.Ordinal);

            // Nobody to remind, and no channel may message a number nobody proved.
            Assert.Equal(0, await db.OutboxMessages.CountAsync(m => m.IdempotencyKey.Contains(id.ToString())));
        }

        // The manage link says so, and offers no change.
        var link = await ReadAsync(anonymous, $"/api/public/bookings/{token}");

        Assert.True(link.GetProperty("phoneUnverified").GetBoolean());
        Assert.False(link.GetProperty("canChange").GetBoolean());
        Assert.True(link.GetProperty("canCancel").GetBoolean());

        // Staff read the flag on the floor's list, with the reason.
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        var awaiting = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/reservations/awaiting");
        var card = awaiting.EnumerateArray().Single(c => c.GetProperty("reservation").GetProperty("id").GetGuid() == id);

        Assert.True(card.GetProperty("reservation").GetProperty("phoneUnverified").GetBoolean());
        Assert.Equal(ApprovalTrigger.UnverifiedPhone, (ApprovalTrigger)card.GetProperty("trigger").GetInt32());

        // And beside the table's next booking on the floor.
        var floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        var floorRow = floor.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("tableId").GetGuid() == branch.FirstTableId);
        Assert.Equal(id, floorRow.GetProperty("nextReservationId").GetGuid());
        Assert.True(floorRow.GetProperty("nextReservationPhoneUnverified").GetBoolean());

        // And the waiter approves it with the existing machinery.
        var approved = await waiter.PostAsJsonAsync($"/api/reservations/{id}/approve", new { });

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(
            ReservationStatus.Confirmed,
            (ReservationStatus)(await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
    }

    /// <summary>A branch that takes such bookings on trust confirms them, still flagged.</summary>
    [SkippableFact]
    public async Task A_branch_that_does_not_approve_unverified_bookings_first_confirms_them_still_flagged()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory, unverifiedNeedApproval: false);
        using var anonymous = factory.CreateClient();

        var created = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(ReservationStatus.Confirmed, (ReservationStatus)body.GetProperty("status").GetInt32());
        Assert.True(body.GetProperty("phoneUnverified").GetBoolean());
    }

    /// <summary>
    /// The party cap: over <c>unverifiedMaxPartySize</c> is a 422 naming the cap, and nothing is written.
    /// </summary>
    [SkippableFact]
    public async Task An_unverified_booking_over_the_party_cap_is_refused_naming_the_cap()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory, unverifiedMaxPartySize: 2);
        using var anonymous = factory.CreateClient();

        var refused = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch, partySize: 3));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("reservation-unverified-party-too-large", problem.GetProperty("code").GetString());
        Assert.Equal(2, problem.GetProperty("context").GetProperty("maxPartySize").GetInt32());
        Assert.Equal(3, problem.GetProperty("context").GetProperty("partySize").GetInt32());

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await db.Reservations.CountAsync(r => r.BranchId == branch.BranchId));
    }

    /// <summary>
    /// The web door is the web's: off while the branch does not take web bookings, and a number that is not
    /// one is a 400 naming the field.
    /// </summary>
    [SkippableFact]
    public async Task The_web_door_needs_web_bookings_on_a_real_number_and_a_device()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory, acceptsWebBookings: false);
        using var anonymous = factory.CreateClient();

        var off = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch));

        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Equal("web-bookings-not-accepted", (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await SetPolicyAsync(factory, branch.BranchId, acceptsWebBookings: true);

        var notANumber = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: "call me"));

        Assert.Equal(HttpStatusCode.BadRequest, notANumber.StatusCode);
        Assert.Equal(
            "guestPhone",
            (await notANumber.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("field").GetString());
    }

    /// <summary>
    /// A retry from the same browser answers with the booking and no token; the same id from another browser
    /// is a collision, not a way to read somebody's booking.
    /// </summary>
    [SkippableFact]
    public async Task A_retry_from_the_same_browser_replays_and_another_browser_reusing_the_id_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        var commandId = Guid.CreateVersion7();
        var browserA = $"browser-a-{Guid.NewGuid():N}";
        var phoneA = UniquePhone();

        var first = await anonymous.PostAsJsonAsync(
            PublicBookingUrl(branch), NewWebBooking(factory, branch, commandId: commandId, device: browserA, phone: phoneA));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await first.Content.ReadFromJsonAsync<JsonElement>();

        var retry = await anonymous.PostAsJsonAsync(
            PublicBookingUrl(branch), NewWebBooking(factory, branch, commandId: commandId, device: browserA, phone: phoneA));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var replayed = await retry.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(original.GetProperty("id").GetGuid(), replayed.GetProperty("id").GetGuid());
        Assert.True(replayed.GetProperty("wasReplay").GetBoolean());
        Assert.False(replayed.TryGetProperty("manageToken", out _), "The token is returned once.");

        var stranger = await anonymous.PostAsJsonAsync(
            PublicBookingUrl(branch),
            // Another slot, so the table is free and the id itself is what refuses it.
            NewWebBooking(factory, branch, commandId: commandId, device: "browser-b", time: "20:00"));

        Assert.Equal(HttpStatusCode.Conflict, stranger.StatusCode);

        var problem = await stranger.Content.ReadAsStringAsync();

        Assert.Contains("client-command-id-in-use", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(original.GetProperty("code").GetString()!, problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The manage link reads and cancels an unverified booking, never changes it - and cancelling frees the
    /// number at once.
    /// </summary>
    [SkippableFact]
    public async Task The_manage_link_cancels_but_never_changes_an_unverified_booking_and_cancelling_frees_the_number()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        var phone = UniquePhone();
        var created = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: phone));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("manageToken").GetString()!;

        var moved = await anonymous.PostAsJsonAsync(
            $"/api/public/bookings/{token}/reschedule",
            new
            {
                date = body.GetProperty("localDate").GetString(),
                time = "19:00",
                partySize = 2,
                diningTableId = branch.FirstTableId,
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Forbidden, moved.StatusCode);
        Assert.Equal("phone-not-verified", (await moved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The same number again, from another browser: the first booking holds it.
        var second = await anonymous.PostAsJsonAsync(
            PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: phone, time: "20:00"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("phone-has-active-booking", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var cancelled = await anonymous.PostAsJsonAsync($"/api/public/bookings/{token}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var again = await anonymous.PostAsJsonAsync(
            PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: phone, time: "20:00"));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    /// <summary>One active unverified booking per browser, whatever number it types.</summary>
    [SkippableFact]
    public async Task One_browser_holds_one_active_unverified_booking_whatever_number_it_types()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        var sameBrowser = $"same-browser-{Guid.NewGuid():N}";
        var first = await anonymous.PostAsJsonAsync(
            PublicBookingUrl(branch), NewWebBooking(factory, branch, device: sameBrowser));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await anonymous.PostAsJsonAsync(
            PublicBookingUrl(branch),
            NewWebBooking(factory, branch, device: sameBrowser, time: "20:00"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("device-has-active-booking", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("context", out _));
    }

    // ---------------------------------------------------------------- one number, one active booking

    /// <summary>
    /// A verified diner with a booking cannot make a second anywhere - told which one they hold, because it is
    /// theirs - and a web visitor typing that diner's number afterwards is refused with nothing about it: an
    /// unverified booking is held back by any hold on the number. The other way round - a stranger's
    /// unverified hold first - never stops the owner (review B1, the test after the race).
    /// </summary>
    [SkippableFact]
    public async Task One_number_holds_one_active_booking_named_only_to_its_own_account()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        var other = await ArrangeAsync(factory);

        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        var first = await diner.PostAsJsonAsync("/api/reservations", NewAppBooking(factory, branch));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var held = await first.Content.ReadFromJsonAsync<JsonElement>();

        // A verified booker keeps today's behaviour: confirmed per policy, not flagged.
        Assert.Equal(ReservationStatus.Confirmed, (ReservationStatus)held.GetProperty("status").GetInt32());
        Assert.False(held.GetProperty("phoneUnverified").GetBoolean());

        // Another venue, another day: still the same number.
        var second = await diner.PostAsJsonAsync("/api/reservations", NewAppBooking(factory, other, daysAhead: 3));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("phone-has-active-booking", problem.GetProperty("code").GetString());

        var context = problem.GetProperty("context");

        Assert.Equal(held.GetProperty("id").GetGuid(), context.GetProperty("reservationId").GetGuid());
        Assert.Equal(held.GetProperty("code").GetString(), context.GetProperty("code").GetString());
        Assert.Equal(held.GetProperty("localDate").GetString(), context.GetProperty("localDate").GetString());
        Assert.True(context.TryGetProperty("venueName", out _));
        Assert.True(context.TryGetProperty("branchName", out _));

        // A stranger typing the number on the web learns that it has a booking, and nothing more.
        using var anonymous = factory.CreateClient();
        var stranger = await anonymous.PostAsJsonAsync(PublicBookingUrl(other), NewWebBooking(factory, other, phone: phone));

        Assert.Equal(HttpStatusCode.Conflict, stranger.StatusCode);

        var strangerProblem = await stranger.Content.ReadAsStringAsync();

        Assert.Contains("phone-has-active-booking", strangerProblem, StringComparison.Ordinal);
        Assert.DoesNotContain("\"context\"", strangerProblem, StringComparison.Ordinal);
        Assert.DoesNotContain(held.GetProperty("code").GetString()!, strangerProblem, StringComparison.Ordinal);

        // Moving the booking the number holds is never refused by the rule.
        var moved = await diner.PostAsJsonAsync(
            $"/api/reservations/{held.GetProperty("id").GetGuid()}/reschedule",
            new
            {
                date = held.GetProperty("localDate").GetString(),
                time = "19:00",
                partySize = 2,
                diningTableId = branch.FirstTableId,
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
    }

    /// <summary>
    /// Two bookings for one number sent at the same instant, on two tables at two venues: exactly one is
    /// written. The application lock on the number is what serialises them.
    /// </summary>
    [SkippableFact]
    public async Task Two_bookings_for_one_number_at_the_same_moment_write_exactly_one()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var first = await ArrangeAsync(factory);
        var second = await ArrangeAsync(factory);
        using var a = factory.CreateClient();
        using var b = factory.CreateClient();

        var phone = UniquePhone();

        var results = await Task.WhenAll(
            a.PostAsJsonAsync(PublicBookingUrl(first), NewWebBooking(factory, first, phone: phone, device: "racer-a")),
            b.PostAsJsonAsync(PublicBookingUrl(second), NewWebBooking(factory, second, phone: phone, device: "racer-b")));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await db.Reservations.CountAsync(r => r.BookerPhoneE164 == phone));
    }

    // ---------------------------------------------------------------- the app door

    /// <summary>
    /// An app account whose number was never proved books - flagged, waiting for the venue - instead of the
    /// 403 it used to get; moving it still asks for the proof.
    /// </summary>
    [SkippableFact]
    public async Task An_app_account_with_an_unproved_number_books_unverified_and_cannot_move_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await RegisterUnprovedDinerAsync(factory));

        var created = await diner.PostAsJsonAsync("/api/reservations", NewAppBooking(factory, branch));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("phoneUnverified").GetBoolean());
        Assert.Equal(ApprovalTrigger.UnverifiedPhone, (ApprovalTrigger)body.GetProperty("awaitingApprovalBecause").GetInt32());

        var moved = await diner.PostAsJsonAsync(
            $"/api/reservations/{body.GetProperty("id").GetGuid()}/reschedule",
            new
            {
                date = body.GetProperty("localDate").GetString(),
                time = "19:00",
                partySize = 2,
                diningTableId = branch.FirstTableId,
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Forbidden, moved.StatusCode);

        // The app's own list carries the flag.
        var mine = await ReadAsync(diner, "/api/reservations/mine");
        Assert.True(mine.GetProperty("upcoming")[0].GetProperty("phoneUnverified").GetBoolean());
    }

    /// <summary>
    /// The two policy settings are read, written, bounded and kept when an older console leaves them out.
    /// </summary>
    [SkippableFact]
    public async Task The_unverified_settings_round_trip_through_the_policy_and_are_kept_when_left_out()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));

        var policyUrl = $"/api/branches/{branch.BranchId}/reservation-policy";
        var current = await ReadAsync(manager, policyUrl);

        Assert.True(current.GetProperty("unverifiedBookingsNeedApproval").GetBoolean());
        Assert.Equal(6, current.GetProperty("unverifiedMaxPartySize").GetInt32());

        var saved = await manager.PutAsJsonAsync(policyUrl, PolicyBody(current, needApproval: false, maxParty: 4));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // An older console sends neither: both stay as they are.
        var older = await manager.PutAsJsonAsync(policyUrl, PolicyBody(current, needApproval: null, maxParty: null));
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);

        var after = await ReadAsync(manager, policyUrl);
        Assert.False(after.GetProperty("unverifiedBookingsNeedApproval").GetBoolean());
        Assert.Equal(4, after.GetProperty("unverifiedMaxPartySize").GetInt32());

        var tooSmall = await manager.PutAsJsonAsync(policyUrl, PolicyBody(current, needApproval: true, maxParty: 0));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSmall.StatusCode);
        Assert.Contains("unverifiedMaxPartySize", await tooSmall.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- after review (2026-10-20)

    /// <summary>
    /// Review B1: a stranger's unverified booking with a proved person's number never stops that person from
    /// booking. The owner sees it among their bookings, flagged as made with their number and not by them, and
    /// cancels it with the usual cancel; nobody else's account can.
    /// </summary>
    [SkippableFact]
    public async Task A_strangers_unverified_booking_with_my_number_never_stops_me_and_I_can_cancel_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        var other = await ArrangeAsync(factory);

        var (token, phone) = await SignInVerifiedDinerAsync(factory);

        // The prank: the public page, the owner's number, a browser of its own.
        using var anonymous = factory.CreateClient();
        var prank = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: phone));
        Assert.Equal(HttpStatusCode.Created, prank.StatusCode);
        var prankId = (await prank.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The owner books elsewhere as if it were not there.
        using var diner = factory.CreateClientWithToken(token);
        var own = await diner.PostAsJsonAsync("/api/reservations", NewAppBooking(factory, other, daysAhead: 3));
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        var ownId = (await own.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // And sees it on their list, flagged; their own booking is not.
        var upcoming = (await ReadAsync(diner, "/api/reservations/mine")).GetProperty("upcoming").EnumerateArray().ToList();

        var flagged = upcoming.Single(r => r.GetProperty("id").GetGuid() == prankId);
        Assert.True(flagged.GetProperty("madeWithYourNumber").GetBoolean());
        Assert.True(flagged.GetProperty("phoneUnverified").GetBoolean());
        Assert.False(upcoming.Single(r => r.GetProperty("id").GetGuid() == ownId).GetProperty("madeWithYourNumber").GetBoolean());

        // Another proved account cannot cancel it: the number is not theirs.
        var (strangerToken, _) = await SignInVerifiedDinerAsync(factory);
        using var stranger = factory.CreateClientWithToken(strangerToken);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.PostAsJsonAsync($"/api/reservations/{prankId}/cancel", new { })).StatusCode);

        // The owner can.
        Assert.Equal(HttpStatusCode.OK, (await diner.PostAsJsonAsync($"/api/reservations/{prankId}/cancel", new { })).StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);
        var cancelled = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == prankId);
        Assert.NotEqual(ReservationStatus.PendingApproval, cancelled.Status);
        Assert.Null(cancelled.DinerUserId);
    }

    /// <summary>
    /// Review S1: the number check answers after the table's own refusal, so a probe naming a booked table
    /// gets the same answer whether the number it types holds a booking or not.
    /// </summary>
    [SkippableFact]
    public async Task A_probe_on_a_booked_table_cannot_tell_whether_a_number_holds_a_booking()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);

        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);
        Assert.Equal(HttpStatusCode.Created, (await diner.PostAsJsonAsync("/api/reservations", NewAppBooking(factory, branch))).StatusCode);

        using var anonymous = factory.CreateClient();

        var heldNumber = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: phone));
        var freeNumber = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch));

        Assert.Equal(HttpStatusCode.Conflict, heldNumber.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, freeNumber.StatusCode);
        Assert.Equal("table-already-booked", (await heldNumber.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("table-already-booked", (await freeNumber.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>
    /// Review S2: one budget per network for bookings without a proved number, shared by both doors - an IPv6
    /// caller by its /64, and a bearer token (a tab participant's, from any photographed QR code) buys no fresh
    /// one. A proved booker spends nothing from it.
    /// </summary>
    [SkippableFact]
    public async Task The_unverified_budget_is_per_network_shared_by_both_doors_and_no_token_resets_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:GlobalPermitLimit", "10000")
            .With("RateLimiting:PublicBranchPermitLimit", "10000")
            .With("RateLimiting:PublicUnverifiedBookingPermitLimit", "1");

        var branch = await ArrangeAsync(factory);
        var other = await ArrangeAsync(factory);

        using var client = factory.CreateClient();

        // The first from 2001:db8:1:2::/64 goes through.
        Assert.Equal(
            HttpStatusCode.Created,
            (await SendFromAsync(client, "2001:db8:1:2::10", HttpMethod.Post, PublicBookingUrl(branch), NewWebBooking(factory, branch))).StatusCode);

        // Another address in the same /64, carrying a tab participant's token: the same budget, spent.
        string participantToken;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var qrToken = await db.DiningTables.AsNoTracking().Where(t => t.Id == other.FirstTableId).Select(t => t.QrToken).SingleAsync();
            var scanned = await client.PostAsJsonAsync(
                "/api/tabs/open", new { qrToken, deviceId = $"scanner-{Guid.NewGuid():N}", clientCommandId = Guid.CreateVersion7() });
            Assert.Equal(HttpStatusCode.OK, scanned.StatusCode);
            participantToken = (await scanned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetProperty("accessToken").GetString()!;
        }

        var withToken = await SendFromAsync(
            client, "2001:db8:1:2::99", HttpMethod.Post, PublicBookingUrl(branch), NewWebBooking(factory, branch, time: "20:30"), participantToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, withToken.StatusCode);
        Assert.Equal("rate-limited", (await withToken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The app's door, from the same /64, for an account that never proved its number: the same budget.
        var unproved = await RegisterUnprovedDinerAsync(factory);
        var appDoor = await SendFromAsync(client, "2001:db8:1:2::abc", HttpMethod.Post, "/api/reservations", NewAppBooking(factory, other), unproved);
        Assert.Equal(HttpStatusCode.TooManyRequests, appDoor.StatusCode);

        // A proved booker from that network is not on the budget at all.
        var (provedToken, _) = await SignInVerifiedDinerAsync(factory);
        Assert.Equal(
            HttpStatusCode.Created,
            (await SendFromAsync(client, "2001:db8:1:2::abc", HttpMethod.Post, "/api/reservations", NewAppBooking(factory, other, daysAhead: 3), provedToken)).StatusCode);

        // Another /64 is another network.
        Assert.Equal(
            HttpStatusCode.Created,
            (await SendFromAsync(client, "2001:db8:1:3::10", HttpMethod.Post, PublicBookingUrl(branch), NewWebBooking(factory, branch, time: "21:30"))).StatusCode);
    }

    /// <summary>
    /// Review S3, the deadline: a booking without a proved number that waits for the venue has a day at most
    /// for its answer (two hours before it starts, if sooner). Unanswered, the approval sweep declines it for
    /// no answer, and the number is free again.
    /// </summary>
    [SkippableFact]
    public async Task An_unanswered_unverified_booking_is_declined_by_the_sweep_at_its_deadline()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        var phone = UniquePhone();
        var created = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: phone));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        // Two days ahead, so the day is the sooner of the two.
        var deadline = body.GetProperty("approvalExpiresAtUtc").GetDateTime().ToUniversalTime();
        var expected = factory.Clock.UtcNow.Add(UnverifiedBookingRules.AnswerWindow);
        Assert.InRange(deadline, expected.AddSeconds(-1), expected.AddSeconds(1));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var declined = await fixture.CreateApprovalExpirySweep(db, new TestClock(deadline.AddMinutes(1))).RunOnceAsync();
            Assert.InRange(declined, 1, int.MaxValue);
        }

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id);

            Assert.NotEqual(ReservationStatus.PendingApproval, row.Status);
            Assert.Equal(DeclineReason.NoAnswer, row.DeclineReason);
        }

        var again = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: phone, time: "20:30"));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    /// <summary>
    /// Review S3, the ceiling: a branch takes twenty bookings without a proved number waiting for its answer at
    /// once; the twenty-first is refused (from a new browser and a new number), and a proved booker is not
    /// counted.
    /// </summary>
    [SkippableFact]
    public async Task A_branch_takes_twenty_unverified_bookings_waiting_for_it_and_refuses_the_next()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        var slots = Enumerable.Range(2, 10)
            .SelectMany(day => branch.TableIds.Take(2).Select(table => (day, table)))
            .ToList();
        Assert.Equal(UnverifiedBookingRules.OutstandingPendingPerBranch, slots.Count);

        foreach (var (day, table) in slots)
        {
            var made = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), WebBookingAt(factory, table, day));
            Assert.True(made.StatusCode == HttpStatusCode.Created, await made.Content.ReadAsStringAsync());
        }

        var refused = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), WebBookingAt(factory, branch.FirstTableId, 12));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("branch-unverified-queue-full", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var (token, _) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);
        Assert.Equal(HttpStatusCode.Created, (await diner.PostAsJsonAsync("/api/reservations", NewAppBooking(factory, branch, daysAhead: 12))).StatusCode);
    }

    /// <summary>
    /// Review S8: deleting an account takes its number off the bookings it leaves to the venue, so whoever
    /// registers the number next books at once.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_an_account_frees_its_number_for_whoever_registers_it_next()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        var other = await ArrangeAsync(factory);

        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        using (var diner = factory.CreateClientWithToken(token))
        {
            Assert.Equal(HttpStatusCode.Created, (await diner.PostAsJsonAsync("/api/reservations", NewAppBooking(factory, branch))).StatusCode);

            // Fresh from a code sign-in: no password, no code needed.
            var deleted = await diner.SendAsync(
                new HttpRequestMessage(HttpMethod.Delete, "/api/diner/me") { Content = JsonContent.Create(new { }) });
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var (againToken, _) = await SignInVerifiedDinerAsync(factory, phone);
        using var again = factory.CreateClientWithToken(againToken);

        Assert.Equal(HttpStatusCode.Created, (await again.PostAsJsonAsync("/api/reservations", NewAppBooking(factory, other))).StatusCode);
    }

    /// <summary>
    /// Review S9: a number typed with Armenia's trunk 0 after the country code is the same number, so it holds
    /// the same one booking.
    /// </summary>
    [SkippableFact]
    public async Task A_number_typed_with_a_trunk_zero_is_the_same_number()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        var other = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        var phone = UniquePhone();
        var created = await anonymous.PostAsJsonAsync(PublicBookingUrl(branch), NewWebBooking(factory, branch, phone: phone));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var withZero = await anonymous.PostAsJsonAsync(
            PublicBookingUrl(other), NewWebBooking(factory, other, phone: "+3740" + phone[4..]));
        Assert.Equal(HttpStatusCode.Conflict, withZero.StatusCode);
        Assert.Equal("phone-has-active-booking", (await withZero.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<AuthBranch> ArrangeAsync(
        YallaApiFactory factory,
        bool acceptsWebBookings = true,
        bool unverifiedNeedApproval = true,
        int unverifiedMaxPartySize = 6)
    {
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow);
        }

        await SetPolicyAsync(factory, branch.BranchId, acceptsWebBookings, unverifiedNeedApproval, unverifiedMaxPartySize);

        return branch;
    }

    private async Task SetPolicyAsync(
        YallaApiFactory factory,
        Guid branchId,
        bool acceptsWebBookings,
        bool unverifiedNeedApproval = true,
        int unverifiedMaxPartySize = 6)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var row = await db.Branches.FirstAsync(b => b.Id == branchId);
        var p = row.ReservationPolicy;

        row.SetAcceptsWebBookings(acceptsWebBookings);
        row.UpdateReservationPolicy(new Domain.Venues.ReservationPolicy(
            p.TurnTimeMinutes, p.BufferMinutes, p.GraceMinutes, p.LateNudgeAfterMinutes, p.GraceExtensionMinutes,
            p.MinLeadMinutes, p.BookingWindowDays, p.CancellationDeadlineMinutes, p.AutoConfirm,
            p.ServiceChargePercent, p.PricesIncludeVat, p.MaxSeatOverhang, p.ApprovalRequiredAbovePartySize,
            p.WalkInHoldbackMinutes, p.ReminderHoursBefore, unverifiedNeedApproval, unverifiedMaxPartySize),
            factory.Clock.UtcNow);

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A number no other test holds a booking with: the database is shared across the run, and one number
    /// holds one active booking.
    /// </summary>
    private static string UniquePhone() => $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}";

    private static string PublicBookingUrl(AuthBranch branch) => $"/api/public/branches/{branch.BranchId}/bookings";

    private static string LocalDate(YallaApiFactory factory, int daysAhead)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        return DateOnly.FromDateTime(localNow).AddDays(daysAhead).ToString("yyyy-MM-dd");
    }

    private static object NewWebBooking(
        YallaApiFactory factory,
        AuthBranch branch,
        string? phone = null,
        string? device = null,
        int partySize = 2,
        Guid? commandId = null,
        string time = "18:00") =>
        new
        {
            tableId = branch.FirstTableId,
            date = LocalDate(factory, 2),
            time,
            partySize,
            guestName = "Ani Web",
            guestPhone = phone ?? UniquePhone(),
            deviceId = device ?? $"web-device-{Guid.NewGuid():N}",
            clientCommandId = commandId ?? Guid.CreateVersion7(),
        };

    /// <summary>A web booking on a given table and day at 18:00, with a number and a browser of its own.</summary>
    private static object WebBookingAt(YallaApiFactory factory, Guid tableId, int daysAhead) =>
        new
        {
            tableId,
            date = LocalDate(factory, daysAhead),
            time = "18:00",
            partySize = 2,
            guestName = "Ani Web",
            guestPhone = UniquePhone(),
            deviceId = $"web-device-{Guid.NewGuid():N}",
            clientCommandId = Guid.CreateVersion7(),
        };

    /// <summary>A request as if from <paramref name="address"/>, with a bearer token when one is given.</summary>
    private static async Task<HttpResponseMessage> SendFromAsync(
        HttpClient client,
        string address,
        HttpMethod method,
        string url,
        object body,
        string? bearer = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add(YallaApiFactory.RemoteAddressHeader, address);

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request);
    }

    private static object NewAppBooking(YallaApiFactory factory, AuthBranch branch, int daysAhead = 2) =>
        new
        {
            branchId = branch.BranchId,
            tableId = branch.FirstTableId,
            date = LocalDate(factory, daysAhead),
            time = "18:00",
            partySize = 2,
            guestName = "Ani App",
            guestPhone = "+37411223344",
            clientCommandId = Guid.CreateVersion7(),
            channel = ReservationChannel.App,
        };

    private static object PolicyBody(JsonElement current, bool? needApproval, int? maxParty) =>
        new
        {
            turnTimeMinutes = current.GetProperty("turnTimeMinutes").GetInt32(),
            bufferMinutes = current.GetProperty("bufferMinutes").GetInt32(),
            graceMinutes = current.GetProperty("graceMinutes").GetInt32(),
            lateNudgeAfterMinutes = current.GetProperty("lateNudgeAfterMinutes").GetInt32(),
            graceExtensionMinutes = current.GetProperty("graceExtensionMinutes").GetInt32(),
            minLeadMinutes = current.GetProperty("minLeadMinutes").GetInt32(),
            bookingWindowDays = current.GetProperty("bookingWindowDays").GetInt32(),
            cancellationDeadlineMinutes = current.GetProperty("cancellationDeadlineMinutes").GetInt32(),
            autoConfirm = current.GetProperty("autoConfirm").GetBoolean(),
            serviceChargePercent = current.GetProperty("serviceChargePercent").GetDecimal(),
            pricesIncludeVat = current.GetProperty("pricesIncludeVat").GetBoolean(),
            maxSeatOverhang = current.TryGetProperty("maxSeatOverhang", out var o) ? o.GetInt32() : (int?)null,
            approvalRequiredAbovePartySize = current.TryGetProperty("approvalRequiredAbovePartySize", out var a)
                ? a.GetInt32()
                : (int?)null,
            walkInHoldbackMinutes = current.GetProperty("walkInHoldbackMinutes").GetInt32(),
            unverifiedBookingsNeedApproval = needApproval,
            unverifiedMaxPartySize = maxParty,
        };

    private static async Task<(string Token, string Phone)> SignInVerifiedDinerAsync(YallaApiFactory factory, string? phone = null)
    {
        using var client = factory.CreateClient();
        phone ??= $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();
        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        return ((await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!, phone);
    }

    private static async Task<string> RegisterUnprovedDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var suffix = Random.Shared.Next(1_000_000, 9_999_999);

        var registered = await client.PostAsJsonAsync(
            "/api/auth/diner/register",
            new
            {
                username = $"ani{suffix}",
                email = $"ani{suffix}@example.test",
                password = "a long enough password",
                phoneE164 = $"+3749{suffix}",
                displayName = "Ani",
            });

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> SignInManagerAsync(YallaApiFactory factory, AuthBranch branch)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-in", new { email = branch.ManagerEmail, password = branch.ManagerPassword });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
