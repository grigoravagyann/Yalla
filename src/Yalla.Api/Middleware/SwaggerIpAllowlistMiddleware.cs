using System.Net;
using Yalla.Api.ApplicationExtensions;

namespace Yalla.Api.Middleware;

/// <summary>
/// Restricts <c>/swagger</c> to a list of addresses, when one is configured.
/// </summary>
/// <remarks>
/// <para>
/// The case this exists for is a QA environment that has to show Swagger to the office or a VPN
/// without showing it to the internet. An empty <c>Swagger:AllowedIps</c> means no restriction,
/// so the setting costs nothing where it is not wanted.
/// </para>
/// <para>
/// A disallowed address gets <b>404</b>, not 403. A 403 says "this exists and you may not see
/// it", which tells a probe exactly where to point its next attempt; a 404 says nothing at all.
/// </para>
/// </remarks>
internal sealed class SwaggerIpAllowlistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SwaggerIpAllowlistMiddleware> _logger;
    private readonly IReadOnlyList<IPAddress> _allowed;

    public SwaggerIpAllowlistMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<SwaggerIpAllowlistMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        var configured = configuration
            .GetSection(SwaggerExtensions.SectionName)
            .GetSection("AllowedIps")
            .Get<string[]>() ?? [];

        _allowed = configured
            .Select(value => IPAddress.TryParse(value.Trim(), out var address) ? address : null)
            .OfType<IPAddress>()
            .ToList();

        if (_allowed.Count != configured.Length)
        {
            // Silently dropping an unparseable entry would quietly narrow the allowlist and lock
            // QA out of a tool that worked yesterday.
            _logger.LogError(
                "Swagger:AllowedIps has {Configured} entries but only {Parsed} are valid IP addresses. "
                + "The invalid ones are ignored, which means fewer addresses can reach Swagger than intended.",
                configured.Length, _allowed.Count);
        }
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_allowed.Count == 0 || !context.Request.Path.StartsWithSegments(SwaggerExtensions.BasePath))
        {
            return _next(context);
        }

        var remote = context.Connection.RemoteIpAddress;

        // IPv4 arriving over a dual-stack socket looks like ::ffff:10.0.0.1, which does not equal
        // 10.0.0.1. Mapping both sides back to IPv4 is what makes a plainly written allowlist work.
        if (remote is not null && _allowed.Any(allowed => Matches(allowed, remote)))
        {
            return _next(context);
        }

        _logger.LogWarning(
            "Swagger request from {RemoteAddress} refused: not in Swagger:AllowedIps.", remote);

        context.Response.StatusCode = StatusCodes.Status404NotFound;

        return Task.CompletedTask;
    }

    private static bool Matches(IPAddress allowed, IPAddress remote) =>
        Normalise(allowed).Equals(Normalise(remote));

    private static IPAddress Normalise(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
