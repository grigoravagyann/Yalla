using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Which callers may say who the client really is (K10), through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="YallaApiFactory.RemoteAddressHeader"/> plays the connection's own address - the proxy's
/// when the request comes through one - and <c>X-Forwarded-For</c> is what the proxy, or a client
/// pretending to be one, adds.
/// </para>
/// <para>
/// Most of these read the result through the Swagger allowlist, which admits one address and answers
/// 404 to every other: an observable "what does the API think the caller's address is" that needs no
/// database. The rate-limit test is the one the setting exists for.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ForwardedHeadersTests(SqlServerFixture fixture)
{
    private const string DocumentPath = "/swagger/v1/swagger.json";
    private const string Proxy = "10.0.0.1";
    private const string Client = "203.0.113.1";

    // ------------------------------------------------------------ who is trusted

    [Fact]
    public async Task A_known_proxy_is_believed_about_the_callers_address()
    {
        await using var factory = SwaggerFor(Client).With("ForwardedHeaders:KnownProxies:0", Proxy);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, DocumentPath, Proxy, Client));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(client, DocumentPath, Proxy, "203.0.113.2"));
    }

    [Fact]
    public async Task The_same_header_from_an_address_that_is_not_a_known_proxy_is_ignored()
    {
        await using var factory = SwaggerFor(Client).With("ForwardedHeaders:KnownProxies:0", Proxy);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(client, DocumentPath, "10.0.0.99", Client));

        // The allowlist itself works: the client connecting directly is admitted.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, DocumentPath, Client, forwardedFor: null));
    }

    /// <summary>
    /// Nothing configured trusts nobody - not even loopback, which the framework's own defaults trust,
    /// and not "everybody", which is what it does with both of its lists cleared.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData(Proxy)]
    public async Task With_no_proxy_listed_the_header_is_ignored_from_every_address(string remote)
    {
        await using var factory = SwaggerFor(Client);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(client, DocumentPath, remote, Client));
    }

    [Fact]
    public async Task A_known_network_trusts_every_address_inside_it_and_none_outside()
    {
        await using var factory = SwaggerFor(Client).With("ForwardedHeaders:KnownNetworks:0", "10.1.0.0/16");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, DocumentPath, "10.1.2.3", Client));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(client, DocumentPath, "10.2.0.1", Client));
    }

    /// <summary>
    /// A client can write anything at the left of the header; a proxy appends on the right. With one
    /// proxy only the right-hand hop is read.
    /// </summary>
    [Fact]
    public async Task Only_the_hop_the_proxy_appended_is_read()
    {
        await using var factory = SwaggerFor(Client).With("ForwardedHeaders:KnownProxies:0", Proxy);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, DocumentPath, Proxy, $"198.51.100.7, {Client}"));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(client, DocumentPath, Proxy, $"{Client}, 198.51.100.7"));
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-address", "KnownProxies:0")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/33", "KnownNetworks:0")]
    [InlineData("ForwardedHeaders:ForwardLimit", "0", "ForwardLimit")]
    public void A_value_that_cannot_be_read_refuses_to_start(string key, string value, string named)
    {
        using var factory = new YallaApiFactory().With(key, value);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains(named, failure!.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the rate limit it exists for

    /// <summary>
    /// Through a known proxy each client has its own budget on a place's details - 120 a minute as
    /// shipped - and the header from anybody else changes nothing.
    /// </summary>
    [SkippableFact]
    public async Task Through_a_known_proxy_each_forwarded_client_has_its_own_place_budget()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        const int budget = 120;

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("ForwardedHeaders:KnownProxies:0", Proxy)
            .With("RateLimiting:Enabled", "true")

            // Generous, so the per-place budget from appsettings.json is the one that fires.
            .With("RateLimiting:GlobalPermitLimit", "10000")
            .With("RateLimiting:PublicBranchPermitLimit", "10000");

        Guid branchId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branchId = (await AuthTestData.CreateBranchAsync(db)).BranchId;
        }

        using var client = factory.CreateClient();
        var route = $"/api/public/branches/{branchId}";

        for (var i = 0; i < budget; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await SendAsync(client, route, Proxy, Client));
        }

        using (var refused = Request(route, Proxy, Client))
        {
            var response = await client.SendAsync(refused);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("rate-limited", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // Another client behind the same proxy is somebody else.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, route, Proxy, "203.0.113.2"));

        // Not a proxy: every request names a different client, and all of them are one caller.
        const string stranger = "10.9.9.9";

        for (var i = 0; i < budget; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await SendAsync(client, route, stranger, $"198.51.100.{i + 1}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(client, route, stranger, "198.51.100.200"));
    }

    // ------------------------------------------------------------ the startup warning

    [Fact]
    public void Production_with_rate_limiting_on_and_no_proxy_listed_warns_at_startup()
    {
        var log = new CapturedLog();

        using (var factory = new YallaApiFactory()
                   .WithEnvironment(Environments.Production)
                   .Without("RateLimiting:Enabled")
                   .WithCapturedLog(log))
        {
            factory.CreateClient().Dispose();
        }

        Assert.Contains(
            log.Text.Split(Environment.NewLine),
            line => line.StartsWith("Warning ", StringComparison.Ordinal)
                    && line.Contains("No reverse proxy is trusted", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_with_a_proxy_listed_says_which_and_does_not_warn()
    {
        var log = new CapturedLog();

        using (var factory = new YallaApiFactory()
                   .WithEnvironment(Environments.Production)
                   .Without("RateLimiting:Enabled")
                   .With("ForwardedHeaders:KnownProxies:0", Proxy)
                   .WithCapturedLog(log))
        {
            factory.CreateClient().Dispose();
        }

        Assert.Contains($"Trusting X-Forwarded-For and X-Forwarded-Proto from proxies [{Proxy}]", log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("No reverse proxy is trusted", log.Text, StringComparison.Ordinal);
    }

    /// <summary>Development and Staging stay quiet about it: nobody is load-balanced there by accident.</summary>
    [Fact]
    public void Development_with_no_proxy_listed_does_not_warn()
    {
        var log = new CapturedLog();

        using (var factory = new YallaApiFactory().With("RateLimiting:Enabled", "true").WithCapturedLog(log))
        {
            factory.CreateClient().Dispose();
        }

        Assert.Contains("Photo storage root is", log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("No reverse proxy is trusted", log.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A host whose Swagger document is served only to <paramref name="allowed"/>.</summary>
    private static YallaApiFactory SwaggerFor(string allowed) =>
        new YallaApiFactory().WithSwagger(enabled: true, allowed);

    private static HttpRequestMessage Request(string path, string remote, string? forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(YallaApiFactory.RemoteAddressHeader, remote);

        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        return request;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string path, string remote, string? forwardedFor)
    {
        using var request = Request(path, remote, forwardedFor);

        return (await client.SendAsync(request)).StatusCode;
    }
}
