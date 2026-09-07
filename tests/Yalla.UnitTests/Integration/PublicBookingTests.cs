using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// What the public page publishes, and how somebody with no app manages the booking they made on it.
/// </summary>
/// <remarks>
/// <para>
/// Two halves that only make sense together. The page needs six facts it was never given - most of
/// all <c>acceptsWebBookings</c>, which gates the whole booking UI - and a diner who books here has
/// no app, so no push reminder and no one-tap cancel reach them. Without the manage link the
/// product has built a no-show generator: the entire reservation story rests on cancelling being
/// easier than not turning up.
/// </para>
/// <para>
/// As in <see cref="PublicSurfaceTests"/>, the tests that matter most are the negative ones - what
/// is <b>absent</b> from a body served to anybody holding a URL.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class PublicBookingTests(SqlServerFixture fixture)
{
    // ------------------------------------------------------------ 1. the completed branch page

    /// <summary>
    /// <b>Test 1.</b> The public branch response carries all six new fields, and a suspended branch
    /// still 404s.
    /// </summary>
    /// <remarks>
    /// The frontend built its page against an assumed shape and none of these existed, so the page
    /// says "not available yet" on every route. The second half is the rule the whole surface is
    /// built around, re-checked because adding fields is exactly when it would be lost.
    /// </remarks>
    [SkippableFact]
    public async Task The_public_branch_page_carries_every_field_it_was_missing()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var page = await ReadAsync(anonymous, world.PageUrl);

        // Every field the page was missing, by name. A missing one is the bug this exists for.
        //
        // There is no `status`: it was computed from isOpenNow and said nothing new under a name
        // that implied venue lifecycle. Asserted absent so it cannot come back.
        Assert.False(page.TryGetProperty("status", out _));
        Assert.True(page.TryGetProperty("isOpenNow", out _));

        // phoneE164 is null here, and the API omits null properties entirely
        // (DefaultIgnoreCondition = WhenWritingNull) - so "absent" is how a branch with no
        // published number appears on the wire. Worth pinning: a client that expected null would
        // be reading a field that is not there.
        Assert.True(
            !page.TryGetProperty("phoneE164", out var noPhone) || noPhone.ValueKind == JsonValueKind.Null);

        Assert.False(page.GetProperty("acceptsWebBookings").GetBoolean());
        Assert.Equal(14, page.GetProperty("bookingWindowDays").GetInt32());
        Assert.True(page.TryGetProperty("policy", out _));

        var asOf = page.GetProperty("asOfUtc").GetDateTime();
        Assert.InRange(asOf, factory.Clock.UtcNow.AddMinutes(-5), factory.Clock.UtcNow.AddMinutes(5));

        // The phone, once a manager publishes one.
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, world.Branch));

        var saved = await manager.PutAsJsonAsync(
            $"/api/branches/{world.BranchId}/public-profile",
            new { phoneE164 = "+374 11 22 33 44", acceptsWebBookings = true });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Normalised on the way in: one canonical form, or the number is three people.
        Assert.Equal(
            "+37411223344",
            (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("phoneE164").GetString());

        var republished = await ReadAsync(anonymous, world.PageUrl);
        Assert.Equal("+37411223344", republished.GetProperty("phoneE164").GetString());
        Assert.True(republished.GetProperty("acceptsWebBookings").GetBoolean());

        // And the rule that outranks all of it: a suspended venue is a 404, fields or no fields.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var venue = await db.Venues.FirstAsync(v => v.Id == world.VenueId);
            venue.Suspend(factory.Clock.UtcNow);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(world.PageUrl)).StatusCode);
    }

    // ------------------------------------------------------------ 2. what policy withholds

    /// <summary>
    /// <b>Test 2.</b> The published policy is the diner's subset and nothing else.
    /// </summary>
    /// <remarks>
    /// Asserted against the <b>serialised body</b>, not the type. A test that read the record's
    /// properties would still pass on the day somebody serialises the full policy through it, which
    /// is the failure worth catching: this route has no authentication, so a field added here is a
    /// field published to anybody with a URL. A scraper reading walk-in holdbacks across the estate
    /// learns how every venue in the city runs its floor.
    /// </remarks>
    [SkippableFact]
    public async Task The_published_policy_withholds_everything_a_diner_does_not_need()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();
        var page = await ReadAsync(anonymous, world.PageUrl);
        var policy = page.GetProperty("policy");

        // Exactly three fields, and they are these.
        Assert.Equal(
            new[] { "cancellationDeadlineMinutes", "minLeadMinutes", "turnTimeMinutes" },
            policy.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Named individually as well, so a failure says which one leaked rather than only that the
        // count changed.
        foreach (var withheld in Withheld)
        {
            Assert.False(policy.TryGetProperty(withheld, out _), $"policy published {withheld}.");
        }

        // And not smuggled onto the page beside the policy either.
        foreach (var withheld in Withheld)
        {
            Assert.False(page.TryGetProperty(withheld, out _), $"the branch page published {withheld}.");
        }
    }

    /// <summary>Policy settings that are commercial or operational, and are nobody's business here.</summary>
    private static readonly string[] Withheld =
    [
        "serviceChargePercent",
        "walkInHoldbackMinutes",
        "approvalRequiredAbovePartySize",
        "maxSeatOverhang",
        "autoConfirm",
        "pricesIncludeVat",
        "bufferMinutes",
        "graceMinutes",
        "graceExtensionMinutes",
        "lateNudgeAfterMinutes",
        "reminderHoursBefore",
    ];

    // ------------------------------------------------------------ 3. the default

    /// <summary>
    /// <b>Test 3.</b> A newly created branch does not take web bookings.
    /// </summary>
    /// <remarks>
    /// The default is the decision. A venue that has never been asked has not agreed to take
    /// bookings from strangers on the internet, and a branch created by the platform API is exactly
    /// a venue nobody has asked yet.
    /// </remarks>
    [SkippableFact]
    public async Task A_new_branch_does_not_accept_web_bookings_until_somebody_says_so()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.Branches.AsNoTracking()
                .Where(b => b.Id == world.BranchId)
                .Select(b => b.AcceptsWebBookings)
                .FirstAsync());
        }

        // The checklist reports it without blocking on it: a venue that does not want web bookings
        // is not an unfinished venue, and a line that blocked going live would teach every
        // onboarder to switch it on without reading it.
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, world.Branch));
        var readiness = await ReadAsync(manager, $"/api/branches/{world.BranchId}/readiness");

        Assert.False(readiness.GetProperty("acceptsWebBookings").GetBoolean());

        var blockers = readiness.GetProperty("blockers").EnumerateArray()
            .Select(b => b.GetString() ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(blockers, b => b.Contains("web booking", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------ 4. the gate

    /// <summary>
    /// <b>Test 4.</b> A booking from the public page is refused while the branch has web bookings
    /// off; the same booking from the app succeeds.
    /// </summary>
    [SkippableFact]
    public async Task A_web_booking_is_refused_while_the_branch_has_not_switched_them_on()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var fromWeb = await diner.PostAsJsonAsync(
            "/api/reservations", NewBooking(factory, world.Branch, ReservationChannel.Web));

        Assert.Equal(HttpStatusCode.Conflict, fromWeb.StatusCode);
        Assert.Equal(
            "web-bookings-not-accepted",
            (await fromWeb.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The app is a different channel and is not gated by a setting about the public page.
        var fromApp = await diner.PostAsJsonAsync(
            "/api/reservations", NewBooking(factory, world.Branch, ReservationChannel.App));

        Assert.Equal(HttpStatusCode.Created, fromApp.StatusCode);

        // Switch the page on, and the web booking goes through too.
        await SetWebBookingsAsync(factory, world, accepts: true);

        var allowed = await diner.PostAsJsonAsync(
            "/api/reservations",
            NewBooking(factory, world.Branch, ReservationChannel.Web, world.Branch.TableIds[1]));

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    // ------------------------------------------------------------ 5. the token is hashed

    /// <summary>
    /// <b>Test 5.</b> Creating a booking returns a manage token, and only its hash is stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same treatment as a refresh handle and an enrolment code, per the split in
    /// <c>Secrets</c>: 256 bits from a CSPRNG, so guessing is already impossible and the only job
    /// left is making a database dump useless. It matters more here than for a refresh handle,
    /// because this one lives in a URL that gets pasted into WhatsApp.
    /// </para>
    /// <para>
    /// An <b>app</b> booking, deliberately. A web booking's reminder carries the manage URL in its
    /// outbox payload - it has to, since the token is knowable only at creation and a dispatcher
    /// could not mint one later - so the plaintext is in that one row on purpose. Test 12 pins
    /// that, and the two together say where the token is and is not allowed to be.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_new_booking_returns_a_manage_token_that_is_stored_only_as_a_hash()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var created = await diner.PostAsJsonAsync(
            "/api/reservations", NewBooking(factory, world.Branch, ReservationChannel.App));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("manageToken").GetString();

        Assert.False(string.IsNullOrWhiteSpace(token));

        var reservationId = body.GetProperty("id").GetGuid();

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var stored = await db.Reservations.AsNoTracking()
                .Where(r => r.Id == reservationId)
                .Select(r => r.ManageTokenHash)
                .FirstAsync();

            Assert.Equal(Sha256Hex(token!), stored);
            Assert.NotEqual(token, stored);

            // The plaintext is in no column of either table that could have held it.
            Assert.False(await db.Reservations.AnyAsync(r =>
                r.ManageTokenHash == token || r.Code == token || r.GuestName == token));

            Assert.False(await db.OutboxMessages.AnyAsync(m => m.PayloadJson.Contains(token!)));
        }

        // And it is issued once. Reading the booking back never re-issues it - the server holds only
        // the hash, so it could not even if a later endpoint wanted to.
        var mine = await ReadAsync(diner, "/api/reservations/mine");

        foreach (var row in mine.GetProperty("upcoming").EnumerateArray())
        {
            Assert.True(
                !row.TryGetProperty("manageToken", out var again) || again.ValueKind == JsonValueKind.Null,
                "A read re-issued the manage token.");
        }
    }

    // ------------------------------------------------------------ 6. what the link shows

    /// <summary>
    /// <b>Test 6.</b> The manage link returns the confirmation fields and none of the excluded ones.
    /// </summary>
    /// <remarks>
    /// On the serialised body, for the same reason as test 2. The caller here has no account and no
    /// session; a URL is the whole credential, and that URL ends up in a group chat.
    /// </remarks>
    [SkippableFact]
    public async Task The_manage_link_shows_the_confirmation_and_nothing_that_could_hurt_anybody()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);
        var booking = await BookAsync(factory, world);

        using var anonymous = factory.CreateClient();
        var view = await ReadAsync(anonymous, $"/api/public/bookings/{booking.Token}");

        // What the confirmation screen renders.
        Assert.Equal(world.VenueName, view.GetProperty("venueName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(view.GetProperty("branchName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(view.GetProperty("branchAddress").GetString()));
        Assert.Equal("Asia/Yerevan", view.GetProperty("timeZoneId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(view.GetProperty("tableLabel").GetString()));
        Assert.Equal(booking.Code, view.GetProperty("code").GetString());
        Assert.Equal(2, view.GetProperty("partySize").GetInt32());
        Assert.Equal((int)ReservationStatus.Confirmed, view.GetProperty("status").GetInt32());
        Assert.True(view.GetProperty("canCancel").GetBoolean());
        Assert.False(view.GetProperty("cancelledAfterDeadline").GetBoolean());

        // The deadline as an instant, so the page does not reimplement the arithmetic and a later
        // policy edit cannot move a deadline the diner has already been shown.
        Assert.True(view.GetProperty("cancellationDeadlineUtc").GetDateTime() < booking.StartUtc);

        // And nothing else. Every one of these is a real field on the authenticated shape.
        foreach (var withheld in new[]
                 {
                     "guestPhone",
                     "guestName",
                     "dinerUserId",
                     "id",
                     "tableId",
                     "branchId",
                     "clientCommandId",
                     "manageToken",
                     "floorPlan",
                 })
        {
            Assert.False(view.TryGetProperty(withheld, out _), $"the manage link published {withheld}.");
        }
    }

    // ------------------------------------------------------------ 7. cancelling, either side of the deadline

    /// <summary>
    /// <b>Test 7.</b> Cancelling before the deadline is free; after it, it still cancels and the
    /// lateness is recorded.
    /// </summary>
    /// <remarks>
    /// Never blocked, on either side. A diner who cannot cancel simply does not turn up, and a
    /// no-show costs the venue the same table plus the chance to resell it.
    /// </remarks>
    [SkippableFact]
    public async Task Cancelling_is_free_before_the_deadline_and_recorded_as_late_after_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var anonymous = factory.CreateClient();

        // Comfortably before the deadline, which defaults to two hours.
        var early = await BookAsync(factory, world);
        var freed = await anonymous.PostAsJsonAsync($"/api/public/bookings/{early.Token}/cancel", new { });

        Assert.Equal(HttpStatusCode.OK, freed.StatusCode);

        var freedBody = await freed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)ReservationStatus.CancelledByDiner, freedBody.GetProperty("status").GetInt32());
        Assert.False(freedBody.GetProperty("cancelledAfterDeadline").GetBoolean());
        Assert.False(freedBody.GetProperty("canCancel").GetBoolean());

        // A second booking, cancelled with the clock wound to inside the deadline.
        var late = await BookAsync(factory, world, tableId: world.Branch.TableIds[1]);
        factory.Clock.UtcNow = late.StartUtc.AddMinutes(-30);

        var lateCancel = await anonymous.PostAsJsonAsync(
            $"/api/public/bookings/{late.Token}/cancel", new { reason = "running late" });

        // Allowed, and recorded. Refusing it would produce a no-show instead.
        Assert.Equal(HttpStatusCode.OK, lateCancel.StatusCode);

        var lateBody = await lateCancel.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)ReservationStatus.CancelledByDiner, lateBody.GetProperty("status").GetInt32());
        Assert.True(lateBody.GetProperty("cancelledAfterDeadline").GetBoolean());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == late.Id);
            Assert.True(row.CancelledAfterDeadline);
            Assert.Equal("running late", row.CancellationReason);

            // The reminder went with it, in the same transaction - cancelling an unsent message
            // removes its row. A push about a table somebody already gave back is the one they
            // remember, and it teaches them the notifications are wrong.
            Assert.False(await db.OutboxMessages.AnyAsync(m =>
                m.IdempotencyKey.Contains(late.Id.ToString()) && m.SentAtUtc == null));
        }
    }

    // ------------------------------------------------------------ 8. an old link still answers

    /// <summary>
    /// <b>Test 8.</b> A cancelled booking's link reports its state rather than 404.
    /// </summary>
    /// <remarks>
    /// Somebody clicking a link from three weeks ago should learn what happened to their table. A
    /// 404 teaches them only that something is broken, and they ring the venue to ask.
    /// </remarks>
    [SkippableFact]
    public async Task A_cancelled_bookings_link_reports_that_it_was_cancelled()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);
        var booking = await BookAsync(factory, world);

        using var anonymous = factory.CreateClient();

        await anonymous.PostAsJsonAsync($"/api/public/bookings/{booking.Token}/cancel", new { });

        var after = await anonymous.GetAsync($"/api/public/bookings/{booking.Token}");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);

        var body = await after.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)ReservationStatus.CancelledByDiner, body.GetProperty("status").GetInt32());
        Assert.False(body.GetProperty("canCancel").GetBoolean());

        // Cancelling it again is not an error either - it comes back as it stands, untouched.
        var again = await anonymous.PostAsJsonAsync($"/api/public/bookings/{booking.Token}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);
        var row = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == booking.Id);

        // And the second cancel did not move the first one's timestamp.
        Assert.Equal(
            body.GetProperty("status").GetInt32(),
            (int)row.Status);
    }

    // ------------------------------------------------------------ 9. not an oracle

    /// <summary>
    /// <b>Test 9.</b> An unknown token and an expired one are indistinguishable.
    /// </summary>
    /// <remarks>
    /// The whole point of the failure being a constant. If the two answered differently, somebody
    /// with a list of candidate tokens could learn which ones are real - and "real" is the only
    /// thing between them and a stranger's booking.
    /// </remarks>
    [SkippableFact]
    public async Task An_unknown_token_and_an_expired_one_fail_identically()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);
        var booking = await BookAsync(factory, world);

        using var anonymous = factory.CreateClient();

        // Wind past the end of the booking plus its grace, and the link stops working.
        factory.Clock.UtcNow = booking.StartUtc.AddDays(Reservation.ManageTokenGraceDays + 1);

        var expired = await anonymous.GetAsync($"/api/public/bookings/{booking.Token}");
        var unknown = await anonymous.GetAsync($"/api/public/bookings/{NewTokenLikeString()}");

        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var expiredBody = await expired.Content.ReadAsStringAsync();
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        // Byte for byte, once the traceId - which is per-request by design - is taken out.
        Assert.Equal(WithoutTraceId(unknownBody), WithoutTraceId(expiredBody));

        // The cancel route answers the same way, so it cannot be used as the oracle the read is not.
        var expiredCancel = await anonymous.PostAsJsonAsync(
            $"/api/public/bookings/{booking.Token}/cancel", new { });

        var unknownCancel = await anonymous.PostAsJsonAsync(
            $"/api/public/bookings/{NewTokenLikeString()}/cancel", new { });

        Assert.Equal(HttpStatusCode.NotFound, expiredCancel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownCancel.StatusCode);

        Assert.Equal(
            WithoutTraceId(await unknownCancel.Content.ReadAsStringAsync()),
            WithoutTraceId(await expiredCancel.Content.ReadAsStringAsync()));
    }

    // ------------------------------------------------------------ 10. one link, one budget

    /// <summary>
    /// <b>Test 10.</b> The manage routes are rate limited per token.
    /// </summary>
    /// <remarks>
    /// The per-address limit already bounds somebody guessing tokens. What it does not bound is a
    /// link that went round a group chat being hammered from forty phones, which is what this is.
    /// </remarks>
    [SkippableFact]
    public async Task The_manage_link_is_rate_limited_per_token()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        const int limit = 4;

        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:PublicBookingPermitLimit", limit.ToString())
            .With("RateLimiting:PublicBookingWindowSeconds", "60")

            // Generous, so it is the per-token ceiling that fires and not one of the others.
            .With("RateLimiting:PublicPermitLimit", "1000")
            .With("RateLimiting:PublicBranchPermitLimit", "1000")
            .With("RateLimiting:GlobalPermitLimit", "1000");

        var world = await ArrangeAsync(factory);
        var first = await BookAsync(factory, world);
        var second = await BookAsync(factory, world, tableId: world.Branch.TableIds[1]);

        using var anonymous = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < limit + 3; i++)
        {
            statuses.Add((await anonymous.GetAsync($"/api/public/bookings/{first.Token}")).StatusCode);
        }

        Assert.Equal(limit, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        // A different booking has its own budget - one link exhausting itself must not take every
        // other diner's confirmation down with it.
        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/public/bookings/{second.Token}")).StatusCode);
    }

    // ------------------------------------------------------------ 11. the phone number

    /// <summary>
    /// <b>Test 11.</b> A non-E.164 phone number is refused, and the refusal names the field.
    /// </summary>
    [SkippableFact]
    public async Task A_phone_number_that_is_not_e164_is_refused_and_names_its_field()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var world = await ArrangeAsync(factory);

        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, world.Branch));
        var url = $"/api/branches/{world.BranchId}/public-profile";

        var refused = await manager.PutAsJsonAsync(
            url, new { phoneE164 = "011 22 33 44", acceptsWebBookings = false });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Named, so the form puts the message against its own input rather than at the top.
        Assert.Equal(
            "phoneE164",
            (await refused.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("context").GetProperty("field").GetString());

        // Nothing was saved: a bad number must not leave the branch half-updated. Absent rather
        // than null, because the API omits null properties.
        var profile = await ReadAsync(manager, url);
        Assert.True(
            !profile.TryGetProperty("phoneE164", out var stored) || stored.ValueKind == JsonValueKind.Null);

        // Blank clears it rather than failing, because "no published number" is a real state.
        var cleared = await manager.PutAsJsonAsync(
            url, new { phoneE164 = (string?)null, acceptsWebBookings = false });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    }

    // ------------------------------------------------------------ 12. the reminder carries the link

    /// <summary>
    /// <b>Test 12.</b> A web booking's reminder payload carries the manage URL.
    /// </summary>
    /// <remarks>
    /// Written now, before any channel exists that could send it. The token is knowable only at
    /// creation - it is never stored in plaintext and never returned again - so a dispatcher added
    /// later that had to go and mint one would find that it cannot.
    /// </remarks>
    [SkippableFact]
    public async Task A_web_bookings_reminder_carries_the_manage_url()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory()
            .With("PublicWeb:ManageBookingUrlTemplate", "https://yalla.test/booking/{token}");

        var world = await ArrangeAsync(factory);
        await SetWebBookingsAsync(factory, world, accepts: true);

        var web = await BookAsync(factory, world, channel: ReservationChannel.Web);

        await using var db = fixture.CreateContext(factory.Clock);

        var reminder = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == "reservation.reminder" && m.PayloadJson.Contains(web.Id.ToString()))
            .Select(m => m.PayloadJson)
            .FirstAsync();

        Assert.Contains($"https://yalla.test/booking/{web.Token}", reminder, StringComparison.Ordinal);

        // An app booking's reminder does not carry one. A capability that opens somebody's booking
        // belongs in as few rows as possible, and the app already has a better cancel route.
        var app = await BookAsync(
            factory, world, tableId: world.Branch.TableIds[1], channel: ReservationChannel.App);

        var appReminder = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == "reservation.reminder" && m.PayloadJson.Contains(app.Id.ToString()))
            .Select(m => m.PayloadJson)
            .FirstAsync();

        Assert.DoesNotContain(app.Token, appReminder, StringComparison.Ordinal);
        Assert.Contains("\"manageUrl\":null", appReminder.Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ helpers

    private sealed record World(
        AuthBranch Branch,
        Guid VenueId,
        Guid BranchId,
        string VenueName,
        string PageUrl);

    private sealed record Booking(Guid Id, string Token, string Code, DateTime StartUtc);

    private async Task<World> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await AuthTestData.CreateBranchAsync(db);

        var slugs = await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => new { b.Slug, VenueSlug = b.Venue.Slug, b.VenueId, VenueName = b.Venue.Name })
            .FirstAsync();

        return new World(
            branch,
            slugs.VenueId,
            branch.BranchId,
            slugs.VenueName,
            $"/api/public/branches/{slugs.VenueSlug}/{slugs.Slug}");
    }

    /// <summary>Books a table and returns what the manage link needs, including the one-time token.</summary>
    private async Task<Booking> BookAsync(
        YallaApiFactory factory,
        World world,
        Guid? tableId = null,
        ReservationChannel channel = ReservationChannel.App)
    {
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var created = await diner.PostAsJsonAsync(
            "/api/reservations", NewBooking(factory, world.Branch, channel, tableId));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        return new Booking(
            body.GetProperty("id").GetGuid(),
            body.GetProperty("manageToken").GetString()!,
            body.GetProperty("code").GetString()!,
            body.GetProperty("startUtc").GetDateTime());
    }

    private async Task SetWebBookingsAsync(YallaApiFactory factory, World world, bool accepts)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await db.Branches.FirstAsync(b => b.Id == world.BranchId);

        branch.SetAcceptsWebBookings(accepts);
        await db.SaveChangesAsync();
    }

    private static object NewBooking(
        YallaApiFactory factory,
        AuthBranch branch,
        ReservationChannel channel,
        Guid? tableId = null)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        return new
        {
            branchId = branch.BranchId,
            tableId = tableId ?? branch.FirstTableId,
            date = DateOnly.FromDateTime(localNow).AddDays(2).ToString("yyyy-MM-dd"),
            time = "18:00",
            partySize = 2,
            guestName = "Ani Test",
            guestPhone = "+37411223344",
            clientCommandId = Guid.CreateVersion7(),
            channel,
        };
    }

    /// <summary>A string shaped like a real token, so the unknown case is not rejected for its form.</summary>
    private static string NewTokenLikeString() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Strips the per-request traceId, which every problem body carries and which is meant to differ.
    /// </summary>
    private static string WithoutTraceId(string body)
    {
        using var document = JsonDocument.Parse(body);

        return string.Join(
            "|",
            document.RootElement.EnumerateObject()
                .Where(p => !PerRequest.Contains(p.Name, StringComparer.Ordinal))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value.GetRawText()}"));
    }

    /// <summary>
    /// Problem-body fields that differ per request by design, and cannot be an oracle.
    /// </summary>
    /// <remarks>
    /// <c>traceId</c> is meant to be unique. <c>instance</c> is the request path, which contains
    /// the token the caller just sent - it tells them only what they already typed, and the same
    /// path is in the web server's access log either way. What must not differ is anything derived
    /// from what the server <i>found</i>: the status, the code and the sentence.
    /// </remarks>
    private static readonly string[] PerRequest = ["traceId", "instance"];

    private static async Task<string> SignInManagerAsync(YallaApiFactory factory, AuthBranch branch)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-in", new { email = branch.ManagerEmail, password = branch.ManagerPassword });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
