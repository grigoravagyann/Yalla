using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Sign-ins and bookings for the favourites and notifications feed tests (K11, K12).
/// </summary>
/// <remarks>
/// Every number comes from <see cref="ReviewTestData.NextPhone"/>, the +374 99 000 xxx test range.
/// </remarks>
internal static class DinerFeedTestData
{
    public const string Password = "khachapuri-2026";

    /// <summary>A diner signed in with a code, so the number is proved.</summary>
    public static async Task<(string AccessToken, Guid DinerUserId)> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = ReviewTestData.NextPhone();

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = phone, code });
        verified.EnsureSuccessStatusCode();

        var body = await verified.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("dinerUserId").GetGuid());
    }

    /// <summary>A password account whose number was never proved.</summary>
    public static async Task<(string AccessToken, Guid DinerUserId)> RegisterUnprovedAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var registered = await client.PostAsJsonAsync(
            "/api/auth/diner/register",
            new
            {
                username = $"narek_{suffix}",
                email = $"narek-{suffix}@example.test",
                password = Password,
                phoneE164 = ReviewTestData.NextPhone(),
                displayName = "Narek",
            });

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var body = await registered.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("dinerUserId").GetGuid());
    }

    /// <summary>Books <paramref name="tableId"/> at 18:00 Yerevan time, <paramref name="daysOut"/> days out.</summary>
    public static async Task<Guid> BookAsync(
        HttpClient diner,
        YallaApiFactory factory,
        AuthBranch branch,
        Guid tableId,
        int daysOut,
        int partySize = 2)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        var created = await diner.PostAsJsonAsync(
            "/api/reservations",
            new
            {
                branchId = branch.BranchId,
                tableId,
                date = DateOnly.FromDateTime(localNow).AddDays(daysOut).ToString("yyyy-MM-dd"),
                time = "18:00",
                partySize,
                guestName = "Ani Feed",
                guestPhone = ReviewTestData.NextPhone(),
                clientCommandId = Guid.CreateVersion7(),
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
