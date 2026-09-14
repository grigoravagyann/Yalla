using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// Which reverse proxies may say who the caller really is, from the <c>ForwardedHeaders</c> section (K10).
/// </summary>
/// <remarks>
/// <para>
/// Behind a load balancer every connection comes from the balancer, so everything that reads the
/// remote address - the per-address rate limits, the Swagger allowlist, the address on an audit row -
/// sees one address for the whole internet. A per-address budget of thirty a minute then becomes
/// thirty a minute for everybody, and the first busy lunchtime locks every anonymous diner out.
/// </para>
/// <para>
/// <c>X-Forwarded-For</c> fixes that, and is also something any client can type. So it is honoured
/// only on a connection from an address listed in <c>KnownProxies</c> or inside a
/// <c>KnownNetworks</c> CIDR block, and only the last <c>ForwardLimit</c> hops of it are read - the
/// ones the trusted proxies appended - never the left-hand end a client wrote.
/// </para>
/// <para>
/// <b>Both lists empty trusts no proxy, and the middleware is then not added at all.</b> That is not
/// the framework's own rule: <see cref="ForwardedHeadersOptions"/> starts with loopback trusted, and
/// with both lists cleared it checks nothing and trusts every caller's header. Not registering it is
/// the only reading of "empty" that is safe.
/// </para>
/// </remarks>
public static class ForwardedHeadersExtensions
{
    /// <summary>Configuration section holding <c>KnownProxies</c>, <c>KnownNetworks</c> and <c>ForwardLimit</c>.</summary>
    public const string SectionName = "ForwardedHeaders";

    /// <summary>How many hops of <c>X-Forwarded-For</c> are read when the section does not say.</summary>
    public const int DefaultForwardLimit = 1;

    /// <summary>The proxies this host trusts, read and validated once.</summary>
    /// <param name="KnownProxies">Single addresses.</param>
    /// <param name="KnownNetworks">CIDR blocks.</param>
    /// <param name="ForwardLimit">Hops read from the right-hand end of the header.</param>
    internal sealed record ProxyTrust(
        IReadOnlyList<IPAddress> KnownProxies,
        IReadOnlyList<System.Net.IPNetwork> KnownNetworks,
        int ForwardLimit)
    {
        /// <summary>Whether any proxy at all is trusted - the condition for adding the middleware.</summary>
        public bool TrustsAnyProxy => KnownProxies.Count > 0 || KnownNetworks.Count > 0;
    }

    /// <summary>
    /// Reads the section, refusing to start on a value that is not an address, a CIDR block or a
    /// positive limit.
    /// </summary>
    /// <remarks>
    /// A typo here does not fail a request, it quietly changes who is trusted - so it fails startup,
    /// with the key and the value in the message.
    /// </remarks>
    internal static ProxyTrust ReadTrust(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        var proxies = new List<IPAddress>();

        foreach (var (key, value) in Entries(section, "KnownProxies"))
        {
            if (!IPAddress.TryParse(value, out var address))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:KnownProxies:{key} is '{value}', which is not an IP address. List each "
                    + $"reverse proxy's own address (ForwardedHeaders__KnownProxies__{key}), or use "
                    + "KnownNetworks for a CIDR block.");
            }

            proxies.Add(address);
        }

        var networks = new List<System.Net.IPNetwork>();

        foreach (var (key, value) in Entries(section, "KnownNetworks"))
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:KnownNetworks:{key} is '{value}', which is not a CIDR block such as "
                    + $"10.0.0.0/16 (ForwardedHeaders__KnownNetworks__{key}).");
            }

            networks.Add(network);
        }

        var limit = section.GetValue<int?>("ForwardLimit") ?? DefaultForwardLimit;

        if (limit < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ForwardLimit is {limit}. It is the number of proxies in front of this API, "
                + "one or more; leave both proxy lists empty to trust none.");
        }

        return new ProxyTrust(proxies, networks, limit);
    }

    /// <summary>
    /// Adds the forwarded-headers middleware when a proxy is trusted, and says at startup what was decided.
    /// </summary>
    /// <remarks>
    /// Call it first in the pipeline, before anything that reads the caller's address or scheme: the
    /// Swagger allowlist, HTTPS redirection, authentication and the rate limiter.
    /// </remarks>
    public static WebApplication UseYallaForwardedHeaders(this WebApplication app)
    {
        var trust = ReadTrust(app.Configuration);

        // ASPNETCORE_FORWARDEDHEADERS_ENABLED makes the host add its own copy of this middleware with
        // both lists cleared - which trusts a forwarded header from anybody at all.
        if (app.Configuration.GetValue<bool>("FORWARDEDHEADERS_ENABLED"))
        {
            app.Logger.LogWarning(
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED is set, so the host trusts X-Forwarded-For from every "
                + "caller and any client can choose its own rate-limit budget. Unset it and list the proxies "
                + "under {Section} instead.",
                SectionName);
        }

        if (!trust.TrustsAnyProxy)
        {
            if (app.Environment.IsProduction() && RateLimitingExtensions.IsEnabled(app.Configuration, app.Environment))
            {
                app.Logger.LogWarning(
                    "No reverse proxy is trusted ({Section}:KnownProxies and KnownNetworks are empty), so "
                    + "X-Forwarded-For is ignored and anonymous callers are rate limited by the connection's "
                    + "address. Behind a load balancer that is the balancer's address, and every anonymous "
                    + "caller shares one budget. List the proxy addresses before this serves traffic through one.",
                    SectionName);
            }

            return app;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = trust.ForwardLimit,
        };

        // The defaults trust loopback. Only what the section lists is trusted here.
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();

        foreach (var proxy in trust.KnownProxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in trust.KnownNetworks)
        {
            options.KnownNetworks.Add(
                new Microsoft.AspNetCore.HttpOverrides.IPNetwork(network.BaseAddress, network.PrefixLength));
        }

        app.UseForwardedHeaders(options);

        app.Logger.LogInformation(
            "Trusting X-Forwarded-For and X-Forwarded-Proto from proxies [{Proxies}] and networks [{Networks}], "
            + "reading {ForwardLimit} hop(s).",
            string.Join(", ", trust.KnownProxies),
            string.Join(", ", trust.KnownNetworks),
            trust.ForwardLimit);

        return app;
    }

    /// <summary>The non-blank entries of one list, with their index for the error message.</summary>
    private static IEnumerable<(string Key, string Value)> Entries(IConfigurationSection section, string name) =>
        section.GetSection(name)
            .GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value))
            .Select(child => (child.Key, child.Value!.Trim()));
}
