using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yalla.Application.Telegram;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A bot client that records what the handler asked Telegram to send, instead of sending it.
/// </summary>
/// <remarks>
/// Everything the handler says to a person goes through <c>sendMessage</c>, so the recorded list
/// is the whole observable conversation: which chat, which text, and whether the contact keyboard
/// was shown or taken away.
/// </remarks>
internal sealed class FakeTelegramBotClient : ITelegramBotClient
{
    public List<(long ChatId, string Text, TelegramReplyMarkup? Markup)> Sent { get; } = [];

    public List<(string Url, string Secret)> Webhooks { get; } = [];

    public Task SendMessageAsync(long chatId, string text, TelegramReplyMarkup? replyMarkup, CancellationToken ct)
    {
        Sent.Add((chatId, text, replyMarkup));

        return Task.CompletedTask;
    }

    public Task SetWebhookAsync(string url, string secretToken, CancellationToken ct)
    {
        Webhooks.Add((url, secretToken));

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long? offset, int timeoutSeconds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TelegramUpdate>>([]);

    public (long ChatId, string Text, TelegramReplyMarkup? Markup) Last => Sent[^1];
}

/// <summary>Test-host settings and helpers for the Telegram routes.</summary>
internal static class TelegramTestHost
{
    public const string BotUsername = "YallaTestBot";

    public const string WebhookSecret = "test-webhook-secret-that-nobody-guesses";

    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    /// <summary>
    /// Switches the Telegram routes on in a test host, with a bot that only records.
    /// </summary>
    /// <remarks>
    /// The token is a syntactically plausible fake; nothing in a test host ever reaches
    /// api.telegram.org because the typed client is replaced by <paramref name="bot"/>.
    /// </remarks>
    public static YallaApiFactory WithTelegram(this YallaApiFactory factory, FakeTelegramBotClient? bot = null) =>
        factory
            .With("Telegram:Enabled", "true")
            .With("Telegram:WebhookPublicUrl", "")
            .With("Telegram:BotToken", "123456789:TEST-TOKEN-never-a-real-one")
            .With("Telegram:BotUsername", BotUsername)
            .With("Telegram:WebhookSecret", WebhookSecret)
            .With("Telegram:Mode", "Webhook")
            .WithServices(services =>
            {
                services.RemoveAll<ITelegramBotClient>();
                services.AddSingleton<ITelegramBotClient>(bot ?? new FakeTelegramBotClient());
            });

    /// <summary>Telegram's <c>Update</c> JSON for a private-chat <c>/start</c> command.</summary>
    public static string StartUpdate(long userId, string nonce, string languageCode = "en", int updateId = 1) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["update_id"] = updateId,
            ["message"] = new Dictionary<string, object?>
            {
                ["message_id"] = updateId,
                ["from"] = new Dictionary<string, object?>
                {
                    ["id"] = userId, ["is_bot"] = false, ["first_name"] = "Ani", ["language_code"] = languageCode,
                },
                ["chat"] = new Dictionary<string, object?> { ["id"] = userId, ["type"] = "private" },
                ["date"] = 1760000000,
                ["text"] = $"/start {nonce}",
            },
        });

    /// <summary>Telegram's <c>Update</c> JSON for a shared contact.</summary>
    /// <param name="fromUserId">Who sent the message.</param>
    /// <param name="contactUserId">Whose contact card it is - the same person when they tapped the button, somebody else when they forwarded a card.</param>
    /// <param name="phoneNumber">As Telegram sends it: digits, sometimes with and sometimes without the plus.</param>
    /// <param name="updateId">Telegram's update sequence number.</param>
    public static string ContactUpdate(long fromUserId, long? contactUserId, string phoneNumber, int updateId = 2)
    {
        var contact = new Dictionary<string, object?> { ["phone_number"] = phoneNumber, ["first_name"] = "Ani" };

        if (contactUserId is { } id)
        {
            contact["user_id"] = id;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["update_id"] = updateId,
            ["message"] = new Dictionary<string, object?>
            {
                ["message_id"] = updateId,
                ["from"] = new Dictionary<string, object?> { ["id"] = fromUserId, ["is_bot"] = false, ["first_name"] = "Ani" },
                ["chat"] = new Dictionary<string, object?> { ["id"] = fromUserId, ["type"] = "private" },
                ["date"] = 1760000001,
                ["contact"] = contact,
            },
        });
    }

    public static async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string body, string? secret = WebhookSecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (secret is not null)
        {
            request.Headers.Add(SecretHeader, secret);
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// A Telegram user id nobody else in the run uses. The tests share one database, and a contact
    /// is matched to the newest pending challenge of its sender - so two tests on one id would
    /// verify each other's challenges.
    /// </summary>
    public static long NewTelegramUserId() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);

    /// <summary>Starts a challenge and returns its id and the nonce out of the deep link.</summary>
    public static async Task<(Guid ChallengeId, string Nonce, JsonElement Body)> StartAsync(HttpClient client, string localeCode = "en")
    {
        var response = await client.PostAsJsonAsync("/api/auth/diner/telegram/start", new { localeCode });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var link = body.GetProperty("deepLinkUrl").GetString()!;
        var prefix = $"https://t.me/{BotUsername}?start=";

        Assert.StartsWith(prefix, link);

        return (body.GetProperty("challengeId").GetGuid(), link[prefix.Length..], body);
    }

    public static Task<HttpResponseMessage> ClaimAsync(HttpClient client, Guid challengeId, string localeCode = "en") =>
        client.PostAsJsonAsync($"/api/auth/diner/telegram/{challengeId}/claim", new { localeCode });
}
