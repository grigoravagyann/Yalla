using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yalla.Application.Telegram;
using Yalla.Infrastructure.Telegram;

namespace Yalla.UnitTests;

/// <summary>
/// The thin HTTP client over Telegram's Bot API: the request shapes it sends, and the one thing it
/// must never do, which is let the bot token out through a log line or an exception.
/// </summary>
public class TelegramBotClientTests
{
    private const string Token = "123456789:AAE-this-is-the-secret-token";

    private static TelegramOptions Options() => new()
    {
        Enabled = true,
        BotToken = Token,
        BotUsername = "YallaTestBot",
        ApiBaseUrl = "https://telegram.example.test",
    };

    [Fact]
    public async Task Send_message_posts_json_to_the_method_url_with_the_contact_keyboard()
    {
        var handler = new RecordingHandler("""{"ok":true,"result":{}}""");
        var client = Client(handler, out _);

        await client.SendMessageAsync(42, "Share your number", new TelegramRequestContactKeyboard("Share"), CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://telegram.example.test/bot{Token}/sendMessage", request.Url);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;

        Assert.Equal(42, root.GetProperty("chat_id").GetInt64());
        Assert.Equal("Share your number", root.GetProperty("text").GetString());

        var keyboard = root.GetProperty("reply_markup");
        var button = keyboard.GetProperty("keyboard")[0][0];

        Assert.Equal("Share", button.GetProperty("text").GetString());
        Assert.True(button.GetProperty("request_contact").GetBoolean());
        Assert.True(keyboard.GetProperty("one_time_keyboard").GetBoolean());
        Assert.True(keyboard.GetProperty("resize_keyboard").GetBoolean());
    }

    [Fact]
    public async Task Removing_the_keyboard_and_sending_plain_text_use_the_matching_shapes()
    {
        var handler = new RecordingHandler("""{"ok":true,"result":{}}""");
        var client = Client(handler, out _);

        await client.SendMessageAsync(42, "Done", new TelegramRemoveKeyboard(), CancellationToken.None);
        await client.SendMessageAsync(42, "Hm?", replyMarkup: null, CancellationToken.None);

        using var removed = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.True(removed.RootElement.GetProperty("reply_markup").GetProperty("remove_keyboard").GetBoolean());

        using var plain = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.False(plain.RootElement.TryGetProperty("reply_markup", out _));

        // Exactly two markups exist. There is deliberately no inline-URL button: Telegram accepts
        // only http(s) and tg:// there and refused the app's yalla:// link with a 400.
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Set_webhook_sends_the_url_and_the_secret_token()
    {
        var handler = new RecordingHandler("""{"ok":true,"result":true}""");
        var client = Client(handler, out _);

        await client.SetWebhookAsync("https://api.yalla.app/api/telegram/webhook", "hook-secret", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://telegram.example.test/bot{Token}/setWebhook", request.Url);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("https://api.yalla.app/api/telegram/webhook", body.RootElement.GetProperty("url").GetString());
        Assert.Equal("hook-secret", body.RootElement.GetProperty("secret_token").GetString());

        // Only messages: the bot has no business receiving channel posts or inline queries.
        Assert.Equal("message", body.RootElement.GetProperty("allowed_updates")[0].GetString());
    }

    [Fact]
    public async Task Get_updates_long_polls_from_the_offset_and_parses_what_comes_back()
    {
        var handler = new RecordingHandler(
            """{"ok":true,"result":[{"update_id":7,"message":{"message_id":1,"from":{"id":1001,"is_bot":false,"first_name":"Ani","language_code":"hy"},"chat":{"id":1001,"type":"private"},"date":1,"text":"/start abc"}},{"update_id":8,"message":{"message_id":2,"from":{"id":1001,"is_bot":false,"first_name":"Ani"},"chat":{"id":1001,"type":"private"},"date":2,"contact":{"phone_number":"37411223344","first_name":"Ani","user_id":1001}}}]}""");
        var client = Client(handler, out _);

        var updates = await client.GetUpdatesAsync(offset: 7, timeoutSeconds: 25, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(7, body.RootElement.GetProperty("offset").GetInt64());
        Assert.Equal(25, body.RootElement.GetProperty("timeout").GetInt32());

        Assert.Equal(2, updates.Count);
        Assert.Equal(7, updates[0].UpdateId);
        Assert.Equal("/start abc", updates[0].Message!.Text);
        Assert.Equal(1001, updates[0].Message!.From!.Id);
        Assert.Equal("hy", updates[0].Message!.From!.LanguageCode);
        Assert.Equal(1001, updates[0].Message!.Chat.Id);
        Assert.Equal("37411223344", updates[1].Message!.Contact!.PhoneNumber);
        Assert.Equal(1001, updates[1].Message!.Contact!.UserId);
    }

    [Fact]
    public async Task A_failure_never_puts_the_token_in_the_exception_or_the_log()
    {
        var handler = new RecordingHandler("""{"ok":false,"error_code":401,"description":"Unauthorized"}""", HttpStatusCode.Unauthorized);
        var client = Client(handler, out var log);

        var thrown = await Assert.ThrowsAsync<TelegramApiException>(() =>
            client.SendMessageAsync(42, "hi", null, CancellationToken.None));

        Assert.DoesNotContain(Token, thrown.ToString(), StringComparison.Ordinal);
        Assert.Contains("sendMessage", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("401", thrown.Message, StringComparison.Ordinal);

        // And when the transport itself fails - the message of which carries the request URL.
        var dead = new RecordingHandler(exception: new HttpRequestException($"No such host: https://telegram.example.test/bot{Token}/sendMessage"));
        var deadClient = Client(dead, out var deadLog);

        var transport = await Assert.ThrowsAsync<TelegramApiException>(() =>
            deadClient.SendMessageAsync(42, "hi", null, CancellationToken.None));

        Assert.DoesNotContain(Token, transport.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", transport.Message, StringComparison.Ordinal);

        foreach (var line in log.Lines.Concat(deadLog.Lines))
        {
            Assert.DoesNotContain(Token, line, StringComparison.Ordinal);
        }
    }

    private static TelegramBotClient Client(RecordingHandler handler, out CapturingLogger<TelegramBotClient> log)
    {
        log = new CapturingLogger<TelegramBotClient>();

        return new TelegramBotClient(new HttpClient(handler), Options(), log);
    }

    private sealed class RecordingHandler(
        string body = "{}",
        HttpStatusCode status = HttpStatusCode.OK,
        Exception? exception = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (exception is not null)
            {
                throw exception;
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
