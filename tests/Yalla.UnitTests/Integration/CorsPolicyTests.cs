using System.Net;
using Yalla.Api.ApplicationExtensions;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// CORS as the browsers on the local network see it: the Development predicate, and the real
/// preflight through the real pipeline. No database.
/// </summary>
public class CorsPolicyTests
{
    private static readonly string[] NoConfigured = [];

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost:8081")]
    [InlineData("http://localhost:19006")]
    [InlineData("http://127.0.0.1:5174")]
    [InlineData("https://localhost:5173")]
    [InlineData("http://192.168.1.42:5173")]
    [InlineData("http://172.20.10.3:8081")]
    [InlineData("http://10.0.0.7:19006")]
    public void Development_allows_loopback_and_private_network_origins(string origin) =>
        Assert.True(CorsExtensions.IsAllowedInDevelopment(origin, NoConfigured));

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://8.8.8.8:5173")]
    [InlineData("http://172.32.0.1:5173")]       // just outside 172.16/12
    [InlineData("http://192.169.0.1:5173")]      // just outside 192.168/16
    [InlineData("exp://192.168.1.42:8081")]      // Expo's deep-link scheme is not a browser origin
    [InlineData("null")]
    [InlineData("")]
    public void Development_refuses_public_and_non_http_origins(string origin) =>
        Assert.False(CorsExtensions.IsAllowedInDevelopment(origin, NoConfigured));

    [Fact]
    public void A_configured_origin_is_allowed_even_when_it_is_public() =>
        Assert.True(CorsExtensions.IsAllowedInDevelopment("https://admin.yalla.app", ["https://admin.yalla.app"]));

    /// <summary>
    /// A real preflight from a Vite server on the LAN, through the real pipeline in Development.
    /// The origin is echoed back - never <c>*</c> - with credentials allowed.
    /// </summary>
    [Fact]
    public async Task A_preflight_from_a_LAN_origin_is_answered_with_that_origin_and_credentials()
    {
        await using var factory = new YallaApiFactory();
        using var client = factory.CreateClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/auth/venue/sign-in");
        preflight.Headers.Add("Origin", "http://192.168.1.42:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");

        var response = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://192.168.1.42:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.Contains("authorization", response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_preflight_from_a_public_origin_gets_no_CORS_headers()
    {
        await using var factory = new YallaApiFactory();
        using var client = factory.CreateClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/auth/venue/sign-in");
        preflight.Headers.Add("Origin", "https://evil.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(preflight);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
