using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Telegram;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The whole Telegram door, end to end through the real pipeline: start, the bot's webhook,
/// claim, and what the claimed token can do.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class TelegramVerificationFlowTests(SqlServerFixture fixture)
{
    private readonly long Ani = TelegramTestHost.NewTelegramUserId();

    private static string NewPhone() => $"+3749{Random.Shared.Next(1_000_000, 9_999_999)}";

    [SkippableFact]
    public async Task Start_returns_a_deep_link_to_the_configured_bot_and_the_polling_hints()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var (challengeId, nonce, body) = await TelegramTestHost.StartAsync(client, "hy");

        Assert.NotEqual(Guid.Empty, challengeId);
        Assert.Equal(300, body.GetProperty("expiresInSeconds").GetInt32());
        Assert.Equal(2000, body.GetProperty("pollAfterMs").GetInt32());

        // Telegram's start parameter: at most 64 characters of [A-Za-z0-9_-].
        Assert.InRange(nonce.Length, 32, 64);
        Assert.Matches("^[A-Za-z0-9_-]+$", nonce);

        // Only the hash is stored; a database dump holds no usable link.
        await using var db = fixture.CreateContext(factory.Clock);
        var stored = await db.TelegramVerifications.SingleAsync(v => v.Id == challengeId);

        Assert.Equal(Secrets.Hash(nonce), stored.NonceHash);
        Assert.Equal("hy", stored.LocaleCode);
        Assert.Equal(TelegramVerificationStatus.Pending, stored.Status);
    }

    [SkippableFact]
    public async Task Claiming_before_the_bot_has_verified_says_pending_and_nothing_else()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var (challengeId, _, _) = await TelegramTestHost.StartAsync(client);
        var response = await TelegramTestHost.ClaimAsync(client, challengeId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("pending", body.GetProperty("state").GetString());

        // Nothing else: nulls are omitted on the wire, and nothing about the challenge is said.
        Assert.False(body.TryGetProperty("signIn", out _));
        Assert.False(body.TryGetProperty("phoneE164", out _));
    }

    [SkippableFact]
    public async Task The_webhook_refuses_a_wrong_or_missing_secret_and_changes_nothing()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var bot = new FakeTelegramBotClient();
        await using var factory = NewFactory(bot);
        using var client = factory.CreateClient();

        var (challengeId, nonce, _) = await TelegramTestHost.StartAsync(client);

        var wrong = await TelegramTestHost.PostWebhookAsync(client, TelegramTestHost.StartUpdate(Ani, nonce), "wrong-secret");
        var missing = await TelegramTestHost.PostWebhookAsync(client, TelegramTestHost.StartUpdate(Ani, nonce), secret: null);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        // No body: a forged caller learns nothing, not even the error envelope.
        Assert.Empty(await wrong.Content.ReadAsByteArrayAsync());

        Assert.Empty(bot.Sent);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Null((await db.TelegramVerifications.SingleAsync(v => v.Id == challengeId)).TelegramUserId);
    }

    [SkippableFact]
    public async Task A_malformed_webhook_body_still_answers_200_so_telegram_does_not_retry_it_forever()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await TelegramTestHost.PostWebhookAsync(client, "this is not json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TelegramTestHost.PostWebhookAsync(client, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TelegramTestHost.PostWebhookAsync(client, """{"update_id":1,"message":{"message_id":1,"chat":{"id":5}}}""")).StatusCode);
    }

    [SkippableFact]
    public async Task The_whole_flow_signs_in_a_verified_diner_and_the_challenge_is_spent()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var bot = new FakeTelegramBotClient();
        await using var factory = NewFactory(bot);
        using var client = factory.CreateClient();
        var phone = NewPhone();

        var (challengeId, nonce, _) = await TelegramTestHost.StartAsync(client);

        // The person opens the link: the bot asks for their contact.
        var started = await TelegramTestHost.PostWebhookAsync(client, TelegramTestHost.StartUpdate(Ani, nonce));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.IsType<TelegramRequestContactKeyboard>(bot.Last.Markup);

        // Still pending from the app's side.
        var pending = await (await TelegramTestHost.ClaimAsync(client, challengeId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", pending.GetProperty("state").GetString());

        // They tap the button; Telegram sends their own number, without the plus.
        var contact = await TelegramTestHost.PostWebhookAsync(client, TelegramTestHost.ContactUpdate(Ani, Ani, phone[1..]));
        Assert.Equal(HttpStatusCode.OK, contact.StatusCode);

        // One reply: the confirmation, keyboard taken away. It is the last thing the bot says.
        Assert.Equal(2, bot.Sent.Count);
        Assert.Equal(AuthMessages.TelegramVerified("en"), bot.Last.Text);
        Assert.IsType<TelegramRemoveKeyboard>(bot.Last.Markup);

        // The app's next poll gets exactly what verify-code returns.
        var claimed = await TelegramTestHost.ClaimAsync(client, challengeId);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        var body = await claimed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", body.GetProperty("state").GetString());

        // The number, so the app can show it without a trip to the profile.
        Assert.Equal(phone, body.GetProperty("phoneE164").GetString());

        var signIn = body.GetProperty("signIn");
        var accessToken = signIn.GetProperty("accessToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.False(string.IsNullOrWhiteSpace(signIn.GetProperty("refreshToken").GetString()));
        Assert.True(signIn.GetProperty("isNewAccount").GetBoolean());
        var dinerUserId = signIn.GetProperty("dinerUserId").GetGuid();

        // The number is proved, so the booking gate is open: the profile says so and the row says so.
        using var diner = factory.CreateClientWithToken(accessToken);
        var me = await (await diner.GetAsync("/api/diner/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(me.GetProperty("phoneVerified").GetBoolean());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.DinerUsers.SingleAsync(d => d.Id == dinerUserId);
            Assert.Equal(phone, row.PhoneE164);
            Assert.NotNull(row.PhoneVerifiedAtUtc);

            Assert.Equal(TelegramVerificationStatus.Consumed, (await db.TelegramVerifications.SingleAsync(v => v.Id == challengeId)).Status);
        }

        // Claimed once. The second claim is a 410, the same as a challenge that never existed.
        var again = await TelegramTestHost.ClaimAsync(client, challengeId);
        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        Assert.Equal("telegram-verification-expired", await ErrorCodeAsync(again));
    }

    [SkippableFact]
    public async Task A_registered_number_proved_anonymously_over_telegram_displaces_the_registrant_like_a_code_does()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var bot = new FakeTelegramBotClient();
        await using var factory = NewFactory(bot);
        using var client = factory.CreateClient();
        var phone = NewPhone();

        var registered = await client.PostAsJsonAsync("/api/auth/diner/register", new
        {
            username = $"u{Random.Shared.Next(100000, 999999)}",
            email = $"u{Random.Shared.Next(100000, 999999)}@test.yalla",
            password = "a-perfectly-fine-password",
            phoneE164 = phone,
            displayName = "Ani",
        });

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var registrant = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dinerUserId").GetGuid();

        var (challengeId, nonce, _) = await TelegramTestHost.StartAsync(client);
        await TelegramTestHost.PostWebhookAsync(client, TelegramTestHost.StartUpdate(Ani, nonce));
        await TelegramTestHost.PostWebhookAsync(client, TelegramTestHost.ContactUpdate(Ani, Ani, phone));

        var body = await (await TelegramTestHost.ClaimAsync(client, challengeId)).Content.ReadFromJsonAsync<JsonElement>();
        var signIn = body.GetProperty("signIn");

        // The same account, signed in rather than duplicated - and the registrant's password gone,
        // because the claim carried no token for that account.
        Assert.Equal(registrant, signIn.GetProperty("dinerUserId").GetGuid());
        Assert.False(signIn.GetProperty("isNewAccount").GetBoolean());

        using var diner = factory.CreateClientWithToken(signIn.GetProperty("accessToken").GetString()!);
        var me = await (await diner.GetAsync("/api/diner/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(me.GetProperty("phoneVerified").GetBoolean());
        Assert.False(me.GetProperty("hasPassword").GetBoolean());
    }

    [SkippableFact]
    public async Task An_unknown_or_expired_challenge_answers_410()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var bot = new FakeTelegramBotClient();
        await using var factory = NewFactory(bot);
        using var client = factory.CreateClient();

        var unknown = await TelegramTestHost.ClaimAsync(client, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.Gone, unknown.StatusCode);
        Assert.Equal("telegram-verification-expired", await ErrorCodeAsync(unknown));

        var (challengeId, nonce, _) = await TelegramTestHost.StartAsync(client);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));

        var expired = await TelegramTestHost.ClaimAsync(client, challengeId);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal("telegram-verification-expired", await ErrorCodeAsync(expired));

        // And the link itself is dead for the bot too.
        await TelegramTestHost.PostWebhookAsync(client, TelegramTestHost.StartUpdate(Ani, nonce));
        Assert.Equal(AuthMessages.TelegramLinkExpired("en"), bot.Last.Text);

        await using var db = fixture.CreateContext(factory.Clock);
        Assert.Equal(TelegramVerificationStatus.Expired, (await db.TelegramVerifications.SingleAsync(v => v.Id == challengeId)).Status);
    }

    [SkippableFact]
    public async Task The_claim_and_webhook_routes_carry_their_own_rate_limit_policies()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory()
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:GlobalPermitLimit", "1000")
            .With("RateLimiting:CodeRequestPermitLimit", "1000")
            .With("RateLimiting:TelegramClaimPermitLimit", "2")
            .With("RateLimiting:TelegramWebhookPermitLimit", "2");

        using var client = factory.CreateClient();
        var (challengeId, _, _) = await TelegramTestHost.StartAsync(client);

        Assert.Equal(HttpStatusCode.OK, (await TelegramTestHost.ClaimAsync(client, challengeId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TelegramTestHost.ClaimAsync(client, challengeId)).StatusCode);

        var throttled = await TelegramTestHost.ClaimAsync(client, challengeId);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("rate-limited", await ErrorCodeAsync(throttled));

        Assert.Equal(HttpStatusCode.OK, (await TelegramTestHost.PostWebhookAsync(client, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TelegramTestHost.PostWebhookAsync(client, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await TelegramTestHost.PostWebhookAsync(client, "{}")).StatusCode);
    }

    private YallaApiFactory NewFactory(FakeTelegramBotClient? bot = null) =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString).WithTelegram(bot);

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
}
