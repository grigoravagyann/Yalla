using System.Net;
using System.Net.Sockets;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// CORS: an explicit allowlist from <c>Cors:AllowedOrigins</c> everywhere, plus the local
/// network in Development.
/// </summary>
/// <remarks>
/// <para>
/// Origins come from configuration (environment variables <c>Cors__AllowedOrigins__0</c>,
/// <c>__1</c>, …) rather than from code, because the three clients live at different addresses in
/// every environment.
/// </para>
/// <para>
/// <b>In Development</b> the API also answers browsers on the local network: the Vite dev server
/// at <c>http://localhost:5173</c> and at the machine's LAN address, the Expo dev server at 8081
/// and 19006, and a teammate's laptop on the same wifi. That is decided by
/// <see cref="IsAllowedInDevelopment"/> - loopback, or a private IPv4 address, over http or https -
/// and nowhere else, because the API's Kestrel endpoints only bind <c>0.0.0.0</c> in Development
/// in the first place.
/// </para>
/// <para>
/// <b>Everywhere else</b> the configured list is the whole policy. There is no wildcard, and
/// <c>AllowAnyOrigin</c> is never combined with <c>AllowCredentials</c> - the browser refuses that
/// pairing, and the framework throws on it. An empty list outside Development is a locked-down
/// policy that allows no cross-origin browser call at all, and logs an error saying so. The
/// permissive fallback that used to live here would have shipped
/// <c>Access-Control-Allow-Origin: *</c> to production the first time somebody forgot a variable.
/// </para>
/// </remarks>
public static class CorsExtensions
{
    public const string PolicyName = "YallaClients";

    /// <summary>
    /// The headers the auth flows send and read. Any header is allowed, but these are the ones a
    /// client has to be able to see on the response.
    /// </summary>
    private static readonly string[] ExposedHeaders =
    [
        "WWW-Authenticate",
        "Retry-After",
        "Location",
    ];

    public static WebApplicationBuilder AddYallaCors(this WebApplicationBuilder builder)
    {
        var configured = ConfiguredOrigins(builder.Configuration);
        var isDevelopment = builder.Environment.IsDevelopment();

        builder.Services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (isDevelopment)
            {
                // A predicate, not a wildcard: the local network, decided per origin, with
                // credentials. Configured origins pass too.
                policy.SetIsOriginAllowed(origin => IsAllowedInDevelopment(origin, configured));
            }
            else if (configured.Length > 0)
            {
                policy.WithOrigins(configured);
            }
            else
            {
                // No origins, no cross-origin access. Same-origin callers and non-browser clients
                // - the staff tablet, the mobile apps - are unaffected, because CORS is a browser
                // mechanism and they do not send an Origin header.
                return;
            }

            policy.AllowAnyMethod()
                .AllowAnyHeader()
                .WithExposedHeaders(ExposedHeaders)
                .AllowCredentials();
        }));

        return builder;
    }

    public static WebApplication UseYallaCors(this WebApplication app)
    {
        var configured = ConfiguredOrigins(app.Configuration);

        if (app.Environment.IsDevelopment())
        {
            app.Logger.LogInformation(
                "CORS (Development): localhost and private-network origins are allowed with credentials, "
                + "plus {ConfiguredCount} configured origin(s).",
                configured.Length);
        }
        else if (configured.Length == 0)
        {
            app.Logger.LogError(
                "Cors:AllowedOrigins is empty in the {Environment} environment. No browser origin "
                + "can call this API. Configure the client origins.",
                app.Environment.EnvironmentName);
        }
        else
        {
            app.Logger.LogInformation(
                "CORS allows {OriginCount} origin(s): {Origins}",
                configured.Length, string.Join(", ", configured));
        }

        app.UseCors(PolicyName);

        return app;
    }

    /// <summary>
    /// The Development rule, as a function: an origin the configuration names, or any
    /// <c>http</c>/<c>https</c> origin whose host is loopback or a private IPv4 address, on any port.
    /// </summary>
    /// <remarks>
    /// Any port, because Vite, Expo and Storybook each pick their own and a teammate's second
    /// Vite instance lands on 5174. Private address ranges only (10/8, 172.16/12, 192.168/16), so a
    /// page served from the public internet is refused even in Development.
    /// </remarks>
    internal static bool IsAllowedInDevelopment(string origin, IReadOnlyCollection<string> configured)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (configured.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IsPrivateIPv4(address);
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();

        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168);
    }

    private static string[] ConfiguredOrigins(IConfiguration configuration) =>
        (configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .Where(o => !string.IsNullOrWhiteSpace(o))
        .Select(o => o.Trim().TrimEnd('/'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
