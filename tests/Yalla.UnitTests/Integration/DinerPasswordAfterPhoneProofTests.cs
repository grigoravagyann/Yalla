using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A diner who forgot their password sets a new one by proving the phone again: for ten minutes after
/// a code or Telegram sign-in, <c>PUT /api/diner/me/password</c> needs no <c>currentPassword</c>.
/// </summary>
/// <remarks>
/// The proof rides on the access token those two sign-ins mint (<c>phone_proved_at</c>), so a
/// password sign-in, a refreshed token and a proof gone stale are all back to needing the current
/// password - and say so as a 422 on <c>currentPassword</c>.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DinerPasswordAfterPhoneProofTests(SqlServerFixture fixture)
{
    private const string Original = "khachapuri-2026";
    private const string Replacement = "dolma-and-lavash-9";
    private const string NeedsCurrent = "Enter your current password, or sign in again with a code to set a new one.";

    [SkippableFact]
    public async Task A_code_sign_in_sets_a_new_password_without_the_old_one()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();
        var account = await RegisterAsync(anonymous);

        // Proved under the account's own token, so its password survives the proof.
        var proved = await SignInByCodeAsync(factory, account);
        using var me = factory.CreateClientWithToken(proved.AccessToken);

        Assert.True((await me.GetFromJsonAsync<JsonElement>("/api/diner/me")).GetProperty("hasPassword").GetBoolean());

        var set = await me.PutAsJsonAsync("/api/diner/me/password", new { newPassword = Replacement });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        await AssertSignsInAsync(anonymous, account.Username, Replacement, expected: HttpStatusCode.OK);
        await AssertSignsInAsync(anonymous, account.Username, Original, expected: HttpStatusCode.Unauthorized);

        // As every password change does: the other sign-ins' refresh tokens are gone, this one's is kept.
        await using var db = fixture.CreateContext(factory.Clock);
        var live = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.SubjectId == account.DinerUserId && t.RevokedAtUtc == null)
            .Select(t => t.ChainId)
            .ToListAsync();
        Assert.All(live, chain => Assert.NotEqual(account.RegistrationChainId, chain));
    }

    [SkippableFact]
    public async Task A_telegram_sign_in_sets_a_new_password_without_the_old_one()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var bot = new FakeTelegramBotClient();
        await using var factory = NewFactory().WithTelegram(bot);
        using var anonymous = factory.CreateClient();
        var account = await RegisterAsync(anonymous);

        // Started and claimed under the account's own token, like the code above.
        using var holder = factory.CreateClientWithToken(account.AccessToken);
        var telegramUser = TelegramTestHost.NewTelegramUserId();
        var (challengeId, nonce, _) = await TelegramTestHost.StartAsync(holder);
        await TelegramTestHost.PostWebhookAsync(anonymous, TelegramTestHost.StartUpdate(telegramUser, nonce));
        await TelegramTestHost.PostWebhookAsync(anonymous, TelegramTestHost.ContactUpdate(telegramUser, telegramUser, account.Phone));

        var claimed = await (await TelegramTestHost.ClaimAsync(holder, challengeId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", claimed.GetProperty("state").GetString());

        using var me = factory.CreateClientWithToken(claimed.GetProperty("signIn").GetProperty("accessToken").GetString()!);
        Assert.True((await me.GetFromJsonAsync<JsonElement>("/api/diner/me")).GetProperty("hasPassword").GetBoolean());

        var set = await me.PutAsJsonAsync("/api/diner/me/password", new { newPassword = Replacement });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        await AssertSignsInAsync(anonymous, account.Username, Replacement, expected: HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task A_proof_older_than_ten_minutes_needs_the_current_password_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();
        var account = await RegisterAsync(anonymous);

        var proved = await SignInByCodeAsync(factory, account);
        using var me = factory.CreateClientWithToken(proved.AccessToken);

        factory.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        await AssertNeedsCurrentAsync(await me.PutAsJsonAsync("/api/diner/me/password", new { newPassword = Replacement }));

        // The current one still works on the same token, and nothing above changed it.
        await AssertSignsInAsync(anonymous, account.Username, Original, expected: HttpStatusCode.OK);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await me.PutAsJsonAsync(
                "/api/diner/me/password", new { currentPassword = Original, newPassword = Replacement })).StatusCode);
    }

    [SkippableFact]
    public async Task A_password_sign_in_or_a_refreshed_token_needs_the_current_password_and_a_wrong_one_stays_a_401()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();
        var account = await RegisterAsync(anonymous);

        // A password sign-in proved nothing about the phone.
        var login = await (await anonymous.PostAsJsonAsync(
            "/api/auth/diner/login", new { identifier = account.Username, password = Original }))
            .Content.ReadFromJsonAsync<JsonElement>();
        using var me = factory.CreateClientWithToken(login.GetProperty("accessToken").GetString()!);

        await AssertNeedsCurrentAsync(await me.PutAsJsonAsync("/api/diner/me/password", new { newPassword = Replacement }));

        // Nor does a refresh - not even of a sign-in that did prove it.
        var proved = await SignInByCodeAsync(factory, account);
        var refreshed = await anonymous.PostAsJsonAsync("/api/auth/diner/refresh", new { refreshToken = proved.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        using var afterRefresh = factory.CreateClientWithToken(
            (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        await AssertNeedsCurrentAsync(
            await afterRefresh.PutAsJsonAsync("/api/diner/me/password", new { newPassword = Replacement }));

        // A wrong current password is what it always was: 401 invalid-credentials, which the app maps.
        var wrong = await me.PutAsJsonAsync("/api/diner/me/password", new { currentPassword = "not-it-at-all", newPassword = Replacement });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid-credentials", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // And the right one works.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await me.PutAsJsonAsync(
                "/api/diner/me/password", new { currentPassword = Original, newPassword = Replacement })).StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private sealed record Account(
        Guid DinerUserId, string Username, string Phone, string AccessToken, Guid RegistrationChainId);

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);

    private async Task<Account> RegisterAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var phone = $"+3747{Random.Shared.Next(1_000_000, 9_999_999)}";

        var response = await client.PostAsJsonAsync("/api/auth/diner/register", new
        {
            username = $"pw_{suffix}",
            email = $"pw-{suffix}@example.test",
            password = Original,
            phoneE164 = phone,
            displayName = "Ani",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var dinerUserId = body.GetProperty("dinerUserId").GetGuid();

        await using var db = fixture.CreateContext(new TestClock(DateTime.UtcNow));
        var chain = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.SubjectId == dinerUserId)
            .Select(t => t.ChainId)
            .SingleAsync();

        return new Account(dinerUserId, $"pw_{suffix}", phone, body.GetProperty("accessToken").GetString()!, chain);
    }

    /// <summary>The code flow under the account's own token, reading the code out of the Development response.</summary>
    private static async Task<(string AccessToken, string RefreshToken)> SignInByCodeAsync(YallaApiFactory factory, Account account)
    {
        using var holder = factory.CreateClientWithToken(account.AccessToken);

        var requested = await holder.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = account.Phone });
        requested.EnsureSuccessStatusCode();
        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("developmentCode").GetString();

        var verified = await holder.PostAsJsonAsync("/api/auth/diner/verify-code", new { phoneE164 = account.Phone, code });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        var body = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(account.DinerUserId, body.GetProperty("dinerUserId").GetGuid());

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private static async Task AssertNeedsCurrentAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation-failed", problem.GetProperty("code").GetString());
        Assert.Equal("currentPassword", problem.GetProperty("context").GetProperty("field").GetString());
        Assert.Equal(NeedsCurrent, problem.GetProperty("detail").GetString());
    }

    private static async Task AssertSignsInAsync(HttpClient client, string username, string password, HttpStatusCode expected) =>
        Assert.Equal(
            expected,
            (await client.PostAsJsonAsync("/api/auth/diner/login", new { identifier = username, password })).StatusCode);
}
