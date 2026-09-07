using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The two rules that are about the <i>environment</i> rather than about a request: Development
/// refuses to start without a way in, and CORS outside Development is an explicit allowlist.
/// </summary>
/// <remarks>
/// Both branches are invisible to every other test, because the shared factory runs Development
/// with the platform admin configured. A rule with no test is a rule that survives being inverted.
/// </remarks>
public class StartupAndCorsEnvironmentTests
{
    // ------------------------------------------------------------ the startup guard

    /// <remarks>
    /// The key is blanked rather than dropped. Dropping it only makes the setting absent for
    /// factories that have nothing else supplying it - and a developer who followed README.md has
    /// these two in user secrets, which Development loads. A test that passes only on an unset-up
    /// machine is a test that fails for everyone who can actually run the app.
    /// </remarks>
    [Theory]
    [InlineData("PlatformAdmin:Email")]
    [InlineData("PlatformAdmin:Password")]
    public void Development_refuses_to_start_without_the_platform_admin_configured(string missingKey)
    {
        using var factory = new YallaApiFactory().With(missingKey, string.Empty);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);

        // The message has to carry the fix, because the person hitting it has a database with
        // nobody who can sign in and no endpoint that would create one.
        var text = failure!.ToString();
        Assert.Contains("PlatformAdmin:Email", text, StringComparison.Ordinal);
        Assert.Contains("user-secrets", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_environment_starts_without_it_because_a_deployment_supplies_it_or_does_not_seed()
    {
        using var factory = new YallaApiFactory()
            .WithEnvironment(Environments.Staging)
            .With("PlatformAdmin:Email", string.Empty)
            .With("PlatformAdmin:Password", string.Empty);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.Null(failure);
    }

    // ------------------------------------------------------------ the manage-booking link

    /// <summary>
    /// Outside Development, a manage-booking URL that no diner could use refuses to start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one setting whose absence writes broken data rather than failing a request. The
    /// URL goes into a web booking's reminder payload when the booking is made, and the server
    /// keeps only the token's hash - so a booking created under a wrong value carries a dead cancel
    /// link permanently, and nothing later can mint the token again to repair it.
    /// </para>
    /// <para>
    /// The diner holding that link has no app and no other way to cancel, which makes a dead link
    /// the no-show the manage-booking feature was built to prevent.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("", "it is not set")]
    [InlineData("   ", "it is not set")]
    [InlineData("https://yalla.app/booking", "{token}")]
    [InlineData("/booking/{token}", "absolute")]
    [InlineData("ftp://yalla.app/booking/{token}", "absolute")]
    [InlineData("http://localhost:5173/booking/{token}", "loopback")]
    [InlineData("https://127.0.0.1/booking/{token}", "loopback")]
    public void An_unusable_manage_booking_url_refuses_to_start_outside_Development(
        string template, string expected)
    {
        using var factory = new YallaApiFactory()
            .WithEnvironment(Environments.Staging)
            .With("PublicWeb:ManageBookingUrlTemplate", template);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);

        var text = failure!.ToString();

        // The message carries the fix and the reason, because the person reading it is looking at
        // a process that will not start.
        Assert.Contains("PublicWeb__ManageBookingUrlTemplate", text, StringComparison.Ordinal);
        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be repaired", text, StringComparison.Ordinal);
    }

    /// <summary>A real public address starts, which is the whole point of refusing the others.</summary>
    [Fact]
    public void A_real_manage_booking_url_starts_outside_Development()
    {
        using var factory = new YallaApiFactory()
            .WithEnvironment(Environments.Staging)
            .With("PublicWeb:ManageBookingUrlTemplate", "https://yalla.app/booking/{token}");

        Assert.Null(Record.Exception(() => factory.CreateClient()));
    }

    /// <summary>
    /// Development is exempt, and loopback is exactly what it should have.
    /// </summary>
    /// <remarks>
    /// A developer with no settings gets a working local link rather than a startup failure. The
    /// value that is correct here is the one that is refused everywhere else, which is why the
    /// check is on the environment rather than on the value alone.
    /// </remarks>
    [Fact]
    public void Development_starts_on_the_loopback_default()
    {
        using var factory = new YallaApiFactory()
            .WithEnvironment(Environments.Development)
            .With("PublicWeb:ManageBookingUrlTemplate", "http://localhost:5173/booking/{token}");

        Assert.Null(Record.Exception(() => factory.CreateClient()));
    }

    // ------------------------------------------------------------ CORS outside Development

    /// <summary>
    /// The allowlist is the whole policy outside Development: a configured origin passes, and a
    /// LAN origin - which Development would have admitted - does not.
    /// </summary>
    [Fact]
    public async Task Outside_Development_only_configured_origins_are_allowed()
    {
        using var factory = new YallaApiFactory()
            .WithEnvironment(Environments.Staging)
            .With("Cors:AllowedOrigins:0", "https://admin.yalla.app");

        using var client = factory.CreateClient();

        var allowed = await PreflightAsync(client, "https://admin.yalla.app");
        Assert.Equal("https://admin.yalla.app", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        // The Development predicate would have admitted both of these. Outside Development it does
        // not run at all, so a private address is as foreign as a public one.
        foreach (var refused in new[] { "http://192.168.1.42:5173", "http://localhost:5173", "https://evil.example.com" })
        {
            var response = await PreflightAsync(client, refused);

            Assert.False(
                response.Headers.Contains("Access-Control-Allow-Origin"),
                $"{refused} should not be allowed outside Development.");
        }
    }

    /// <summary>
    /// With nothing configured, no browser origin gets in. Never a wildcard - which with
    /// credentials the browser would refuse anyway, and which is what an empty list used to mean.
    /// </summary>
    [Fact]
    public async Task Outside_Development_an_empty_allowlist_admits_nobody()
    {
        using var factory = new YallaApiFactory().WithEnvironment(Environments.Staging);
        using var client = factory.CreateClient();

        var response = await PreflightAsync(client, "https://admin.yalla.app");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static async Task<HttpResponseMessage> PreflightAsync(HttpClient client, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/venue/sign-in");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");

        return await client.SendAsync(request);
    }
}
