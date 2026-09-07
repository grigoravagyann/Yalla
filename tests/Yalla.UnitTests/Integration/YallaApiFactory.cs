using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The real API, hosted in-process, for the tests that can only be written end to end.
/// </summary>
/// <remarks>
/// <para>
/// Most of what this task adds is pipeline behaviour: a policy that compares a claim to a route
/// value, a middleware that answers 404, a token handler that checks a device row. None of that
/// can be tested by calling a class - the interesting failure is "the policy was never applied to
/// the endpoint", which only a real request through the real pipeline can catch.
/// </para>
/// <para>
/// The environment is <c>Development</c> on purpose. It is the environment where Swagger and the
/// verification-code-in-response affordance are meant to be on, so gating them off here proves
/// the gate is the <i>setting</i> rather than the environment name - which is the difference the
/// spec asks for. <c>DevActor:Enabled</c> is forced off so the real claims-based actor is the one
/// under test.
/// </para>
/// </remarks>
public sealed class YallaApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Header the tests use to say where a request came from, so the Swagger IP allowlist has
    /// something to allow or refuse.
    /// </summary>
    /// <remarks>
    /// <c>TestServer</c> leaves <c>RemoteIpAddress</c> null, and there is no way to set it from
    /// outside. The middleware that reads this header is registered <b>only</b> by this factory,
    /// through <c>ConfigureTestServices</c>, so nothing in the shipped application trusts a
    /// client-supplied address.
    /// </remarks>
    public const string RemoteAddressHeader = "X-Test-Remote-Address";

    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase)
    {
        // A syntactically valid string so startup succeeds. Tests that touch the database replace
        // it; tests that do not never open a connection, because nothing connects at startup.
        ["ConnectionStrings:Yalla"] = "Server=localhost;Database=Yalla_Unused;Trusted_Connection=True",

        // The API's own signing key lives in user secrets, which are keyed to the entry assembly
        // and therefore not loaded under the test host. Supplying one here is not a workaround -
        // it is what a deployed environment does with Jwt__SigningKey.
        ["Jwt:SigningKey"] = "test-signing-key-that-is-comfortably-longer-than-thirty-two-bytes",

        ["DevActor:Enabled"] = "false",
        ["Swagger:Enabled"] = "false",

        // Off unless a test asks for it. Throttling is not what most of these tests are about,
        // and a shared fixed window would make them order-dependent.
        ["RateLimiting:Enabled"] = "false",

        // Development refuses to start without a platform admin configured, which is the point of
        // that rule. The values are present so startup passes; seeding at startup is off because
        // most tests never open a database, and the ones that need an admin create one directly.
        ["PlatformAdmin:Email"] = "platform@test.yalla",
        ["PlatformAdmin:Password"] = "platform-admin-test-password",
        ["PlatformAdmin:SeedOnStartup"] = "false",

        // Required outside Development, and for the same reason as the signing key above: this is
        // what a deployment supplies as PublicWeb__ManageBookingUrlTemplate, not a workaround for
        // the check. A test host running as Staging is a deployment-shaped host and has to look
        // like one. The tests that are about the check itself override this.
        ["PublicWeb:ManageBookingUrlTemplate"] = "https://test.yalla.app/booking/{token}",
    };

    /// <summary>Overrides one configuration value. Chainable, and applied before the host starts.</summary>
    public YallaApiFactory With(string key, string? value)
    {
        _settings[key] = value;

        return this;
    }

    /// <summary>
    /// Runs the host as another environment.
    /// </summary>
    /// <remarks>
    /// The environment decides real behaviour here - the CORS policy, HTTPS redirection, whether
    /// the platform-admin configuration is mandatory - so the branches that are not Development
    /// are only reachable in a test through this.
    /// </remarks>
    public YallaApiFactory WithEnvironment(string environmentName)
    {
        _environment = environmentName;

        return this;
    }

    private string _environment = Environments.Development;

    /// <summary>Points the API at a test database.</summary>
    public YallaApiFactory WithDatabase(string connectionString) =>
        With("ConnectionStrings:Yalla", connectionString);

    /// <summary>Turns Swagger on, optionally behind an IP allowlist.</summary>
    public YallaApiFactory WithSwagger(bool enabled, params string[] allowedIps)
    {
        With("Swagger:Enabled", enabled ? "true" : "false");

        for (var i = 0; i < allowedIps.Length; i++)
        {
            With($"Swagger:AllowedIps:{i}", allowedIps[i]);
        }

        return this;
    }

    /// <summary>
    /// The clock the hosted API runs on, so a test can make a five-minute code expire without
    /// waiting five minutes.
    /// </summary>
    /// <remarks>
    /// Replacing <c>IClock</c> is the whole reason it is an injected dependency rather than a
    /// static call. Everything time-dependent in this system - code expiry, session idleness, the
    /// receipt grace period - is testable through this one property.
    /// </remarks>
    public TestClock Clock { get; } = new(DateTime.UtcNow);

    /// <summary>A client that presents <paramref name="accessToken"/> as its bearer token.</summary>
    public HttpClient CreateClientWithToken(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseContentRoot(ApiContentRoot());

        // UseSetting, not only ConfigureAppConfiguration.
        //
        // A ConfigureAppConfiguration source is not merged until the host is built, which is
        // after Program has already read the configuration to register services - so it would
        // arrive too late for the connection string, the signing key and DevActor:Enabled, and
        // the tests would silently run against the developer's own database with the actor stub
        // switched on. UseSetting writes into the builder's configuration immediately.
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        // And again as a source, so anything read after the host is built agrees with the above.
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(_settings));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, RemoteAddressStartupFilter>();
            services.AddSingleton<Yalla.Application.Abstractions.IClock>(Clock);
        });
    }

    /// <summary>
    /// The API project directory, found by walking up to the solution file.
    /// </summary>
    /// <remarks>
    /// Without this the host takes the test project's output directory as its content root and
    /// never finds <c>appsettings.Development.json</c> - so the tests would be exercising a
    /// configuration no deployment has.
    /// </remarks>
    private static string ApiContentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yalla.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException(
                "Could not find Yalla.sln above the test output directory, so the API content root "
                + "is unknown.")
            : Path.Combine(directory.FullName, "src", "Yalla.Api");
    }
}

/// <summary>
/// Test-only: sets <c>RemoteIpAddress</c> from a header so the IP allowlist has something to act
/// on.
/// </summary>
/// <remarks>
/// Registered ahead of the application's own pipeline, which is exactly where it has to be for
/// the Swagger middleware to see the address. It exists only in the test host.
/// </remarks>
internal sealed class RemoteAddressStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            if (context.Request.Headers.TryGetValue(YallaApiFactory.RemoteAddressHeader, out var raw)
                && IPAddress.TryParse(raw.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await following();
        });

        next(app);
    };
}
