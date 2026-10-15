using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// What the host does with the <c>Telegram</c> section before it serves a request: refuse to start
/// half-configured, and map nothing at all when the door is switched off.
/// </summary>
public class TelegramStartupTests
{
    [Fact]
    public void Enabled_without_a_bot_token_refuses_to_start_with_the_secret_instructions()
    {
        using var factory = new YallaApiFactory()
            .With("Telegram:Enabled", "true")

            // Blank on purpose: the Development host loads the API's user secrets, which may hold a
            // real token on a developer's machine, and this test is about its absence.
            .With("Telegram:BotToken", "")
            .With("Telegram:BotUsername", "YallaTestBot")
            .With("Telegram:WebhookSecret", "something");

        var thrown = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains("Telegram:BotToken", Flatten(thrown), StringComparison.Ordinal);
        Assert.Contains("dotnet user-secrets set", Flatten(thrown), StringComparison.Ordinal);
        Assert.Contains("Telegram__BotToken", Flatten(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_in_webhook_mode_without_a_webhook_secret_refuses_to_start()
    {
        using var factory = new YallaApiFactory()
            .With("Telegram:Enabled", "true")
            .With("Telegram:BotToken", "123:abc")
            .With("Telegram:BotUsername", "YallaTestBot")

            // Pinned: a developer's user secrets may say LongPolling, which needs no secret.
            .With("Telegram:Mode", "Webhook")
            .With("Telegram:WebhookSecret", "");

        Assert.Contains("Telegram:WebhookSecret", Flatten(Assert.ThrowsAny<Exception>(() => factory.Services)), StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_without_a_bot_username_refuses_to_start()
    {
        using var factory = new YallaApiFactory()
            .With("Telegram:Enabled", "true")
            .With("Telegram:BotToken", "123:abc")
            .With("Telegram:BotUsername", "")
            .With("Telegram:WebhookSecret", "something");

        Assert.Contains("Telegram:BotUsername", Flatten(Assert.ThrowsAny<Exception>(() => factory.Services)), StringComparison.Ordinal);
    }

    [Fact]
    public void Long_polling_is_refused_outside_development()
    {
        using var factory = new YallaApiFactory()
            .WithEnvironment(Environments.Staging)
            .WithTelegram()
            .With("Telegram:Mode", "LongPolling");

        var message = Flatten(Assert.ThrowsAny<Exception>(() => factory.Services));

        Assert.Contains("LongPolling", message, StringComparison.Ordinal);
        Assert.Contains("Development", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_mode_is_refused()
    {
        using var factory = new YallaApiFactory().WithTelegram().With("Telegram:Mode", "Carrier-Pigeon");

        Assert.Contains("Telegram:Mode", Flatten(Assert.ThrowsAny<Exception>(() => factory.Services)), StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_maps_no_telegram_route_at_all()
    {
        // Explicitly off: a developer's user secrets may switch the door on for the real API.
        using var factory = new YallaApiFactory().With("Telegram:Enabled", "false");

        Assert.Empty(TelegramRoutes(factory));
    }

    [Fact]
    public void Enabled_maps_exactly_the_three_telegram_routes()
    {
        using var factory = new YallaApiFactory().WithTelegram();

        var routes = TelegramRoutes(factory);

        Assert.Equal(
            [
                "/api/auth/diner/telegram/start",
                "/api/auth/diner/telegram/{challengeId:guid}/claim",
                "/api/telegram/webhook",
            ],
            routes.Order(StringComparer.Ordinal).ToArray());
    }

    private static List<string> TelegramRoutes(YallaApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + (e.RoutePattern.RawText ?? string.Empty).Trim('/'))
            .Where(p => p.Contains("telegram", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }

        return string.Join(" | ", parts);
    }
}
