using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The table's evening through the real pipeline: the JSON the diner app and the public page read -
/// requestable and must-free-by tables, quarter-hour slots, the 422 that asks for a time to leave by,
/// the earlier guest's card - and the answer route with its policies and its refusals.
/// </summary>
/// <remarks>
/// Tokens are stamped with the API's clock and checked against the real one, so every token is issued
/// first and the API's clock is moved on - to noon, two days from now, at the branch - only then. The
/// rules themselves are <see cref="TableSequencingFlowTests"/>.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class TableSequencingEndpointTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task The_wire_carries_requestable_tables_the_time_to_leave_by_and_the_earlier_guests_card_and_answer()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);

        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2);
        }

        var (annaToken, _) = await DinerFeedTestData.SignInDinerAsync(factory);
        using var anna = factory.CreateClientWithToken(annaToken);
        using var gor = factory.CreateClientWithToken((await DinerFeedTestData.SignInDinerAsync(factory)).AccessToken);
        using var aram = factory.CreateClientWithToken((await DinerFeedTestData.SignInDinerAsync(factory)).AccessToken);
        using var anonymous = factory.CreateClient();

        // Anna has the app on her phone, so she is the one asked.
        var device = await anna.PostAsJsonAsync(
            "/api/diner/devices", new { pushToken = "ExponentPushToken[sequencing-anna]", platform = 1, locale = "en" });
        Assert.True(device.IsSuccessStatusCode, await device.Content.ReadAsStringAsync());

        // Noon, and the evening two days after that - another day, so nothing here is at short notice.
        var (noonUtc, today) = MoveToNoonInTwoDays(factory);
        var day = today.AddDays(2).ToString("yyyy-MM-dd");

        // Table 1: Anna at 18:00. Table 2: Anna again at 21:30.
        var annas = await BookAsync(anna, branch, branch.TableIds[0], day, "18:00");
        Assert.Equal(HttpStatusCode.Created, annas.StatusCode);
        var annasId = (await annas.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // One number holds one active booking (2026-10-20); Anna's two here are one diner's on purpose.
        await fixture.MakeBookingsFromBeforeTheNumberRuleAsync(factory.Clock, annasId);
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(anna, branch, branch.TableIds[1], day, "21:30")).StatusCode);

        // The day, every quarter hour: 19:30 on table 1 can be asked for; 20:45 on table 2 must be left by 21:25.
        var slots = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{branch.BranchId}/availability/slots?date={day}&partySize=2");

        Assert.Equal(15, slots.GetProperty("stepMinutes").GetInt32());

        var asked = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{branch.BranchId}/availability?date={day}&time=19:45&partySize=2");
        var first = asked.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("tableId").GetGuid() == branch.TableIds[0]);

        Assert.True(first.GetProperty("isAvailable").GetBoolean());
        Assert.True(first.GetProperty("isRequestable").GetBoolean());
        Assert.True(first.GetProperty("requiresApproval").GetBoolean());
        Assert.False(first.TryGetProperty("mustFreeByUtc", out _));

        var cut = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{branch.BranchId}/availability?date={day}&time=20:45&partySize=2");
        var second = cut.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("tableId").GetGuid() == branch.TableIds[1]);

        Assert.False(second.GetProperty("isRequestable").GetBoolean());
        var mustFreeBy = second.GetProperty("mustFreeByUtc").GetDateTime();
        Assert.Equal("21:25:00", second.GetProperty("mustFreeByLocal").GetString());

        // Booking it without accepting the time is refused naming the field, with the time to accept.
        var refused = await BookAsync(gor, branch, branch.TableIds[1], day, "20:45");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reservation-free-by-required", problem.GetProperty("code").GetString());

        var context = problem.GetProperty("context");
        Assert.Equal(FreeByNotAcceptedException.Field, context.GetProperty("field").GetString());
        Assert.Equal(mustFreeBy, context.GetProperty("requiredFreeByUtc").GetDateTime());
        Assert.Equal("21:25", context.GetProperty("requiredFreeByLocal").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("acceptsFreeByUtc", out _));

        var accepted = await BookAsync(gor, branch, branch.TableIds[1], day, "20:45", acceptsFreeByUtc: mustFreeBy);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var gors = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(mustFreeBy, gors.GetProperty("promisedFreeByUtc").GetDateTime());

        // Table 1 at 19:45 as well would be Gor's second table that evening at once: refused, naming the
        // booking he already has, so the app can say "You already have a booking at 20:45 (table 2)". The
        // own-overlap rule answers before the one-booking-per-number rule, which would refuse it too.
        var twice = await BookAsync(gor, branch, branch.TableIds[0], day, "19:45");
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

        var overlap = await twice.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reservation-overlaps-own", overlap.GetProperty("code").GetString());

        var own = overlap.GetProperty("context");
        Assert.Equal(gors.GetProperty("id").GetGuid(), own.GetProperty("reservationId").GetGuid());
        Assert.Equal(gors.GetProperty("code").GetString(), own.GetProperty("code").GetString());
        Assert.Equal("20:45:00", own.GetProperty("localStartTime").GetString());
        Assert.Equal(day, own.GetProperty("localDate").GetString());
        Assert.Equal(gors.GetProperty("tableLabel").GetString(), own.GetProperty("tableLabel").GetString());

        // Aram asks for table 1 at 19:45: he waits, and Anna is asked.
        var waiting = await BookAsync(aram, branch, branch.TableIds[0], day, "19:45");
        Assert.Equal(HttpStatusCode.Created, waiting.StatusCode);

        var waitingBody = await waiting.Content.ReadFromJsonAsync<JsonElement>();
        var waitingId = waitingBody.GetProperty("id").GetGuid();
        Assert.Equal((int)ReservationStatus.PendingApproval, waitingBody.GetProperty("status").GetInt32());
        Assert.Equal((int)ApprovalTrigger.EarlierGuestConsent, waitingBody.GetProperty("awaitingApprovalBecause").GetInt32());
        Assert.Equal(noonUtc.AddHours(2), waitingBody.GetProperty("approvalExpiresAtUtc").GetDateTime());
        Assert.True(waitingBody.GetProperty("awaitsEarlierGuestAnswer").GetBoolean());
        Assert.False(gors.GetProperty("awaitsEarlierGuestAnswer").GetBoolean());

        var mine = await anna.GetFromJsonAsync<JsonElement>("/api/reservations/mine");
        var hers = mine.GetProperty("upcoming").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == annasId);
        var card = hers.GetProperty("incomingTableRequest");

        var requestId = card.GetProperty("requestId").GetGuid();
        Assert.Equal("19:45:00", card.GetProperty("requestedLocalTime").GetString());
        Assert.Equal("19:40:00", card.GetProperty("freeByLocalTime").GetString());
        Assert.Equal(noonUtc.AddHours(2), card.GetProperty("expiresAtUtc").GetDateTime());
        Assert.True(card.TryGetProperty("requestedStartUtc", out _));
        Assert.True(card.TryGetProperty("freeByUtc", out _));

        // Aram's own view of his booking shows no question - it is not his to answer.
        var aramsMine = await aram.GetFromJsonAsync<JsonElement>("/api/reservations/mine");
        Assert.All(aramsMine.GetProperty("upcoming").EnumerateArray(), r => Assert.False(r.TryGetProperty("incomingTableRequest", out _)));

        var route = $"/api/reservations/{annasId}/table-requests/{requestId}/answer";

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(route, new { accept = true, clientCommandId = Guid.CreateVersion7() })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await gor.PostAsJsonAsync(route, new { accept = true, clientCommandId = Guid.CreateVersion7() })).StatusCode);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await anna.PostAsJsonAsync(route, new { clientCommandId = Guid.CreateVersion7() })).StatusCode);

        var yes = await anna.PostAsJsonAsync(route, new { accept = true, clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.OK, yes.StatusCode);

        var yesBody = await yes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(annasId, yesBody.GetProperty("id").GetGuid());
        Assert.True(yesBody.TryGetProperty("promisedFreeByUtc", out _));
        Assert.False(yesBody.TryGetProperty("incomingTableRequest", out _));

        // The other answer now is too late.
        var no = await anna.PostAsJsonAsync(route, new { accept = false, clientCommandId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.Conflict, no.StatusCode);

        var late = await no.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("table-request-not-pending", late.GetProperty("code").GetString());
        Assert.Equal((int)TableShareRequestStatus.Accepted, late.GetProperty("context").GetProperty("status").GetInt32());

        var aramsNow = await aram.GetFromJsonAsync<JsonElement>("/api/reservations/mine");
        Assert.Equal(
            (int)ReservationStatus.Confirmed,
            aramsNow.GetProperty("upcoming").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == waitingId)
                .GetProperty("status").GetInt32());
    }

    private static (DateTime NoonUtc, DateOnly Date) MoveToNoonInTwoDays(YallaApiFactory factory)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);
        var date = DateOnly.FromDateTime(localNow).AddDays(2);

        var noonUtc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(12, 0)), zone);
        factory.Clock.UtcNow = DateTime.SpecifyKind(noonUtc, DateTimeKind.Utc);

        return (factory.Clock.UtcNow, date);
    }

    private static Task<HttpResponseMessage> BookAsync(
        HttpClient diner,
        AuthBranch branch,
        Guid tableId,
        string date,
        string time,
        DateTime? acceptsFreeByUtc = null) =>
        diner.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                branchId = branch.BranchId,
                tableId,
                date,
                time,
                partySize = 2,
                guestName = "Ani Wire",
                guestPhone = "+37411223344",
                clientCommandId = Guid.CreateVersion7(),
                acceptsFreeByUtc,
            });
}
