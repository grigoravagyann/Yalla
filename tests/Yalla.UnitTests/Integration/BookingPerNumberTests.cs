using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// No booking without a proved number, anywhere; and the number rules that replaced "one active booking per
/// number" (the user's revision of 2026-10-20): no two overlapping bookings at any venue, at most three active.
/// </summary>
/// <remarks>
/// <para>
/// Yalla#54 opened two doors for a booker who never proved their number - the public page's
/// <c>POST /api/public/branches/{id}/bookings</c> and the app's booking from an unproved account. Both are shut
/// again: the page books only after verification, the app answers 403 <c>phone-not-verified</c> as it did before.
/// </para>
/// <para>
/// The overlap rule is the old one-table-at-a-time rule widened to every venue: at the same branch the sittings
/// as they are, at another one with half an hour between them to get there - two hours between starts at the
/// shipped 90-minute turn. Three active bookings is the most one number holds at once.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class BookingPerNumberTests(SqlServerFixture fixture)
{
    // ---------------------------------------------------------------- the doors

    /// <summary>The public page's door for booking without verifying is gone, and writes nothing.</summary>
    [SkippableFact]
    public async Task The_public_page_has_no_door_for_booking_without_a_verified_number()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var anonymous = factory.CreateClient();

        // Sent as #54's page sent it. Not PostAsJsonAsync: the request-shape check rightly finds no endpoint here.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/branches/{branch.BranchId}/bookings")
        {
            Content = JsonContent.Create(new
            {
                tableId = branch.FirstTableId,
                date = LocalDate(factory, 2),
                time = "18:00",
                partySize = 2,
                guestName = "Ani Web",
                guestPhone = UniquePhone(),
                deviceId = $"web-device-{Guid.NewGuid():N}",
                clientCommandId = Guid.CreateVersion7(),
            }),
        };

        var response = await anonymous.SendAsync(request);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await db.Reservations.CountAsync(r => r.BranchId == branch.BranchId));
    }

    /// <summary>An app account whose number was never proved is refused as before #54, and nothing is written.</summary>
    [SkippableFact]
    public async Task An_app_account_that_never_proved_its_number_cannot_book()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var diner = factory.CreateClientWithToken(await RegisterUnprovedDinerAsync(factory));

        var response = await diner.PostAsJsonAsync("/api/reservations", AppBooking(factory, branch, daysAhead: 2));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("phone-not-verified", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(0, await db.Reservations.CountAsync(r => r.BranchId == branch.BranchId));
    }

    /// <summary>
    /// The policy no longer carries the two unverified-booking settings - neither on the read nor in the bounds -
    /// and a console from before still saves with them in its body. The scan setting stays; the good-record
    /// setting is there, off.
    /// </summary>
    [SkippableFact]
    public async Task The_policy_has_no_unverified_booking_settings_and_an_older_console_still_saves()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await ArrangeAsync(factory);
        using var manager = factory.CreateClientWithToken(await SignInManagerAsync(factory, branch));

        var policyUrl = $"/api/branches/{branch.BranchId}/reservation-policy";
        var current = await ReadAsync(manager, policyUrl);

        Assert.False(current.TryGetProperty("unverifiedBookingsNeedApproval", out _));
        Assert.False(current.TryGetProperty("unverifiedMaxPartySize", out _));
        Assert.True(current.GetProperty("unverifiedScansNeedAdmission").GetBoolean());
        Assert.False(current.GetProperty("autoConfirmGuestsWithGoodRecord").GetBoolean());

        var older = await manager.PutAsJsonAsync(policyUrl, PolicyBody(current, legacyUnverifiedKeys: true));
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);

        var defaults = await ReadAsync(manager, $"{policyUrl}/defaults");
        Assert.DoesNotContain(
            "unverifiedMaxPartySize",
            defaults.GetProperty("bounds").EnumerateArray().Select(b => b.GetProperty("field").GetString()));
        Assert.False(defaults.GetProperty("defaults").GetProperty("autoConfirmGuestsWithGoodRecord").GetBoolean());
    }

    // ---------------------------------------------------------------- no two overlapping bookings, anywhere

    /// <summary>
    /// At another venue the two sittings need half an hour between them: with a 90-minute turn, a start less than
    /// two hours from the one held is refused - before it or after it - naming the booking held and its venue.
    /// Exactly two hours apart is fine.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_at_another_venue_less_than_two_hours_from_mine_is_refused_naming_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory);
        var there = await ArrangeAsync(factory);
        var (token, _) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        var held = await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2, time: "18:00"));

        foreach (var time in new[] { "19:30", "16:30", "19:45" })
        {
            var refused = await diner.PostAsJsonAsync("/api/reservations", AppBooking(factory, there, daysAhead: 2, time: time));

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("reservation-overlaps-own", problem.GetProperty("code").GetString());

            var context = problem.GetProperty("context");
            Assert.Equal(held.GetProperty("id").GetGuid(), context.GetProperty("reservationId").GetGuid());
            Assert.Equal(held.GetProperty("code").GetString(), context.GetProperty("code").GetString());
            Assert.False(string.IsNullOrEmpty(context.GetProperty("venueName").GetString()));
            Assert.Equal(held.GetProperty("branchName").GetString(), context.GetProperty("branchName").GetString());
        }

        // Two hours after, and two hours before: the 90-minute sitting and half an hour to get there.
        await CreatedAsync(diner, AppBooking(factory, there, daysAhead: 2, time: "20:00"));
        await CreatedAsync(diner, AppBooking(factory, there, daysAhead: 2, time: "16:00"));
    }

    /// <summary>At the same branch the rule is what it was: back to back on two tables is allowed.</summary>
    [SkippableFact]
    public async Task Back_to_back_at_the_same_branch_is_still_allowed_and_an_overlap_still_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory);
        var (token, _) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2, time: "18:00"));
        await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2, time: "19:30", tableIndex: 1));

        var refused = await diner.PostAsJsonAsync("/api/reservations", AppBooking(factory, here, daysAhead: 2, time: "18:30", tableIndex: 1));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("reservation-overlaps-own", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>Moving a booking into the sitting of one held at another venue is refused, and it stays put.</summary>
    [SkippableFact]
    public async Task A_move_close_to_my_booking_at_another_venue_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory);
        var there = await ArrangeAsync(factory);
        var (token, _) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        var held = await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2, time: "18:00"));
        var later = await CreatedAsync(diner, AppBooking(factory, there, daysAhead: 2, time: "21:00"));

        var moved = await diner.PostAsJsonAsync(
            $"/api/reservations/{later.GetProperty("id").GetGuid()}/reschedule",
            new
            {
                date = LocalDate(factory, 2),
                time = "19:00",
                partySize = 2,
                diningTableId = there.FirstTableId,
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);

        var problem = await moved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reservation-overlaps-own", problem.GetProperty("code").GetString());
        Assert.Equal(held.GetProperty("id").GetGuid(), problem.GetProperty("context").GetProperty("reservationId").GetGuid());

        await using var db = fixture.CreateContext(factory.Clock);
        var stored = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == later.GetProperty("id").GetGuid());
        Assert.Equal(new TimeOnly(21, 0), stored.LocalStartTime);
    }

    // ---------------------------------------------------------------- three active bookings per number

    /// <summary>
    /// Three active bookings at two venues on three days are fine; the fourth is refused with the limit and the
    /// earliest booking held, and cancelling one makes room again.
    /// </summary>
    [SkippableFact]
    public async Task One_number_holds_three_active_bookings_and_the_fourth_is_refused_naming_the_earliest()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory);
        var there = await ArrangeAsync(factory);
        var (token, _) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        var first = await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2));
        var second = await CreatedAsync(diner, AppBooking(factory, there, daysAhead: 3));
        await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 4));

        var fourth = await diner.PostAsJsonAsync("/api/reservations", AppBooking(factory, there, daysAhead: 5));

        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);

        var problem = await fourth.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("phone-has-active-booking", problem.GetProperty("code").GetString());
        Assert.Contains("3", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var context = problem.GetProperty("context");
        Assert.Equal(3, context.GetProperty("maxActiveBookings").GetInt32());
        Assert.Equal(first.GetProperty("id").GetGuid(), context.GetProperty("reservationId").GetGuid());
        Assert.Equal(first.GetProperty("localDate").GetString(), context.GetProperty("localDate").GetString());
        Assert.False(string.IsNullOrEmpty(context.GetProperty("venueName").GetString()));

        var cancelled = await diner.PostAsJsonAsync($"/api/reservations/{second.GetProperty("id").GetGuid()}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        await CreatedAsync(diner, AppBooking(factory, there, daysAhead: 5));
    }

    /// <summary>
    /// With two active bookings, two more sent at the same instant to two venues: exactly one is written - the
    /// number's lock (and the account's) judge them one after the other.
    /// </summary>
    [SkippableFact]
    public async Task Two_bookings_for_the_last_place_at_the_same_moment_write_exactly_one()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory);
        var there = await ArrangeAsync(factory);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        using var a = factory.CreateClientWithToken(token);
        using var b = factory.CreateClientWithToken(token);

        await CreatedAsync(a, AppBooking(factory, here, daysAhead: 2));
        await CreatedAsync(a, AppBooking(factory, here, daysAhead: 3));

        var results = await Task.WhenAll(
            a.PostAsJsonAsync("/api/reservations", AppBooking(factory, here, daysAhead: 4)),
            b.PostAsJsonAsync("/api/reservations", AppBooking(factory, there, daysAhead: 5)));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(3, await db.Reservations.CountAsync(r => r.BookerPhoneE164 == phone));
    }

    /// <summary>
    /// Deleting an account takes its number off its bookings, so whoever proves that number next starts with
    /// nothing held against it.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_an_account_frees_its_number_for_whoever_proves_it_next()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory);
        var there = await ArrangeAsync(factory);

        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        using (var diner = factory.CreateClientWithToken(token))
        {
            await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2));
            await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 3));
            await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 4));

            var deleted = await diner.SendAsync(
                new HttpRequestMessage(HttpMethod.Delete, "/api/diner/me") { Content = JsonContent.Create(new { }) });
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var (againToken, _) = await SignInVerifiedDinerAsync(factory, phone);
        using var again = factory.CreateClientWithToken(againToken);

        await CreatedAsync(again, AppBooking(factory, there, daysAhead: 2));
    }

    /// <summary>
    /// A booking is held by the account's proved number, never by the guest phone typed on it: booking for a
    /// friend under their number does not dodge the limit, and the row says how it was made.
    /// </summary>
    [SkippableFact]
    public async Task A_booking_is_held_by_the_accounts_proved_number_never_the_typed_guest_phone()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var here = await ArrangeAsync(factory);
        var (token, phone) = await SignInVerifiedDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        var created = await CreatedAsync(diner, AppBooking(factory, here, daysAhead: 2));

        await using var db = fixture.CreateContext(factory.Clock);
        var stored = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == created.GetProperty("id").GetGuid());
        Assert.Equal(phone, stored.BookerPhoneE164);
        Assert.NotEqual(phone, stored.GuestPhone);
        Assert.False(stored.PhoneUnverified);
    }

    // ---------------------------------------------------------------- helpers

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<AuthBranch> ArrangeAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
        await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, acceptsOnlineBookings: true);

        return branch;
    }

    internal static string UniquePhone() => $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}";

    internal static string LocalDate(YallaApiFactory factory, int daysAhead)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        return DateOnly.FromDateTime(localNow).AddDays(daysAhead).ToString("yyyy-MM-dd");
    }

    internal static object AppBooking(
        YallaApiFactory factory,
        AuthBranch branch,
        int daysAhead,
        string time = "18:00",
        int tableIndex = 0,
        int partySize = 2) =>
        new
        {
            branchId = branch.BranchId,
            tableId = branch.TableIds[tableIndex],
            date = LocalDate(factory, daysAhead),
            time,
            partySize,
            guestName = "Ani App",
            guestPhone = "+37411223344",
            clientCommandId = Guid.CreateVersion7(),
            channel = ReservationChannel.App,
        };

    internal static async Task<JsonElement> CreatedAsync(HttpClient diner, object booking)
    {
        var response = await diner.PostAsJsonAsync("/api/reservations", booking);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static object PolicyBody(JsonElement current, bool legacyUnverifiedKeys) =>
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

            // What a console from before this change still sends: ignored.
            unverifiedBookingsNeedApproval = legacyUnverifiedKeys ? false : (bool?)null,
            unverifiedMaxPartySize = legacyUnverifiedKeys ? 4 : (int?)null,
        };

    internal static async Task<(string Token, string Phone)> SignInVerifiedDinerAsync(YallaApiFactory factory, string? phone = null)
    {
        using var client = factory.CreateClient();
        phone ??= UniquePhone();

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

    internal static async Task<string> SignInManagerAsync(YallaApiFactory factory, AuthBranch branch)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/venue/sign-in", new { email = branch.ManagerEmail, password = branch.ManagerPassword });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    internal static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
