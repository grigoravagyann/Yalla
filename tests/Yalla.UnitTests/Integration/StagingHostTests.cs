using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yalla.Api.ApplicationExtensions;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A host running as Staging throttles as Production does (K10), because <c>appsettings.Staging.json</c>
/// says so - not because a test switched it on.
/// </summary>
/// <remarks>
/// The factory forces <c>RateLimiting:Enabled</c> off for every other test; these drop that setting so
/// the environment's own file decides.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class StagingHostTests(SqlServerFixture fixture)
{
    [Fact]
    public void The_staging_settings_switch_rate_limiting_on()
    {
        using var factory = new YallaApiFactory()
            .WithEnvironment(Environments.Staging)
            .Without("RateLimiting:Enabled");

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var environment = factory.Services.GetRequiredService<IHostEnvironment>();

        Assert.True(environment.IsStaging());
        Assert.True(configuration.GetValue<bool?>("RateLimiting:Enabled"));
        Assert.True(RateLimitingExtensions.IsEnabled(configuration, environment));
    }

    /// <summary>
    /// The <c>public-place</c> policy is registered and enforced, and startup never logs that rate
    /// limiting is disabled.
    /// </summary>
    [SkippableFact]
    public async Task A_staging_host_enforces_the_public_place_limiter_and_never_says_rate_limiting_is_off()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var log = new CapturedLog();

        await using (var factory = new YallaApiFactory()
                         .WithEnvironment(Environments.Staging)
                         .Without("RateLimiting:Enabled")
                         .WithDatabase(fixture.ConnectionString)
                         .With("RateLimiting:PublicPlacePermitLimit", "2")
                         .With("RateLimiting:GlobalPermitLimit", "1000")
                         .WithCapturedLog(log))
        {
            using var client = factory.CreateClient();

            // A place nobody has: 404, and each one still spends the caller's place budget.
            var route = $"/api/public/branches/{Guid.NewGuid()}";

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);

            var refused = await client.GetAsync(route);

            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal("rate-limited", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // Startup lines reach the capture - so the absence below is a real absence.
        Assert.Contains("Photo storage root is", log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Rate limiting is disabled", log.Text, StringComparison.Ordinal);
    }
}

/// <summary>
/// Every line one test host logs, whatever its logging configuration says.
/// </summary>
/// <remarks>
/// <para>
/// Serilog owns logging in the host, and an <c>ILoggerProvider</c> added in a test never sees a line.
/// Pointing its file sink somewhere else from settings did not work either, and Serilog's static
/// logger is shared by every host running in parallel. So the test replaces the host's
/// <see cref="ILoggerFactory"/> - registered after the application's, so it wins - and the capture
/// belongs to that host alone.
/// </para>
/// <para>
/// Every level is kept, so an environment's minimum level cannot hide the line a test is looking for.
/// </para>
/// </remarks>
internal sealed class CapturedLog : ILoggerFactory
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Everything logged so far, one line per entry.</summary>
    public string Text => string.Join(Environment.NewLine, _lines);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            log._lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)}");
    }
}

internal static class CapturedLogExtensions
{
    /// <summary>Sends everything this host logs to <paramref name="log"/>.</summary>
    public static YallaApiFactory WithCapturedLog(this YallaApiFactory factory, CapturedLog log) =>
        factory.WithServices(services => services.AddSingleton<ILoggerFactory>(log));
}
