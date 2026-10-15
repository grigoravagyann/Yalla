using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Yalla.Application.Telegram;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>deploy/.env.example</c> is the configuration a server is handed. A Production host started
/// with exactly its keys has to get past every startup guard, or the first bring-up fails on the
/// one machine with nobody watching a test run.
/// </summary>
/// <remarks>
/// The file is read as Compose reads it: a <c>KEY=VALUE</c> line per variable, <c>${NAME}</c>
/// expanded from the lines above it, <c>__</c> meaning <c>:</c> in the API's configuration. The
/// values are the example's placeholders - a placeholder that fails a guard (a short signing
/// key, a loopback URL template) fails here, which is the point. Compose-only variables
/// (<c>DOMAIN</c>, <c>YALLA_VERSION</c>, <c>SQL_SA_PASSWORD</c>) carry no <c>__</c> and are not
/// settings. The two values compose.yaml adds on top of the file are listed here by hand.
/// </remarks>
public class DeployEnvExampleTests
{
    [Fact]
    public void A_production_host_starts_with_the_example_configuration()
    {
        var settings = ReadEnvExample();

        var factory = new YallaApiFactory().WithEnvironment(Environments.Production);

        foreach (var (key, value) in settings)
        {
            factory.With(key, value);
        }

        // What compose.yaml sets beside the file (its api service's `environment` block).
        factory
            // The one value that is about the container and not the API: /app/.photos is the
            // volume mount, and a test host on a laptop must not create that folder at the root
            // of a drive. The guard it exists for (the root has to be writable) is exercised by
            // the factory's own temporary path.
            .With("PhotoStorage:RootPath", Path.Combine(Path.GetTempPath(), "yalla-test-photos"))
            .With("ForwardedHeaders:KnownNetworks:0", "172.28.0.0/16")
            .With("ForwardedHeaders:KnownProxies:0", "127.0.0.1")
            .With("ForwardedHeaders:ForwardLimit", "1")

            // The example switches the bot on in webhook mode, so the host registers the webhook
            // at startup. That call goes to the fake, as in every other Telegram test.
            .WithServices(services =>
            {
                services.RemoveAll<ITelegramBotClient>();
                services.AddSingleton<ITelegramBotClient>(new FakeTelegramBotClient());
            });

        using (factory)
        {
            var failure = Record.Exception(() => factory.CreateClient());

            Assert.Null(failure);
        }
    }

    [Fact]
    public void The_example_names_every_gate_the_production_host_enforces()
    {
        var settings = ReadEnvExample();

        // The hard gates (startup fails without them) and the silent ones (startup passes and
        // something is quietly wrong). A key that disappears from the example disappears from
        // every new server, so each is pinned here.
        string[] expected =
        [
            "ConnectionStrings:Yalla",
            "Jwt:SigningKey",
            "PlatformAdmin:Email",
            "PlatformAdmin:Password",
            "Auth:PasswordResetUrlTemplate",
            "PublicWeb:ManageBookingUrlTemplate",
            "PhotoStorage:RootPath",
            "PhotoStorage:SweepIntervalMinutes",
            "Serilog:MinimumLevel:Default",
            "Notifications:Channel",
            "Telegram:Enabled",
            "Telegram:Mode",
            "Telegram:BotToken",
            "Telegram:BotUsername",
            "Telegram:WebhookSecret",
            "Telegram:WebhookPublicUrl",
        ];

        foreach (var key in expected)
        {
            Assert.True(settings.ContainsKey(key), $"deploy/.env.example no longer sets {key}.");
        }

        Assert.Equal("Webhook", settings["Telegram:Mode"]);
        Assert.Equal("Expo", settings["Notifications:Channel"]);
        Assert.StartsWith("https://yalla.am/", settings["Telegram:WebhookPublicUrl"], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ reading the file

    private static Dictionary<string, string> ReadEnvExample()
    {
        var path = Path.Combine(RepositoryRoot(), "deploy", ".env.example");
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            Assert.True(separator > 0, $"Not a KEY=VALUE line in deploy/.env.example: '{rawLine}'");

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..];

            // Compose's rule for an unquoted value: a `#` preceded by whitespace starts a comment;
            // one glued to the value (`#token={token}`) is part of it.
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0)
            {
                value = value[..comment];
            }

            value = value.Trim();

            foreach (var (name, known) in raw)
            {
                value = value.Replace("${" + name + "}", known, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("${", value, StringComparison.Ordinal);

            raw[key] = value;
        }

        return raw
            .Where(pair => pair.Key.Contains("__", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key.Replace("__", ":", StringComparison.Ordinal), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Yalla.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Yalla.sln was not found above the test binaries.");
    }
}
