using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>POST /api/public/bookings/{token}/reschedule</c> through the real pipeline: anonymous, the body's
/// binding, the per-link budget, and the 404, 409 and 422 answers a service test cannot see.
/// </summary>
/// <remarks>
/// <para>
/// The rules themselves are <see cref="ManageLinkRescheduleTests"/>' subject, run both ways against the
/// app's move. These are about the wire: what a visitor with nothing but the link is answered, and that
/// the answer carries nothing the link's read does not.
/// </para>
/// <para>
/// The API's clock is moved to noon at the branch the day after the real one, once every diner has
/// signed in - tokens are checked against the real clock - so an evening two days on is never at short
/// notice whatever time the suite runs.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class PublicBookingRescheduleEndpointTests(SqlServerFixture fixture)
{
    private static readonly TimeZoneInfo Yerevan = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");

    [SkippableFact]
    public async Task A_visitor_changes_their_booking_from_the_link_and_the_page_reads_it_back()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory()
            .With("PublicWeb:ManageBookingUrlTemplate", "https://yalla.test/booking/{token}");

        var branch = await ArrangeAsync(factory, acceptsWebBookings: true);
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var date = MoveToNoonTomorrow(factory).AddDays(2);

        var booking = await BookAsync(diner, branch, date, "18:00", branch.FirstTableId, ReservationChannel.Web);
        using var anonymous = factory.CreateClient();

        // The read names the booking and its table now - what the change screen starts from - and the
        // branch's address on the public page, which a link opened as /booking/{token} has nowhere else.
        var read = await ReadAsync(anonymous, $"/api/public/bookings/{booking.Token}");
        Assert.Equal(booking.Id, read.GetProperty("reservationId").GetGuid());
        Assert.Equal(branch.FirstTableId, read.GetProperty("tableId").GetGuid());

        var (venueSlug, branchSlug) = await SlugsAsync(factory, branch);
        Assert.Equal(venueSlug, read.GetProperty("venueSlug").GetString());
        Assert.Equal(branchSlug, read.GetProperty("branchSlug").GetString());

        // The link the reminder was written with is the web's token-only path, exactly.
        var expectedUrl = $"https://yalla.test/booking/{Uri.EscapeDataString(booking.Token)}";
        Assert.Equal([expectedUrl], await ReminderManageUrlsAsync(factory, booking.Id));

        var change = new
        {
            date = Iso(date),
            time = "20:00",
            partySize = 3,
            diningTableId = branch.TableIds[1],
            clientCommandId = Guid.CreateVersion7(),
        };

        var moved = await anonymous.PostAsJsonAsync($"/api/public/bookings/{booking.Token}/reschedule", change);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var movedText = await moved.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(movedText).RootElement;

        // The same booking, at the new slot - and still confirmed: another evening, nobody's approval needed.
        Assert.Equal(booking.Code, body.GetProperty("code").GetString());
        Assert.Equal(booking.Id, body.GetProperty("reservationId").GetGuid());
        Assert.Equal(branch.TableIds[1], body.GetProperty("tableId").GetGuid());
        Assert.Equal("2", body.GetProperty("tableLabel").GetString());
        Assert.Equal("Windows", body.GetProperty("roomName").GetString());
        Assert.Equal(Iso(date), body.GetProperty("localDate").GetString());
        Assert.Equal("20:00:00", body.GetProperty("localStartTime").GetString());
        Assert.Equal(3, body.GetProperty("partySize").GetInt32());
        Assert.Equal((int)ReservationStatus.Confirmed, body.GetProperty("status").GetInt32());
        Assert.True(body.GetProperty("canCancel").GetBoolean());
        Assert.Equal(venueSlug, body.GetProperty("venueSlug").GetString());
        Assert.Equal(branchSlug, body.GetProperty("branchSlug").GetString());

        // The link's shape and nothing else: what the read withholds, the write does too.
        foreach (var withheld in new[]
                 {
                     "guestPhone", "guestName", "dinerUserId", "id", "branchId", "clientCommandId", "manageToken",
                     "wasReplay", "floorPlan", "incomingTableRequest",
                 })
        {
            Assert.False(body.TryGetProperty(withheld, out _), $"the change published {withheld}.");
        }

        // The page reading the link again sees exactly what the change answered.
        var reread = await anonymous.GetStringAsync($"/api/public/bookings/{booking.Token}");
        Assert.Equal(movedText, reread);

        // The reminder for the new time carries the same manage link - a web visitor's only way back.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var reminders = await db.OutboxMessages.AsNoTracking()
                .Where(m => m.Type == "reservation.reminder"
                            && m.SentAtUtc == null
                            && m.IdempotencyKey.Contains(booking.Id.ToString()))
                .Select(m => m.PayloadJson)
                .ToListAsync();

            var reminder = Assert.Single(reminders);
            Assert.Contains("\"localStartTime\":\"20:00:00\"", reminder, StringComparison.Ordinal);
        }

        // Read back off the old reminder and written into the new one unchanged: still the token-only path.
        Assert.Equal([expectedUrl], await ReminderManageUrlsAsync(factory, booking.Id));

        // The page retries: the same answer, nothing moved twice.
        var retry = await anonymous.PostAsJsonAsync($"/api/public/bookings/{booking.Token}/reschedule", change);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(movedText, await retry.Content.ReadAsStringAsync());

        // The same id for a different change is not a retry.
        var reused = await anonymous.PostAsJsonAsync(
            $"/api/public/bookings/{booking.Token}/reschedule", change with { time = "20:30" });

        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("client-command-id-in-use", await CodeAsync(reused));
    }

    [SkippableFact]
    public async Task The_body_is_the_apps_and_is_refused_as_the_apps_is()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var date = MoveToNoonTomorrow(factory).AddDays(2);

        var booking = await BookAsync(diner, branch, date, "18:00", branch.FirstTableId);
        using var anonymous = factory.CreateClient();

        var link = $"/api/public/bookings/{booking.Token}/reschedule";
        var app = $"/api/reservations/{booking.Id}/reschedule";

        // No table: refused naming the field, on both routes.
        var noTable = new { date = Iso(date), time = "20:00", partySize = 2, clientCommandId = Guid.CreateVersion7() };
        var linkNoTable = await anonymous.PostAsJsonAsync(link, noTable);
        var appNoTable = await diner.PostAsJsonAsync(app, noTable);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, linkNoTable.StatusCode);
        Assert.Equal(appNoTable.StatusCode, linkNoTable.StatusCode);

        var violation = await linkNoTable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation-failed", violation.GetProperty("code").GetString());
        Assert.Equal("diningTableId", violation.GetProperty("context").GetProperty("field").GetString());

        // No command id: refused, on both routes alike - a change without one could not be told from its retry.
        var noId = new { date = Iso(date), time = "20:00", partySize = 2, diningTableId = branch.FirstTableId };
        var linkNoId = await anonymous.PostAsJsonAsync(link, noId);
        var appNoId = await diner.PostAsJsonAsync(app, noId);

        Assert.False(linkNoId.IsSuccessStatusCode);
        Assert.Equal(appNoId.StatusCode, linkNoId.StatusCode);
        Assert.Equal(await CodeAsync(appNoId), await CodeAsync(linkNoId));

        // A branch rule: its own code, as for a new booking.
        var lateNight = await anonymous.PostAsJsonAsync(
            link,
            new { date = Iso(date), time = "22:00", partySize = 2, diningTableId = branch.FirstTableId, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, lateNight.StatusCode);
        Assert.Equal("reservation-outside-opening-hours", await CodeAsync(lateNight));

        // Nothing moved.
        var read = await ReadAsync(anonymous, $"/api/public/bookings/{booking.Token}");
        Assert.Equal("18:00:00", read.GetProperty("localStartTime").GetString());
    }

    [SkippableFact]
    public async Task An_unknown_or_expired_link_is_refused_word_for_word_as_the_read_is()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var date = MoveToNoonTomorrow(factory).AddDays(2);

        var booking = await BookAsync(diner, branch, date, "18:00", branch.FirstTableId);
        using var anonymous = factory.CreateClient();

        object Change() => new
        {
            date = Iso(date),
            time = "20:00",
            partySize = 2,
            diningTableId = branch.FirstTableId,
            clientCommandId = Guid.CreateVersion7(),
        };

        var unknownToken = NewTokenLikeString();
        var unknownRead = await anonymous.GetAsync($"/api/public/bookings/{unknownToken}");
        var unknownMove = await anonymous.PostAsJsonAsync($"/api/public/bookings/{unknownToken}/reschedule", Change());

        Assert.Equal(HttpStatusCode.NotFound, unknownMove.StatusCode);
        Assert.Equal(
            WithoutPerRequest(await unknownRead.Content.ReadAsStringAsync()),
            WithoutPerRequest(await unknownMove.Content.ReadAsStringAsync()));

        // Past the end of the booking plus its grace: the same answer again, so the move is no oracle.
        factory.Clock.UtcNow = booking.StartUtc.AddDays(Reservation.ManageTokenGraceDays + 1);

        var expiredMove = await anonymous.PostAsJsonAsync($"/api/public/bookings/{booking.Token}/reschedule", Change());

        Assert.Equal(HttpStatusCode.NotFound, expiredMove.StatusCode);
        Assert.Equal(
            WithoutPerRequest(await unknownMove.Content.ReadAsStringAsync()),
            WithoutPerRequest(await expiredMove.Content.ReadAsStringAsync()));
    }

    [SkippableFact]
    public async Task The_refusals_reach_the_page_with_their_codes_and_nothing_about_another_booking()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var stranger = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var date = MoveToNoonTomorrow(factory).AddDays(2);

        var booking = await BookAsync(diner, branch, date, "18:00", branch.FirstTableId);
        var other = await BookAsync(diner, branch, date, "21:15", branch.TableIds[2]);
        await BookAsync(stranger, branch, date.AddDays(1), "20:00", branch.TableIds[1]);

        using var anonymous = factory.CreateClient();
        var link = $"/api/public/bookings/{booking.Token}/reschedule";

        object Change(string time, Guid table, int days = 0) => new
        {
            date = Iso(date.AddDays(days)),
            time,
            partySize = 2,
            diningTableId = table,
            clientCommandId = Guid.CreateVersion7(),
        };

        // The diner's own other booking at 21:15: the app would name it; the link does not.
        var overlaps = await anonymous.PostAsJsonAsync(link, Change("21:00", branch.FirstTableId));
        Assert.Equal(HttpStatusCode.Conflict, overlaps.StatusCode);

        var overlapText = await overlaps.Content.ReadAsStringAsync();
        var overlap = JsonDocument.Parse(overlapText).RootElement;

        Assert.Equal("reservation-overlaps-own", overlap.GetProperty("code").GetString());
        Assert.False(overlap.TryGetProperty("context", out _));
        Assert.DoesNotContain(other.Code, overlapText, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Id.ToString(), overlapText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("21:15", overlapText, StringComparison.Ordinal);

        // A stranger's booking in the way the next evening: the app's 409, with a floor that leaves this
        // booking out.
        var taken = await anonymous.PostAsJsonAsync(link, Change("20:00", branch.TableIds[1], days: 1));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        var clash = await taken.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("table-already-booked", clash.GetProperty("code").GetString());

        var ownTable = clash.GetProperty("context").GetProperty("availability").GetProperty("tables")
            .EnumerateArray()
            .Single(t => t.GetProperty("tableId").GetGuid() == branch.FirstTableId);
        Assert.True(ownTable.GetProperty("isAvailable").GetBoolean());

        // Cancelled from the link - the answer still says where the branch's page is - and it no longer moves.
        var cancel = await anonymous.PostAsJsonAsync($"/api/public/bookings/{booking.Token}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        var (venueSlug, branchSlug) = await SlugsAsync(factory, branch);
        var cancelBody = await cancel.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(venueSlug, cancelBody.GetProperty("venueSlug").GetString());
        Assert.Equal(branchSlug, cancelBody.GetProperty("branchSlug").GetString());

        var cancelled = await anonymous.PostAsJsonAsync(link, Change("19:00", branch.FirstTableId));
        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);

        var problem = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reservation-not-changeable", problem.GetProperty("code").GetString());
        Assert.Equal((int)ReservationStatus.CancelledByDiner, problem.GetProperty("context").GetProperty("status").GetInt32());
    }

    [SkippableFact]
    public async Task A_link_holder_reaches_only_their_own_booking()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        using var stranger = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var date = MoveToNoonTomorrow(factory).AddDays(2);

        var mine = await BookAsync(diner, branch, date, "18:00", branch.FirstTableId);
        var theirs = await BookAsync(stranger, branch, date, "18:00", branch.TableIds[1]);

        using var anonymous = factory.CreateClient();

        // The body has no booking id to point elsewhere: a key naming theirs is not part of it.
        var change = new
        {
            date = Iso(date),
            time = "20:00",
            partySize = 2,
            diningTableId = branch.FirstTableId,
            clientCommandId = Guid.CreateVersion7(),
        };

        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync($"/api/public/bookings/{mine.Token}/reschedule", change)).StatusCode);

        // My command id on their link is somebody else's command - refused, and it moves nothing of theirs.
        var collision = await anonymous.PostAsJsonAsync(
            $"/api/public/bookings/{theirs.Token}/reschedule", change with { diningTableId = branch.TableIds[1] });

        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);

        var collisionText = await collision.Content.ReadAsStringAsync();
        Assert.Equal("client-command-id-in-use", JsonDocument.Parse(collisionText).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(mine.Code, collisionText, StringComparison.Ordinal);

        var theirsNow = await ReadAsync(anonymous, $"/api/public/bookings/{theirs.Token}");
        Assert.Equal("18:00:00", theirsNow.GetProperty("localStartTime").GetString());

        var mineNow = await ReadAsync(anonymous, $"/api/public/bookings/{mine.Token}");
        Assert.Equal("20:00:00", mineNow.GetProperty("localStartTime").GetString());
    }

    [SkippableFact]
    public async Task The_change_spends_the_links_own_budget()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        const int limit = 3;

        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:PublicBookingPermitLimit", limit.ToString())
            .With("RateLimiting:PublicBookingWindowSeconds", "60")
            .With("RateLimiting:PublicPermitLimit", "1000")
            .With("RateLimiting:PublicBranchPermitLimit", "1000")
            .With("RateLimiting:GlobalPermitLimit", "1000")
            .With("RateLimiting:DinerWritePermitLimit", "1000");

        var branch = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));
        var date = MoveToNoonTomorrow(factory).AddDays(2);

        var booking = await BookAsync(diner, branch, date, "18:00", branch.FirstTableId);
        var otherBooking = await BookAsync(diner, branch, date.AddDays(1), "18:00", branch.FirstTableId);
        using var anonymous = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < limit + 2; i++)
        {
            statuses.Add((await anonymous.PostAsJsonAsync(
                $"/api/public/bookings/{booking.Token}/reschedule",
                new
                {
                    date = Iso(date),
                    time = "20:00",
                    partySize = 2,
                    diningTableId = branch.FirstTableId,
                    clientCommandId = Guid.CreateVersion7(),
                })).StatusCode);
        }

        // Exactly the budget succeeds - each a real 200, not merely "not a 429" - and the rest are refused.
        Assert.Equal(
            [.. Enumerable.Repeat(HttpStatusCode.OK, limit), .. Enumerable.Repeat(HttpStatusCode.TooManyRequests, 2)],
            statuses);

        // The read shares the budget; another booking's link has its own.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.GetAsync($"/api/public/bookings/{booking.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/public/bookings/{otherBooking.Token}")).StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Booked(Guid Id, string Token, string Code, DateTime StartUtc);

    private async Task<AuthBranch> ArrangeAsync(YallaApiFactory factory, bool acceptsWebBookings = false)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);

        if (acceptsWebBookings)
        {
            var stored = await db.Branches.FirstAsync(b => b.Id == branch.BranchId);
            stored.SetAcceptsWebBookings(true);
            await db.SaveChangesAsync();
        }

        return branch;
    }

    /// <summary>
    /// Moves the API's clock to 12:00 at the branch tomorrow - after every token is issued - and answers
    /// with that date.
    /// </summary>
    private static DateOnly MoveToNoonTomorrow(YallaApiFactory factory)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), Yerevan);
        var date = DateOnly.FromDateTime(localNow).AddDays(1);

        factory.Clock.UtcNow = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(12, 0)), Yerevan), DateTimeKind.Utc);

        return date;
    }

    private static async Task<Booked> BookAsync(
        HttpClient diner,
        AuthBranch branch,
        DateOnly date,
        string time,
        Guid tableId,
        ReservationChannel channel = ReservationChannel.Unknown)
    {
        var created = await diner.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                branchId = branch.BranchId,
                tableId,
                date = Iso(date),
                time,
                partySize = 2,
                guestName = "Ani Link",
                guestPhone = "+37411223366",
                clientCommandId = Guid.CreateVersion7(),
                channel,
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        return new Booked(
            body.GetProperty("id").GetGuid(),
            body.GetProperty("manageToken").GetString()!,
            body.GetProperty("code").GetString()!,
            body.GetProperty("startUtc").GetDateTime());
    }

    /// <summary>The branch's slug pair, as its public page is addressed.</summary>
    private async Task<(string VenueSlug, string BranchSlug)> SlugsAsync(YallaApiFactory factory, AuthBranch branch)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var slugs = await db.Branches.AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => new { VenueSlug = b.Venue.Slug, b.Slug })
            .SingleAsync();

        return (slugs.VenueSlug, slugs.Slug);
    }

    /// <summary>The <c>manageUrl</c> of every unsent reminder and late nudge of the booking.</summary>
    private async Task<List<string>> ReminderManageUrlsAsync(YallaApiFactory factory, Guid reservationId)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var payloads = await db.OutboxMessages.AsNoTracking()
            .Where(m => (m.Type == "reservation.reminder" || m.Type == "reservation.late-nudge")
                        && m.SentAtUtc == null
                        && m.IdempotencyKey.Contains(reservationId.ToString()))
            .Select(m => m.PayloadJson)
            .ToListAsync();

        Assert.NotEmpty(payloads);

        return [.. payloads.Select(p => JsonDocument.Parse(p).RootElement.GetProperty("manageUrl").GetString()!).Distinct()];
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A string shaped like a real token, so the unknown case is not refused for its form.</summary>
    private static string NewTokenLikeString() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The problem body without what differs per request by design: the trace id, and the path the
    /// caller sent (which names the route and the token they typed, and so tells them nothing).
    /// </summary>
    private static string WithoutPerRequest(string body)
    {
        using var document = JsonDocument.Parse(body);

        return string.Join(
            "|",
            document.RootElement.EnumerateObject()
                .Where(p => p.Name is not ("traceId" or "instance"))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value.GetRawText()}"));
    }

    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
