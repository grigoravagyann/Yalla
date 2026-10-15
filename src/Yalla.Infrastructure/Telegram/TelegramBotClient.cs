using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yalla.Application.Telegram;

namespace Yalla.Infrastructure.Telegram;

/// <summary>
/// A Bot API call failed. The message names the method and what Telegram said; it never carries
/// the request URL, because the URL carries the token.
/// </summary>
public sealed class TelegramApiException(string message) : Exception(message);

/// <summary>
/// The Bot API over a typed <see cref="HttpClient"/>: three methods, JSON in, JSON out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The token is in the URL path</b> - that is how Telegram's API is shaped - and an
/// <see cref="HttpRequestException"/> quotes the URL in its message. So every failure is caught
/// here and rethrown as a <see cref="TelegramApiException"/> built from what Telegram answered,
/// with the token scrubbed out of any text that might have come from the transport, and nothing
/// here ever logs a request URL. The test for this class asserts the token is absent from both the
/// exception and the log.
/// </para>
/// </remarks>
internal sealed class TelegramBotClient(
    HttpClient http,
    TelegramOptions options,
    ILogger<TelegramBotClient> logger) : ITelegramBotClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SendMessageAsync(long chatId, string text, TelegramReplyMarkup? replyMarkup, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
        };

        switch (replyMarkup)
        {
            case TelegramRequestContactKeyboard keyboard:
                payload["reply_markup"] = new
                {
                    keyboard = new[] { new[] { new { text = keyboard.ButtonText, request_contact = true } } },
                    one_time_keyboard = true,
                    resize_keyboard = true,
                };
                break;

            case TelegramRemoveKeyboard:
                payload["reply_markup"] = new { remove_keyboard = true };
                break;
        }

        using var _ = await CallAsync("sendMessage", payload, ct);
    }

    public async Task SetWebhookAsync(string url, string secretToken, CancellationToken ct)
    {
        using var _ = await CallAsync(
            "setWebhook",
            new Dictionary<string, object?>
            {
                ["url"] = url,
                ["secret_token"] = secretToken,

                // Only messages: the bot has no business receiving channel posts or inline queries.
                ["allowed_updates"] = new[] { "message" },
            },
            ct);
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long? offset, int timeoutSeconds, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["timeout"] = timeoutSeconds,
            ["allowed_updates"] = new[] { "message" },
        };

        if (offset is { } from)
        {
            payload["offset"] = from;
        }

        using var document = await CallAsync("getUpdates", payload, ct);

        if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var updates = new List<TelegramUpdate>();

        foreach (var element in result.EnumerateArray())
        {
            if (TelegramUpdate.TryParse(element) is { } update)
            {
                updates.Add(update);
            }
        }

        return updates;
    }

    /// <summary>
    /// Posts one method call and returns Telegram's envelope, or throws a scrubbed exception.
    /// </summary>
    private async Task<JsonDocument> CallAsync(string method, Dictionary<string, object?> payload, CancellationToken ct)
    {
        var url = $"{options.ApiBaseUrl.TrimEnd('/')}/bot{options.BotToken}/{method}";

        try
        {
            using var response = await http.PostAsJsonAsync(url, payload, Json, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var description = Description(body);

                logger.LogWarning(
                    "Telegram {Method} answered {StatusCode}: {Description}",
                    method, (int)response.StatusCode, description);

                throw new TelegramApiException(
                    $"Telegram {method} answered {(int)response.StatusCode}: {description}");
            }

            return JsonDocument.Parse(body);
        }
        catch (TelegramApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The transport's message quotes the URL. Scrub it before it goes anywhere.
            var scrubbed = Scrub(ex.Message);

            logger.LogWarning("Telegram {Method} could not be reached: {Reason}", method, scrubbed);

            throw new TelegramApiException($"Telegram {method} could not be reached: {scrubbed}");
        }
    }

    /// <summary>Telegram's own words for a failure, which never include the token.</summary>
    private string Description(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("description", out var description)
                && description.ValueKind == JsonValueKind.String)
            {
                return Scrub(description.GetString() ?? string.Empty);
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the scrubbed raw text, truncated.
        }

        return Scrub(body.Length > 200 ? body[..200] : body);
    }

    private string Scrub(string text) =>
        string.IsNullOrEmpty(options.BotToken)
            ? text
            : text.Replace(options.BotToken, "[redacted]", StringComparison.Ordinal);
}
