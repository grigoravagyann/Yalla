using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The headers every response owes a browser, and what the anonymous liveness probe may say.
/// </summary>
public sealed class ResponseHeadersTests
{
    [Fact]
    public async Task Every_response_says_nosniff()
    {
        await using var factory = new YallaApiFactory();
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health");
        using var missing = await client.GetAsync("/api/no-such-route");

        Assert.Equal(["nosniff"], health.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(["nosniff"], missing.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task Sign_in_bodies_are_never_cached()
    {
        await using var factory = new YallaApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/auth/venue/sign-in", new { email = "x", password = "y" });

        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store under /api/auth");

        // Only there: a route with no opinion of its own keeps none. (/health sets no-store itself.)
        using var elsewhere = await client.GetAsync("/api/no-such-route");

        Assert.Null(elsewhere.Headers.CacheControl);
    }

    /// <summary>
    /// With no database to reach, the probe is unhealthy - and says so without the message, which
    /// names the server it could not reach.
    /// </summary>
    [Fact]
    public async Task The_probe_reports_a_failed_check_without_its_exception()
    {
        await using var factory = new YallaApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var check = Assert.Single(body.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
        Assert.False(check.TryGetProperty("error", out _), "no exception text on an anonymous route");
        Assert.DoesNotContain("Server", await response.Content.ReadAsStringAsync());
    }
}
